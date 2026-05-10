using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MediaTools.Application.Abstractions;
using MediaTools.Application.DTOs;
using MediaTools.Application.UseCases;
using MediaTools.Domain.Enums;
using MediaTools.Domain.ValueObjects;
using MediaTools.Presentation.Undo;

namespace MediaTools.Presentation.ViewModels;

public partial class VideoCompressViewModel : ObservableObject
{
    private static readonly HashSet<string> AllowedExtensions =
    [
        ".mp4", ".mkv", ".avi", ".mov", ".wmv", ".flv", ".m4v", ".webm"
    ];

    private readonly CompressVideoUseCase _compressVideoUseCase;
    private readonly IVideoCompressionService _videoCompressionService;
    private readonly UndoRedoHost<VideoCompressUndoSnapshot> _history;
    private CancellationTokenSource? _compressionCts;
    private long _sourceSizeBytes;
    private bool _suppressUndoNotification;

    public VideoCompressViewModel(CompressVideoUseCase compressVideoUseCase, IVideoCompressionService videoCompressionService)
    {
        _compressVideoUseCase = compressVideoUseCase;
        _videoCompressionService = videoCompressionService;

        // Backing field only: setter runs OnOutputDirectoryChanged → NotifyUndoableEdit before _history exists.
        _outputDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
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
    }

    public ObservableCollection<int> AudioBitrates { get; } = [];

    private static int[] AudioBitrateOptions => [64, 96, 128, 160, 192, 256, 320];

    public IEnumerable<VideoCodec> VideoCodecItems => Enum.GetValues<VideoCodec>();

    public IEnumerable<EncodePreset> EncodePresetItems => Enum.GetValues<EncodePreset>();

