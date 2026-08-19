using System.Windows.Controls;
using MediaTools.Presentation.ViewModels;

namespace MediaTools.Presentation.Views;

public partial class FacebookVideoPage : Page
{
    public FacebookVideoPage(FacebookVideoViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }
}
