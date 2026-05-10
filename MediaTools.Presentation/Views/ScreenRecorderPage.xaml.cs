using System.Windows;
using System.Windows.Controls;
using MediaTools.Presentation.ViewModels;

namespace MediaTools.Presentation.Views;

public partial class ScreenRecorderPage : Page
{
    public ScreenRecorderPage(ScreenRecorderViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }

    private void OnDrawRegionOnScreenClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not ScreenRecorderViewModel vm)
        {
            return;
        }

        var owner = Window.GetWindow(this);
        var overlay = new RegionSelectionOverlayWindow
        {
            Owner = owner
        };

        if (overlay.ShowDialog() == true && overlay.SelectedScreenRect is { } r)
        {
            vm.ApplyPickedRegion(r.X, r.Y, r.Width, r.Height);
            vm.StartDelaySeconds = 0;
            if (vm.StartRecordingCommand.CanExecute(null))
            {
                vm.StartRecordingCommand.Execute(null);
            }
        }
    }
}
