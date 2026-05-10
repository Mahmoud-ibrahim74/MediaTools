using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
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

public partial class ThumbnailGeneratorViewModel : ObservableObject
{
    private static readonly HashSet<string> AllowedExtensions = BuildAllowedExtensions();

    private readonly ProcessThumbnailUseCase _processThumbnailUseCase;
    private readonly IThumbnailGeneratorService _thumbnailGeneratorService;
    private readonly IUserPreferencesService _preferences;
    private readonly IWindowsToastNotificationService _toastNotifications;
    private readonly UndoRedoHost<ThumbnailGeneratorUndoSnapshot> _history;
    private CancellationTokenSource? _cts;
    private bool _suppressUndoNotification;

    public ThumbnailGeneratorViewModel(
        ProcessThumbnailUseCase processThumbnailUseCase,
        IThumbnailGeneratorService thumbnailGeneratorService,
        IUserPreferencesService preferences,
        IWindowsToastNotificationService toastNotifications)
    {
        _processThumbnailUseCase = processThumbnailUseCase;
        _thumbnailGeneratorService = thumbnailGeneratorService;
        _preferences = preferences;
        _toastNotifications = toastNotifications;
        _preferences.SaveFolderPathChanged += OnSaveFolderPathChanged;

        foreach (var e in MaxEdgePresets)
        {
            MaxEdgeOptions.Add(e);
        }

        _history = new UndoRedoHost<ThumbnailGeneratorUndoSnapshot>(
            CaptureSnapshot,
            ApplySnapshot,
            CaptureSnapshot(),
            OnUndoRedoHistoryChanged);
    }

