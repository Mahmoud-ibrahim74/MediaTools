using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Diagnostics;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MediaTools.Application.Abstractions;
using MediaTools.Application.DTOs;
using MediaTools.Application.UseCases;
using MediaTools.Domain.Enums;
using MediaTools.Domain.ValueObjects;
using MediaTools.Presentation.Helpers;
using MediaTools.Presentation.Services;
using MediaTools.Presentation.Undo;

namespace MediaTools.Presentation.ViewModels;

public partial class VideoCompressViewModel : ObservableObject
{
    private static readonly HashSet<string> AllowedExtensions =
    [
        ".mp4", ".mkv", ".avi", ".mov", ".wmv", ".flv", ".m4v", ".webm"
    ];

    private const int MaxBatchVideoFiles = 20;

    private readonly CompressVideoUseCase _compressVideoUseCase;
    private readonly IVideoCompressionService _videoCompressionService;
    private readonly IUserPreferencesService _preferences;
    private readonly IWindowsToastNotificationService _toastNotifications;
    private readonly UndoRedoHost<VideoCompressUndoSnapshot> _history;
    private CancellationTokenSource? _compressionCts;
    private long _sourceSizeBytes;
    private bool _suppressUndoNotification;

    public ObservableCollection<BatchCompressEntryViewModel> BatchItems { get; } = [];

    public VideoCompressViewModel(
        CompressVideoUseCase compressVideoUseCase,
        IVideoCompressionService videoCompressionService,
        IUserPreferencesService preferences,
        IWindowsToastNotificationService toastNotifications)
    {
        _compressVideoUseCase = compressVideoUseCase;
        _videoCompressionService = videoCompressionService;
        _preferences = preferences;
        _toastNotifications = toastNotifications;
        _preferences.SaveFolderPathChanged += OnSaveFolderPathChanged;
        _preferences.VideoEncoderSettingsChanged += OnVideoEncoderSettingsChanged;

        foreach (var b in AudioBitrateOptions)
        {
            AudioBitrates.Add(b);
        }

        _suppressUndoNotification = true;
        try
        {
            ApplyProfileKey("Balanced");
        }
        finally
        {
            _suppressUndoNotification = false;
        }

        _history = new UndoRedoHost<VideoCompressUndoSnapshot>(
            CaptureVideoSnapshot,
            ApplyVideoSnapshot,
            CaptureVideoSnapshot(),
            OnUndoRedoHistoryChanged);

        BatchItems.CollectionChanged += OnBatchItemsCollectionChanged;
    }

    private void OnBatchItemsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        OnPropertyChanged(nameof(ShowFileInfoCard));
        OnPropertyChanged(nameof(IsBatchMode));
        OnPropertyChanged(nameof(BatchSummaryHeadline));
        OnPropertyChanged(nameof(StartCompressionButtonLabel));
        StartCompressionCommand.NotifyCanExecuteChanged();
        UndoCommand.NotifyCanExecuteChanged();
        RedoCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Multiple files queued (batch compression).</summary>
    public bool IsBatchMode => BatchItems.Count > 1;

    public string BatchSummaryHeadline =>
        BatchItems.Count <= 1 ? string.Empty : $"{BatchItems.Count} videos · {FormatBytes(BatchTotalBytes)}";

    public string StartCompressionButtonLabel =>
        BatchItems.Count > 1 ? $"Compress {BatchItems.Count} videos" : "Start compression";

    private long BatchTotalBytes
    {
        get
        {
            long t = 0;
            foreach (var item in BatchItems)
            {
                try
                {
                    t += new FileInfo(item.SourcePath).Length;
                }
                catch
                {
                    // ignore
                }
            }

            return t;
        }
    }

    private void OnSaveFolderPathChanged(object? sender, EventArgs e) =>
        StartCompressionCommand.NotifyCanExecuteChanged();

    private void OnVideoEncoderSettingsChanged(object? sender, EventArgs e) =>
        OnPropertyChanged(nameof(ExportVideoEncoderDisplay));

