using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Windows.Media;
using System.Windows.Media.Imaging;
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

public partial class PhotoEnhancerViewModel : ObservableObject
{
    private static readonly HashSet<string> AllowedExtensions =
    [
        ".png", ".jpg", ".jpeg", ".webp", ".bmp", ".tif", ".tiff", ".gif"
    ];

    private readonly ProcessPhotoUseCase _processPhotoUseCase;
    private readonly IImageProcessingService _imageProcessingService;
    private readonly IUserPreferencesService _preferences;
    private readonly IWindowsToastNotificationService _toastNotifications;
    private readonly UndoRedoHost<PhotoEnhancerUndoSnapshot> _history;
    private CancellationTokenSource? _cts;
    private CancellationTokenSource? _previewCts;
    private int _previewVersion;
    private bool _suppressUndoNotification;
    private readonly List<EraserBrushStamp> _eraserStrokes = [];
    private int _imagePixelWidth;
    private int _imagePixelHeight;
    private bool _eraserPointerDown;
    private bool _eraserDabsSinceDown;
    private double _lastEraserImageX;
    private double _lastEraserImageY;
    private bool _hasLastEraserSample;

    public PhotoEnhancerViewModel(
        ProcessPhotoUseCase processPhotoUseCase,
        IImageProcessingService imageProcessingService,
        IUserPreferencesService preferences,
        IWindowsToastNotificationService toastNotifications)
    {
        _processPhotoUseCase = processPhotoUseCase;
        _imageProcessingService = imageProcessingService;
        _preferences = preferences;
        _toastNotifications = toastNotifications;
        _preferences.SaveFolderPathChanged += OnSaveFolderPathChanged;

        foreach (var e in MaxEdgePresets)
        {
            MaxEdgeOptions.Add(e);
        }

        _history = new UndoRedoHost<PhotoEnhancerUndoSnapshot>(
            CapturePhotoSnapshot,
            ApplyPhotoSnapshot,
            CapturePhotoSnapshot(),
            OnUndoRedoHistoryChanged);
    }

    private void OnSaveFolderPathChanged(object? sender, EventArgs e) =>
        ProcessPhotoCommand.NotifyCanExecuteChanged();

    public IEnumerable<double> ScaleFactorOptions => [1, 1.25, 1.5, 2, 2.5, 3, 4];

    public IEnumerable<RasterImageFormat> TargetFormats => Enum.GetValues<RasterImageFormat>();

    public IEnumerable<PhotoResizeIntent> ResizeModes => Enum.GetValues<PhotoResizeIntent>();
    public IEnumerable<UpscaleQualityMode> UpscaleModes => Enum.GetValues<UpscaleQualityMode>();

    public IEnumerable<PhotoFilterKind> FilterKinds => Enum.GetValues<PhotoFilterKind>();

    public ObservableCollection<int> MaxEdgeOptions { get; } = [];

