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
    private readonly UndoRedoHost<PhotoEnhancerUndoSnapshot> _history;
    private CancellationTokenSource? _cts;
    private CancellationTokenSource? _previewCts;
    private int _previewVersion;
    private bool _suppressUndoNotification;

    public PhotoEnhancerViewModel(
        ProcessPhotoUseCase processPhotoUseCase,
        IImageProcessingService imageProcessingService,
        IUserPreferencesService preferences)
    {
        _processPhotoUseCase = processPhotoUseCase;
        _imageProcessingService = imageProcessingService;
        _preferences = preferences;
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

    public bool ShowFileInfoCard => !string.IsNullOrWhiteSpace(SelectedFilePath);

    public bool ShowProgressCard => IsRunning || FinishedAttempt;

    public bool ShowResultCard => Succeeded;

    public bool ShowCancelButton => IsRunning;

    public bool ShowScaleControls => ResizeIntent == PhotoResizeIntent.ScaleByFactor;

    public bool ShowMaxEdgeControls => ResizeIntent == PhotoResizeIntent.FitMaxEdge;

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
        ProgressStatusText = "Processing…";
        ProgressDetailText = string.Empty;
        ResultMessage = string.Empty;

        var settings = BuildSettings();
        var ext = ExtensionFor(settings.TargetFormat);
        var outputPath = Path.Combine(
            _preferences.SaveFolderPath,
            Path.GetFileNameWithoutExtension(SelectedFilePath) + "_enhanced" + ext);

        var request = new ProcessPhotoRequest(SelectedFilePath, outputPath, settings);

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

        try
        {
            var result = await _processPhotoUseCase.ExecuteAsync(request, progress, token).ConfigureAwait(true);

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
            }
            else
            {
                Succeeded = false;
                ProgressStatusText = "Failed";
                ProgressDetailText = result.ErrorMessage ?? "Unknown error";
                ResultMessage = result.ErrorMessage ?? "Processing failed.";
            }
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
            ResultMessage);

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

        OriginalPreviewImage = string.IsNullOrWhiteSpace(s.SelectedFilePath)
            ? null
            : CreatePreviewSource(s.SelectedFilePath);
        EditedPreviewImage = null;
        IsEditedPreviewBusy = false;

        global::System.Windows.Application.Current?.Dispatcher.BeginInvoke(
            RequestEditedPreviewRefresh,
            global::System.Windows.Threading.DispatcherPriority.Background);
    }

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

        var path = SelectedFilePath;
        var settings = BuildSettings();
        _previewCts?.Cancel();
        _previewCts = new CancellationTokenSource();
        var token = _previewCts.Token;
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
            catch (OperationCanceledException)
            {
                var disp = global::System.Windows.Application.Current?.Dispatcher;
                if (disp is not null)
                {
                    await disp.InvokeAsync(() =>
                    {
                        if (Volatile.Read(ref _previewVersion) == version)
                        {
                            IsEditedPreviewBusy = false;
                        }
                    });
                }
            }
            catch
            {
                var disp = global::System.Windows.Application.Current?.Dispatcher;
                if (disp is not null)
                {
                    await disp.InvokeAsync(() =>
                    {
                        if (Volatile.Read(ref _previewVersion) == version)
                        {
                            IsEditedPreviewBusy = false;
                        }
                    });
                }
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
