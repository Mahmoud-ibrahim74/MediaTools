using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MediaTools.Application.Abstractions;
using MediaTools.Application.DTOs;
using MediaTools.Application.UseCases;
using MediaTools.Domain.Enums;
using MediaTools.Infrastructure.Services;
using MediaTools.Presentation.Services;

namespace MediaTools.Presentation.ViewModels;

public partial class FacebookVideoViewModel : ObservableObject
{
    private readonly DownloadFacebookVideoUseCase _downloadUseCase;
    private readonly IFacebookVideoService _facebookVideoService;
    private readonly IUserPreferencesService _preferences;
    private CancellationTokenSource? _downloadCts;

    public FacebookVideoViewModel(
        DownloadFacebookVideoUseCase downloadUseCase,
        IFacebookVideoService facebookVideoService,
        IUserPreferencesService preferences)
    {
        _downloadUseCase = downloadUseCase;
        _facebookVideoService = facebookVideoService;
        _preferences = preferences;
        _selectedFormat = AvailableFormats[0]; // MP4
        _selectedResolution = AvailableResolutions[0]; // Best
        _selectedQuality = AvailableQualities[0]; // High
    }

    // ── URL input ──────────────────────────────────────────

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(FetchVideoInfoCommand))]
    private string _facebookUrl = string.Empty;

    // ── Video metadata ─────────────────────────────────────

    [ObservableProperty]
    private string _videoTitle = string.Empty;

    [ObservableProperty]
    private string _authorName = string.Empty;

    [ObservableProperty]
    private string _durationDisplay = string.Empty;

    [ObservableProperty]
    private string _viewCountDisplay = string.Empty;

    [ObservableProperty]
    private BitmapImage? _thumbnailImage;

    // ── Format & quality ───────────────────────────────────

    public ObservableCollection<string> AvailableFormats { get; } =
        ["MP4", "MKV", "WebM"];

    public ObservableCollection<string> AvailableResolutions { get; } =
        ["Best", "4K (2160p)", "1440p", "1080p", "720p", "480p"];

    public ObservableCollection<string> AvailableQualities { get; } =
        ["High", "Medium", "Low"];

    [ObservableProperty]
    private string _selectedFormat;

    [ObservableProperty]
    private string _selectedResolution;

    [ObservableProperty]
    private string _selectedQuality;

    // ── State flags ────────────────────────────────────────

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(FetchVideoInfoCommand))]
    [NotifyCanExecuteChangedFor(nameof(DownloadVideoCommand))]
    private bool _isFetching;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(FetchVideoInfoCommand))]
    [NotifyCanExecuteChangedFor(nameof(DownloadVideoCommand))]
    [NotifyPropertyChangedFor(nameof(IsNotDownloading))]
    private bool _isDownloading;

    public bool IsNotDownloading => !IsDownloading;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DownloadVideoCommand))]
    private bool _showVideoInfo;

    [ObservableProperty]
    private bool _showProgressCard;

    [ObservableProperty]
    private bool _showResultCard;

    [ObservableProperty]
    private bool _showErrorCard;

    // ── Progress ───────────────────────────────────────────

    [ObservableProperty]
    private double _progressPercent;

    [ObservableProperty]
    private double _progressPercentDisplay;

    [ObservableProperty]
    private string _progressStatusText = string.Empty;

    [ObservableProperty]
    private string _downloadSpeedDisplay = string.Empty;

    [ObservableProperty]
    private string _etaDisplay = string.Empty;

    [ObservableProperty]
    private bool _isMuxing;

    // ── Result ──────────────────────────────────────────────

    [ObservableProperty]
    private string _resultMessage = string.Empty;

    [ObservableProperty]
    private string? _outputFilePath;

    [ObservableProperty]
    private string _errorMessage = string.Empty;

    [ObservableProperty]
    private string _fetchButtonText = "Fetch Info";

    // ── Commands ────────────────────────────────────────────

    private bool CanFetchVideoInfo => !string.IsNullOrWhiteSpace(FacebookUrl) && !IsFetching && !IsDownloading;

    [RelayCommand(CanExecute = nameof(CanFetchVideoInfo))]
    private async Task FetchVideoInfoAsync()
    {
        IsFetching = true;
        FetchButtonText = "Fetching…";
        ShowVideoInfo = false;
        ShowResultCard = false;
        ShowErrorCard = false;
        ErrorMessage = string.Empty;

        try
        {
            var info = await _facebookVideoService.FetchVideoInfoAsync(FacebookUrl.Trim()).ConfigureAwait(true);

            VideoTitle = info.Title;
            AuthorName = info.AuthorName;
            DurationDisplay = FormatDuration(info.Duration);
            ViewCountDisplay = FormatViewCount(info.ViewCount);

            if (!string.IsNullOrWhiteSpace(info.ThumbnailUrl))
            {
                await LoadThumbnailAsync(info.ThumbnailUrl).ConfigureAwait(true);
            }
            else
            {
                ThumbnailImage = null;
            }

            ShowVideoInfo = true;
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Could not fetch video info: {ex.Message}";
            ShowErrorCard = true;
        }
        finally
        {
            IsFetching = false;
            FetchButtonText = "Fetch Info";
        }
    }

    private bool CanDownloadVideo => ShowVideoInfo && !IsFetching && !IsDownloading;

    [RelayCommand(CanExecute = nameof(CanDownloadVideo))]
    private async Task DownloadVideoAsync()
    {
        IsDownloading = true;
        ShowResultCard = false;
        ShowErrorCard = false;
        ShowProgressCard = true;
        IsMuxing = false;
        ProgressPercent = 0;
        ProgressPercentDisplay = 0;
        ProgressStatusText = "Starting download…";
        DownloadSpeedDisplay = string.Empty;
        EtaDisplay = string.Empty;

        _downloadCts?.Dispose();
        _downloadCts = new CancellationTokenSource();

        var request = new FacebookVideoDownloadRequest(
            Url: FacebookUrl.Trim(),
            OutputFolderPath: _preferences.SaveFolderPath,
            VideoFormat: ParseFormat(SelectedFormat),
            Resolution: SelectedResolution,
            VideoQuality: SelectedQuality);

        var progress = new Progress<FacebookDownloadProgress>(report =>
        {
            ProgressPercent = report.ProgressPercent;
            ProgressPercentDisplay = Math.Round(report.ProgressPercent, 1);
            ProgressStatusText = report.StatusText;
            DownloadSpeedDisplay = report.DownloadSpeedDisplay ?? string.Empty;
            EtaDisplay = report.EtaDisplay ?? string.Empty;
            IsMuxing = report.IsMuxing;
        });

        try
        {
            var result = await _downloadUseCase
                .ExecuteAsync(request, progress, _downloadCts.Token)
                .ConfigureAwait(true);

            ShowProgressCard = false;

            if (result.IsSuccess)
            {
                OutputFilePath = result.OutputFilePath;
                ResultMessage = $"Saved: {Path.GetFileName(result.OutputFilePath)}";
                ShowResultCard = true;
            }
            else if (result.IsCancelled)
            {
                ErrorMessage = "Download was cancelled.";
                ShowErrorCard = true;
            }
            else
            {
                ErrorMessage = result.ErrorMessage ?? "Unknown error occurred.";
                ShowErrorCard = true;
            }
        }
        catch (OperationCanceledException)
        {
            ShowProgressCard = false;
            ErrorMessage = "Download was cancelled.";
            ShowErrorCard = true;
        }
        catch (Exception ex)
        {
            ShowProgressCard = false;
            ErrorMessage = ex.Message;
            ShowErrorCard = true;
        }
        finally
        {
            IsDownloading = false;
            IsMuxing = false;
        }
    }

    [RelayCommand]
    private void Cancel()
    {
        _downloadCts?.Cancel();
    }

    [RelayCommand]
    private void OpenOutputFolder()
    {
        var folder = OutputFilePath;
        if (folder is not null && File.Exists(folder))
        {
            folder = Path.GetDirectoryName(folder);
        }

        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
        {
            folder = _preferences.SaveFolderPath;
        }

        if (!string.IsNullOrWhiteSpace(folder) && Directory.Exists(folder))
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = folder,
                UseShellExecute = true
            });
        }
    }

    [RelayCommand]
    private void PasteFromClipboard()
    {
        try
        {
            if (Clipboard.ContainsText())
            {
                var text = Clipboard.GetText().Trim();
                if (YtDlpFacebookVideoService.IsValidFacebookUrl(text))
                {
                    FacebookUrl = text;
                }
            }
        }
        catch
        {
            // Clipboard access can fail when locked by another process.
        }
    }

    // ── Helpers ──────────────────────────────────────────────

    private async Task LoadThumbnailAsync(string url)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            var data = await http.GetByteArrayAsync(url).ConfigureAwait(true);
            var image = new BitmapImage();
            image.BeginInit();
            image.StreamSource = new MemoryStream(data);
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.DecodePixelWidth = 480;
            image.EndInit();
            image.Freeze();
            ThumbnailImage = image;
        }
        catch
        {
            ThumbnailImage = null;
        }
    }

    private static string FormatDuration(TimeSpan ts)
    {
        if (ts.TotalHours >= 1)
        {
            return ts.ToString(@"h\:mm\:ss");
        }

        return ts.ToString(@"m\:ss");
    }

    private static string FormatViewCount(long views)
    {
        return views switch
        {
            >= 1_000_000_000 => $"{views / 1_000_000_000d:0.#}B views",
            >= 1_000_000 => $"{views / 1_000_000d:0.#}M views",
            >= 1_000 => $"{views / 1_000d:0.#}K views",
            _ when views > 0 => $"{views:N0} views",
            _ => "Facebook Video"
        };
    }

    private static FacebookVideoFormat ParseFormat(string display) =>
        display switch
        {
            "MP4" => FacebookVideoFormat.Mp4,
            "MKV" => FacebookVideoFormat.Mkv,
            "WebM" => FacebookVideoFormat.Webm,
            _ => FacebookVideoFormat.Mp4
        };
}
