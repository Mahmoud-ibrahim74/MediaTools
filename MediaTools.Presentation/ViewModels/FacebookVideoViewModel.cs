using System.Collections.ObjectModel;
using System.IO;
using System.Net.Http;
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
    private CancellationTokenSource? _queueCts;

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

    // ── Observable Collections & Queue ──────────────────────

    public ObservableCollection<FacebookQueueItemViewModel> QueueItems { get; } = [];

    public ObservableCollection<string> AvailableFormats { get; } =
        ["MP4", "MKV", "WebM"];

    public ObservableCollection<string> AvailableResolutions { get; } =
        ["Best", "4K (2160p)", "1440p", "1080p", "720p", "480p"];

    public ObservableCollection<string> AvailableQualities { get; } =
        ["High", "Medium", "Low"];

    // ── Bindable Properties ─────────────────────────────────

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddToQueueCommand))]
    private string _facebookUrl = string.Empty;

    [ObservableProperty]
    private string _selectedFormat;

    [ObservableProperty]
    private string _selectedResolution;

    [ObservableProperty]
    private string _selectedQuality;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartQueueCommand))]
    [NotifyCanExecuteChangedFor(nameof(AddToQueueCommand))]
    [NotifyPropertyChangedFor(nameof(IsNotQueueRunning))]
    private bool _isQueueRunning;

    public bool IsNotQueueRunning => !IsQueueRunning;

    [ObservableProperty]
    private string _queueStatusMessage = "Queue is idle";

    [ObservableProperty]
    private double _overallProgressPercent;

    [ObservableProperty]
    private int _completedCount;

    [ObservableProperty]
    private int _totalQueueCount;

    [ObservableProperty]
    private bool _isEmptyQueue = true;

    [ObservableProperty]
    private bool _hasQueueItems;

    [ObservableProperty]
    private bool _hasInputError;

    [ObservableProperty]
    private string _inputErrorMessage = string.Empty;

    [ObservableProperty]
    private bool _showCooldownNotice;

    [ObservableProperty]
    private string _cooldownMessage = string.Empty;

    // ── Commands ────────────────────────────────────────────

    private bool CanAddToQueue => !string.IsNullOrWhiteSpace(FacebookUrl) && !IsQueueRunning;

    [RelayCommand(CanExecute = nameof(CanAddToQueue))]
    private void AddToQueue()
    {
        HasInputError = false;
        InputErrorMessage = string.Empty;

        if (string.IsNullOrWhiteSpace(FacebookUrl))
        {
            return;
        }

        // Split by lines or spaces to support batch URL addition
        var rawUrls = FacebookUrl.Split(['\r', '\n', ',', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var addedCount = 0;
        var invalidCount = 0;

        foreach (var rawUrl in rawUrls)
        {
            if (YtDlpFacebookVideoService.IsValidFacebookUrl(rawUrl))
            {
                // Check if already in queue
                if (QueueItems.Any(item => item.Url.Equals(rawUrl, StringComparison.OrdinalIgnoreCase) && item.Status is not (FacebookQueueItemStatus.Completed or FacebookQueueItemStatus.Failed or FacebookQueueItemStatus.Cancelled)))
                {
                    continue;
                }

                var queueItem = new FacebookQueueItemViewModel(
                    url: rawUrl,
                    format: ParseFormat(SelectedFormat),
                    resolution: SelectedResolution,
                    quality: SelectedQuality,
                    onRemoveRequested: RemoveQueueItem);

                QueueItems.Add(queueItem);
                addedCount++;
            }
            else
            {
                invalidCount++;
            }
        }

        if (addedCount > 0)
        {
            FacebookUrl = string.Empty;
            UpdateQueueStats();
            QueueStatusMessage = $"{addedCount} video(s) added to queue.";
        }

        if (invalidCount > 0)
        {
            HasInputError = true;
            InputErrorMessage = $"{invalidCount} URL(s) were not recognized as valid Facebook video links.";
        }

        StartQueueCommand.NotifyCanExecuteChanged();
    }

    private bool CanStartQueue => QueueItems.Any(i => i.Status is FacebookQueueItemStatus.Queued or FacebookQueueItemStatus.Failed) && !IsQueueRunning;

    [RelayCommand(CanExecute = nameof(CanStartQueue))]
    private async Task StartQueueAsync()
    {
        if (IsQueueRunning) return;

        IsQueueRunning = true;
        ShowCooldownNotice = false;
        _queueCts?.Dispose();
        _queueCts = new CancellationTokenSource();
        var token = _queueCts.Token;

        // Build FIFO Queue data structure to ensure strictly sequential URL-by-URL processing
        var downloadQueue = new Queue<FacebookQueueItemViewModel>(
            QueueItems.Where(i => i.Status is FacebookQueueItemStatus.Queued or FacebookQueueItemStatus.Failed));

        if (downloadQueue.Count == 0)
        {
            IsQueueRunning = false;
            return;
        }

        var initialQueueTotal = downloadQueue.Count;
        var processedSoFar = 0;

        try
        {
            while (downloadQueue.Count > 0)
            {
                token.ThrowIfCancellationRequested();

                // 1. Dequeue next URL
                var currentItem = downloadQueue.Dequeue();
                processedSoFar++;

                QueueStatusMessage = $"Processing {processedSoFar} of {initialQueueTotal}…";

                // 2. Fetch Video Metadata
                currentItem.Status = FacebookQueueItemStatus.FetchingInfo;
                currentItem.ProgressStatusText = "Fetching info…";

                try
                {
                    var info = await _facebookVideoService.FetchVideoInfoAsync(currentItem.Url, token).ConfigureAwait(true);
                    currentItem.Title = info.Title;
                    currentItem.AuthorName = info.AuthorName;
                    currentItem.DurationDisplay = FormatDuration(info.Duration);

                    if (!string.IsNullOrWhiteSpace(info.ThumbnailUrl))
                    {
                        currentItem.ThumbnailImage = await LoadThumbnailAsync(info.ThumbnailUrl).ConfigureAwait(true);
                    }
                }
                catch (OperationCanceledException)
                {
                    currentItem.Status = FacebookQueueItemStatus.Cancelled;
                    currentItem.ProgressStatusText = "Cancelled";
                    break;
                }
                catch (Exception ex)
                {
                    currentItem.Status = FacebookQueueItemStatus.Failed;
                    currentItem.ErrorMessage = ex.Message;
                    currentItem.ProgressStatusText = "Metadata error";
                    UpdateQueueStats();
                    continue; // Proceed to next in queue
                }

                // 3. Download & DASH Merge
                currentItem.Status = FacebookQueueItemStatus.Downloading;
                currentItem.ProgressPercent = 0;
                currentItem.ProgressPercentDisplay = 0;
                currentItem.ProgressStatusText = "Starting download…";

                var request = new FacebookVideoDownloadRequest(
                    Url: currentItem.Url,
                    OutputFolderPath: _preferences.SaveFolderPath,
                    VideoFormat: currentItem.Format,
                    Resolution: currentItem.Resolution,
                    VideoQuality: currentItem.Quality);

                var progress = new Progress<FacebookDownloadProgress>(report =>
                {
                    currentItem.ProgressPercent = report.ProgressPercent;
                    currentItem.ProgressPercentDisplay = Math.Round(report.ProgressPercent, 1);
                    currentItem.ProgressStatusText = report.StatusText;
                    currentItem.DownloadSpeedDisplay = report.DownloadSpeedDisplay ?? string.Empty;
                    currentItem.EtaDisplay = report.EtaDisplay ?? string.Empty;

                    if (report.IsMuxing)
                    {
                        currentItem.Status = FacebookQueueItemStatus.Muxing;
                    }
                });

                try
                {
                    var result = await _downloadUseCase.ExecuteAsync(request, progress, token).ConfigureAwait(true);

                    if (result.IsSuccess)
                    {
                        currentItem.Status = FacebookQueueItemStatus.Completed;
                        currentItem.ProgressPercent = 100;
                        currentItem.ProgressPercentDisplay = 100;
                        currentItem.ProgressStatusText = "Completed";
                        currentItem.OutputFilePath = result.OutputFilePath;
                    }
                    else if (result.IsCancelled)
                    {
                        currentItem.Status = FacebookQueueItemStatus.Cancelled;
                        currentItem.ProgressStatusText = "Cancelled";
                    }
                    else
                    {
                        currentItem.Status = FacebookQueueItemStatus.Failed;
                        currentItem.ErrorMessage = result.ErrorMessage ?? "Download failed";
                        currentItem.ProgressStatusText = "Failed";
                    }
                }
                catch (OperationCanceledException)
                {
                    currentItem.Status = FacebookQueueItemStatus.Cancelled;
                    currentItem.ProgressStatusText = "Cancelled";
                    break;
                }
                catch (Exception ex)
                {
                    currentItem.Status = FacebookQueueItemStatus.Failed;
                    currentItem.ErrorMessage = ex.Message;
                    currentItem.ProgressStatusText = "Error";
                }

                UpdateQueueStats();

                // 4. Polite Anti-Ban Delay before next URL (3 seconds cooldown)
                if (downloadQueue.Count > 0 && !token.IsCancellationRequested)
                {
                    ShowCooldownNotice = true;
                    for (int i = 3; i > 0; i--)
                    {
                        CooldownMessage = $"Rate-limit protection: Pausing {i}s before next download…";
                        await Task.Delay(1000, token).ConfigureAwait(true);
                    }
                    ShowCooldownNotice = false;
                }
            }

            QueueStatusMessage = token.IsCancellationRequested
                ? "Queue was cancelled."
                : "Queue processing completed!";
        }
        catch (OperationCanceledException)
        {
            QueueStatusMessage = "Queue was cancelled.";
        }
        finally
        {
            IsQueueRunning = false;
            ShowCooldownNotice = false;
            UpdateQueueStats();
            StartQueueCommand.NotifyCanExecuteChanged();
        }
    }

    [RelayCommand]
    private void CancelQueue()
    {
        if (IsQueueRunning)
        {
            QueueStatusMessage = "Cancelling queue…";
            _queueCts?.Cancel();
        }
    }

    [RelayCommand]
    private void ClearCompleted()
    {
        var completed = QueueItems.Where(i => i.Status is FacebookQueueItemStatus.Completed or FacebookQueueItemStatus.Cancelled).ToList();
        foreach (var item in completed)
        {
            QueueItems.Remove(item);
        }
        UpdateQueueStats();
        StartQueueCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    private void ClearAll()
    {
        if (IsQueueRunning) return;

        QueueItems.Clear();
        UpdateQueueStats();
        QueueStatusMessage = "Queue cleared.";
        StartQueueCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    private void PasteFromClipboard()
    {
        try
        {
            if (Clipboard.ContainsText())
            {
                var text = Clipboard.GetText().Trim();
                FacebookUrl = text;
                AddToQueue();
            }
        }
        catch
        {
            // Ignore clipboard access exceptions
        }
    }

    private void RemoveQueueItem(FacebookQueueItemViewModel item)
    {
        if (IsQueueRunning && item.IsActive) return;

        QueueItems.Remove(item);
        UpdateQueueStats();
        StartQueueCommand.NotifyCanExecuteChanged();
    }

    private void UpdateQueueStats()
    {
        TotalQueueCount = QueueItems.Count;
        CompletedCount = QueueItems.Count(i => i.Status == FacebookQueueItemStatus.Completed);
        IsEmptyQueue = TotalQueueCount == 0;
        HasQueueItems = TotalQueueCount > 0;
        OverallProgressPercent = TotalQueueCount == 0 ? 0 : (double)CompletedCount / TotalQueueCount * 100;
    }

    private static async Task<BitmapImage?> LoadThumbnailAsync(string url)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            var data = await http.GetByteArrayAsync(url).ConfigureAwait(true);
            var image = new BitmapImage();
            image.BeginInit();
            image.StreamSource = new MemoryStream(data);
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.DecodePixelWidth = 240;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch
        {
            return null;
        }
    }

    private static string FormatDuration(TimeSpan ts) =>
        ts.TotalHours >= 1 ? ts.ToString(@"h\:mm\:ss") : ts.ToString(@"m\:ss");

    private static FacebookVideoFormat ParseFormat(string display) =>
        display switch
        {
            "MP4" => FacebookVideoFormat.Mp4,
            "MKV" => FacebookVideoFormat.Mkv,
            "WebM" => FacebookVideoFormat.Webm,
            _ => FacebookVideoFormat.Mp4
        };
}