    private static int[] MaxEdgePresets => [640, 1024, 1920, 2560, 3840, 4096];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowFileInfoCard))]
    private string? _selectedFilePath;

    [ObservableProperty]
    private string _fileDisplayName = string.Empty;

    [ObservableProperty]
    private string _fileSizeDisplay = string.Empty;

    [ObservableProperty]
    private string _dimensionsDisplay = string.Empty;

    [ObservableProperty]
    private string _formatDisplay = string.Empty;

    [ObservableProperty]
    private ImageSource? _originalPreviewImage;

    [ObservableProperty]
    private ImageSource? _editedPreviewImage;

    [ObservableProperty]
    private bool _isEditedPreviewBusy;

    [ObservableProperty]
    private RasterImageFormat _targetFormat = RasterImageFormat.Jpeg;

    [ObservableProperty]
    private int _encodingQuality = 90;

    [ObservableProperty]
    private PhotoResizeIntent _resizeIntent = PhotoResizeIntent.Original;

    [ObservableProperty]
    private double _scaleFactor = 2;

    [ObservableProperty]
    private int _selectedMaxEdge = 1920;

    [ObservableProperty]
    private UpscaleQualityMode _upscaleQualityMode = UpscaleQualityMode.HighQuality;

    [ObservableProperty]
    private PhotoFilterKind _selectedFilter = PhotoFilterKind.None;

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
    [NotifyPropertyChangedFor(nameof(ProcessPrimaryLabel))]
    [NotifyPropertyChangedFor(nameof(IsEnhanceWorkspace))]
    [NotifyPropertyChangedFor(nameof(IsBackgroundWorkspace))]
    [NotifyPropertyChangedFor(nameof(IsEraserWorkspace))]
    private int _workspaceTabIndex;

    public bool IsEnhanceWorkspace => WorkspaceTabIndex == 0;

    public bool IsBackgroundWorkspace => WorkspaceTabIndex == 1;

    public bool IsEraserWorkspace => WorkspaceTabIndex == 2;

    [ObservableProperty]
    private BackgroundRemovalMode _selectedBackgroundRemovalMode = BackgroundRemovalMode.AutoEdge;

    [ObservableProperty]
    private int _bgTolerance = 35;

    [ObservableProperty]
    private float _bgFeatherSigma = 2f;

    [ObservableProperty]
    private int _bgKeyR;

    [ObservableProperty]
    private int _bgKeyG = 255;

    [ObservableProperty]
    private int _bgKeyB;

    [ObservableProperty]
    private double _bgLuminanceThreshold = 0.92;

    [ObservableProperty]
    private int _bgEdgeExpandPx;

    [ObservableProperty]
    private float _eraserBlurSigma = 14f;

    [ObservableProperty]
    private float _eraserBrushSoftness = 0.55f;

    [ObservableProperty]
    private float _eraserBrushRadiusPx = 36f;

    public int ImagePixelWidth => _imagePixelWidth;

    public int ImagePixelHeight => _imagePixelHeight;

    public IReadOnlyList<EraserBrushStamp> EraserStrokes => _eraserStrokes;

    public string ProcessPrimaryLabel => WorkspaceTabIndex switch
    {
        0 => "Process image",
        1 => "Remove background & save",
        2 => "Apply eraser & save",
        _ => "Process"
    };

    public IEnumerable<BackgroundRemovalMode> BackgroundRemovalModes => Enum.GetValues<BackgroundRemovalMode>();

    public bool ShowFileInfoCard => !string.IsNullOrWhiteSpace(SelectedFilePath);

    public bool ShowProgressCard => IsRunning || FinishedAttempt;

    public bool ShowResultCard => Succeeded;

    public bool ShowCancelButton => IsRunning;

    public bool ShowScaleControls => ResizeIntent == PhotoResizeIntent.ScaleByFactor;

    public bool ShowMaxEdgeControls => ResizeIntent == PhotoResizeIntent.FitMaxEdge;

    public int ProgressPercentDisplay => (int)Math.Round(ProgressPercent01 * 100, MidpointRounding.AwayFromZero);

    private bool CanStartProcess()
    {
        if (string.IsNullOrWhiteSpace(SelectedFilePath)
            || !Directory.Exists(_preferences.SaveFolderPath)
            || IsRunning)
        {
            return false;
        }

        if (WorkspaceTabIndex == 2 && _eraserStrokes.Count == 0)
        {
            return false;
        }

        return true;
    }

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

    private void OnSettingsChangedForPreview()
    {
        NotifyUndoableEdit();
        RequestEditedPreviewRefresh();
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
            Filter = "Images|*.png;*.jpg;*.jpeg;*.webp;*.bmp;*.tif;*.tiff;*.gif|All files|*.*"
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
            _previewCts?.Cancel();
            SelectedFilePath = null;
            FileDisplayName = string.Empty;
            FileSizeDisplay = string.Empty;
            DimensionsDisplay = string.Empty;
            FormatDisplay = string.Empty;
            OriginalPreviewImage = null;
            EditedPreviewImage = null;
            IsEditedPreviewBusy = false;
            FinishedAttempt = false;
            Succeeded = false;
            ProgressPercent01 = 0;
            ProgressStatusText = string.Empty;
            ProgressDetailText = string.Empty;
            ResultMessage = string.Empty;
            _eraserStrokes.Clear();
            _imagePixelWidth = 0;
            _imagePixelHeight = 0;
        });
    }

    [RelayCommand(CanExecute = nameof(CanUndoOperation))]
    private void Undo() => _history.TryUndo();

    [RelayCommand(CanExecute = nameof(CanRedoOperation))]
    private void Redo() => _history.TryRedo();

    [RelayCommand(CanExecute = nameof(CanStartProcess))]
    private async Task ProcessPhotoAsync()
    {
        if (SelectedFilePath is null || !CanStartProcess())
        {
            return;
        }

        _cts = new CancellationTokenSource();
        var token = _cts.Token;

        _previewCts?.Cancel();
        IsEditedPreviewBusy = false;

        IsRunning = true;
        FinishedAttempt = false;
        Succeeded = false;
        ProgressPercent01 = 0;
        ProgressStatusText = WorkspaceTabIndex switch
        {
            1 => "Removing background…",
            2 => "Applying object eraser…",
            _ => "Processing…",
        };
        ProgressDetailText = string.Empty;
        ResultMessage = string.Empty;

        var dispatcher = global::System.Windows.Application.Current?.Dispatcher;

        var progress = new Progress<PhotoProgressReport>(r =>
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
            if (WorkspaceTabIndex == 1)
            {
                var bgSettings = BuildBackgroundRemovalSettings();
                var bgOut = Path.Combine(
                    _preferences.SaveFolderPath,
                    Path.GetFileNameWithoutExtension(SelectedFilePath) + "_nobg.png");

                await _imageProcessingService
                    .RemoveBackgroundToFileAsync(SelectedFilePath, bgOut, bgSettings, progress, token)
                    .ConfigureAwait(true);

                if (token.IsCancellationRequested)
                {
                    ProgressStatusText = "Cancelled";
                    Succeeded = false;
                }
                else if (File.Exists(bgOut))
                {
                    Succeeded = true;
                    _preferences.IncrementLifetimeStat(AppLifetimeStatKind.PhotoEnhanced);
                    ProgressStatusText = "Complete";
                    ProgressPercent01 = 1;
                    var len = new FileInfo(bgOut).Length;
                    ResultMessage = $"Saved to {bgOut} ({FormatBytes(len)})";
                    toastTitle = "Background removed";
                    toastBody = $"{Path.GetFileName(bgOut)} · {FormatBytes(len)}";
                    toastSuccess = true;
                }
                else
                {
                    Succeeded = false;
                    ProgressStatusText = "Failed";
                    ProgressDetailText = "Output file was not created.";
                    ResultMessage = ProgressDetailText;
                    toastTitle = "Background removal failed";
                    toastBody = ResultMessage;
                    toastSuccess = false;
                }
            }
            else if (WorkspaceTabIndex == 2)
            {
                var encode = BuildSettings();
                var ext = ExtensionFor(encode.TargetFormat);
                var erOut = Path.Combine(
                    _preferences.SaveFolderPath,
                    Path.GetFileNameWithoutExtension(SelectedFilePath) + "_erased" + ext);

                await _imageProcessingService
                    .ApplyObjectEraserToFileAsync(
                        SelectedFilePath,
                        erOut,
                        _eraserStrokes,
                        BuildObjectEraserSettings(),
                        encode,
                        progress,
                        token)
                    .ConfigureAwait(true);

                if (token.IsCancellationRequested)
                {
                    ProgressStatusText = "Cancelled";
                    Succeeded = false;
                }
                else if (File.Exists(erOut))
                {
                    Succeeded = true;
                    _preferences.IncrementLifetimeStat(AppLifetimeStatKind.PhotoEnhanced);
                    ProgressStatusText = "Complete";
                    ProgressPercent01 = 1;
                    var len = new FileInfo(erOut).Length;
                    ResultMessage = $"Saved to {erOut} ({FormatBytes(len)})";
                    toastTitle = "Object eraser complete";
                    toastBody = $"{Path.GetFileName(erOut)} · {FormatBytes(len)}";
                    toastSuccess = true;
                }
                else
                {
                    Succeeded = false;
                    ProgressStatusText = "Failed";
                    ProgressDetailText = "Output file was not created.";
                    ResultMessage = ProgressDetailText;
                    toastTitle = "Object eraser failed";
                    toastBody = ResultMessage;
                    toastSuccess = false;
                }
            }
            else
            {
                var settings = BuildSettings();
                var ext = ExtensionFor(settings.TargetFormat);
                var outputPath = Path.Combine(
                    _preferences.SaveFolderPath,
                    Path.GetFileNameWithoutExtension(SelectedFilePath) + "_enhanced" + ext);

                var request = new ProcessPhotoRequest(SelectedFilePath, outputPath, settings);
                var result = await _processPhotoUseCase.ExecuteAsync(request, progress, token).ConfigureAwait(true);

                if (result.IsCancelled)
                {
                    ProgressStatusText = "Cancelled";
                    Succeeded = false;
                }
                else if (result.IsSuccess && File.Exists(outputPath))
                {
                    Succeeded = true;
                    _preferences.IncrementLifetimeStat(AppLifetimeStatKind.PhotoEnhanced);
                    ProgressStatusText = "Complete";
                    ProgressPercent01 = 1;
                    var len = new FileInfo(outputPath).Length;
                    ResultMessage = $"Saved to {outputPath} ({FormatBytes(len)})";
                    toastTitle = "Photo enhancement complete";
                    toastBody = $"{Path.GetFileName(outputPath)} · {FormatBytes(len)}";
                    toastSuccess = true;
                }
                else if (result.IsSuccess)
                {
                    Succeeded = false;
                    ProgressStatusText = "Failed";
                    ProgressDetailText = "Output file was not created.";
                    ResultMessage = ProgressDetailText;
                    toastTitle = "Photo enhancement failed";
                    toastBody = ResultMessage;
                    toastSuccess = false;
                }
                else
                {
                    Succeeded = false;
                    ProgressStatusText = "Failed";
                    ProgressDetailText = result.ErrorMessage ?? "Unknown error";
                    ResultMessage = result.ErrorMessage ?? "Processing failed.";
                    toastTitle = "Photo enhancement failed";
                    toastBody = ResultMessage;
                    toastSuccess = false;
                }
            }
        }
        catch (OperationCanceledException)
        {
            ProgressStatusText = "Cancelled";
            Succeeded = false;
        }
        catch (Exception ex)
        {
            Succeeded = false;
            ProgressStatusText = "Failed";
            ProgressDetailText = ex.Message;
            ResultMessage = ex.Message;
            toastTitle = WorkspaceTabIndex switch
            {
                1 => "Background removal failed",
                2 => "Object eraser failed",
                _ => "Photo enhancement failed",
            };
            toastBody = ResultMessage;
            toastSuccess = false;
        }
        finally
        {
            IsRunning = false;
            FinishedAttempt = true;
            ProcessPhotoCommand.NotifyCanExecuteChanged();
            UndoCommand.NotifyCanExecuteChanged();
            RedoCommand.NotifyCanExecuteChanged();
            _history.FlushPendingEdit();
            RequestEditedPreviewRefresh();

            if (toastTitle is not null)
            {
                _toastNotifications.ShowToolFinished(
                    toastTitle,
                    toastBody ?? string.Empty,
                    toastSuccess,
                    "Photo Enhancer");
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

    private PhotoEnhanceSettings BuildSettings()
    {
        int? maxEdge = ResizeIntent == PhotoResizeIntent.FitMaxEdge ? SelectedMaxEdge : null;
        var scale = ResizeIntent == PhotoResizeIntent.ScaleByFactor ? ScaleFactor : 1;

        return new PhotoEnhanceSettings(
            TargetFormat,
            EncodingQuality,
            ResizeIntent,
            scale,
            maxEdge,
            UpscaleQualityMode,
            SelectedFilter);
    }

    private static string ExtensionFor(RasterImageFormat format) =>
        format switch
        {
            RasterImageFormat.Png => ".png",
            RasterImageFormat.Jpeg => ".jpg",
            RasterImageFormat.Webp => ".webp",
            RasterImageFormat.Bmp => ".bmp",
            RasterImageFormat.Tiff => ".tif",
            RasterImageFormat.Ico => ".ico",
            _ => ".png"
        };

    private async Task<bool> LoadFileAsync(string path)
    {
        try
        {
            var info = await _imageProcessingService.AnalyzeAsync(path).ConfigureAwait(true);
            SelectedFilePath = path;
            FileDisplayName = info.FileName;
            FileSizeDisplay = info.FormattedFileSize;
            DimensionsDisplay = $"{info.Width} × {info.Height}";
            FormatDisplay = info.FormatHint;
            _imagePixelWidth = info.Width;
            _imagePixelHeight = info.Height;
            _eraserStrokes.Clear();
            _hasLastEraserSample = false;
            OriginalPreviewImage = CreatePreviewSource(path);
            FinishedAttempt = false;
            Succeeded = false;
            RequestEditedPreviewRefresh();
            return true;
        }
        catch (Exception ex)
        {
            OriginalPreviewImage = null;
            EditedPreviewImage = null;
            MessageBoxHelper.ShowWarning($"Could not read image: {ex.Message}");
            return false;
        }
    }

    private PhotoEnhancerUndoSnapshot CapturePhotoSnapshot() =>
        new(
            SelectedFilePath,
            FileDisplayName,
            FileSizeDisplay,
            DimensionsDisplay,
            FormatDisplay,
            TargetFormat,
            EncodingQuality,
            ResizeIntent,
            ScaleFactor,
            SelectedMaxEdge,
            UpscaleQualityMode,
            SelectedFilter,
            ProgressPercent01,
            ProgressStatusText,
            ProgressDetailText,
            FinishedAttempt,
            Succeeded,
            ResultMessage,
            (PhotoEnhancerWorkspace)Math.Clamp(WorkspaceTabIndex, 0, 2),
            SelectedBackgroundRemovalMode,
            BgTolerance,
            BgFeatherSigma,
            (byte)Math.Clamp(BgKeyR, 0, 255),
            (byte)Math.Clamp(BgKeyG, 0, 255),
            (byte)Math.Clamp(BgKeyB, 0, 255),
            (float)BgLuminanceThreshold,
            BgEdgeExpandPx,
            _eraserStrokes.ToArray(),
            EraserBlurSigma,
            EraserBrushSoftness,
            EraserBrushRadiusPx);

    private void ApplyPhotoSnapshot(PhotoEnhancerUndoSnapshot s)
    {
        SelectedFilePath = s.SelectedFilePath;
        FileDisplayName = s.FileDisplayName;
        FileSizeDisplay = s.FileSizeDisplay;
        DimensionsDisplay = s.DimensionsDisplay;
        FormatDisplay = s.FormatDisplay;
        TargetFormat = s.TargetFormat;
        EncodingQuality = s.EncodingQuality;
        ResizeIntent = s.ResizeIntent;
        ScaleFactor = s.ScaleFactor;
        SelectedMaxEdge = s.SelectedMaxEdge;
        UpscaleQualityMode = s.UpscaleQualityMode;
        SelectedFilter = s.SelectedFilter;
        ProgressPercent01 = s.ProgressPercent01;
        ProgressStatusText = s.ProgressStatusText;
        ProgressDetailText = s.ProgressDetailText;
        FinishedAttempt = s.FinishedAttempt;
        Succeeded = s.Succeeded;
        ResultMessage = s.ResultMessage;
        WorkspaceTabIndex = (int)s.Workspace;
        SelectedBackgroundRemovalMode = s.BgMode;
        BgTolerance = s.BgTolerance;
        BgFeatherSigma = s.BgFeatherSigma;
        BgKeyR = s.BgKeyR;
        BgKeyG = s.BgKeyG;
        BgKeyB = s.BgKeyB;
        BgLuminanceThreshold = s.BgLuminanceThreshold01;
        BgEdgeExpandPx = s.BgEdgeExpandPx;
        EraserBlurSigma = s.EraserBlurSigma;
        EraserBrushSoftness = s.EraserBrushSoftness01;
        EraserBrushRadiusPx = s.EraserBrushRadiusPx;

        _eraserStrokes.Clear();
        foreach (var st in s.EraserStrokes)
        {
            _eraserStrokes.Add(st);
        }

        OriginalPreviewImage = string.IsNullOrWhiteSpace(s.SelectedFilePath)
            ? null
            : CreatePreviewSource(s.SelectedFilePath);
        EditedPreviewImage = null;
        IsEditedPreviewBusy = false;

        global::System.Windows.Application.Current?.Dispatcher.BeginInvoke(
            RequestEditedPreviewRefresh,
            global::System.Windows.Threading.DispatcherPriority.Background);
    }

    public void EraserPointerDown()
    {
        _eraserPointerDown = true;
        _eraserDabsSinceDown = false;
        _hasLastEraserSample = false;
    }

    public void EraserPointerUp()
    {
        _eraserPointerDown = false;
        if (_eraserDabsSinceDown)
        {
            NotifyUndoableEdit();
        }

        _eraserDabsSinceDown = false;
        _hasLastEraserSample = false;
        ProcessPhotoCommand.NotifyCanExecuteChanged();
    }

    public void EraserPointerMove(double imagePixelX, double imagePixelY)
    {
        if (!_eraserPointerDown || _imagePixelWidth <= 0)
        {
            return;
        }

        var ix = Math.Clamp(imagePixelX, 0, _imagePixelWidth - 0.001);
        var iy = Math.Clamp(imagePixelY, 0, _imagePixelHeight - 0.001);

        const double minSpacing = 2.5;
        if (_hasLastEraserSample)
        {
            var dx = ix - _lastEraserImageX;
            var dy = iy - _lastEraserImageY;
            if (dx * dx + dy * dy < minSpacing * minSpacing)
            {
                return;
            }
        }

        _eraserStrokes.Add(new EraserBrushStamp(
            (float)ix,
            (float)iy,
            EraserBrushRadiusPx,
            EraserBrushSoftness));

        _hasLastEraserSample = true;
        _lastEraserImageX = ix;
        _lastEraserImageY = iy;
        _eraserDabsSinceDown = true;

        RequestEditedPreviewRefresh();
        ProcessPhotoCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    private void ClearEraserStrokes()
    {
        if (_eraserStrokes.Count == 0)
        {
            return;
        }

        _eraserStrokes.Clear();
        NotifyUndoableEdit();
        RequestEditedPreviewRefresh();
        ProcessPhotoCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    private void UndoLastEraserStroke()
    {
        if (_eraserStrokes.Count == 0)
        {
            return;
        }

        _eraserStrokes.RemoveAt(_eraserStrokes.Count - 1);
        NotifyUndoableEdit();
        RequestEditedPreviewRefresh();
        ProcessPhotoCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    private void SelectWorkspaceTab(object? parameter)
    {
        var idx = parameter switch
        {
            int i => i,
            string s when int.TryParse(s, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var j) => j,
            _ => 0
        };

        WorkspaceTabIndex = Math.Clamp(idx, 0, 2);
    }

    partial void OnWorkspaceTabIndexChanged(int value)
    {
        OnPropertyChanged(nameof(ProcessPrimaryLabel));
        RequestEditedPreviewRefresh();
        ProcessPhotoCommand.NotifyCanExecuteChanged();
    }

    partial void OnSelectedBackgroundRemovalModeChanged(BackgroundRemovalMode value) => RequestEditedPreviewRefresh();

    partial void OnBgToleranceChanged(int value) => RequestEditedPreviewRefresh();

    partial void OnBgFeatherSigmaChanged(float value) => RequestEditedPreviewRefresh();

    partial void OnBgKeyRChanged(int value) => RequestEditedPreviewRefresh();

    partial void OnBgKeyGChanged(int value) => RequestEditedPreviewRefresh();

    partial void OnBgKeyBChanged(int value) => RequestEditedPreviewRefresh();

    partial void OnBgLuminanceThresholdChanged(double value) => RequestEditedPreviewRefresh();

    partial void OnBgEdgeExpandPxChanged(int value) => RequestEditedPreviewRefresh();

    partial void OnEraserBlurSigmaChanged(float value) => RequestEditedPreviewRefresh();

    partial void OnEraserBrushSoftnessChanged(float value) => RequestEditedPreviewRefresh();

    partial void OnEraserBrushRadiusPxChanged(float value) => RequestEditedPreviewRefresh();

    partial void OnResizeIntentChanged(PhotoResizeIntent value)
    {
        OnPropertyChanged(nameof(ShowScaleControls));
        OnPropertyChanged(nameof(ShowMaxEdgeControls));
        OnSettingsChangedForPreview();
    }

    partial void OnTargetFormatChanged(RasterImageFormat value) => OnSettingsChangedForPreview();

    partial void OnEncodingQualityChanged(int value) => OnSettingsChangedForPreview();

    partial void OnScaleFactorChanged(double value) => OnSettingsChangedForPreview();

    partial void OnSelectedMaxEdgeChanged(int value) => OnSettingsChangedForPreview();

    partial void OnUpscaleQualityModeChanged(UpscaleQualityMode value) => OnSettingsChangedForPreview();

    partial void OnSelectedFilterChanged(PhotoFilterKind value) => OnSettingsChangedForPreview();

    partial void OnSelectedFilePathChanged(string? value) =>
        ProcessPhotoCommand.NotifyCanExecuteChanged();

    partial void OnIsRunningChanged(bool value)
    {
        ProcessPhotoCommand.NotifyCanExecuteChanged();
        UndoCommand.NotifyCanExecuteChanged();
        RedoCommand.NotifyCanExecuteChanged();
        if (value)
        {
            _previewCts?.Cancel();
            IsEditedPreviewBusy = false;
        }
    }

    private void RequestEditedPreviewRefresh()
    {
        if (string.IsNullOrWhiteSpace(SelectedFilePath) || !File.Exists(SelectedFilePath))
        {
            EditedPreviewImage = null;
            IsEditedPreviewBusy = false;
            return;
        }

        if (IsRunning)
        {
            return;
        }

        _previewCts?.Cancel();
        _previewCts = new CancellationTokenSource();
        var token = _previewCts.Token;
        var path = SelectedFilePath;

        switch (WorkspaceTabIndex)
        {
            case 1:
                QueueBackgroundRemovalPreview(path, token);
                return;
            case 2:
                QueueObjectEraserPreview(path, token);
                return;
            default:
                QueueEnhancePreview(path, token);
                return;
        }
    }

    private BackgroundRemovalSettings BuildBackgroundRemovalSettings() =>
        new(
            SelectedBackgroundRemovalMode,
            BgTolerance,
            BgFeatherSigma,
            (byte)Math.Clamp(BgKeyR, 0, 255),
            (byte)Math.Clamp(BgKeyG, 0, 255),
            (byte)Math.Clamp(BgKeyB, 0, 255),
            (float)BgLuminanceThreshold,
            BgEdgeExpandPx);

    private ObjectEraserSettings BuildObjectEraserSettings() =>
        new(EraserBlurSigma, EraserBrushSoftness);

    private void QueueEnhancePreview(string path, CancellationToken token)
    {
        var settings = BuildSettings();
        var version = Interlocked.Increment(ref _previewVersion);
        IsEditedPreviewBusy = true;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(280, token).ConfigureAwait(false);
                if (Volatile.Read(ref _previewVersion) != version)
                {
                    return;
                }

                var bytes = await _imageProcessingService.GetEditedPreviewPngAsync(path, settings, token).ConfigureAwait(false);
                await FinishPreviewDispatch(bytes, version, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                await ClearPreviewBusyIfVersion(version).ConfigureAwait(false);
            }
            catch
            {
                await ClearPreviewBusyIfVersion(version).ConfigureAwait(false);
            }
        }, token);
    }

    private void QueueBackgroundRemovalPreview(string path, CancellationToken token)
    {
        var bg = BuildBackgroundRemovalSettings();
        var version = Interlocked.Increment(ref _previewVersion);
        IsEditedPreviewBusy = true;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(220, token).ConfigureAwait(false);
                if (Volatile.Read(ref _previewVersion) != version)
                {
                    return;
                }

                var bytes = await _imageProcessingService.GetBackgroundRemovalPreviewPngAsync(path, bg, token).ConfigureAwait(false);
                await FinishPreviewDispatch(bytes, version, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                await ClearPreviewBusyIfVersion(version).ConfigureAwait(false);
            }
            catch
            {
                await ClearPreviewBusyIfVersion(version).ConfigureAwait(false);
            }
        }, token);
    }

    private void QueueObjectEraserPreview(string path, CancellationToken token)
    {
        var version = Interlocked.Increment(ref _previewVersion);

        if (_eraserStrokes.Count == 0)
        {
            var disp = global::System.Windows.Application.Current?.Dispatcher;
            if (disp is not null)
            {
                disp.Invoke(() =>
                {
                    if (Volatile.Read(ref _previewVersion) != version)
                    {
                        return;
                    }

                    EditedPreviewImage = OriginalPreviewImage;
                    IsEditedPreviewBusy = false;
                });
            }

            return;
        }

        var eraser = BuildObjectEraserSettings();
        var stamps = (IReadOnlyList<EraserBrushStamp>)_eraserStrokes.ToArray();
        IsEditedPreviewBusy = true;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(200, token).ConfigureAwait(false);
                if (Volatile.Read(ref _previewVersion) != version)
                {
                    return;
                }

                var bytes = await _imageProcessingService.GetObjectEraserPreviewPngAsync(path, stamps, eraser, token).ConfigureAwait(false);
                await FinishPreviewDispatch(bytes, version, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                await ClearPreviewBusyIfVersion(version).ConfigureAwait(false);
            }
            catch
            {
                await ClearPreviewBusyIfVersion(version).ConfigureAwait(false);
            }
        }, token);
    }

    private async Task FinishPreviewDispatch(byte[]? bytes, int version, CancellationToken token)
    {
        if (token.IsCancellationRequested || Volatile.Read(ref _previewVersion) != version)
        {
            return;
        }

        var disp = global::System.Windows.Application.Current?.Dispatcher;
        if (disp is null)
        {
            return;
        }

        if (bytes is null || bytes.Length == 0)
        {
            await disp.InvokeAsync(() =>
            {
                if (Volatile.Read(ref _previewVersion) != version)
                {
                    return;
                }

                IsEditedPreviewBusy = false;
            });
            return;
        }

        var bmp = CreateBitmapFromPngBytes(bytes);
        await disp.InvokeAsync(() =>
        {
            if (Volatile.Read(ref _previewVersion) != version)
            {
                return;
            }

            EditedPreviewImage = bmp;
            IsEditedPreviewBusy = false;
        });
    }

    private async Task ClearPreviewBusyIfVersion(int version)
    {
        var disp = global::System.Windows.Application.Current?.Dispatcher;
        if (disp is null)
        {
            return;
        }

        await disp.InvokeAsync(() =>
        {
            if (Volatile.Read(ref _previewVersion) == version)
            {
                IsEditedPreviewBusy = false;
            }
        });
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

    private static ImageSource? CreateBitmapFromPngBytes(byte[] data)
    {
        try
        {
            using var ms = new MemoryStream(data);
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.StreamSource = ms;
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch
        {
            return null;
        }
    }

    private static ImageSource? CreatePreviewSource(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return null;
        }

        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.UriSource = new Uri(path, UriKind.Absolute);
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.DecodePixelWidth = 720;
            bmp.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch
        {
            return null;
        }
    }
}
