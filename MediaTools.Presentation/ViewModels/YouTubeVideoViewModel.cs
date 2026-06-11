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
using MediaTools.Presentation.Services;

namespace MediaTools.Presentation.ViewModels;

public partial class YouTubeVideoViewModel : ObservableObject
{
    private readonly DownloadYouTubeVideoUseCase _downloadUseCase;
    private readonly IYouTubeVideoService _youTubeVideo;
    private readonly IUserPreferencesService _preferences;
    private CancellationTokenSource? _downloadCts;
    private bool _isPlaylist;

    public YouTubeVideoViewModel(
        DownloadYouTubeVideoUseCase downloadUseCase,
        IYouTubeVideoService youTubeVideo,
        IUserPreferencesService preferences)
    {
        _downloadUseCase = downloadUseCase;
        _youTubeVideo = youTubeVideo;
        _preferences = preferences;
        _selectedFormat = AvailableFormats[0];
        _selectedResolution = AvailableResolutions[0]; // Best default
    }

    // ── URL input ──────────────────────────────────────────

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(FetchVideoInfoCommand))]
    private string _youtubeUrl = string.Empty;

    // ── Video metadata ─────────────────────────────────────

    [ObservableProperty]
    private string _videoTitle = string.Empty;

    [ObservableProperty]
    private string _channelName = string.Empty;

    [ObservableProperty]
    private string _durationDisplay = string.Empty;

    [ObservableProperty]
    private string _viewCountDisplay = string.Empty;

    [ObservableProperty]
    private BitmapImage? _thumbnailImage;

    public ObservableCollection<PlaylistItemViewModel> PlaylistItems { get; } = [];

    [ObservableProperty]
    private bool _showPlaylistItems;

    [ObservableProperty]
    private bool _selectAllItems = true;

    partial void OnSelectAllItemsChanged(bool value)
    {
        foreach (var item in PlaylistItems)
        {
            item.IsSelected = value;
        }
    }

    // ── Format & quality ───────────────────────────────────

    public ObservableCollection<string> AvailableFormats { get; } =
        ["MP4", "MKV", "WebM"];

    public ObservableCollection<string> AvailableResolutions { get; } =
        ["Best", "4K (2160p)", "1440p", "1080p", "720p", "480p"];

    [ObservableProperty]
    private string _selectedFormat;

    [ObservableProperty]
    private string _selectedResolution;

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
    private string _progressStatusText = string.Empty;

    [ObservableProperty]
    private string _downloadSpeedDisplay = string.Empty;

    [ObservableProperty]
    private string _etaDisplay = string.Empty;

    [ObservableProperty]
    private double _progressPercentDisplay;

    // ── Result ──────────────────────────────────────────────

    [ObservableProperty]
    private string _resultMessage = string.Empty;

    [ObservableProperty]
    private string? _outputFilePath;

    [ObservableProperty]
    private string _errorMessage = string.Empty;

    // ── Fetch info button tooltip ───────────────────────────

    [ObservableProperty]
    private string _fetchButtonText = "Fetch Info";

    // ── Commands ────────────────────────────────────────────

    private bool CanFetchVideoInfo => !string.IsNullOrWhiteSpace(YoutubeUrl) && !IsFetching && !IsDownloading;

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
            var info = await _youTubeVideo.FetchVideoInfoAsync(YoutubeUrl.Trim()).ConfigureAwait(true);

            _isPlaylist = info.IsPlaylist;
            VideoTitle = info.Title;
            ChannelName = info.ChannelName;

            if (info.IsPlaylist)
            {
                DurationDisplay = $"{info.VideoCount} videos";
                ViewCountDisplay = "Playlist";
                PlaylistItems.Clear();
                if (info.PlaylistItems != null)
                {
                    foreach (var item in info.PlaylistItems)
                    {
                        PlaylistItems.Add(new PlaylistItemViewModel(
                            item.Title,
                            item.Url,
                            item.ChannelName ?? ChannelName,
                            item.Duration,
                            async (vm) => await DownloadItemCommand(vm).ConfigureAwait(false)));
                    }
                }
                SelectAllItems = true; // reset
                ShowPlaylistItems = PlaylistItems.Count > 0;
            }
            else
            {
                DurationDisplay = FormatDuration(info.Duration);
                ViewCountDisplay = FormatViewCount(info.ViewCount);
                PlaylistItems.Clear();
                ShowPlaylistItems = false;
            }

            // Load thumbnail
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

        _downloadCts?.Dispose();
        _downloadCts = new CancellationTokenSource();

        if (_isPlaylist && PlaylistItems.Count > 0)
        {
            var selectedItems = PlaylistItems.Where(i => i.IsSelected).ToList();
            if (selectedItems.Count == 0)
            {
                ResultMessage = "No videos selected.";
                ShowResultCard = true;
                IsDownloading = false;
                return;
            }

            ShowProgressCard = false;
            foreach (var item in selectedItems)
            {
                if (_downloadCts.Token.IsCancellationRequested)
                    break;
                
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
                    _downloadCts.Token, 
                    item.GetNewToken());

                await DownloadSingleVideoAsync(item, linkedCts.Token).ConfigureAwait(true);
            }
            
            if (!_downloadCts.Token.IsCancellationRequested)
            {
                ResultMessage = "All downloads complete!";
                ShowResultCard = true;
            }
            IsDownloading = false;
            return;
        }

        ShowProgressCard = true;
        ProgressPercent = 0;
        ProgressPercentDisplay = 0;
        ProgressStatusText = "Starting download…";
        DownloadSpeedDisplay = string.Empty;
        EtaDisplay = string.Empty;

        var request = new YouTubeVideoDownloadRequest(
            Url: YoutubeUrl.Trim(),
            OutputFolderPath: _preferences.SaveFolderPath,
            VideoFormat: ParseFormat(SelectedFormat),
            Resolution: SelectedResolution,
            IsPlaylist: false);

        var progress = new Progress<YouTubeDownloadProgress>(report =>
        {
            ProgressPercent = report.ProgressPercent;
            ProgressPercentDisplay = Math.Round(report.ProgressPercent);
            ProgressStatusText = report.StatusText;
            DownloadSpeedDisplay = report.DownloadSpeedDisplay ?? string.Empty;
            EtaDisplay = report.EtaDisplay ?? string.Empty;
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
        }
    }

    private async Task DownloadItemCommand(PlaylistItemViewModel vm)
    {
        if (IsDownloading && _isPlaylist) return; // Ignore if global download is active
        
        IsDownloading = true;
        await DownloadSingleVideoAsync(vm, vm.GetNewToken()).ConfigureAwait(true);
        IsDownloading = false;
    }

    private async Task DownloadSingleVideoAsync(PlaylistItemViewModel vm, CancellationToken? externalToken = null)
    {
        vm.IsDownloading = true;
        vm.IsSuccess = false;
        vm.IsError = false;
        vm.ProgressPercent = 0;
        vm.ProgressPercentDisplay = 0;
        vm.ProgressStatusText = "Starting download…";

        var request = new YouTubeVideoDownloadRequest(
            Url: vm.Url,
            OutputFolderPath: _preferences.SaveFolderPath,
            VideoFormat: ParseFormat(SelectedFormat),
            Resolution: SelectedResolution,
            IsPlaylist: false);

        var progress = new Progress<YouTubeDownloadProgress>(report =>
        {
            vm.ProgressPercent = report.ProgressPercent;
            vm.ProgressPercentDisplay = Math.Round(report.ProgressPercent);
            vm.ProgressStatusText = report.StatusText;
        });

        var token = externalToken ?? _downloadCts?.Token ?? default;

        try
        {
            var result = await _downloadUseCase
                .ExecuteAsync(request, progress, token)
                .ConfigureAwait(true);

            if (result.IsSuccess)
            {
                vm.IsSuccess = true;
                vm.ProgressStatusText = "Complete";
                vm.ProgressPercent = 100;
                vm.ProgressPercentDisplay = 100;
            }
            else
            {
                vm.IsError = true;
                vm.ErrorMessage = result.IsCancelled ? "Cancelled" : (result.ErrorMessage ?? "Error");
                vm.ProgressStatusText = "Error";
            }
        }
        catch (Exception ex)
        {
            vm.IsError = true;
            vm.ErrorMessage = ex.Message;
            vm.ProgressStatusText = "Error";
        }
        finally
        {
            vm.IsDownloading = false;
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
                if (IsYouTubeUrl(text))
                {
                    YoutubeUrl = text;
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
            _ => $"{views:N0} views"
        };
    }

    private static YouTubeVideoFormat ParseFormat(string display) =>
        display switch
        {
            "MP4" => YouTubeVideoFormat.Mp4,
            "MKV" => YouTubeVideoFormat.Mkv,
            "WebM" => YouTubeVideoFormat.Webm,
            _ => YouTubeVideoFormat.Mp4
        };

    private static bool IsYouTubeUrl(string text) =>
        Regex.IsMatch(text, @"^https?://(www\.)?(youtube\.com|youtu\.be|music\.youtube\.com)/", RegexOptions.IgnoreCase);
}
