using System.Windows.Controls;
using MahApps.Metro.Controls;
using MediaTools.Presentation.ViewModels;
using MediaTools.Presentation.Views;
using Microsoft.Extensions.DependencyInjection;

namespace MediaTools.Presentation;

public partial class MainWindow : MetroWindow
{
    private readonly IServiceProvider _services;

    public MainWindow(MainWindowViewModel viewModel, IServiceProvider services)
    {
        _services = services;
        DataContext = viewModel;
        InitializeComponent();

        viewModel.NavigationRequested += OnNavigationRequested;
        Loaded += (_, _) => viewModel.NavigateCommand.Execute("Dashboard");
    }

    private void OnNavigationRequested(object? sender, string target)
    {
        Page? page = target switch
        {
            "Dashboard" => _services.GetRequiredService<DashboardPage>(),
            "VideoCompress" => _services.GetRequiredService<VideoCompressPage>(),
            "PhotoEnhancer" => _services.GetRequiredService<PhotoEnhancerPage>(),
            "AudioEnhancer" => _services.GetRequiredService<AudioEnhancerPage>(),
            "ThumbnailGenerator" => _services.GetRequiredService<ThumbnailGeneratorPage>(),
            "AppSettings" => _services.GetRequiredService<AppSettingsPage>(),
            _ => null
        };

        if (page is not null)
        {
            ContentFrame.Navigate(page);
        }
    }
}
