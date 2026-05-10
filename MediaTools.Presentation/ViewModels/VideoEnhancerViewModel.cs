using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
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
using MediaTools.Presentation.Undo;

namespace MediaTools.Presentation.ViewModels;

public partial class VideoEnhancerViewModel : ObservableObject
{
    private static readonly HashSet<string> AllowedExtensions =
    [
        ".mp4", ".mkv", ".mov", ".avi", ".wmv", ".flv", ".webm", ".m4v"
    ];

    private static readonly HashSet<string> AllowedWatermarkExtensions =
    [
        ".png", ".jpg", ".jpeg", ".webp", ".bmp", ".gif"
    ];

    private readonly ProcessVideoEnhanceUseCase _processVideoEnhanceUseCase;
    private readonly IVideoEnhanceService _videoEnhanceService;
    private readonly IUserPreferencesService _preferences;
    private readonly IWindowsToastNotificationService _toastNotifications;
    private CancellationTokenSource? _cts;
    private readonly DispatcherTimer _effectPreviewDebounce;
    private CancellationTokenSource? _effectPreviewRunCts;
    private readonly UndoRedoHost<VideoEnhancerUndoSnapshot> _history;
    private bool _suppressUndoNotification;

    private static readonly HashSet<string> UndoablePropertyNames = new(StringComparer.Ordinal)
    {
        nameof(Operation),
        nameof(WatermarkKind),
        nameof(WatermarkImagePath),
        nameof(WatermarkText),
        nameof(WatermarkPosition),
        nameof(WatermarkOpacityPercent),
        nameof(WatermarkSizePercent),
        nameof(SpeedFactor),
        nameof(SpeedPreservePitch),
        nameof(StabilizerSmoothing),
        nameof(StabilizerZoom),
        nameof(ColorBrightness),
        nameof(ColorContrast),
        nameof(ColorSaturation),
        nameof(ColorGamma),
        nameof(ColorHue),
        nameof(CropEnabled),
        nameof(CropX),
        nameof(CropY),
        nameof(CropWidth),
        nameof(CropHeight),
        nameof(ResizeEnabled),
        nameof(ResizeWidth),
        nameof(ResizeHeight),
        nameof(AudioFormat),
        nameof(AudioBitrateKbps),
        nameof(PreviewMuted),
        nameof(SubExportFormat),
        nameof(SubSelectedTrack)
    };

    private static readonly HashSet<string> EffectPreviewPropertyNames = new(StringComparer.Ordinal)
    {
        nameof(Operation),
        nameof(SelectedFilePath),
        nameof(IsRunning),
        nameof(SubIsRunning),
        nameof(IsAnalyzing),
        nameof(ColorBrightness),
        nameof(ColorContrast),
        nameof(ColorSaturation),
        nameof(ColorGamma),
        nameof(ColorHue),
        nameof(CropEnabled),
        nameof(CropX),
        nameof(CropY),
        nameof(CropWidth),
        nameof(CropHeight),
        nameof(ResizeEnabled),
        nameof(ResizeWidth),
        nameof(ResizeHeight),
        nameof(WatermarkKind),
        nameof(WatermarkImagePath),
        nameof(WatermarkText),
        nameof(WatermarkPosition),
        nameof(WatermarkOpacityPercent),
        nameof(WatermarkSizePercent)
    };