    private static HashSet<string> BuildAllowedExtensions()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in new[]
                 {
                     ".png", ".jpg", ".jpeg", ".webp", ".bmp", ".gif", ".tif", ".tiff",
                     ".mp4", ".mkv", ".avi", ".mov", ".wmv", ".flv", ".m4v", ".webm"
                 })
        {
            set.Add(e);
        }

        return set;
    }

    private void OnSaveFolderPathChanged(object? sender, EventArgs e) =>
        GenerateThumbnailCommand.NotifyCanExecuteChanged();

    private static int[] MaxEdgePresets => [320, 480, 640, 1280, 1920];

    public ObservableCollection<int> MaxEdgeOptions { get; } = [];

    public IEnumerable<ThumbnailOutputFormat> OutputFormats => Enum.GetValues<ThumbnailOutputFormat>();

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
    private string _dimensionsDisplay = string.Empty;

    [ObservableProperty]
    private string _formatDisplay = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowVideoTimeControls))]
    [NotifyPropertyChangedFor(nameof(VideoTimeOffsetMaximum))]
    private bool _isSourceVideo;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowVideoTimeControls))]
    [NotifyPropertyChangedFor(nameof(VideoTimeOffsetMaximum))]
    private double _sourceDurationSeconds;

    [ObservableProperty]
    private ImageSource? _sourcePreviewImage;

    [ObservableProperty]
    private int _selectedMaxEdge = 640;

    [ObservableProperty]
    private ThumbnailOutputFormat _outputFormat = ThumbnailOutputFormat.Jpeg;

    [ObservableProperty]
    private int _jpegWebpQuality = 85;

    [ObservableProperty]
    private double _videoTimeOffsetSeconds = 1;

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

    public bool ShowVideoTimeControls => IsSourceVideo;

    public bool ShowLossyQualityControls =>
        OutputFormat is ThumbnailOutputFormat.Jpeg or ThumbnailOutputFormat.Webp;

    public double VideoTimeOffsetMaximum =>
        !IsSourceVideo
            ? 1
            : SourceDurationSeconds > 0.1
                ? Math.Max(0, SourceDurationSeconds - 0.05)
                : 3600;

    public int ProgressPercentDisplay => (int)Math.Round(ProgressPercent01 * 100, MidpointRounding.AwayFromZero);

    private bool CanStartGenerate() =>
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

            if (AllowedExtensions.Contains(ext))
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
                "Images & video|*.png;*.jpg;*.jpeg;*.webp;*.bmp;*.gif;*.tif;*.tiff;*.mp4;*.mkv;*.avi;*.mov;*.wmv;*.flv;*.m4v;*.webm|All files|*.*"
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
            DimensionsDisplay = string.Empty;
            FormatDisplay = string.Empty;
            IsSourceVideo = false;
            SourceDurationSeconds = 0;
            SourcePreviewImage = null;
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

    [RelayCommand(CanExecute = nameof(CanStartGenerate))]
    private async Task GenerateThumbnailAsync()
    {
        if (SelectedFilePath is null || !CanStartGenerate())
        {
            return;
        }

        _cts = new CancellationTokenSource();
        var token = _cts.Token;

        IsRunning = true;
        FinishedAttempt = false;
        Succeeded = false;
        ProgressPercent01 = 0;
        ProgressStatusText = "Generating thumbnail…";
        ProgressDetailText = string.Empty;
        ResultMessage = string.Empty;

        var settings = BuildSettings();
        var ext = ExtensionFor(settings.OutputFormat);
        var outputPath = Path.Combine(
            _preferences.SaveFolderPath,
            Path.GetFileNameWithoutExtension(SelectedFilePath) + "_thumb" + ext);

        var request = new ProcessThumbnailRequest(SelectedFilePath, outputPath, settings);

        var dispatcher = global::System.Windows.Application.Current?.Dispatcher;

        var progress = new Progress<ThumbnailProgressReport>(r =>
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
            var result = await _processThumbnailUseCase.ExecuteAsync(request, progress, token).ConfigureAwait(true);

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
                toastTitle = "Thumbnail saved";
                toastBody = $"{Path.GetFileName(outputPath)} · {FormatBytes(len)}";
                toastSuccess = true;
            }
            else if (result.IsSuccess)
            {
                Succeeded = false;
                ProgressStatusText = "Failed";
                ProgressDetailText = "Output file was not created.";
                ResultMessage = ProgressDetailText;
                toastTitle = "Thumbnail failed";
                toastBody = ResultMessage;
                toastSuccess = false;
            }
            else
            {
                Succeeded = false;
                ProgressStatusText = "Failed";
                ProgressDetailText = result.ErrorMessage ?? "Unknown error";
                ResultMessage = result.ErrorMessage ?? "Processing failed.";
                toastTitle = "Thumbnail failed";
                toastBody = ResultMessage;
                toastSuccess = false;
            }
        }
        finally
        {
            IsRunning = false;
            FinishedAttempt = true;
            GenerateThumbnailCommand.NotifyCanExecuteChanged();
            UndoCommand.NotifyCanExecuteChanged();
            RedoCommand.NotifyCanExecuteChanged();
            _history.FlushPendingEdit();

            if (toastTitle is not null)
            {
                _toastNotifications.ShowToolFinished(
                    toastTitle,
                    toastBody ?? string.Empty,
                    toastSuccess,
                    "Thumbnail Generator");
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

    private ThumbnailGeneratorSettings BuildSettings()
    {
        var maxEdge = Math.Clamp(SelectedMaxEdge, 32, 8192);
        var quality = Math.Clamp(JpegWebpQuality, 1, 100);
        var t = VideoTimeOffsetSeconds;
        if (IsSourceVideo && SourceDurationSeconds > 0)
        {
            t = Math.Clamp(t, 0, Math.Max(0, SourceDurationSeconds - 0.001));
        }
        else if (IsSourceVideo)
        {
            t = Math.Max(0, t);
        }

        return new ThumbnailGeneratorSettings(maxEdge, quality, t, OutputFormat);
    }

    private static string ExtensionFor(ThumbnailOutputFormat format) =>
        format switch
        {
            ThumbnailOutputFormat.Jpeg => ".jpg",
            ThumbnailOutputFormat.Png => ".png",
            ThumbnailOutputFormat.Webp => ".webp",
            _ => ".jpg"
        };

    private async Task<bool> LoadFileAsync(string path)
    {
        try
        {
            var analysis = await _thumbnailGeneratorService.AnalyzeAsync(path).ConfigureAwait(true);
            SelectedFilePath = path;
            FileDisplayName = analysis.FileName;
            FileSizeDisplay = FormatBytes(analysis.FileSizeBytes);
            DimensionsDisplay = analysis.MediaWidth is { } w && analysis.MediaHeight is { } h
                ? $"{w} × {h}"
                : "—";
            IsSourceVideo = analysis.IsVideo;
            SourceDurationSeconds = analysis.Duration?.TotalSeconds ?? 0;
            DurationDisplay = analysis.IsVideo
                ? analysis.Duration is { } d
                    ? FormatDuration(d)
                    : "Unknown"
                : "—";
            FormatDisplay = analysis.FormatHint;
            SourcePreviewImage = !analysis.IsVideo ? CreatePreviewSource(path) : null;

            OnPropertyChanged(nameof(VideoTimeOffsetMaximum));
            if (analysis.IsVideo)
            {
                var max = VideoTimeOffsetMaximum;
                VideoTimeOffsetSeconds = Math.Clamp(VideoTimeOffsetSeconds, 0, max > 0 ? max : 3600);
                if (max <= 0 && VideoTimeOffsetSeconds <= 0)
                {
                    VideoTimeOffsetSeconds = 0;
                }
            }
            else
            {
                VideoTimeOffsetSeconds = 0;
            }

            FinishedAttempt = false;
            Succeeded = false;
            return true;
        }
        catch (Exception ex)
        {
            SourcePreviewImage = null;
            MessageBoxHelper.ShowWarning($"Could not analyze file: {ex.Message}");
            return false;
        }
    }

    private ThumbnailGeneratorUndoSnapshot CaptureSnapshot() =>
        new(
            SelectedFilePath,
            FileDisplayName,
            FileSizeDisplay,
            DurationDisplay,
            DimensionsDisplay,
            FormatDisplay,
            IsSourceVideo,
            SourceDurationSeconds,
            SelectedMaxEdge,
            JpegWebpQuality,
            VideoTimeOffsetSeconds,
            OutputFormat,
            ProgressPercent01,
            ProgressStatusText,
            ProgressDetailText,
            FinishedAttempt,
            Succeeded,
            ResultMessage);

    private void ApplySnapshot(ThumbnailGeneratorUndoSnapshot s)
    {
        SelectedFilePath = s.SelectedFilePath;
        FileDisplayName = s.FileDisplayName;
        FileSizeDisplay = s.FileSizeDisplay;
        DurationDisplay = s.DurationDisplay;
        DimensionsDisplay = s.DimensionsDisplay;
        FormatDisplay = s.FormatDisplay;
        IsSourceVideo = s.IsSourceVideo;
        SourceDurationSeconds = s.SourceDurationSeconds;
        SelectedMaxEdge = s.MaxEdgePixels;
        JpegWebpQuality = s.JpegWebpQuality;
        VideoTimeOffsetSeconds = s.VideoTimeOffsetSeconds;
        OutputFormat = s.OutputFormat;
        ProgressPercent01 = s.ProgressPercent01;
        ProgressStatusText = s.ProgressStatusText;
        ProgressDetailText = s.ProgressDetailText;
        FinishedAttempt = s.FinishedAttempt;
        Succeeded = s.Succeeded;
        ResultMessage = s.ResultMessage;

        OnPropertyChanged(nameof(VideoTimeOffsetMaximum));
        SourcePreviewImage = string.IsNullOrWhiteSpace(s.SelectedFilePath) || s.IsSourceVideo
            ? null
            : CreatePreviewSource(s.SelectedFilePath);

        OnPropertyChanged(nameof(ShowLossyQualityControls));
        OnPropertyChanged(nameof(ShowVideoTimeControls));
    }

    partial void OnOutputFormatChanged(ThumbnailOutputFormat value)
    {
        OnPropertyChanged(nameof(ShowLossyQualityControls));
        NotifyUndoableEdit();
    }

    partial void OnSelectedMaxEdgeChanged(int value) => NotifyUndoableEdit();

    partial void OnJpegWebpQualityChanged(int value) => NotifyUndoableEdit();

    partial void OnVideoTimeOffsetSecondsChanged(double value) => NotifyUndoableEdit();

    partial void OnSelectedFilePathChanged(string? value) =>
        GenerateThumbnailCommand.NotifyCanExecuteChanged();

    partial void OnIsRunningChanged(bool value)
    {
        GenerateThumbnailCommand.NotifyCanExecuteChanged();
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
