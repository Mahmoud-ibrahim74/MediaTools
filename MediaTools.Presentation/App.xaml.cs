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

public partial class App : System.Windows.Application
{
    private IHost? _host;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _host = Host.CreateDefaultBuilder(e.Args)
            .ConfigureServices(
                (_, services) =>
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
                    services.AddSingleton<MainWindowViewModel>();
                    services.AddTransient<DashboardViewModel>();
                    services.AddTransient<VideoCompressViewModel>();
                    services.AddTransient<PhotoEnhancerViewModel>();
                    services.AddTransient<AudioEnhancerViewModel>();
                    services.AddTransient<ThumbnailGeneratorViewModel>();
                    services.AddTransient<SubtitleExtractorViewModel>();
                    services.AddTransient<ScreenRecorderViewModel>();
                    services.AddTransient<AppSettingsViewModel>();
                    services.AddTransient<DashboardPage>();
                    services.AddTransient<VideoCompressPage>();
                    services.AddTransient<PhotoEnhancerPage>();
                    services.AddTransient<AudioEnhancerPage>();
                    services.AddTransient<ThumbnailGeneratorPage>();
                    services.AddTransient<SubtitleExtractorPage>();
                    services.AddTransient<ScreenRecorderPage>();
                    services.AddTransient<AppSettingsPage>();
                    services.AddSingleton<MainWindow>();
                })
            .Build();

        await _host.Services.GetRequiredService<IVideoCompressionService>()
            .EnsureToolsReadyAsync()
            .ConfigureAwait(true);

        var mainWindow = _host.Services.GetRequiredService<MainWindow>();
        mainWindow.Show();
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
