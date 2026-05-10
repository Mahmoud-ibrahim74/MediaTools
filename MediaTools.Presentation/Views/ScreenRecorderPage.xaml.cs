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
}
