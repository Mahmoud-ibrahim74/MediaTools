using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Threading;
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

public partial class AudioEnhancerViewModel : ObservableObject
{
    private static readonly HashSet<string> AllowedExtensions =
    [
        ".mp3", ".wav", ".flac", ".aac", ".m4a", ".ogg", ".opus", ".wma", ".aiff", ".aif", ".caf", ".mp4", ".mka"
    ];

    private readonly ProcessAudioUseCase _processAudioUseCase;
    private readonly IAudioProcessingService _audioProcessingService;
    private readonly IUserPreferencesService _preferences;
    private readonly IWindowsToastNotificationService _toastNotifications;
    private readonly UndoRedoHost<AudioEnhancerUndoSnapshot> _history;
    private CancellationTokenSource? _cts;
    private bool _suppressUndoNotification;
    private CancellationTokenSource? _previewDebounceCts;
    private CancellationTokenSource? _previewBuildCts;
    private string? _tempPreviewPath;

    public AudioEnhancerViewModel(
        ProcessAudioUseCase processAudioUseCase,
        IAudioProcessingService audioProcessingService,
        IUserPreferencesService preferences,
        IWindowsToastNotificationService toastNotifications)
    {
        _processAudioUseCase = processAudioUseCase;
        _audioProcessingService = audioProcessingService;
        _preferences = preferences;
        _toastNotifications = toastNotifications;
        _preferences.SaveFolderPathChanged += OnSaveFolderPathChanged;

        foreach (var b in BitratePresets)
        {
            BitrateOptions.Add(b);
        }

        _history = new UndoRedoHost<AudioEnhancerUndoSnapshot>(
            CaptureSnapshot,
            ApplySnapshot,
            CaptureSnapshot(),
            OnUndoRedoHistoryChanged,
            debounceMs: 580);
    }

    private void OnSaveFolderPathChanged(object? sender, EventArgs e) =>
        ProcessAudioCommand.NotifyCanExecuteChanged();

    private static int[] BitratePresets => [128, 160, 192, 224, 256, 320];

    public ObservableCollection<int> BitrateOptions { get; } = [];

    public IEnumerable<AudioExportFormat> TargetFormats => Enum.GetValues<AudioExportFormat>();

    public IEnumerable<AudioSampleRateOption> SampleRateOptions => Enum.GetValues<AudioSampleRateOption>();

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
    private string _codecDisplay = string.Empty;

    [ObservableProperty]
    private string _sampleRateDisplay = string.Empty;

    [ObservableProperty]
    private string _channelsDisplay = string.Empty;

    [ObservableProperty]
    private AudioExportFormat _targetFormat = AudioExportFormat.Mp3;

    [ObservableProperty]
    private int _bitrateKbps = 192;

    [ObservableProperty]
    private AudioSampleRateOption _sampleRate = AudioSampleRateOption.Original;

    [ObservableProperty]
    private bool _normalizeLoudness = true;

    [ObservableProperty]
    private int _volumePercent = 100;

