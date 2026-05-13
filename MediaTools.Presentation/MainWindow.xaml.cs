using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Navigation;
using MahApps.Metro.Controls;
using MediaTools.Presentation.Services;
using MediaTools.Presentation.ViewModels;
using MediaTools.Presentation.Views;
using Microsoft.Extensions.DependencyInjection;

namespace MediaTools.Presentation;

public partial class MainWindow : MetroWindow
{
    private const int WmHotkey = 0x0312;

    private readonly IServiceProvider _services;
    private readonly IUserPreferencesService _preferences;
    private HwndSource? _hwndSource;
    private IntPtr _hwnd;

    public MainWindow(MainWindowViewModel viewModel, IServiceProvider services, IUserPreferencesService preferences)
    {
        _services = services;
        _preferences = preferences;
        DataContext = viewModel;
        InitializeComponent();

        ContentFrame.Navigated += OnContentFrameNavigated;

        viewModel.NavigationRequested += OnNavigationRequested;
        Loaded += (_, _) => viewModel.NavigateCommand.Execute("Dashboard");
        Loaded += OnMainWindowLoaded;
        Closed += OnMainWindowClosed;
    }

    private static void OnContentFrameNavigated(object sender, NavigationEventArgs e)
    {
        if (e.Content is DashboardPage page && page.DataContext is DashboardViewModel vm)
        {
            vm.RefreshAll();
        }
    }

    private void OnMainWindowLoaded(object sender, RoutedEventArgs e)
    {
        var helper = new WindowInteropHelper(this);
        helper.EnsureHandle();
        _hwnd = helper.Handle;
        _hwndSource = HwndSource.FromHwnd(_hwnd);
        _hwndSource?.AddHook(WndProc);
        RegisterScreenRecorderHotkeys();
        _preferences.ScreenRecorderHotkeysChanged += OnScreenRecorderHotkeysChanged;
    }

    private void OnScreenRecorderHotkeysChanged(object? sender, EventArgs e) =>
        Dispatcher.Invoke(RegisterScreenRecorderHotkeys);

    private void RegisterScreenRecorderHotkeys()
    {
        ScreenRecorderHotkeyRegistration.RegisterAll(
            _hwnd,
            _preferences.ScreenRecorderStartHotkey,
            _preferences.ScreenRecorderPauseHotkey);
    }

    private void OnMainWindowClosed(object? sender, EventArgs e)
    {
        _preferences.ScreenRecorderHotkeysChanged -= OnScreenRecorderHotkeysChanged;
        ScreenRecorderHotkeyRegistration.UnregisterAll(_hwnd);
        if (_hwndSource is not null)
        {
            _hwndSource.RemoveHook(WndProc);
            _hwndSource = null;
        }
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WmHotkey)
        {
            return IntPtr.Zero;
        }

        var vm = _services.GetRequiredService<ScreenRecorderViewModel>();
        switch (wParam.ToInt32())
        {
            case ScreenRecorderHotkeyRegistration.IdStart:
                vm.HandleGlobalHotkeyStartRecording();
                handled = true;
                break;
            case ScreenRecorderHotkeyRegistration.IdPause:
                vm.HandleGlobalHotkeyPauseToggle();
                handled = true;
                break;
        }

        return IntPtr.Zero;
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
            "ScreenRecorder" => _services.GetRequiredService<ScreenRecorderPage>(),
            "VideoEnhancer" => _services.GetRequiredService<VideoEnhancerPage>(),
            "AppSettings" => _services.GetRequiredService<AppSettingsPage>(),
            _ => null
        };

        if (page is not null)
        {
            ContentFrame.Navigate(page);
        }
    }
}
