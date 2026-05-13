using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using MediaTools.Presentation.ViewModels;

namespace MediaTools.Presentation.Views;

public partial class AppSettingsPage : Page
{
    private Window? _ownerWindow;

    public AppSettingsPage(AppSettingsViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        Loaded += OnPageLoaded;
        Unloaded += OnPageUnloaded;
    }

    private void OnPageLoaded(object sender, RoutedEventArgs e)
    {
        if (DataContext is AppSettingsViewModel vm)
        {
            vm.RefreshFromPreferences();
        }

        _ownerWindow = Window.GetWindow(this);
        if (_ownerWindow is not null)
        {
            _ownerWindow.PreviewKeyDown += OnOwnerWindowPreviewKeyDown;
        }
    }

    private void OnPageUnloaded(object sender, RoutedEventArgs e)
    {
        if (_ownerWindow is not null)
        {
            _ownerWindow.PreviewKeyDown -= OnOwnerWindowPreviewKeyDown;
            _ownerWindow = null;
        }
    }

    private void OnOwnerWindowPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (DataContext is AppSettingsViewModel vm)
        {
            _ = vm.TryConsumePreviewKeyDown(e);
        }
    }
}