    [ObservableProperty]
    private bool _clarityBoost;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowVocalStereoWarning))]
    private bool _includeVocalRemover;

    [ObservableProperty]
    private bool _includeNoiseReduction;

    [ObservableProperty]
    private bool _includeSilenceRemover;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEnhanceWorkspace))]
    [NotifyPropertyChangedFor(nameof(IsVocalWorkspace))]
    [NotifyPropertyChangedFor(nameof(IsNoiseWorkspace))]
    [NotifyPropertyChangedFor(nameof(IsSilenceWorkspace))]
    [NotifyPropertyChangedFor(nameof(ShowVocalStereoWarning))]
    private int _workspaceTabIndex;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowVocalStereoWarning))]
    private int _sourceAudioChannels;

    [ObservableProperty]
    private double _vocalRemoverStrength = 0.65;

    [ObservableProperty]
    private double _noiseReductionStrength = 0.5;

    [ObservableProperty]
    private double _silenceThresholdDb = -40;

    [ObservableProperty]
    private double _minSilenceDurationSec = 0.5;

    [ObservableProperty]
    private double _silenceDetectionWindowSec = 0.02;

    public bool IsEnhanceWorkspace => WorkspaceTabIndex == 0;

    public bool IsVocalWorkspace => WorkspaceTabIndex == 1;

    public bool IsNoiseWorkspace => WorkspaceTabIndex == 2;

    public bool IsSilenceWorkspace => WorkspaceTabIndex == 3;

    public bool ShowVocalStereoWarning =>
        IncludeVocalRemover && SourceAudioChannels > 0 && SourceAudioChannels < 2;

    /// <summary>0 = original file, 1 = processed WAV snippet.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPreviewSourceMode))]
    [NotifyPropertyChangedFor(nameof(IsPreviewProcessedMode))]
    private int _previewListenModeIndex;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanPreviewPlaybackControls))]
    private Uri? _previewMediaUri;

    [ObservableProperty]
    private bool _previewMuted;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanPreviewPlaybackControls))]
    private bool _isPreviewBuilding;

    [ObservableProperty]
    private string _previewStatusText = string.Empty;

    public bool IsPreviewSourceMode => PreviewListenModeIndex == 0;

    public bool IsPreviewProcessedMode => PreviewListenModeIndex == 1;

    public bool CanPreviewPlaybackControls => PreviewMediaUri is not null && !IsPreviewBuilding;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProgressPercentDisplay))]
    [NotifyPropertyChangedFor(nameof(ShowProgressCard))]
    private double _progressPercent01;

    [ObservableProperty]
    private string _progressStatusText = string.Empty;

    [ObservableProperty]
    private string _progressDetailText = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowProgressCard))]
    [NotifyPropertyChangedFor(nameof(ShowCancelButton))]
    private bool _isRunning;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowProgressCard))]
    private bool _finishedAttempt;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowResultCard))]
    private bool _succeeded;

    [ObservableProperty]
    private string _resultMessage = string.Empty;

    [ObservableProperty]
    private bool _isDropHover;

    public bool ShowFileInfoCard => !string.IsNullOrWhiteSpace(SelectedFilePath);

    public bool ShowProgressCard => IsRunning || FinishedAttempt;

    public bool ShowResultCard => Succeeded;

    public bool ShowCancelButton => IsRunning;

    public bool ShowBitrateControls =>
        TargetFormat is AudioExportFormat.Mp3 or AudioExportFormat.M4aAac or AudioExportFormat.OggOpus;

    public int ProgressPercentDisplay => (int)Math.Round(ProgressPercent01 * 100, MidpointRounding.AwayFromZero);

    private bool CanStartProcess() =>
        !string.IsNullOrWhiteSpace(SelectedFilePath)
        && Directory.Exists(_preferences.SaveFolderPath)
        && !IsRunning
        && !(IncludeVocalRemover && SourceAudioChannels > 0 && SourceAudioChannels < 2);

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
        SchedulePreviewRebuildIfNeeded();
    }

    private void SchedulePreviewRebuildIfNeeded()
    {
        if (PreviewListenModeIndex != 1 || string.IsNullOrEmpty(SelectedFilePath) || IsRunning)
        {
            return;
        }

        _previewDebounceCts?.Cancel();
        _previewDebounceCts = new CancellationTokenSource();
        var token = _previewDebounceCts.Token;
        _ = DebouncedPreviewWorkAsync(token);
    }

    private async Task DebouncedPreviewWorkAsync(CancellationToken token)
    {
        try
        {
            await Task.Delay(700, token).ConfigureAwait(true);
            await RebuildProcessedPreviewCoreAsync(token).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task RebuildProcessedPreviewCoreAsync(CancellationToken externalToken)
    {
        if (PreviewListenModeIndex != 1 || string.IsNullOrEmpty(SelectedFilePath))
        {
            return;
        }

        if (IncludeVocalRemover && SourceAudioChannels < 2)
        {
            PreviewStatusText = "Stereo is required when vocal remover is included.";
            PreviewMediaUri = null;
            IsPreviewBuilding = false;
            return;
        }

        _previewBuildCts?.Cancel();
        _previewBuildCts?.Dispose();

        CancellationTokenSource buildCts = externalToken.CanBeCanceled
            ? CancellationTokenSource.CreateLinkedTokenSource(externalToken)
            : new CancellationTokenSource();
        _previewBuildCts = buildCts;
        var token = buildCts.Token;

        IsPreviewBuilding = true;
        PreviewStatusText = "Building preview…";

        try
        {
            var dir = Path.Combine(Path.GetTempPath(), "MediaTools");
            Directory.CreateDirectory(dir);
            var outPath = Path.Combine(dir, $"audio_preview_{Guid.NewGuid():N}.wav");

            var settings = BuildPreviewSettings();

            await _audioProcessingService
                .ProcessAsync(
                    SelectedFilePath,
                    outPath,
                    settings,
                    new Progress<AudioProgressReport>(_ => { }),
                    token,
                    TimeSpan.FromSeconds(45))
                .ConfigureAwait(true);

            if (token.IsCancellationRequested || PreviewListenModeIndex != 1)
            {
                TryDeleteFileIgnoreErrors(outPath);
                return;
            }

            TryDeleteFileIgnoreErrors(_tempPreviewPath);
            _tempPreviewPath = outPath;
            PreviewMediaUri = new Uri(outPath);
            PreviewStatusText = "Preview: first 45 s as WAV (export can use another format).";
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            PreviewStatusText = ex.Message;
            PreviewMediaUri = null;
        }
        finally
        {
            IsPreviewBuilding = false;
        }
    }

    private static void TryDeleteFileIgnoreErrors(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return;
        }

        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
        }
    }

    private AudioEnhanceSettings BuildPreviewSettings() => BuildSettings() with { TargetFormat = AudioExportFormat.Wav };

    private void ResetPreviewForClear()
    {
        _previewDebounceCts?.Cancel();
        _previewBuildCts?.Cancel();
        _previewBuildCts?.Dispose();
        _previewBuildCts = null;
        TryDeleteFileIgnoreErrors(_tempPreviewPath);
        _tempPreviewPath = null;
        PreviewMediaUri = null;
        PreviewListenModeIndex = 0;
        PreviewStatusText = string.Empty;
        IsPreviewBuilding = false;
    }

    private void SyncPreviewAfterSnapshotApply()
    {
        if (string.IsNullOrEmpty(SelectedFilePath))
        {
            ResetPreviewForClear();
            return;
        }

        if (PreviewListenModeIndex == 0)
        {
            PreviewMediaUri = new Uri(Path.GetFullPath(SelectedFilePath));
            PreviewStatusText = "Playing original file.";
            return;
        }

        _ = RebuildProcessedPreviewCoreAsync(CancellationToken.None);
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
            Filter =
                "Audio|*.mp3;*.wav;*.flac;*.aac;*.m4a;*.ogg;*.opus;*.wma;*.aiff;*.aif;*.caf;*.mp4;*.mka|All files|*.*"
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
            loaded = await LoadFileAsync(dlg.FileName).ConfigureAwait(true);
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
    private void ClearFile()
    {
        _history.PushUndoFrameAnd(() =>
        {
            SelectedFilePath = null;
            FileDisplayName = string.Empty;
            FileSizeDisplay = string.Empty;
            DurationDisplay = string.Empty;
            CodecDisplay = string.Empty;
            SampleRateDisplay = string.Empty;
            ChannelsDisplay = string.Empty;
            FinishedAttempt = false;
            Succeeded = false;
            ProgressPercent01 = 0;
            ProgressStatusText = string.Empty;
            ProgressDetailText = string.Empty;
            ResultMessage = string.Empty;
            SourceAudioChannels = 0;
            IncludeVocalRemover = false;
            IncludeNoiseReduction = false;
            IncludeSilenceRemover = false;
            ResetPreviewForClear();
        });
    }

    [RelayCommand]
    private void SelectPreviewListenMode(object? parameter)
    {
        var idx = parameter switch
        {
            int i => i,
            string s when int.TryParse(s, System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out var j) => j,
            _ => 0
        };

        PreviewListenModeIndex = Math.Clamp(idx, 0, 1);
    }

    [RelayCommand]
    private void SelectWorkspaceTab(object? parameter)
    {
        var idx = parameter switch
        {
            int i => i,
            string s when int.TryParse(s, System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out var j) => j,
            _ => 0
        };

        WorkspaceTabIndex = Math.Clamp(idx, 0, 3);
    }

    [RelayCommand(CanExecute = nameof(CanUndoOperation))]
    private void Undo() => _history.TryUndo();

    [RelayCommand(CanExecute = nameof(CanRedoOperation))]
    private void Redo() => _history.TryRedo();

    [RelayCommand(CanExecute = nameof(CanStartProcess))]
    private async Task ProcessAudioAsync()
    {
        if (SelectedFilePath is null || !CanStartProcess())
        {
            return;
        }

        _cts = new CancellationTokenSource();
        var token = _cts.Token;

        IsRunning = true;
        FinishedAttempt = false;
        Succeeded = false;
        ProgressPercent01 = 0;
        ProgressDetailText = string.Empty;
        ResultMessage = string.Empty;

        var settings = BuildSettings();
        ProgressStatusText = "Processing audio…";

        var ext = ExtensionFor(settings.TargetFormat);
        var stem = Path.GetFileNameWithoutExtension(SelectedFilePath);
        var suffix = HasAnyAudioProcessing(settings) ? "_processed" : "_converted";
        var outputPath = Path.Combine(_preferences.SaveFolderPath, stem + suffix + ext);

        var request = new ProcessAudioRequest(SelectedFilePath, outputPath, settings);

        var dispatcher = global::System.Windows.Application.Current?.Dispatcher;

        var progress = new Progress<AudioProgressReport>(r =>
        {
            void Apply()
            {
                ProgressPercent01 = r.Percent01;
                ProgressDetailText = r.StepDescription;
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

        string? toastTitle = null;
        string? toastBody = null;
        var toastSuccess = false;

        try
        {
            var result = await _processAudioUseCase.ExecuteAsync(request, progress, token).ConfigureAwait(true);

            if (result.IsCancelled)
            {
                ProgressStatusText = "Cancelled";
                Succeeded = false;
            }
            else if (result.IsSuccess && File.Exists(outputPath))
            {
                Succeeded = true;
                ProgressStatusText = "Complete";
                ProgressPercent01 = 1;
                var len = new FileInfo(outputPath).Length;
                ResultMessage = $"Saved to {outputPath} ({FormatBytes(len)})";
                toastTitle = "Audio export complete";
                toastBody = $"{Path.GetFileName(outputPath)} · {FormatBytes(len)}";
                toastSuccess = true;
            }
            else if (result.IsSuccess)
            {
                Succeeded = false;
                ProgressStatusText = "Failed";
                ProgressDetailText = "Output file was not created.";
                ResultMessage = ProgressDetailText;
                toastTitle = "Audio processing failed";
                toastBody = ResultMessage;
                toastSuccess = false;
            }
            else
            {
                Succeeded = false;
                ProgressStatusText = "Failed";
                ProgressDetailText = result.ErrorMessage ?? "Unknown error";
                ResultMessage = result.ErrorMessage ?? "Processing failed.";
                toastTitle = "Audio processing failed";
                toastBody = ResultMessage;
                toastSuccess = false;
            }
        }
        finally
        {
            IsRunning = false;
            FinishedAttempt = true;
            ProcessAudioCommand.NotifyCanExecuteChanged();
            UndoCommand.NotifyCanExecuteChanged();
            RedoCommand.NotifyCanExecuteChanged();
            _history.FlushPendingEdit();

            if (toastTitle is not null)
            {
                _toastNotifications.ShowToolFinished(
                    toastTitle,
                    toastBody ?? string.Empty,
                    toastSuccess,
                    "Audio Converter");
            }
        }
    }

    [RelayCommand]
    private void Cancel() => _cts?.Cancel();

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

    private async Task LoadFromDropWithUndoAsync(string path)
    {
        _history.BeginUndoGroup();
        _suppressUndoNotification = true;
        bool loaded;
        try
        {
            loaded = await LoadFileAsync(path).ConfigureAwait(true);
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

    private AudioEnhanceSettings BuildSettings() =>
        new(
            TargetFormat,
            BitrateKbps,
            SampleRate,
            NormalizeLoudness,
            VolumePercent,
            ClarityBoost,
            IncludeVocalRemover,
            IncludeNoiseReduction,
            IncludeSilenceRemover,
            (float)VocalRemoverStrength,
            (float)NoiseReductionStrength,
            (float)SilenceThresholdDb,
            (float)MinSilenceDurationSec,
            (float)SilenceDetectionWindowSec);

    private static bool HasAnyAudioProcessing(AudioEnhanceSettings s) =>
        s.IncludeVocalRemover
        || s.IncludeNoiseReduction
        || s.IncludeSilenceRemover
        || s.ClarityBoost
        || s.VolumePercent != 100
        || s.NormalizeLoudness;

    private static string ExtensionFor(AudioExportFormat format) =>
        format switch
        {
            AudioExportFormat.Mp3 => ".mp3",
            AudioExportFormat.M4aAac => ".m4a",
            AudioExportFormat.Flac => ".flac",
            AudioExportFormat.OggOpus => ".opus",
            AudioExportFormat.Wav => ".wav",
            _ => ".mp3"
        };

    private async Task<bool> LoadFileAsync(string path)
    {
        try
        {
            var info = await _audioProcessingService.AnalyzeAsync(path).ConfigureAwait(true);
            SelectedFilePath = path;
            FileDisplayName = info.FileName;
            FileSizeDisplay = FormatBytes(info.FileSizeBytes);
            DurationDisplay = FormatDuration(info.Duration);
            CodecDisplay = info.Codec;
            SampleRateDisplay = $"{info.SampleRateHz} Hz";
            ChannelsDisplay = FormatChannels(info.Channels);
            SourceAudioChannels = info.Channels;
            FinishedAttempt = false;
            Succeeded = false;
            PreviewListenModeIndex = 0;
            PreviewMediaUri = new Uri(Path.GetFullPath(path));
            PreviewStatusText = "Playing original file.";
            return true;
        }
        catch (Exception ex)
        {
            MessageBoxHelper.ShowWarning($"Could not read audio: {ex.Message}");
            return false;
        }
    }

    private AudioEnhancerUndoSnapshot CaptureSnapshot() =>
        new(
            SelectedFilePath,
            FileDisplayName,
            FileSizeDisplay,
            DurationDisplay,
            CodecDisplay,
            SampleRateDisplay,
            ChannelsDisplay,
            SourceAudioChannels,
            WorkspaceTabIndex,
            IncludeVocalRemover,
            IncludeNoiseReduction,
            IncludeSilenceRemover,
            VocalRemoverStrength,
            NoiseReductionStrength,
            SilenceThresholdDb,
            MinSilenceDurationSec,
            SilenceDetectionWindowSec,
            TargetFormat,
            BitrateKbps,
            SampleRate,
            NormalizeLoudness,
            VolumePercent,
            ClarityBoost,
            ProgressPercent01,
            ProgressStatusText,
            ProgressDetailText,
            FinishedAttempt,
            Succeeded,
            ResultMessage);

    private void ApplySnapshot(AudioEnhancerUndoSnapshot s)
    {
        SelectedFilePath = s.SelectedFilePath;
        FileDisplayName = s.FileDisplayName;
        FileSizeDisplay = s.FileSizeDisplay;
        DurationDisplay = s.DurationDisplay;
        CodecDisplay = s.CodecDisplay;
        SampleRateDisplay = s.SampleRateDisplay;
        ChannelsDisplay = s.ChannelsDisplay;
        SourceAudioChannels = s.SourceAudioChannels;
        WorkspaceTabIndex = s.WorkspaceTabIndex;
        IncludeVocalRemover = s.IncludeVocalRemover;
        IncludeNoiseReduction = s.IncludeNoiseReduction;
        IncludeSilenceRemover = s.IncludeSilenceRemover;
        VocalRemoverStrength = s.VocalRemoverStrength;
        NoiseReductionStrength = s.NoiseReductionStrength;
        SilenceThresholdDb = s.SilenceThresholdDb;
        MinSilenceDurationSec = s.MinSilenceDurationSec;
        SilenceDetectionWindowSec = s.SilenceDetectionWindowSec;
        TargetFormat = s.TargetFormat;
        BitrateKbps = s.BitrateKbps;
        SampleRate = s.SampleRate;
        NormalizeLoudness = s.NormalizeLoudness;
        VolumePercent = s.VolumePercent;
        ClarityBoost = s.ClarityBoost;
        ProgressPercent01 = s.ProgressPercent01;
        ProgressStatusText = s.ProgressStatusText;
        ProgressDetailText = s.ProgressDetailText;
        FinishedAttempt = s.FinishedAttempt;
        Succeeded = s.Succeeded;
        ResultMessage = s.ResultMessage;
        OnPropertyChanged(nameof(ShowBitrateControls));
        OnPropertyChanged(nameof(IsEnhanceWorkspace));
        OnPropertyChanged(nameof(IsVocalWorkspace));
        OnPropertyChanged(nameof(IsNoiseWorkspace));
        OnPropertyChanged(nameof(IsSilenceWorkspace));
        OnPropertyChanged(nameof(ShowVocalStereoWarning));
        SyncPreviewAfterSnapshotApply();
    }

    partial void OnTargetFormatChanged(AudioExportFormat value)
    {
        OnPropertyChanged(nameof(ShowBitrateControls));
        NotifyUndoableEdit();
    }

    partial void OnBitrateKbpsChanged(int value) => NotifyUndoableEdit();

    partial void OnSampleRateChanged(AudioSampleRateOption value) => NotifyUndoableEdit();

    partial void OnNormalizeLoudnessChanged(bool value) => NotifyUndoableEdit();

    partial void OnVolumePercentChanged(int value) => NotifyUndoableEdit();

    partial void OnClarityBoostChanged(bool value) => NotifyUndoableEdit();

    partial void OnIncludeVocalRemoverChanged(bool value) => NotifyUndoableEdit();

    partial void OnIncludeNoiseReductionChanged(bool value) => NotifyUndoableEdit();

    partial void OnIncludeSilenceRemoverChanged(bool value) => NotifyUndoableEdit();

    partial void OnWorkspaceTabIndexChanged(int value)
    {
        ProcessAudioCommand.NotifyCanExecuteChanged();
        NotifyUndoableEdit();
    }

    partial void OnPreviewListenModeIndexChanged(int value)
    {
        _previewDebounceCts?.Cancel();
        if (string.IsNullOrEmpty(SelectedFilePath))
        {
            return;
        }

        if (value == 0)
        {
            _previewBuildCts?.Cancel();
            _previewBuildCts?.Dispose();
            _previewBuildCts = null;
            PreviewMediaUri = new Uri(Path.GetFullPath(SelectedFilePath));
            PreviewStatusText = "Playing original file.";
            IsPreviewBuilding = false;
            return;
        }

        _ = RebuildProcessedPreviewCoreAsync(CancellationToken.None);
    }

    partial void OnSourceAudioChannelsChanged(int value) =>
        ProcessAudioCommand.NotifyCanExecuteChanged();

    partial void OnVocalRemoverStrengthChanged(double value) => NotifyUndoableEdit();

    partial void OnNoiseReductionStrengthChanged(double value) => NotifyUndoableEdit();

    partial void OnSilenceThresholdDbChanged(double value) => NotifyUndoableEdit();

    partial void OnMinSilenceDurationSecChanged(double value) => NotifyUndoableEdit();

    partial void OnSilenceDetectionWindowSecChanged(double value) => NotifyUndoableEdit();

    partial void OnSelectedFilePathChanged(string? value) =>
        ProcessAudioCommand.NotifyCanExecuteChanged();

    partial void OnIsRunningChanged(bool value)
    {
        ProcessAudioCommand.NotifyCanExecuteChanged();
        UndoCommand.NotifyCanExecuteChanged();
        RedoCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Called from the page when WPF MediaElement cannot open or decode the preview URI.</summary>
    public void ReportPreviewPlaybackFailed(string? detail)
    {
        PreviewStatusText =
            string.IsNullOrWhiteSpace(detail)
                ? "Preview playback failed. Try “With current settings” for a WAV snippet, or convert to MP3/WAV."
                : $"Preview playback failed: {detail} — Opus/FLAC may not play here; use processed preview (WAV) or another format.";
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

    private static string FormatDuration(TimeSpan ts) =>
        ts.TotalHours >= 1 ? $"{(int)ts.TotalHours}:{ts.Minutes:D2}:{ts.Seconds:D2}" : $"{ts.Minutes:D2}:{ts.Seconds:D2}";

    private static string FormatChannels(int ch) =>
        ch switch
        {
            1 => "Mono",
            2 => "Stereo",
            _ => $"{ch} channels"
        };
}