    public IEnumerable<AudioCodec> AudioCodecItems => Enum.GetValues<AudioCodec>();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowFileInfoCard))]
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
    private string _outputDirectory = string.Empty;

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

    public bool ShowFileInfoCard => !string.IsNullOrWhiteSpace(SelectedFilePath);

    public bool ShowProgressCard => IsRunning || CompressionAttemptFinished;

    public bool ShowResultCard => CompressionSucceeded;

    public bool ShowCancelButton => IsRunning;

    public int ProgressPercentDisplay => (int)Math.Round(ProgressPercent01 * 100, MidpointRounding.AwayFromZero);

    private bool CanStartCompression() =>
        !string.IsNullOrWhiteSpace(SelectedFilePath)
        && Directory.Exists(OutputDirectory)
        && !IsRunning;

    private bool CanUndoOperation() => _history.CanUndo && !IsRunning;

    private bool CanRedoOperation() => _history.CanRedo && !IsRunning;

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
        foreach (var path in paths)
        {
            var ext = Path.GetExtension(path);
            if (string.IsNullOrEmpty(ext))
            {
                continue;
            }

            if (AllowedExtensions.Contains(ext.ToLowerInvariant()))
            {
                _ = LoadFromDropWithUndoAsync(path);
                break;
            }
        }
    }

    [RelayCommand]
    private async Task BrowseFileAsync()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "Video files|*.mp4;*.mkv;*.avi;*.mov;*.wmv;*.flv;*.m4v;*.webm|All files|*.*"
        };

        if (dlg.ShowDialog() != true)
        {
            return;
        }

        _history.BeginUndoGroup();
        _suppressUndoNotification = true;
        bool loaded;
        try
        {
            loaded = await LoadSelectedFileAsync(dlg.FileName).ConfigureAwait(true);
        }
        finally
        {
            _suppressUndoNotification = false;
        }

        if (loaded)
        {
            _history.EndUndoGroup();
        }
        else
        {
            _history.CancelUndoGroup();
        }
    }

    [RelayCommand]
    private void BrowseOutput()
    {
        var dlg = new Ookii.Dialogs.Wpf.VistaFolderBrowserDialog
        {
            SelectedPath = Directory.Exists(OutputDirectory)
                ? OutputDirectory
                : Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),
            UseDescriptionForTitle = true,
            Description = "Choose output folder"
        };

        if (dlg.ShowDialog() != true || string.IsNullOrWhiteSpace(dlg.SelectedPath))
        {
            return;
        }

        var path = dlg.SelectedPath;
        _history.PushUndoFrameAnd(() => OutputDirectory = path);
    }

    [RelayCommand]
    private void ClearFile()
    {
        _history.PushUndoFrameAnd(() =>
        {
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
        if (SelectedFilePath is null || !CanStartCompression())
        {
            return;
        }

        CompressionAttemptFinished = false;
        CompressionSucceeded = false;
        IsRunning = true;
        ProgressPercent01 = 0;
        ProgressStatusText = "Compressing…";
        ProgressDetailText = "Preparing encoder";
        ElapsedDisplay = FormatShortTime(TimeSpan.Zero);
        EstimatedRemainingDisplay = "—";

        _compressionCts = new CancellationTokenSource();
        var token = _compressionCts.Token;

        var profile = BuildCurrentProfile();
        var outputName = Path.GetFileNameWithoutExtension(SelectedFilePath) + profile.OutputFileExtension;
        var outputPath = Path.Combine(OutputDirectory, outputName);

        var request = new CompressVideoRequest(SelectedFilePath, outputPath, profile);

        var dispatcher = global::System.Windows.Application.Current?.Dispatcher;

        var progress = new Progress<CompressionProgressReport>(r =>
        {
            void Apply()
            {
                ProgressPercent01 = r.Percent01;
                ProgressDetailText = r.CurrentStepDescription;
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

        try
        {
            var result = await _compressVideoUseCase
                .ExecuteAsync(request, progress, token)
                .ConfigureAwait(true);

            if (result.IsCancelled)
            {
                ProgressStatusText = "Cancelled";
                CompressionSucceeded = false;
            }
            else if (result.IsSuccess)
            {
                CompressionSucceeded = true;
                ProgressStatusText = "Complete";
                ProgressPercent01 = 1;
                var outLen = new FileInfo(outputPath).Length;
                ResultOriginalSizeDisplay = FormatBytes(_sourceSizeBytes);
                ResultCompressedSizeDisplay = FormatBytes(outLen);
                SavedPercentDisplay = _sourceSizeBytes > 0
                    ? $"{(1 - (double)outLen / _sourceSizeBytes) * 100:0.#}%"
                    : "0%";
                ResultSummaryText =
                    $"{ResultOriginalSizeDisplay} → {ResultCompressedSizeDisplay} ({SavedPercentDisplay} saved)";
            }
            else
            {
                ProgressStatusText = "Failed";
                CompressionSucceeded = false;
                ProgressDetailText = result.ErrorMessage ?? "Unknown error";
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
        if (!Directory.Exists(OutputDirectory))
        {
            return;
        }

        Process.Start(new ProcessStartInfo
        {
            FileName = OutputDirectory,
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
            GetExtensionForSelection());
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

    private async Task LoadFromDropWithUndoAsync(string path)
    {
        _history.BeginUndoGroup();
        _suppressUndoNotification = true;
        bool loaded;
        try
        {
            loaded = await LoadSelectedFileAsync(path).ConfigureAwait(true);
        }
        finally
        {
            _suppressUndoNotification = false;
        }

        if (loaded)
        {
            _history.EndUndoGroup();
        }
        else
        {
            _history.CancelUndoGroup();
        }
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
            global::System.Windows.MessageBox.Show(
                $"Could not read media file: {ex.Message}",
                "MediaTools",
                global::System.Windows.MessageBoxButton.OK,
                global::System.Windows.MessageBoxImage.Warning);
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
            OutputDirectory,
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
        OutputDirectory = s.OutputDirectory;
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

    partial void OnOutputDirectoryChanged(string value)
    {
        StartCompressionCommand.NotifyCanExecuteChanged();
        NotifyUndoableEdit();
    }

    partial void OnSelectedFilePathChanged(string? value) =>
        StartCompressionCommand.NotifyCanExecuteChanged();

    partial void OnIsRunningChanged(bool value)
    {
        StartCompressionCommand.NotifyCanExecuteChanged();
        UndoCommand.NotifyCanExecuteChanged();
        RedoCommand.NotifyCanExecuteChanged();
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