    /// <summary>Effective H.264/H.265 encoder when GPU path is used (App settings + last encoder scan).</summary>
    private VideoHardwareEncoderKind ResolveEncoderForExport()
    {
        var pref = _preferences.PreferredVideoHardwareEncoder;
        if (pref == VideoHardwareEncoderKind.Software)
        {
            return VideoHardwareEncoderKind.Software;
        }

        if (_preferences.LastVideoEncoderScan is not { } scan)
        {
            return VideoHardwareEncoderKind.Software;
        }

        return pref switch
        {
            VideoHardwareEncoderKind.Nvenc when scan.NvencAvailable => VideoHardwareEncoderKind.Nvenc,
            VideoHardwareEncoderKind.Amf when scan.AmfAvailable => VideoHardwareEncoderKind.Amf,
            VideoHardwareEncoderKind.QuickSync when scan.QuickSyncAvailable => VideoHardwareEncoderKind.QuickSync,
            _ => VideoHardwareEncoderKind.Software
        };
    }

    private static string FormatEncoderForUi(VideoHardwareEncoderKind k) =>
        k switch
        {
            VideoHardwareEncoderKind.Nvenc => "NVENC — NVIDIA GPU Encoder",
            VideoHardwareEncoderKind.Amf => "AMF — AMD GPU Encoder",
            VideoHardwareEncoderKind.QuickSync => "QuickSync — Intel GPU Encoder",
            _ => "Software (libx264 / libx265) — CPU"
        };

    /// <summary>
    /// For H.264/H.265 exports, reflects App settings hardware choice when the scan says it is available;
    /// AV1/VP9 always use CPU codecs regardless of this label.
    /// </summary>
    public string ExportVideoEncoderDisplay => FormatEncoderForUi(ResolveEncoderForExport());

    public ObservableCollection<int> AudioBitrates { get; } = [];

    private static int[] AudioBitrateOptions => [64, 96, 128, 160, 192, 256, 320];

    public IEnumerable<VideoCodec> VideoCodecItems => Enum.GetValues<VideoCodec>();

    public IEnumerable<EncodePreset> EncodePresetItems => Enum.GetValues<EncodePreset>();

    public IEnumerable<AudioCodec> AudioCodecItems => Enum.GetValues<AudioCodec>();

    [ObservableProperty]
    private string? _selectedFilePath;

    [ObservableProperty]
    private string _fileDisplayName = string.Empty;

    [ObservableProperty]
    private string _fileSizeDisplay = string.Empty;

    [ObservableProperty]
    private string _durationDisplay = string.Empty;

    [ObservableProperty]
    private string _formatDisplay = string.Empty;

    [ObservableProperty]
    private int _crf = 23;

    [ObservableProperty]
    private VideoCodec _selectedVideoCodec = VideoCodec.H264;

    [ObservableProperty]
    private AudioCodec _selectedAudioCodec = AudioCodec.AAC;

    [ObservableProperty]
    private EncodePreset _selectedEncodePreset = EncodePreset.Medium;

    [ObservableProperty]
    private string? _targetWidthInput;

    [ObservableProperty]
    private string? _targetHeightInput;

    [ObservableProperty]
    private int _audioBitrateKbps = 160;

