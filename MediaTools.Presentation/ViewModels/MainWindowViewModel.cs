using System.Reflection;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MediaTools.Application.Abstractions;

namespace MediaTools.Presentation.ViewModels;

public partial class MainWindowViewModel : ObservableObject
{
    private readonly IVideoCompressionService _videoCompression;

    public event EventHandler<string>? NavigationRequested;

    public MainWindowViewModel(IVideoCompressionService videoCompression)
    {
        _videoCompression = videoCompression;
        _videoCompression.ToolsAvailabilityChanged += OnToolsAvailabilityChanged;

        var v = Assembly.GetExecutingAssembly().GetName().Version;
        AppVersionDisplay = v is null ? "2.0.0" : $"{v.Major}.{v.Minor}.{v.Build}";

        SyncFfmpegFromService();
    }

    private void OnToolsAvailabilityChanged(object? sender, EventArgs e)
    {
        var app = System.Windows.Application.Current;
        if (app is null)
        {
            return;
        }

        if (app.Dispatcher.CheckAccess())
        {
            SyncFfmpegFromService();
        }
        else
        {
            app.Dispatcher.Invoke(SyncFfmpegFromService);
        }
    }

    private void SyncFfmpegFromService()
    {
        IsFfmpegReady = _videoCompression.IsToolsReady;
        IsFfmpegPreparing = _videoCompression.IsToolsPreparing;
        FfmpegPrepareError = _videoCompression.ToolsPrepareError;
        FfmpegGateActive = !IsFfmpegReady;
        OnPropertyChanged(nameof(ShowFfmpegRetry));
        OnPropertyChanged(nameof(FfmpegOverlayDetail));
    }

    [ObservableProperty]
    private string _currentNavigationKey = "Dashboard";

    [ObservableProperty]
    private string _appVersionDisplay = "2.0.0";

    [ObservableProperty]
    private bool _isFfmpegReady;

    [ObservableProperty]
    private bool _isFfmpegPreparing;

    [ObservableProperty]
    private string? _ffmpegPrepareError;

    /// <summary>When true, a modal layer blocks the shell until FFmpeg is ready.</summary>
    [ObservableProperty]
    private bool _ffmpegGateActive;

    public bool ShowFfmpegRetry =>
        !IsFfmpegReady && !IsFfmpegPreparing && !string.IsNullOrWhiteSpace(FfmpegPrepareError);

    public string FfmpegOverlayDetail =>
        IsFfmpegPreparing
            ? "Downloading FFmpeg (first launch, about 100 MB). Keep this window open."
            : ShowFfmpegRetry
                ? FfmpegPrepareError ?? "Could not prepare FFmpeg."
                : "Preparing Media Tools…";

    [RelayCommand]
    private async Task RetryFfmpegPrepareAsync()
    {
        try
        {
            await _videoCompression.EnsureToolsReadyAsync().ConfigureAwait(false);
        }
        catch
        {
            // Error is stored on the service; UI syncs via ToolsAvailabilityChanged.
        }
    }

    [RelayCommand]
    private void Navigate(string? target)
    {
        if (string.IsNullOrWhiteSpace(target))
        {
            return;
        }

        CurrentNavigationKey = target;
        NavigationRequested?.Invoke(this, target);
    }
}
