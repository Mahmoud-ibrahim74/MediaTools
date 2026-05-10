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
            OnUndoRedoHistoryChanged);
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
        });
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
        ProgressStatusText = "Converting…";
        ProgressDetailText = string.Empty;
        ResultMessage = string.Empty;

        var settings = BuildSettings();
        var ext = ExtensionFor(settings.TargetFormat);
        var outputPath = Path.Combine(
            _preferences.SaveFolderPath,
            Path.GetFileNameWithoutExtension(SelectedFilePath) + "_converted" + ext);

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
                toastTitle = "Audio conversion complete";
                toastBody = $"{Path.GetFileName(outputPath)} · {FormatBytes(len)}";
                toastSuccess = true;
            }
            else if (result.IsSuccess)
            {
                Succeeded = false;
                ProgressStatusText = "Failed";
                ProgressDetailText = "Output file was not created.";
                ResultMessage = ProgressDetailText;
                toastTitle = "Audio conversion failed";
                toastBody = ResultMessage;
                toastSuccess = false;
            }
            else
            {
                Succeeded = false;
                ProgressStatusText = "Failed";
                ProgressDetailText = result.ErrorMessage ?? "Unknown error";
                ResultMessage = result.ErrorMessage ?? "Processing failed.";
                toastTitle = "Audio conversion failed";
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
            ClarityBoost);

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
            FinishedAttempt = false;
            Succeeded = false;
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

    partial void OnSelectedFilePathChanged(string? value) =>
        ProcessAudioCommand.NotifyCanExecuteChanged();

    partial void OnIsRunningChanged(bool value)
    {
        ProcessAudioCommand.NotifyCanExecuteChanged();
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
