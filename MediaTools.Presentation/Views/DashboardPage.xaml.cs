using System.Windows.Controls;
using MediaTools.Presentation.ViewModels;

namespace MediaTools.Presentation.Views;

public partial class DashboardPage : Page
{
    public DashboardPage(DashboardViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        Loaded += (_, _) => viewModel.RefreshStats();
    }
}
