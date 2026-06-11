using System.Windows.Controls;
using MediaTools.Presentation.ViewModels;

namespace MediaTools.Presentation.Views;

public partial class YouTubeVideoPage : Page
{
    public YouTubeVideoPage(YouTubeVideoViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }
}