    [ObservableProperty]
    private bool _removeAudio;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProgressPercentDisplay))]
    [NotifyPropertyChangedFor(nameof(ShowProgressCard))]
    private double _progressPercent01;

    [ObservableProperty]
    private string _progressStatusText = string.Empty;

    [ObservableProperty]
    private string _progressDetailText = string.Empty;

    [ObservableProperty]
    private string _elapsedDisplay = "00:00";

    [ObservableProperty]
    private string _estimatedRemainingDisplay = "—";

    [ObservableProperty]
    private string _resultOriginalSizeDisplay = string.Empty;

    [ObservableProperty]
    private string _resultCompressedSizeDisplay = string.Empty;

    [ObservableProperty]
    private string _savedPercentDisplay = string.Empty;

    [ObservableProperty]
    private string _resultSummaryText = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowProgressCard))]
    [NotifyPropertyChangedFor(nameof(ShowCancelButton))]
    private bool _isRunning;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowProgressCard))]
    [NotifyPropertyChangedFor(nameof(ShowResultCard))]
    private bool _compressionSucceeded;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowProgressCard))]
    private bool _compressionAttemptFinished;

    [ObservableProperty]
    private string _selectedProfileKey = "Balanced";

    [ObservableProperty]
    private bool _isDropHover;

    public bool ShowFileInfoCard => BatchItems.Count > 0;

    public bool ShowProgressCard => IsRunning || CompressionAttemptFinished;

    public bool ShowResultCard => CompressionSucceeded;

    public bool ShowCancelButton => IsRunning;

    public int ProgressPercentDisplay => (int)Math.Round(ProgressPercent01 * 100, MidpointRounding.AwayFromZero);

    private bool CanStartCompression() =>
        BatchItems.Count > 0
        && Directory.Exists(_preferences.SaveFolderPath)
        && !IsRunning;

    private bool CanUndoOperation() => _history.CanUndo && !IsRunning && BatchItems.Count <= 1;

    private bool CanRedoOperation() => _history.CanRedo && !IsRunning && BatchItems.Count <= 1;

    private void OnUndoRedoHistoryChanged()
    {
        UndoCommand.NotifyCanExecuteChanged();
        RedoCommand.NotifyCanExecuteChanged();
    }

    private void NotifyUndoableEdit()
    {
        if (_suppressUndoNotification || _history.IsApplyingHistory)
        {
            return;
        }

        _history.NotifyEdit();
    }

    public void HandleDrop(IEnumerable<string> paths)
    {
        var list = paths
            .Where(p => AllowedExtensions.Contains(Path.GetExtension(p).ToLowerInvariant()))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (list.Count == 0)
        {
            return;
        }

        _ = ReplaceBatchFromDropWithUndoAsync(list);
    }

    private async Task ReplaceBatchFromDropWithUndoAsync(List<string> paths)
    {
        _history.BeginUndoGroup();
        _suppressUndoNotification = true;
        bool ok;
        try
        {
            ok = await ReplaceBatchFromPathsAsync(paths).ConfigureAwait(true);
        }
        finally
        {
            _suppressUndoNotification = false;
        }

        if (ok)
        {
            _history.EndUndoGroup();
        }
        else
        {
            _history.CancelUndoGroup();
        }
    }

    [RelayCommand]
    private async Task BrowseFileAsync()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Multiselect = true,
            Filter = "Video files|*.mp4;*.mkv;*.avi;*.mov;*.wmv;*.flv;*.m4v;*.webm|All files|*.*"
        };

        if (dlg.ShowDialog() != true || dlg.FileNames.Length == 0)
        {
            return;
        }

        var paths = dlg.FileNames
            .Where(p => AllowedExtensions.Contains(Path.GetExtension(p).ToLowerInvariant()))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (paths.Count == 0)
        {
            MessageBoxHelper.ShowWarning("No supported video files were selected.");
            return;
        }

        _history.BeginUndoGroup();
        _suppressUndoNotification = true;
        bool ok;
        try
        {
            ok = await ReplaceBatchFromPathsAsync(paths).ConfigureAwait(true);
        }
        finally
        {
            _suppressUndoNotification = false;
        }

        if (ok)
        {
            _history.EndUndoGroup();
        }
        else
        {
            _history.CancelUndoGroup();
        }
    }

    [RelayCommand]
    private void ClearFile()
    {
        _history.PushUndoFrameAnd(() =>
        {
            BatchItems.Clear();
            SelectedFilePath = null;
            FileDisplayName = string.Empty;
            FileSizeDisplay = string.Empty;
            DurationDisplay = string.Empty;
            FormatDisplay = string.Empty;
            _sourceSizeBytes = 0;
            ProgressPercent01 = 0;
            ProgressStatusText = string.Empty;
            ProgressDetailText = string.Empty;
            CompressionSucceeded = false;
            CompressionAttemptFinished = false;
            ElapsedDisplay = FormatShortTime(TimeSpan.Zero);
            EstimatedRemainingDisplay = "—";
            ResultOriginalSizeDisplay = string.Empty;
            ResultCompressedSizeDisplay = string.Empty;
            SavedPercentDisplay = string.Empty;
            ResultSummaryText = string.Empty;
        });
    }

    [RelayCommand]
    private void SelectProfile(string? key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return;
        }

        _history.PushUndoFrameAnd(() => ApplyProfileKey(key));
    }

    [RelayCommand(CanExecute = nameof(CanUndoOperation))]
    private void Undo() => _history.TryUndo();

    [RelayCommand(CanExecute = nameof(CanRedoOperation))]
    private void Redo() => _history.TryRedo();

    [RelayCommand(CanExecute = nameof(CanStartCompression))]
    private async Task StartCompressionAsync()
    {
        if (BatchItems.Count == 0 || !CanStartCompression())
        {
            return;
        }

        foreach (var item in BatchItems)
        {
            item.Status = BatchCompressEntryStatus.Pending;
            item.DetailMessage = string.Empty;
            item.ProducedOutputPath = null;
        }

        CompressionAttemptFinished = false;
        CompressionSucceeded = false;
        IsRunning = true;
        ProgressPercent01 = 0;
        ProgressStatusText = BatchItems.Count > 1 ? "Starting batch…" : "Compressing…";
        ProgressDetailText = "Preparing encoder";
        ElapsedDisplay = FormatShortTime(TimeSpan.Zero);
        EstimatedRemainingDisplay = "—";

        _compressionCts = new CancellationTokenSource();
        var token = _compressionCts.Token;

        var profile = BuildCurrentProfile();
        var outExt = profile.OutputFileExtension;

        var dispatcher = global::System.Windows.Application.Current?.Dispatcher;

        var total = BatchItems.Count;
        var successCount = 0;
        var failCount = 0;
        var cancelled = false;

        string? toastTitle = null;
        string? toastBody = null;
        var toastSuccess = false;

        try
        {
            for (var i = 0; i < total; i++)
            {
                if (token.IsCancellationRequested)
                {
                    cancelled = true;
                    for (var j = i; j < total; j++)
                    {
                        var st = BatchItems[j].Status;
                        if (st == BatchCompressEntryStatus.Pending || st == BatchCompressEntryStatus.Running)
                        {
                            BatchItems[j].Status = BatchCompressEntryStatus.Skipped;
                            BatchItems[j].DetailMessage = "Skipped — cancelled.";
                        }
                    }

                    break;
                }

                var entry = BatchItems[i];
                entry.Status = BatchCompressEntryStatus.Running;
                entry.DetailMessage = "Compressing…";

                long sourceLen = 0;
                try
                {
                    sourceLen = new FileInfo(entry.SourcePath).Length;
                }
                catch
                {
                    // ignore
                }

                var stem = SanitizeFileName(Path.GetFileNameWithoutExtension(entry.SourcePath));
                if (string.IsNullOrWhiteSpace(stem))
                {
                    stem = "video";
                }

                var outputPath = GetUniqueOutputPath(_preferences.SaveFolderPath, stem, outExt);
                try
                {
                    if (File.Exists(outputPath))
                    {
                        File.Delete(outputPath);
                    }
                }
                catch
                {
                    // ignore
                }

                var request = new CompressVideoRequest(entry.SourcePath, outputPath, profile);
                var fileIndex = i;

                var progress = new Progress<CompressionProgressReport>(r =>
                {
                    void Apply()
                    {
                        ProgressPercent01 = (fileIndex + r.Percent01) / total;
                        ProgressDetailText = r.CurrentStepDescription;
                        ProgressStatusText =
                            total > 1
                                ? $"File {fileIndex + 1} of {total}: {entry.FileName}"
                                : "Compressing…";
                        ElapsedDisplay = FormatShortTime(r.Elapsed);
                        EstimatedRemainingDisplay = r.EstimatedRemaining is { } er
                            ? FormatShortTime(er)
                            : "—";
                    }

                    if (dispatcher is null || dispatcher.CheckAccess())
                    {
                        Apply();
                    }
                    else
                    {
                        dispatcher.Invoke(Apply);
                    }
                });

                CompressVideoResult result;
                try
                {
                    result = await _compressVideoUseCase
                        .ExecuteAsync(request, progress, token)
                        .ConfigureAwait(true);
                }
                catch (Exception ex)
                {
                    entry.Status = BatchCompressEntryStatus.Failed;
                    entry.DetailMessage = ex.Message;
                    failCount++;
                    ProgressPercent01 = (fileIndex + 1d) / total;
                    continue;
                }

                if (result.IsCancelled)
                {
                    entry.Status = BatchCompressEntryStatus.Cancelled;
                    entry.DetailMessage = "Cancelled.";
                    cancelled = true;
                    for (var j = i + 1; j < total; j++)
                    {
                        if (BatchItems[j].Status == BatchCompressEntryStatus.Pending)
                        {
                            BatchItems[j].Status = BatchCompressEntryStatus.Skipped;
                            BatchItems[j].DetailMessage = "Skipped — batch stopped.";
                        }
                    }

                    break;
                }

                if (result.IsSuccess && File.Exists(outputPath))
                {
                    entry.Status = BatchCompressEntryStatus.Success;
                    entry.ProducedOutputPath = outputPath;
                    var outLen = new FileInfo(outputPath).Length;
                    var pct = sourceLen > 0 ? $"{(1 - (double)outLen / sourceLen) * 100:0.#}%" : "0%";
                    entry.DetailMessage =
                        $"Saved · {FormatBytes(sourceLen)} → {FormatBytes(outLen)} ({pct} saved)";
                    successCount++;
                    _preferences.IncrementLifetimeStat(AppLifetimeStatKind.VideoCompressed);

                    if (total == 1)
                    {
                        ResultOriginalSizeDisplay = FormatBytes(sourceLen);
                        ResultCompressedSizeDisplay = FormatBytes(outLen);
                        SavedPercentDisplay = pct;
                        ResultSummaryText =
                            $"{ResultOriginalSizeDisplay} → {ResultCompressedSizeDisplay} ({SavedPercentDisplay} saved)";
                    }

                    ProgressPercent01 = (fileIndex + 1d) / total;
                }
                else if (result.IsSuccess)
                {
                    entry.Status = BatchCompressEntryStatus.Failed;
                    entry.DetailMessage = result.ErrorMessage ?? "Output file was not created.";
                    failCount++;
                    ProgressPercent01 = (fileIndex + 1d) / total;
                }
                else
                {
                    entry.Status = BatchCompressEntryStatus.Failed;
                    entry.DetailMessage = result.ErrorMessage ?? "Compression failed.";
                    failCount++;
                    ProgressPercent01 = (fileIndex + 1d) / total;
                }
            }

            if (cancelled)
            {
                ProgressStatusText = "Cancelled";
                CompressionSucceeded = successCount > 0;
                ProgressPercent01 = successCount > 0 ? 1 : 0;
                ProgressDetailText = string.Empty;
                if (total > 1)
                {
                    ResultSummaryText = $"Stopped — {successCount} of {total} saved before cancel.";
                }
                else if (successCount == 0)
                {
                    ResultSummaryText = "Cancelled.";
                }

                toastTitle = "Compression cancelled";
                toastBody = successCount > 0
                    ? $"{successCount} file(s) saved before cancel."
                    : "No files were saved.";
                toastSuccess = successCount > 0;
            }
            else if (failCount == 0 && successCount == total && total > 0)
            {
                CompressionSucceeded = true;
                ProgressStatusText = total > 1 ? $"Batch complete — {successCount} files." : "Complete";
                ProgressPercent01 = 1;
                ProgressDetailText = string.Empty;
                if (total > 1)
                {
                    ResultSummaryText = $"All {successCount} files compressed successfully.";
                }

                toastTitle = total > 1 ? "Batch compression complete" : "Video compression complete";
                toastBody = total > 1
                    ? $"{successCount} file(s) saved to your export folder."
                    : $"{Path.GetFileName(BatchItems[0].ProducedOutputPath ?? "")} · {ResultSummaryText}";
                toastSuccess = true;
            }
            else if (successCount > 0)
            {
                CompressionSucceeded = true;
                ProgressStatusText = $"Finished — {successCount} succeeded, {failCount} failed.";
                ProgressPercent01 = 1;
                ProgressDetailText = string.Empty;
                ResultSummaryText =
                    $"{successCount} of {total} succeeded. {failCount} failed — see log below.";
                toastTitle = "Batch compression finished";
                toastBody = $"{successCount} ok, {failCount} failed.";
                toastSuccess = false;
            }
            else
            {
                CompressionSucceeded = false;
                ProgressStatusText = total > 1 ? "Batch failed" : "Failed";
                ProgressPercent01 = 1;
                ProgressDetailText = string.Empty;
                ResultSummaryText =
                    total > 1
                        ? $"All {total} file(s) failed. See log below."
                        : (BatchItems[0].DetailMessage.Length > 0
                            ? BatchItems[0].DetailMessage
                            : "Compression failed.");
                toastTitle = total > 1 ? "Batch compression failed" : "Video compression failed";
                toastBody = ResultSummaryText;
                toastSuccess = false;
            }
        }
        finally
        {
            IsRunning = false;
            CompressionAttemptFinished = true;
            StartCompressionCommand.NotifyCanExecuteChanged();
            UndoCommand.NotifyCanExecuteChanged();
            RedoCommand.NotifyCanExecuteChanged();
            _history.FlushPendingEdit();

            if (toastTitle is not null)
            {
                _toastNotifications.ShowToolFinished(
                    toastTitle,
                    toastBody ?? string.Empty,
                    toastSuccess,
                    "Video Compress");
            }
        }
    }

    [RelayCommand]
    private void Cancel()
    {
        _compressionCts?.Cancel();
    }

    [RelayCommand]
    private void OpenOutputFolder()
    {
        var folder = _preferences.SaveFolderPath;
        if (!Directory.Exists(folder))
        {
            return;
        }

        Process.Start(new ProcessStartInfo
        {
            FileName = folder,
            UseShellExecute = true
        });
    }

    private void ApplyProfileKey(string key)
    {
        SelectedProfileKey = key;
        var profile = key switch
        {
            "HighQuality" => CompressionProfile.HighQuality,
            "Balanced" => CompressionProfile.Balanced,
            "SmallSize" => CompressionProfile.SmallSize,
            "Web" => CompressionProfile.Web,
            _ => CompressionProfile.Balanced
        };

        Crf = profile.Crf;
        SelectedVideoCodec = profile.VideoCodec;
        SelectedAudioCodec = profile.AudioCodec;
        SelectedEncodePreset = profile.EncodePreset;
        TargetWidthInput = profile.TargetWidth?.ToString() ?? string.Empty;
        TargetHeightInput = profile.TargetHeight?.ToString() ?? string.Empty;
        AudioBitrateKbps = profile.AudioBitrateKbps;
        RemoveAudio = profile.RemoveAudio;
    }

    private CompressionProfile BuildCurrentProfile()
    {
        int? w = int.TryParse(TargetWidthInput, out var wi) ? wi : null;
        int? h = int.TryParse(TargetHeightInput, out var hi) ? hi : null;
        return new CompressionProfile(
            SelectedVideoCodec,
            SelectedAudioCodec,
            Crf,
            SelectedEncodePreset,
            w,
            h,
            AudioBitrateKbps,
            RemoveAudio,
            GetExtensionForSelection(),
            ResolveEncoderForExport());
    }

    private string GetExtensionForSelection()
    {
        var ext = SelectedVideoCodec switch
        {
            VideoCodec.VP9 => ".webm",
            _ => ".mp4"
        };
        return ext;
    }

    private async Task<bool> ReplaceBatchFromPathsAsync(IEnumerable<string> paths)
    {
        var candidates = paths
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(p =>
            {
                var ext = Path.GetExtension(p).ToLowerInvariant();
                return AllowedExtensions.Contains(ext) && File.Exists(p);
            })
            .ToList();

        if (candidates.Count > MaxBatchVideoFiles)
        {
            MessageBoxHelper.ShowWarning(
                $"You can add at most {MaxBatchVideoFiles} videos per batch. Only the first {MaxBatchVideoFiles} will be used.");
            candidates = candidates.Take(MaxBatchVideoFiles).ToList();
        }

        BatchItems.Clear();

        foreach (var path in candidates)
        {
            BatchItems.Add(new BatchCompressEntryViewModel(path));
        }

        if (BatchItems.Count == 0)
        {
            MessageBoxHelper.ShowWarning("No valid video files were found.");
            return false;
        }

        await RefreshBatchPreviewMetadataAsync().ConfigureAwait(true);
        CompressionSucceeded = false;
        CompressionAttemptFinished = false;
        return true;
    }

    private async Task RefreshBatchPreviewMetadataAsync()
    {
        if (BatchItems.Count == 0)
        {
            SelectedFilePath = null;
            FileDisplayName = string.Empty;
            FileSizeDisplay = string.Empty;
            DurationDisplay = string.Empty;
            FormatDisplay = string.Empty;
            _sourceSizeBytes = 0;
            return;
        }

        if (BatchItems.Count == 1)
        {
            await LoadSelectedFileAsync(BatchItems[0].SourcePath).ConfigureAwait(true);
            return;
        }

        SelectedFilePath = BatchItems[0].SourcePath;
        FileDisplayName = $"{BatchItems.Count} videos selected";
        long sum = 0;
        foreach (var item in BatchItems)
        {
            try
            {
                sum += new FileInfo(item.SourcePath).Length;
            }
            catch
            {
                // ignore
            }
        }

        _sourceSizeBytes = sum;
        FileSizeDisplay = FormatBytes(sum);
        DurationDisplay = "—";
        FormatDisplay = "Various";
        CompressionSucceeded = false;
        CompressionAttemptFinished = false;
    }

    private async Task<bool> LoadSelectedFileAsync(string path)
    {
        try
        {
            var media = await _videoCompressionService.AnalyzeAsync(path).ConfigureAwait(true);
            SelectedFilePath = path;
            FileDisplayName = media.FileName;
            FileSizeDisplay = media.FormattedFileSize;
            _sourceSizeBytes = media.FileSizeBytes;
            DurationDisplay = FormatTimeSpan(media.Duration);
            FormatDisplay = media.Format;
            CompressionSucceeded = false;
            CompressionAttemptFinished = false;
            return true;
        }
        catch (Exception ex)
        {
            MessageBoxHelper.ShowWarning($"Could not read media file: {ex.Message}");
            return false;
        }
    }

    private VideoCompressUndoSnapshot CaptureVideoSnapshot() =>
        new(
            SelectedFilePath,
            FileDisplayName,
            FileSizeDisplay,
            DurationDisplay,
            FormatDisplay,
            _sourceSizeBytes,
            Crf,
            SelectedVideoCodec,
            SelectedAudioCodec,
            SelectedEncodePreset,
            TargetWidthInput,
            TargetHeightInput,
            AudioBitrateKbps,
            RemoveAudio,
            ProgressPercent01,
            ProgressStatusText,
            ProgressDetailText,
            ElapsedDisplay,
            EstimatedRemainingDisplay,
            ResultOriginalSizeDisplay,
            ResultCompressedSizeDisplay,
            SavedPercentDisplay,
            ResultSummaryText,
            CompressionSucceeded,
            CompressionAttemptFinished,
            SelectedProfileKey);

    private void ApplyVideoSnapshot(VideoCompressUndoSnapshot s)
    {
        BatchItems.Clear();
        if (!string.IsNullOrWhiteSpace(s.SelectedFilePath))
        {
            BatchItems.Add(new BatchCompressEntryViewModel(s.SelectedFilePath));
        }

        SelectedFilePath = s.SelectedFilePath;
        FileDisplayName = s.FileDisplayName;
        FileSizeDisplay = s.FileSizeDisplay;
        DurationDisplay = s.DurationDisplay;
        FormatDisplay = s.FormatDisplay;
        _sourceSizeBytes = s.SourceSizeBytes;
        Crf = s.Crf;
        SelectedVideoCodec = s.SelectedVideoCodec;
        SelectedAudioCodec = s.SelectedAudioCodec;
        SelectedEncodePreset = s.SelectedEncodePreset;
        TargetWidthInput = s.TargetWidthInput;
        TargetHeightInput = s.TargetHeightInput;
        AudioBitrateKbps = s.AudioBitrateKbps;
        RemoveAudio = s.RemoveAudio;
        ProgressPercent01 = s.ProgressPercent01;
        ProgressStatusText = s.ProgressStatusText;
        ProgressDetailText = s.ProgressDetailText;
        ElapsedDisplay = s.ElapsedDisplay;
        EstimatedRemainingDisplay = s.EstimatedRemainingDisplay;
        ResultOriginalSizeDisplay = s.ResultOriginalSizeDisplay;
        ResultCompressedSizeDisplay = s.ResultCompressedSizeDisplay;
        SavedPercentDisplay = s.SavedPercentDisplay;
        ResultSummaryText = s.ResultSummaryText;
        CompressionSucceeded = s.CompressionSucceeded;
        CompressionAttemptFinished = s.CompressionAttemptFinished;
        SelectedProfileKey = s.SelectedProfileKey;
    }

    partial void OnCrfChanged(int value) => NotifyUndoableEdit();

    partial void OnSelectedVideoCodecChanged(VideoCodec value) => NotifyUndoableEdit();

    partial void OnSelectedAudioCodecChanged(AudioCodec value) => NotifyUndoableEdit();

    partial void OnSelectedEncodePresetChanged(EncodePreset value) => NotifyUndoableEdit();

    partial void OnTargetWidthInputChanged(string? value) => NotifyUndoableEdit();

    partial void OnTargetHeightInputChanged(string? value) => NotifyUndoableEdit();

    partial void OnAudioBitrateKbpsChanged(int value) => NotifyUndoableEdit();

    partial void OnRemoveAudioChanged(bool value) => NotifyUndoableEdit();

    partial void OnIsRunningChanged(bool value)
    {
        StartCompressionCommand.NotifyCanExecuteChanged();
        UndoCommand.NotifyCanExecuteChanged();
        RedoCommand.NotifyCanExecuteChanged();
    }

    private static string SanitizeFileName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return "video";
        }

        var invalid = Path.GetInvalidFileNameChars();
        var chars = name.Select(c => invalid.Contains(c) ? '_' : c).ToArray();
        return new string(chars);
    }

    private static string GetUniqueOutputPath(string folder, string stem, string ext)
    {
        var safeStem = SanitizeFileName(stem);
        var basePath = Path.Combine(folder, safeStem + ext);
        if (!File.Exists(basePath))
        {
            return basePath;
        }

        for (var n = 1; n < 10_000; n++)
        {
            var candidate = Path.Combine(folder, $"{safeStem} ({n}){ext}");
            if (!File.Exists(candidate))
            {
                return candidate;
            }
        }

        return Path.Combine(folder, $"{safeStem}_{Guid.NewGuid():N}{ext}");
    }

    private static string FormatBytes(long bytes)
    {
        const long kb = 1024;
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double size = bytes;
        var order = 0;
        while (size >= kb && order < units.Length - 1)
        {
            size /= kb;
            order++;
        }

        return $"{size:0.##} {units[order]}";
    }

    private static string FormatTimeSpan(TimeSpan ts) =>
        ts.TotalHours >= 1 ? $"{(int)ts.TotalHours}:{ts.Minutes:D2}:{ts.Seconds:D2}" : $"{ts.Minutes:D2}:{ts.Seconds:D2}";

    private static string FormatShortTime(TimeSpan ts) =>
        $"{(int)ts.TotalHours:D2}:{ts.Minutes:D2}:{ts.Seconds:D2}";
}
