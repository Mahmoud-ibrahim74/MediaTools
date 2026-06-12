using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MediaTools.Application.Abstractions;
using MediaTools.Application.DTOs;
using MediaTools.Application.UseCases;
using MediaTools.Domain.Enums;
using MediaTools.Domain.ValueObjects;
using MediaTools.Presentation.Helpers;
using MediaTools.Presentation.Services;
using MediaTools.Presentation.Views;

namespace MediaTools.Presentation.ViewModels;

public partial class ScreenRecorderViewModel : ObservableObject
{
    private readonly StartScreenRecordingUseCase _startScreenRecordingUseCase;
    private readonly IScreenRecordingService _screenRecordingService;
    private readonly IUserPreferencesService _preferences;
    private readonly IWindowsToastNotificationService _toastNotifications;

    private CancellationTokenSource? _stopCts;
    private CancellationTokenSource? _hardCancelCts;
    private DispatcherTimer? _elapsedTimer;
    private DateTime _recordingStartedAt;
    private IPausableRecordingControl? _pauseControl;
    private TimeSpan _pausedBeforeCurrentSegment;
    private DateTime? _pauseSegmentStarted;
    private readonly SemaphoreSlim _microphoneLoadGate = new(1, 1);

    /// <summary>
    /// True only after the user finishes <see cref="ApplyPickedRegion"/> (draw overlay). Selecting Custom in the UI alone does not count.
    /// </summary>
    private bool _customRegionConfirmed;

    public ScreenRecorderViewModel(
        StartScreenRecordingUseCase startScreenRecordingUseCase,
        IScreenRecordingService screenRecordingService,
        IUserPreferencesService preferences,
        IWindowsToastNotificationService toastNotifications)
    {
        _startScreenRecordingUseCase = startScreenRecordingUseCase;
        _screenRecordingService = screenRecordingService;
        _preferences = preferences;
        _toastNotifications = toastNotifications;
        _preferences.SaveFolderPathChanged += OnSaveFolderPathChanged;
        _preferences.VideoEncoderSettingsChanged += OnVideoEncoderSettingsChanged;

        ApplyPrimaryMonitorBounds();
    }

    private void OnSaveFolderPathChanged(object? sender, EventArgs e) =>
        StartRecordingCommand.NotifyCanExecuteChanged();

