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

public partial class YouTubeAudioViewModel : ObservableObject
{
    private readonly DownloadYouTubeAudioUseCase _downloadUseCase;
    private readonly IYouTubeAudioService _youTubeAudio;
    private readonly IUserPreferencesService _preferences;
    private CancellationTokenSource? _downloadCts;
    private bool _isPlaylist;

    public YouTubeAudioViewModel(
        DownloadYouTubeAudioUseCase downloadUseCase,
        IYouTubeAudioService youTubeAudio,
        IUserPreferencesService preferences)
    {
        _downloadUseCase = downloadUseCase;
        _youTubeAudio = youTubeAudio;
        _preferences = preferences;
        _selectedFormat = AvailableFormats[0];
        _selectedBitrate = AvailableBitrates[2]; // 256 kbps default
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

    // ── Format & quality ───────────────────────────────────

    public ObservableCollection<string> AvailableFormats { get; } =
        ["MP3", "AAC (M4A)", "FLAC", "WAV", "OGG (Vorbis)", "OPUS"];

    public ObservableCollection<int> AvailableBitrates { get; } =
        [128, 192, 256, 320];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowBitrateSelector))]
    private string _selectedFormat;

    [ObservableProperty]
    private int _selectedBitrate;

    public bool ShowBitrateSelector =>
        SelectedFormat is "MP3" or "AAC (M4A)" or "OGG (Vorbis)" or "OPUS";

    // ── State flags ────────────────────────────────────────

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(FetchVideoInfoCommand))]
    [NotifyCanExecuteChangedFor(nameof(DownloadAudioCommand))]
    private bool _isFetching;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(FetchVideoInfoCommand))]
    [NotifyCanExecuteChangedFor(nameof(DownloadAudioCommand))]
    private bool _isDownloading;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DownloadAudioCommand))]
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
            var info = await _youTubeAudio.FetchVideoInfoAsync(YoutubeUrl.Trim()).ConfigureAwait(true);

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

    private bool CanDownloadAudio => ShowVideoInfo && !IsFetching && !IsDownloading;

    [RelayCommand(CanExecute = nameof(CanDownloadAudio))]
    private async Task DownloadAudioAsync()
    {
        IsDownloading = true;
        ShowResultCard = false;
        ShowErrorCard = false;

        _downloadCts?.Dispose();
        _downloadCts = new CancellationTokenSource();

        if (_isPlaylist && PlaylistItems.Count > 0)
        {
            ShowProgressCard = false;
            foreach (var item in PlaylistItems)
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

        var request = new YouTubeDownloadRequest(
            Url: YoutubeUrl.Trim(),
            OutputFolderPath: _preferences.SaveFolderPath,
            AudioFormat: ParseFormat(SelectedFormat),
            BitrateKbps: SelectedBitrate,
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

        var request = new YouTubeDownloadRequest(
            Url: vm.Url,
            OutputFolderPath: _preferences.SaveFolderPath,
            AudioFormat: ParseFormat(SelectedFormat),
            BitrateKbps: SelectedBitrate,
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

    private static YouTubeAudioFormat ParseFormat(string display) =>
        display switch
        {
            "MP3" => YouTubeAudioFormat.Mp3,
            "AAC (M4A)" => YouTubeAudioFormat.Aac,
            "FLAC" => YouTubeAudioFormat.Flac,
            "WAV" => YouTubeAudioFormat.Wav,
            "OGG (Vorbis)" => YouTubeAudioFormat.Ogg,
            "OPUS" => YouTubeAudioFormat.Opus,
            _ => YouTubeAudioFormat.Mp3
        };

    private static bool IsYouTubeUrl(string text) =>
        Regex.IsMatch(text, @"^https?://(www\.)?(youtube\.com|youtu\.be|music\.youtube\.com)/", RegexOptions.IgnoreCase);
}
