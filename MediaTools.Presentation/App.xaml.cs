using System;
using System.Threading.Tasks;
using System.Windows;
using MediaTools.Application.Abstractions;
using MediaTools.Application.UseCases;
using MediaTools.Infrastructure;
using MediaTools.Presentation.Services;
using MediaTools.Presentation.ViewModels;
using MediaTools.Presentation.Views;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace MediaTools.Presentation;

/// <summary>
/// All application configuration (DI host, services, FFmpeg/tools readiness) runs here while
/// <see cref="SplashWindow"/> is shown; <see cref="MainWindow"/> opens only after that completes.
/// </summary>
public partial class App : System.Windows.Application
{
    private IHost? _host;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Closing the splash must not exit the process (default is OnLastWindowClose).
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        var splash = new SplashWindow();
        splash.Show();
        splash.SetStartupStatus("Loading configuration…");
        await Task.Delay(TimeSpan.FromSeconds(3)).ConfigureAwait(true);

        try
        {
            _host = CreateHost(e.Args);
            splash.SetStartupStatus("Wiring services…");

            await CompleteStartupWithSplashAsync(splash).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            splash.SetStartupStatus($"Could not start: {ex.Message}", isBusy: false);
            await Task.Delay(2500).ConfigureAwait(true);
            Shutdown(1);
            return;
        }

        ShowMainWindow();
    }

    private static IHost CreateHost(string[] args) =>
        Host.CreateDefaultBuilder(args).ConfigureServices(ConfigureApplicationServices).Build();

    /// <summary>Registers all Presentation + Application + Infrastructure services in one place.</summary>
    private static void ConfigureApplicationServices(HostBuilderContext _, IServiceCollection services)
    {
        services.AddInfrastructure();
        services.AddSingleton<IUserPreferencesService, UserPreferencesService>();
        services.AddSingleton<IWindowsToastNotificationService, WindowsToastNotificationService>();
        services.AddSingleton<CompressVideoUseCase>();
        services.AddSingleton<ProcessPhotoUseCase>();
        services.AddSingleton<ProcessAudioUseCase>(); 
        services.AddSingleton<ProcessThumbnailUseCase>();
        services.AddSingleton<ProcessSubtitleExtractUseCase>();
        services.AddSingleton<StartScreenRecordingUseCase>();
        services.AddSingleton<ProcessVideoEnhanceUseCase>();
        services.AddSingleton<MainWindowViewModel>();
        services.AddSingleton<DashboardViewModel>();
        services.AddSingleton<VideoCompressViewModel>();
        services.AddSingleton<PhotoEnhancerViewModel>();
        services.AddSingleton<AudioEnhancerViewModel>();
        services.AddSingleton<ThumbnailGeneratorViewModel>();
        services.AddSingleton<ScreenRecorderViewModel>();
        services.AddSingleton<VideoEnhancerViewModel>();
        services.AddSingleton<AppSettingsViewModel>();
        services.AddSingleton<DashboardPage>();
        services.AddSingleton<VideoCompressPage>();
        services.AddSingleton<PhotoEnhancerPage>();
        services.AddSingleton<AudioEnhancerPage>();
        services.AddSingleton<ThumbnailGeneratorPage>();
        services.AddSingleton<ScreenRecorderPage>();
        services.AddSingleton<VideoEnhancerPage>();
        services.AddSingleton<AppSettingsPage>();
        services.AddSingleton<MainWindow>();
    }

    private async Task CompleteStartupWithSplashAsync(SplashWindow splash)
    {
        if (_host is null)
        {
            throw new InvalidOperationException("Host is not initialized.");
        }

        var videoCompression = _host.Services.GetRequiredService<IVideoCompressionService>();

        void OnToolsChanged(object? _, EventArgs __)
        {
            Dispatcher.Invoke(() => splash.ApplyToolsState(videoCompression));
        }

        videoCompression.ToolsAvailabilityChanged += OnToolsChanged;
        splash.SetStartupStatus("Preparing media tools…");
        splash.ApplyToolsState(videoCompression);

        try
        {
            await videoCompression.EnsureToolsReadyAsync().ConfigureAwait(true);
        }
        catch
        {
            splash.ApplyToolsState(videoCompression);
        }

        videoCompression.ToolsAvailabilityChanged -= OnToolsChanged;
        splash.ApplyToolsState(videoCompression);
        await splash.ShowReadyPauseAsync().ConfigureAwait(true);
        splash.Close();
    }

    private void ShowMainWindow()
    {
        if (_host is null)
        {
            throw new InvalidOperationException("Host is not initialized.");
        }

        var mainWindow = _host.Services.GetRequiredService<MainWindow>();
        MainWindow = mainWindow;
        mainWindow.Show();
        ShutdownMode = ShutdownMode.OnMainWindowClose;
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        if (_host is not null)
        {
            await _host.StopAsync().ConfigureAwait(false);
            _host.Dispose();
        }

        base.OnExit(e);
    }
}