    public VideoEnhancerViewModel(
        ProcessVideoEnhanceUseCase processVideoEnhanceUseCase,
        IVideoEnhanceService videoEnhanceService,
        ProcessSubtitleExtractUseCase processSubtitleExtractUseCase,
        ISubtitleExtractorService subtitleExtractorService,
        IUserPreferencesService preferences,
        IWindowsToastNotificationService toastNotifications)
    {
        _processVideoEnhanceUseCase = processVideoEnhanceUseCase;
        _videoEnhanceService = videoEnhanceService;
        _processSubtitleExtractUseCase = processSubtitleExtractUseCase;
        _subtitleExtractorService = subtitleExtractorService;
        _preferences = preferences;
        _toastNotifications = toastNotifications;
        _preferences.SaveFolderPathChanged += OnSaveFolderPathChanged;

        SubtitleTracks.CollectionChanged += OnSubtitleTracksCollectionChanged;

        _history = new UndoRedoHost<VideoEnhancerUndoSnapshot>(
            CaptureSnapshot,
            ApplySnapshot,
            CaptureSnapshot(),
            OnUndoRedoHistoryChanged);

        PropertyChanged += OnAnyPropertyChanged;

        _effectPreviewDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(450) };
        _effectPreviewDebounce.Tick += (_, _) =>
        {
            _effectPreviewDebounce.Stop();
            _ = RunEffectPreviewOnceAsync();
        };
    }

    private void OnSaveFolderPathChanged(object? sender, EventArgs e)
    {
        StartCommand.NotifyCanExecuteChanged();
        ExtractSubtitleCommand.NotifyCanExecuteChanged();
    }

    public IEnumerable<VideoEnhanceOperation> Operations => Enum.GetValues<VideoEnhanceOperation>();

    public IEnumerable<WatermarkSourceKind> WatermarkSourceKinds => Enum.GetValues<WatermarkSourceKind>();

    public IEnumerable<WatermarkPosition> WatermarkPositions => Enum.GetValues<WatermarkPosition>();

    public IEnumerable<AudioExportFormat> AudioFormats => Enum.GetValues<AudioExportFormat>();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowFileInfoCard))]
    [NotifyPropertyChangedFor(nameof(PreviewMediaUri))]
    [NotifyPropertyChangedFor(nameof(ShowEffectStillPreviewSection))]
    [NotifyPropertyChangedFor(nameof(ShowNonStillPreviewHint))]
    private string? _selectedFilePath;

    [ObservableProperty]
    private string _fileDisplayName = string.Empty;

    [ObservableProperty]
    private string _fileSizeDisplay = string.Empty;

    [ObservableProperty]
    private string _durationDisplay = string.Empty;

    [ObservableProperty]
    private string _dimensionsDisplay = string.Empty;

    [ObservableProperty]
    private string _codecDisplay = string.Empty;

    [ObservableProperty]
    private bool _hasAudio;

    [ObservableProperty]
    private int _sourceWidth;

    [ObservableProperty]
    private int _sourceHeight;

    [ObservableProperty]
    private bool _isAnalyzing;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowWatermarkPanel))]
    [NotifyPropertyChangedFor(nameof(ShowSpeedPanel))]
    [NotifyPropertyChangedFor(nameof(ShowReversePanel))]
    [NotifyPropertyChangedFor(nameof(ShowStabilizePanel))]
    [NotifyPropertyChangedFor(nameof(ShowColorPanel))]
    [NotifyPropertyChangedFor(nameof(ShowCropResizePanel))]
    [NotifyPropertyChangedFor(nameof(ShowExtractAudioPanel))]
    [NotifyPropertyChangedFor(nameof(ShowEffectStillPreviewSection))]
    [NotifyPropertyChangedFor(nameof(ShowNonStillPreviewHint))]
    private VideoEnhanceOperation _operation = VideoEnhanceOperation.Watermark;

    // Watermark
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsWatermarkImage))]
    [NotifyPropertyChangedFor(nameof(IsWatermarkText))]
    private WatermarkSourceKind _watermarkKind = WatermarkSourceKind.Text;

    [ObservableProperty]
    private string? _watermarkImagePath;

    [ObservableProperty]
    private string _watermarkText = "MediaTools";

    [ObservableProperty]
    private WatermarkPosition _watermarkPosition = WatermarkPosition.BottomRight;

    [ObservableProperty]
    private int _watermarkOpacityPercent = 80;

    [ObservableProperty]
    private int _watermarkSizePercent = 20;

    public bool IsWatermarkImage => WatermarkKind == WatermarkSourceKind.Image;
    public bool IsWatermarkText => WatermarkKind == WatermarkSourceKind.Text;

    // Speed
    [ObservableProperty]
    private double _speedFactor = 2.0;

    [ObservableProperty]
    private bool _speedPreservePitch = true;

    // Stabilize
    [ObservableProperty]
    private int _stabilizerSmoothing = 10;

    [ObservableProperty]
    private double _stabilizerZoom = 0;

    // Color grading
    [ObservableProperty]
    private double _colorBrightness;

    [ObservableProperty]
    private double _colorContrast = 1;

    [ObservableProperty]
    private double _colorSaturation = 1;

    [ObservableProperty]
    private double _colorGamma = 1;

    [ObservableProperty]
    private double _colorHue;

    // Crop & resize
    [ObservableProperty]
    private bool _cropEnabled;

    [ObservableProperty]
    private int _cropX;

    [ObservableProperty]
    private int _cropY;

    [ObservableProperty]
    private int _cropWidth;

    [ObservableProperty]
    private int _cropHeight;

    [ObservableProperty]
    private bool _resizeEnabled;

    [ObservableProperty]
    private int _resizeWidth;

    [ObservableProperty]
    private int _resizeHeight;

    // Extract audio
    [ObservableProperty]
    private AudioExportFormat _audioFormat = AudioExportFormat.Mp3;

    [ObservableProperty]
    private int _audioBitrateKbps = 192;

    // Progress / state
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

    [ObservableProperty]
    private bool _previewMuted = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowEffectStillPreviewSection))]
    private ImageSource? _effectPreviewImage;

    [ObservableProperty]
    private bool _isEffectPreviewBusy;

    public bool ShowFileInfoCard => !string.IsNullOrWhiteSpace(SelectedFilePath);

    public Uri? PreviewMediaUri =>
        string.IsNullOrWhiteSpace(SelectedFilePath)
            ? null
            : new Uri(Path.GetFullPath(SelectedFilePath));

    public bool SupportsEffectStillPreview =>
        Operation is VideoEnhanceOperation.ColorGrading
            or VideoEnhanceOperation.CropAndResize
            or VideoEnhanceOperation.Watermark;

    public bool ShowEffectStillPreviewSection => ShowFileInfoCard && SupportsEffectStillPreview;

    public bool ShowNonStillPreviewHint =>
        ShowFileInfoCard
        && (Operation == VideoEnhanceOperation.SpeedChange
            || Operation == VideoEnhanceOperation.Reverse
            || Operation == VideoEnhanceOperation.Stabilize
            || Operation == VideoEnhanceOperation.ExtractAudio);

    public bool ShowWatermarkPanel => Operation == VideoEnhanceOperation.Watermark;

    public bool ShowSpeedPanel => Operation == VideoEnhanceOperation.SpeedChange;

    public bool ShowReversePanel => Operation == VideoEnhanceOperation.Reverse;

    public bool ShowStabilizePanel => Operation == VideoEnhanceOperation.Stabilize;

    public bool ShowColorPanel => Operation == VideoEnhanceOperation.ColorGrading;

    public bool ShowCropResizePanel => Operation == VideoEnhanceOperation.CropAndResize;

    public bool ShowExtractAudioPanel => Operation == VideoEnhanceOperation.ExtractAudio;

    public bool ShowProgressCard => IsRunning || FinishedAttempt;

    public bool ShowResultCard => Succeeded;

    public bool ShowCancelButton => IsRunning;

    public int ProgressPercentDisplay => (int)Math.Round(ProgressPercent01 * 100, MidpointRounding.AwayFromZero);

    private bool CanStart() =>
        !string.IsNullOrWhiteSpace(SelectedFilePath)
        && Directory.Exists(_preferences.SaveFolderPath)
        && !IsRunning
        && !IsAnalyzing;

    private void OnAnyPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not null && EffectPreviewPropertyNames.Contains(e.PropertyName))
        {
            ScheduleEffectPreview();
        }

        if (e.PropertyName is not null
            && UndoablePropertyNames.Contains(e.PropertyName)
            && !_suppressUndoNotification
            && !_history.IsApplyingHistory)
        {
            _history.NotifyEdit();
        }
    }

    private void OnUndoRedoHistoryChanged()
    {
        UndoCommand.NotifyCanExecuteChanged();
        RedoCommand.NotifyCanExecuteChanged();
    }

    private VideoEnhancerUndoSnapshot CaptureSnapshot() =>
        new(
            SelectedFilePath,
            FileDisplayName,
            FileSizeDisplay,
            DurationDisplay,
            DimensionsDisplay,
            CodecDisplay,
            HasAudio,
            SourceWidth,
            SourceHeight,
            Operation,
            WatermarkKind,
            WatermarkImagePath,
            WatermarkText,
            WatermarkPosition,
            WatermarkOpacityPercent,
            WatermarkSizePercent,
            SpeedFactor,
            SpeedPreservePitch,
            StabilizerSmoothing,
            StabilizerZoom,
            ColorBrightness,
            ColorContrast,
            ColorSaturation,
            ColorGamma,
            ColorHue,
            CropEnabled,
            CropX,
            CropY,
            CropWidth,
            CropHeight,
            ResizeEnabled,
            ResizeWidth,
            ResizeHeight,
            AudioFormat,
            AudioBitrateKbps,
            PreviewMuted,
            ProgressPercent01,
            ProgressStatusText,
            ProgressDetailText,
            FinishedAttempt,
            Succeeded,
            ResultMessage,
            CaptureSubtitleSnapshot());

    private void ApplySnapshot(VideoEnhancerUndoSnapshot s)
    {
        _effectPreviewDebounce.Stop();
        _effectPreviewRunCts?.Cancel();
        EffectPreviewImage = null;
        IsEffectPreviewBusy = false;

        SelectedFilePath = s.SelectedFilePath;
        FileDisplayName = s.FileDisplayName;
        FileSizeDisplay = s.FileSizeDisplay;
        DurationDisplay = s.DurationDisplay;
        DimensionsDisplay = s.DimensionsDisplay;
        CodecDisplay = s.CodecDisplay;
        HasAudio = s.HasAudio;
        SourceWidth = s.SourceWidth;
        SourceHeight = s.SourceHeight;
        Operation = s.Operation;
        WatermarkKind = s.WatermarkKind;
        WatermarkImagePath = s.WatermarkImagePath;
        WatermarkText = s.WatermarkText;
        WatermarkPosition = s.WatermarkPosition;
        WatermarkOpacityPercent = s.WatermarkOpacityPercent;
        WatermarkSizePercent = s.WatermarkSizePercent;
        SpeedFactor = s.SpeedFactor;
        SpeedPreservePitch = s.SpeedPreservePitch;
        StabilizerSmoothing = s.StabilizerSmoothing;
        StabilizerZoom = s.StabilizerZoom;
        ColorBrightness = s.ColorBrightness;
        ColorContrast = s.ColorContrast;
        ColorSaturation = s.ColorSaturation;
        ColorGamma = s.ColorGamma;
        ColorHue = s.ColorHue;
        CropEnabled = s.CropEnabled;
        CropX = s.CropX;
        CropY = s.CropY;
        CropWidth = s.CropWidth;
        CropHeight = s.CropHeight;
        ResizeEnabled = s.ResizeEnabled;
        ResizeWidth = s.ResizeWidth;
        ResizeHeight = s.ResizeHeight;
        AudioFormat = s.AudioFormat;
        AudioBitrateKbps = s.AudioBitrateKbps;
        PreviewMuted = s.PreviewMuted;
        ProgressPercent01 = s.ProgressPercent01;
        ProgressStatusText = s.ProgressStatusText;
        ProgressDetailText = s.ProgressDetailText;
        FinishedAttempt = s.FinishedAttempt;
        Succeeded = s.Succeeded;
        ResultMessage = s.ResultMessage;
        IsAnalyzing = false;

        OnPropertyChanged(nameof(PreviewMediaUri));
        OnPropertyChanged(nameof(ShowFileInfoCard));
        OnPropertyChanged(nameof(ShowEffectStillPreviewSection));
        OnPropertyChanged(nameof(ShowNonStillPreviewHint));
        OnPropertyChanged(nameof(IsWatermarkImage));
        OnPropertyChanged(nameof(IsWatermarkText));
        ApplySubtitleUndoState(s.Subtitle);
        ScheduleEffectPreview();
        StartCommand.NotifyCanExecuteChanged();
    }

    private bool CanUndoOperation() => _history.CanUndo && !IsRunning && !SubIsRunning;

    private bool CanRedoOperation() => _history.CanRedo && !IsRunning && !SubIsRunning;

    [RelayCommand(CanExecute = nameof(CanUndoOperation))]
    private void Undo() => _history.TryUndo();

    [RelayCommand(CanExecute = nameof(CanRedoOperation))]
    private void Redo() => _history.TryRedo();

    private void ScheduleEffectPreview()
    {
        if (!Dispatcher.CurrentDispatcher.CheckAccess())
        {
            Dispatcher.CurrentDispatcher.Invoke(ScheduleEffectPreview);
            return;
        }

        if (string.IsNullOrWhiteSpace(SelectedFilePath) || IsRunning || SubIsRunning || IsAnalyzing)
        {
            _effectPreviewDebounce.Stop();
            _effectPreviewRunCts?.Cancel();
            EffectPreviewImage = null;
            IsEffectPreviewBusy = false;
            return;
        }

        if (!SupportsEffectStillPreview)
        {
            _effectPreviewDebounce.Stop();
            _effectPreviewRunCts?.Cancel();
            EffectPreviewImage = null;
            IsEffectPreviewBusy = false;
            return;
        }

        _effectPreviewDebounce.Stop();
        _effectPreviewDebounce.Start();
    }

    private async Task RunEffectPreviewOnceAsync()
    {
        if (string.IsNullOrWhiteSpace(SelectedFilePath) || IsRunning || SubIsRunning || IsAnalyzing || !SupportsEffectStillPreview)
        {
            EffectPreviewImage = null;
            return;
        }

        if (!TryBuildSettings(out var settings, out _))
        {
            EffectPreviewImage = null;
            return;
        }

        _effectPreviewRunCts?.Cancel();
        _effectPreviewRunCts = new CancellationTokenSource();
        var token = _effectPreviewRunCts.Token;

        var path = SelectedFilePath;
        IsEffectPreviewBusy = true;
        try
        {
            var bytes = await _videoEnhanceService.TryRenderEffectPreviewJpegAsync(path, settings, token)
                .ConfigureAwait(false);

            if (bytes is null || token.IsCancellationRequested)
            {
                return;
            }

            await Dispatcher.CurrentDispatcher.InvokeAsync(() =>
            {
                if (token.IsCancellationRequested)
                {
                    return;
                }

                try
                {
                    using var ms = new MemoryStream(bytes);
                    var img = new BitmapImage();
                    img.BeginInit();
                    img.CacheOption = BitmapCacheOption.OnLoad;
                    img.StreamSource = ms;
                    img.EndInit();
                    img.Freeze();
                    EffectPreviewImage = img;
                }
                catch
                {
                    EffectPreviewImage = null;
                }
            });
        }
        finally
        {
            await Dispatcher.CurrentDispatcher.InvokeAsync(() => IsEffectPreviewBusy = false);
        }
    }

    [RelayCommand]
    private void SelectOperation(VideoEnhanceOperation op) => Operation = op;

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

    [RelayCommand]
    private async Task BrowseFileAsync()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "Video|*.mp4;*.mkv;*.avi;*.mov;*.wmv;*.flv;*.m4v;*.webm|All files|*.*"
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
    private void BrowseWatermarkImage()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "Image|*.png;*.jpg;*.jpeg;*.webp;*.bmp;*.gif|All files|*.*"
        };

        if (dlg.ShowDialog() != true)
        {
            return;
        }

        var ext = Path.GetExtension(dlg.FileName).ToLowerInvariant();
        if (!AllowedWatermarkExtensions.Contains(ext))
        {
            MessageBoxHelper.ShowWarning("Unsupported image format.");
            return;
        }

        WatermarkImagePath = dlg.FileName;
    }

    [RelayCommand]
    private void ClearFile()
    {
        _history.PushUndoFrameAnd(ResetUiToNoFile);
    }

    private void ResetUiToNoFile()
    {
        _effectPreviewDebounce.Stop();
        _effectPreviewRunCts?.Cancel();
        EffectPreviewImage = null;
        IsEffectPreviewBusy = false;

        SelectedFilePath = null;
        FileDisplayName = string.Empty;
        FileSizeDisplay = string.Empty;
        DurationDisplay = string.Empty;
        DimensionsDisplay = string.Empty;
        CodecDisplay = string.Empty;
        HasAudio = false;
        SourceWidth = 0;
        SourceHeight = 0;
        FinishedAttempt = false;
        Succeeded = false;
        ProgressPercent01 = 0;
        ProgressStatusText = string.Empty;
        ProgressDetailText = string.Empty;
        ResultMessage = string.Empty;

        OnPropertyChanged(nameof(PreviewMediaUri));
        OnPropertyChanged(nameof(ShowFileInfoCard));
        OnPropertyChanged(nameof(ShowEffectStillPreviewSection));
        OnPropertyChanged(nameof(ShowNonStillPreviewHint));
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

    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task StartAsync()
    {
        if (SelectedFilePath is null || !CanStart())
        {
            return;
        }

        if (!TryBuildSettings(out var settings, out var errorMessage))
        {
            MessageBoxHelper.ShowWarning(errorMessage ?? "Invalid settings.");
            return;
        }

        var outputPath = BuildOutputPath();
        var request = new ProcessVideoEnhanceRequest(SelectedFilePath, outputPath, settings);

        _cts = new CancellationTokenSource();
        var token = _cts.Token;

        IsRunning = true;
        FinishedAttempt = false;
        Succeeded = false;
        ProgressPercent01 = 0;
        ProgressStatusText = "Working…";
        ProgressDetailText = string.Empty;
        ResultMessage = string.Empty;

        var dispatcher = global::System.Windows.Application.Current?.Dispatcher;

        var progress = new Progress<VideoEnhanceProgressReport>(r =>
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
            var result = await _processVideoEnhanceUseCase.ExecuteAsync(request, progress, token).ConfigureAwait(true);

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
                toastTitle = $"{OperationLabel(Operation)} done";
                toastBody = $"{Path.GetFileName(outputPath)} · {FormatBytes(len)}";
                toastSuccess = true;
            }
            else if (result.IsSuccess)
            {
                Succeeded = false;
                ProgressStatusText = "Failed";
                ProgressDetailText = "Output file was not created.";
                ResultMessage = ProgressDetailText;
                toastTitle = "Video Enhancer failed";
                toastBody = ResultMessage;
            }
            else
            {
                Succeeded = false;
                ProgressStatusText = "Failed";
                ProgressDetailText = result.ErrorMessage ?? "Unknown error";
                ResultMessage = result.ErrorMessage ?? "Operation failed.";
                toastTitle = "Video Enhancer failed";
                toastBody = ResultMessage;
            }
        }
        finally
        {
            IsRunning = false;
            FinishedAttempt = true;
            StartCommand.NotifyCanExecuteChanged();
            UndoCommand.NotifyCanExecuteChanged();
            RedoCommand.NotifyCanExecuteChanged();

            if (toastTitle is not null)
            {
                _toastNotifications.ShowToolFinished(
                    toastTitle,
                    toastBody ?? string.Empty,
                    toastSuccess,
                    "Video Enhancer");
            }

            _history.FlushPendingEdit();
        }
    }

    private bool TryBuildSettings(out VideoEnhanceSettings settings, out string? error)
    {
        settings = new VideoEnhanceSettings(Operation, null, null, null, null, null, null);
        error = null;

        switch (Operation)
        {
            case VideoEnhanceOperation.Watermark:
            {
                if (WatermarkKind == WatermarkSourceKind.Image)
                {
                    if (string.IsNullOrWhiteSpace(WatermarkImagePath) || !File.Exists(WatermarkImagePath))
                    {
                        error = "Choose a watermark image first.";
                        return false;
                    }
                }
                else if (string.IsNullOrWhiteSpace(WatermarkText))
                {
                    error = "Watermark text cannot be empty.";
                    return false;
                }

                settings = settings with
                {
                    Watermark = new VideoWatermarkSettings(
                        WatermarkKind,
                        WatermarkImagePath,
                        WatermarkText,
                        WatermarkPosition,
                        WatermarkOpacityPercent,
                        WatermarkSizePercent)
                };
                return true;
            }

            case VideoEnhanceOperation.SpeedChange:
                settings = settings with
                {
                    Speed = new VideoSpeedSettings(SpeedFactor, SpeedPreservePitch)
                };
                return true;

            case VideoEnhanceOperation.Reverse:
                return true;

            case VideoEnhanceOperation.Stabilize:
                settings = settings with
                {
                    Stabilizer = new VideoStabilizerSettings(StabilizerSmoothing, StabilizerZoom)
                };
                return true;

            case VideoEnhanceOperation.ColorGrading:
                settings = settings with
                {
                    ColorGrading = new VideoColorGradingSettings(
                        ColorBrightness,
                        ColorContrast,
                        ColorSaturation,
                        ColorGamma,
                        ColorHue)
                };
                return true;

            case VideoEnhanceOperation.CropAndResize:
            {
                if (!CropEnabled && !ResizeEnabled)
                {
                    error = "Enable crop or resize.";
                    return false;
                }

                settings = settings with
                {
                    CropResize = new VideoCropResizeSettings(
                        CropEnabled,
                        CropX,
                        CropY,
                        CropWidth,
                        CropHeight,
                        ResizeEnabled,
                        ResizeWidth > 0 ? ResizeWidth : null,
                        ResizeHeight > 0 ? ResizeHeight : null)
                };
                return true;
            }

            case VideoEnhanceOperation.ExtractAudio:
            {
                if (!HasAudio)
                {
                    error = "This file has no audio stream to extract.";
                    return false;
                }

                settings = settings with
                {
                    ToAudio = new VideoToAudioSettings(AudioFormat, AudioBitrateKbps)
                };
                return true;
            }

            default:
                error = "Unknown operation.";
                return false;
        }
    }

    private string BuildOutputPath()
    {
        var folder = _preferences.SaveFolderPath;
        var baseName = Path.GetFileNameWithoutExtension(SelectedFilePath ?? "video");
        var ext = Path.GetExtension(SelectedFilePath ?? string.Empty);
        if (string.IsNullOrEmpty(ext))
        {
            ext = ".mp4";
        }

        var suffix = Operation switch
        {
            VideoEnhanceOperation.Watermark => "watermark",
            VideoEnhanceOperation.SpeedChange => $"speed{SpeedFactor:0.##}x",
            VideoEnhanceOperation.Reverse => "reverse",
            VideoEnhanceOperation.Stabilize => "stabilized",
            VideoEnhanceOperation.ColorGrading => "graded",
            VideoEnhanceOperation.CropAndResize => "edit",
            VideoEnhanceOperation.ExtractAudio => "audio",
            _ => "out"
        };

        if (Operation == VideoEnhanceOperation.ExtractAudio)
        {
            ext = AudioFormat switch
            {
                AudioExportFormat.Mp3 => ".mp3",
                AudioExportFormat.M4aAac => ".m4a",
                AudioExportFormat.Flac => ".flac",
                AudioExportFormat.OggOpus => ".ogg",
                AudioExportFormat.Wav => ".wav",
                _ => ".mp3"
            };
        }
        else if (ext.Equals(".mkv", StringComparison.OrdinalIgnoreCase))
        {
            // libx264 + faststart pairs best with mp4 — keep mkv only if user requests; default to mp4
            ext = ".mp4";
        }

        var candidate = Path.Combine(folder, $"{baseName}_{suffix}{ext}");
        var counter = 1;
        while (File.Exists(candidate))
        {
            candidate = Path.Combine(folder, $"{baseName}_{suffix}_{counter}{ext}");
            counter++;
        }

        return candidate;
    }

    private async Task<bool> LoadFileAsync(string path)
    {
        IsAnalyzing = true;
        try
        {
            var analysis = await _videoEnhanceService.AnalyzeAsync(path).ConfigureAwait(true);
            SelectedFilePath = path;
            FileDisplayName = analysis.FileName;
            FileSizeDisplay = FormatBytes(analysis.FileSizeBytes);
            DurationDisplay = analysis.Duration > TimeSpan.Zero ? FormatDuration(analysis.Duration) : "—";
            DimensionsDisplay = $"{analysis.Width} × {analysis.Height}";
            CodecDisplay = string.IsNullOrWhiteSpace(analysis.VideoCodec) ? "—" : analysis.VideoCodec;
            HasAudio = analysis.HasAudio;
            SourceWidth = analysis.Width;
            SourceHeight = analysis.Height;

            // Seed crop & resize defaults to source size
            CropX = 0;
            CropY = 0;
            CropWidth = analysis.Width;
            CropHeight = analysis.Height;
            ResizeWidth = analysis.Width;
            ResizeHeight = analysis.Height;

            FinishedAttempt = false;
            Succeeded = false;
            OnPropertyChanged(nameof(PreviewMediaUri));
            OnPropertyChanged(nameof(ShowFileInfoCard));
            OnPropertyChanged(nameof(ShowEffectStillPreviewSection));
            OnPropertyChanged(nameof(ShowNonStillPreviewHint));
            ScheduleEffectPreview();
            return true;
        }
        catch (Exception ex)
        {
            MessageBoxHelper.ShowWarning($"Could not read media: {ex.Message}");
            ResetUiToNoFile();
            return false;
        }
        finally
        {
            IsAnalyzing = false;
            StartCommand.NotifyCanExecuteChanged();
            UndoCommand.NotifyCanExecuteChanged();
            RedoCommand.NotifyCanExecuteChanged();
        }
    }

    partial void OnSelectedFilePathChanged(string? value) => StartCommand.NotifyCanExecuteChanged();

    partial void OnIsRunningChanged(bool value)
    {
        StartCommand.NotifyCanExecuteChanged();
        UndoCommand.NotifyCanExecuteChanged();
        RedoCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsAnalyzingChanged(bool value) => StartCommand.NotifyCanExecuteChanged();

    private static string OperationLabel(VideoEnhanceOperation op) =>
        op switch
        {
            VideoEnhanceOperation.Watermark => "Watermark",
            VideoEnhanceOperation.SpeedChange => "Speed change",
            VideoEnhanceOperation.Reverse => "Reverse",
            VideoEnhanceOperation.Stabilize => "Stabilize",
            VideoEnhanceOperation.ColorGrading => "Color grading",
            VideoEnhanceOperation.CropAndResize => "Crop & resize",
            VideoEnhanceOperation.ExtractAudio => "Audio extract",
            _ => "Video enhance"
        };

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
}