    private void OnVideoEncoderSettingsChanged(object? sender, EventArgs e) =>
        OnPropertyChanged(nameof(ExportVideoEncoderDisplay));

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
            _ => "Software (libx264) — CPU"
        };

    /// <summary>Effective H.264 encoder for this recording (from App settings + last encoder scan).</summary>
    public string ExportVideoEncoderDisplay => FormatEncoderForUi(ResolveEncoderForExport());

    public IEnumerable<ScreenRecordingRegion> Regions => Enum.GetValues<ScreenRecordingRegion>();

    public IEnumerable<ScreenRecordingOutputFormat> OutputFormats => Enum.GetValues<ScreenRecordingOutputFormat>();

    public IEnumerable<int> FrameRates => [15, 24, 30, 60, 90, 120];

    public IEnumerable<int> StartDelays => [0, 3, 5, 10];

    public ObservableCollection<AudioInputDeviceDto> MicrophoneDevices { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowCustomRegionEditor))]
    [NotifyPropertyChangedFor(nameof(ShowPrimaryMonitorHint))]
    private ScreenRecordingRegion _region = ScreenRecordingRegion.PrimaryMonitor;

    [ObservableProperty]
    private int _offsetX;

    [ObservableProperty]
    private int _offsetY;

    [ObservableProperty]
    private int _captureWidth = 1920;

    [ObservableProperty]
    private int _captureHeight = 1080;

    [ObservableProperty]
    private int _frameRate = 30;

    [ObservableProperty]
    private int _crf = 23;

    [ObservableProperty]
    private bool _captureCursor = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowMicrophonePicker))]
    [NotifyPropertyChangedFor(nameof(ShowMicMutedWarning))]
    private bool _includeMicrophone;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowMicMutedWarning))]
    private bool _isMicrophoneMuted;

    public bool ShowMicMutedWarning => IncludeMicrophone && IsMicrophoneMuted;

    [ObservableProperty]
    private AudioInputDeviceDto? _selectedMicrophone;

    [ObservableProperty]
    private ScreenRecordingOutputFormat _outputFormat = ScreenRecordingOutputFormat.Mp4;

    [ObservableProperty]
    private int _startDelaySeconds;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowCountdown))]
    private int _countdownSeconds;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowRecordingPanel))]
    [NotifyPropertyChangedFor(nameof(ShowStartButton))]
    [NotifyPropertyChangedFor(nameof(ShowStopButton))]
    [NotifyPropertyChangedFor(nameof(ShowPauseRecordingButton))]
    [NotifyPropertyChangedFor(nameof(ShowResumeRecordingButton))]
    private bool _isRecording;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowPauseRecordingButton))]
    [NotifyPropertyChangedFor(nameof(ShowResumeRecordingButton))]
    private bool _isPaused;

    [ObservableProperty]
    private string _elapsedDisplay = "00:00";

    [ObservableProperty]
    private string _statusText = "Idle";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowResultCard))]
    private bool _succeeded;

    [ObservableProperty]
    private string _resultMessage = string.Empty;

    [ObservableProperty]
    private string? _lastOutputFilePath;

    public bool ShowCustomRegionEditor => Region == ScreenRecordingRegion.Custom;

    public bool ShowPrimaryMonitorHint => Region == ScreenRecordingRegion.PrimaryMonitor;

    public bool ShowMicrophonePicker => IncludeMicrophone;

    public bool ShowRecordingPanel => IsRecording || CountdownSeconds > 0;

    public bool ShowCountdown => CountdownSeconds > 0;

    public bool ShowStartButton => !IsRecording && CountdownSeconds == 0;

    public bool ShowStopButton => IsRecording;

    /// <summary>Pause while FFmpeg is running (see <see cref="PauseRecordingCommand"/>).</summary>
    public bool ShowPauseRecordingButton => IsRecording && !IsPaused;

    /// <summary>Resume after pause.</summary>
    public bool ShowResumeRecordingButton => IsRecording && IsPaused;

    public bool ShowResultCard => Succeeded && !IsRecording;

    /// <summary>Apply a screen-space rectangle from the drag overlay and switch to <see cref="ScreenRecordingRegion.Custom"/>.</summary>
    public void ApplyPickedRegion(int offsetX, int offsetY, int width, int height)
    {
        var w = Math.Max(16, width);
        var h = Math.Max(16, height);
        w -= w % 2;
        h -= h % 2;
        if (w < 16)
        {
            w = 16;
        }

        if (h < 16)
        {
            h = 16;
        }

        Region = ScreenRecordingRegion.Custom;
        OffsetX = offsetX;
        OffsetY = offsetY;
        CaptureWidth = w;
        CaptureHeight = h;
        _customRegionConfirmed = true;
        StartRecordingCommand.NotifyCanExecuteChanged();
    }

    private bool CanStartRecording() =>
        !IsRecording
        && CountdownSeconds == 0
        && Directory.Exists(_preferences.SaveFolderPath)
        && (Region != ScreenRecordingRegion.Custom || _customRegionConfirmed);

    private bool CanStopRecording() => IsRecording;

    private bool CanPauseRecording() =>
        IsRecording && !IsPaused && _pauseControl is not null;

    private bool CanResumeRecording() =>
        IsRecording && IsPaused && _pauseControl is not null;

    /// <summary>
    /// Never cache a failed/empty enumeration: the old <c>_devicesLoaded</c> flag blocked all later loads after one
    /// empty run (e.g. FFmpeg still downloading), so toggling "Record microphone" appeared broken.
    /// </summary>
    [RelayCommand]
    private async Task LoadMicrophonesAsync()
    {
        await _microphoneLoadGate.WaitAsync().ConfigureAwait(true);
        try
        {
            var devices = await _screenRecordingService.GetAudioInputDevicesAsync().ConfigureAwait(true);
            MicrophoneDevices.Clear();
            foreach (var d in devices)
            {
                MicrophoneDevices.Add(d);
            }

            SelectedMicrophone = MicrophoneDevices.FirstOrDefault();
        }
        catch (Exception)
        {
            MicrophoneDevices.Clear();
            SelectedMicrophone = null;
        }
        finally
        {
            _microphoneLoadGate.Release();
        }
    }

    [RelayCommand(CanExecute = nameof(CanStartRecording))]
    private async Task StartRecordingAsync()
    {
        if (!CanStartRecording())
        {
            return;
        }

        if (Region == ScreenRecordingRegion.Custom
            && (CaptureWidth < 16 || CaptureHeight < 16))
        {
            MessageBoxHelper.ShowWarning("Custom region must be at least 16×16 pixels.");
            return;
        }

        if (IncludeMicrophone && SelectedMicrophone is null)
        {
            await LoadMicrophonesAsync().ConfigureAwait(true);
            if (SelectedMicrophone is null)
            {
                MessageBoxHelper.ShowWarning("No microphone device was found. Disable microphone or connect a device.");
                return;
            }
        }

        // Re-check mute state right before recording starts.
        if (IncludeMicrophone)
        {
            CheckMicrophoneMuteState();
            if (IsMicrophoneMuted)
            {
                var answer = MessageBoxHelper.Show(
                    "Your microphone is currently muted in Windows.\nThe recording will capture silence from the mic.\n\nDo you want to continue anyway?",
                    "Microphone Muted",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning);
                if (answer != MessageBoxResult.Yes)
                {
                    return;
                }
            }
        }

        // Close the draw-region modal if it is still open (e.g. user pressed the global Start hotkey after selecting a rectangle).
        RegionSelectionOverlayWindow.CloseAllForRecordingStart();

        if (StartDelaySeconds > 0)
        {
            CountdownSeconds = StartDelaySeconds;
            try
            {
                while (CountdownSeconds > 0)
                {
                    await Task.Delay(1000).ConfigureAwait(true);
                    CountdownSeconds--;
                }
            }
            catch
            {
                CountdownSeconds = 0;
            }
        }

        await BeginRecordingAsync().ConfigureAwait(true);
    }

    private async Task BeginRecordingAsync()
    {
        Succeeded = false;
        ResultMessage = string.Empty;
        LastOutputFilePath = null;

        var settings = BuildSettings();
        var ext = OutputFormat == ScreenRecordingOutputFormat.Mkv ? ".mkv" : ".mp4";
        var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        var outputPath = Path.Combine(_preferences.SaveFolderPath, $"screen_recording_{stamp}{ext}");

        var request = new StartScreenRecordingRequest(outputPath, settings);

        _stopCts = new CancellationTokenSource();
        _hardCancelCts = new CancellationTokenSource();
        _pauseControl = null;
        IsPaused = false;
        _pausedBeforeCurrentSegment = TimeSpan.Zero;
        _pauseSegmentStarted = null;

        var dispatcher = global::System.Windows.Application.Current?.Dispatcher;

        var progress = new Progress<ScreenRecordingProgressReport>(r =>
        {
            void Apply()
            {
                if (r.Elapsed > TimeSpan.Zero)
                {
                    ElapsedDisplay = FormatDuration(r.Elapsed);
                }

                if (!string.IsNullOrEmpty(r.StepDescription))
                {
                    StatusText = r.StepDescription;
                }
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

        IsRecording = true;
        StatusText = "Recording…";
        ElapsedDisplay = "00:00";
        _recordingStartedAt = DateTime.Now;
        StartElapsedTimer();

        string? toastTitle = null;
        string? toastBody = null;
        var toastSuccess = false;

        try
        {
            var result = await _startScreenRecordingUseCase
                .ExecuteAsync(
                    request,
                    progress,
                    _stopCts.Token,
                    _hardCancelCts.Token,
                    onRecordingStarted: c =>
                    {
                        _pauseControl = c;
                        PauseRecordingCommand.NotifyCanExecuteChanged();
                        ResumeRecordingCommand.NotifyCanExecuteChanged();
                    })
                .ConfigureAwait(true);

            if (result.IsCancelled)
            {
                StatusText = "Cancelled";
                Succeeded = false;
            }
            else if (result.IsSuccess && result.OutputFilePath is not null)
            {
                Succeeded = true;
                _preferences.IncrementLifetimeStat(AppLifetimeStatKind.ScreenRecorded);
                StatusText = "Saved";
                LastOutputFilePath = result.OutputFilePath;
                var size = result.OutputFileSizeBytes ?? 0;
                ResultMessage = $"Saved to {result.OutputFilePath} ({FormatBytes(size)} · {FormatDuration(result.TotalDuration)})";
                toastTitle = "Screen recording saved";
                toastBody = $"{Path.GetFileName(result.OutputFilePath)} · {FormatBytes(size)}";
                toastSuccess = true;
            }
            else
            {
                Succeeded = false;
                StatusText = "Failed";
                ResultMessage = result.ErrorMessage ?? "Recording failed.";
                toastTitle = "Screen recording failed";
                toastBody = ResultMessage;
                toastSuccess = false;
            }
        }
        finally
        {
            StopElapsedTimer();
            IsRecording = false;
            IsPaused = false;
            _pauseControl = null;
            _pausedBeforeCurrentSegment = TimeSpan.Zero;
            _pauseSegmentStarted = null;
            StartRecordingCommand.NotifyCanExecuteChanged();
            StopRecordingCommand.NotifyCanExecuteChanged();
            PauseRecordingCommand.NotifyCanExecuteChanged();
            ResumeRecordingCommand.NotifyCanExecuteChanged();
            _stopCts?.Dispose();
            _stopCts = null;
            _hardCancelCts?.Dispose();
            _hardCancelCts = null;

            if (toastTitle is not null)
            {
                _toastNotifications.ShowToolFinished(
                    toastTitle,
                    toastBody ?? string.Empty,
                    toastSuccess,
                    "Screen Recorder");
            }
        }
    }

    [RelayCommand(CanExecute = nameof(CanPauseRecording))]
    private void PauseRecording()
    {
        _pauseControl?.Pause();
        if (_pauseControl is { IsPaused: true })
        {
            _pauseSegmentStarted = DateTime.Now;
            IsPaused = true;
        }
    }

    [RelayCommand(CanExecute = nameof(CanResumeRecording))]
    private void ResumeRecording()
    {
        if (_pauseSegmentStarted.HasValue)
        {
            _pausedBeforeCurrentSegment += DateTime.Now - _pauseSegmentStarted.Value;
            _pauseSegmentStarted = null;
        }

        _pauseControl?.Resume();
        IsPaused = false;
    }

    /// <summary>Global shortcut from App settings — starts recording when idle (same rules as the Start button).</summary>
    public void HandleGlobalHotkeyStartRecording()
    {
        global::System.Windows.Application.Current?.Dispatcher.Invoke(() =>
        {
            if (Region == ScreenRecordingRegion.Custom && !_customRegionConfirmed)
            {
                MessageBoxHelper.ShowWarning(
                    "Custom region is not set. Use \"Draw region on screen…\", drag to select an area, then start recording or press the hotkey again.");
                return;
            }

            if (!StartRecordingCommand.CanExecute(null))
            {
                return;
            }

            StartRecordingCommand.Execute(null);
        });
    }

    /// <summary>Global shortcut — toggles pause/resume while recording.</summary>
    public void HandleGlobalHotkeyPauseToggle()
    {
        global::System.Windows.Application.Current?.Dispatcher.Invoke(() =>
        {
            if (!IsRecording)
            {
                return;
            }

            if (IsPaused)
            {
                if (ResumeRecordingCommand.CanExecute(null))
                {
                    ResumeRecordingCommand.Execute(null);
                }
            }
            else if (PauseRecordingCommand.CanExecute(null))
            {
                PauseRecordingCommand.Execute(null);
            }
        });
    }

    [RelayCommand(CanExecute = nameof(CanStopRecording))]
    private void StopRecording()
    {
        StatusText = "Finalizing…";
        try
        {
            _stopCts?.Cancel();
        }
        catch
        {
            // ignore
        }
    }

    [RelayCommand]
    private void CancelRecording()
    {
        StatusText = "Cancelling…";
        try
        {
            _hardCancelCts?.Cancel();
        }
        catch
        {
            // ignore
        }
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

    [RelayCommand]
    private async Task RefreshDevicesAsync()
    {
        await LoadMicrophonesAsync().ConfigureAwait(true);
        CheckMicrophoneMuteState();
    }

    private ScreenRecordingSettings BuildSettings()
    {
        int offX = OffsetX;
        int offY = OffsetY;
        int width = CaptureWidth;
        int height = CaptureHeight;

        if (Region == ScreenRecordingRegion.PrimaryMonitor)
        {
            offX = 0;
            offY = 0;
            width = (int)Math.Round(SystemParameters.PrimaryScreenWidth);
            height = (int)Math.Round(SystemParameters.PrimaryScreenHeight);
        }
        else if (Region == ScreenRecordingRegion.FullDesktop)
        {
            offX = (int)Math.Round(SystemParameters.VirtualScreenLeft);
            offY = (int)Math.Round(SystemParameters.VirtualScreenTop);
            width = (int)Math.Round(SystemParameters.VirtualScreenWidth);
            height = (int)Math.Round(SystemParameters.VirtualScreenHeight);
        }

        return new ScreenRecordingSettings(
            Region: Region,
            OffsetX: offX,
            OffsetY: offY,
            CaptureWidth: Math.Max(16, width),
            CaptureHeight: Math.Max(16, height),
            FrameRate: FrameRate,
            Crf: Crf,
            CaptureCursor: CaptureCursor,
            IncludeMicrophone: IncludeMicrophone,
            MicrophoneDeviceName: IncludeMicrophone ? SelectedMicrophone?.Name : null,
            OutputFormat: OutputFormat,
            VideoEncoder: ResolveEncoderForExport());
    }

    private void ApplyPrimaryMonitorBounds()
    {
        try
        {
            CaptureWidth = (int)Math.Round(SystemParameters.PrimaryScreenWidth);
            CaptureHeight = (int)Math.Round(SystemParameters.PrimaryScreenHeight);
        }
        catch
        {
            // keep defaults
        }
    }

    private void StartElapsedTimer()
    {
        StopElapsedTimer();
        _elapsedTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _elapsedTimer.Tick += OnElapsedTick;
        _elapsedTimer.Start();
    }

    private void StopElapsedTimer()
    {
        if (_elapsedTimer is null)
        {
            return;
        }

        _elapsedTimer.Stop();
        _elapsedTimer.Tick -= OnElapsedTick;
        _elapsedTimer = null;
    }

    private void OnElapsedTick(object? sender, EventArgs e)
    {
        var now = DateTime.Now;
        var wall = now - _recordingStartedAt;
        var currentPause =
            IsPaused && _pauseSegmentStarted.HasValue ? now - _pauseSegmentStarted.Value : TimeSpan.Zero;
        var active = wall - _pausedBeforeCurrentSegment - currentPause;
        if (active < TimeSpan.Zero)
        {
            active = TimeSpan.Zero;
        }

        ElapsedDisplay = FormatDuration(active);
    }

    partial void OnIsRecordingChanged(bool value)
    {
        StartRecordingCommand.NotifyCanExecuteChanged();
        StopRecordingCommand.NotifyCanExecuteChanged();
        PauseRecordingCommand.NotifyCanExecuteChanged();
        ResumeRecordingCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsPausedChanged(bool value)
    {
        PauseRecordingCommand.NotifyCanExecuteChanged();
        ResumeRecordingCommand.NotifyCanExecuteChanged();
        if (IsRecording)
        {
            StatusText = value ? "Paused" : "Recording…";
        }
    }

    partial void OnRegionChanged(ScreenRecordingRegion value)
    {
        if (value != ScreenRecordingRegion.Custom)
        {
            _customRegionConfirmed = false;
        }

        StartRecordingCommand.NotifyCanExecuteChanged();

        if (value == ScreenRecordingRegion.PrimaryMonitor)
        {
            ApplyPrimaryMonitorBounds();
            OffsetX = 0;
            OffsetY = 0;
        }
    }

    partial void OnIncludeMicrophoneChanged(bool value)
    {
        if (value)
        {
            _ = LoadMicrophonesAsync();
            CheckMicrophoneMuteState();
        }
        else
        {
            IsMicrophoneMuted = false;
        }
    }

    partial void OnSelectedMicrophoneChanged(AudioInputDeviceDto? value)
    {
        if (IncludeMicrophone)
        {
            CheckMicrophoneMuteState();
        }
    }

    /// <summary>
    /// Queries the Windows Core Audio API to determine whether the default
    /// capture device is muted, and updates <see cref="IsMicrophoneMuted"/>.
    /// </summary>
    private void CheckMicrophoneMuteState()
    {
        try
        {
            IsMicrophoneMuted = MicrophoneMuteHelper.IsDefaultMicrophoneMuted();
        }
        catch
        {
            IsMicrophoneMuted = false;
        }
    }

    private static string FormatDuration(TimeSpan ts) =>
        ts.TotalHours >= 1
            ? $"{(int)ts.TotalHours:D2}:{ts.Minutes:D2}:{ts.Seconds:D2}"
            : $"{ts.Minutes:D2}:{ts.Seconds:D2}";

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
}
