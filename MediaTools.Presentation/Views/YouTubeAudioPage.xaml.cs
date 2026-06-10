using System.Windows.Controls;
using MediaTools.Presentation.ViewModels;

namespace MediaTools.Presentation.Views;

public partial class YouTubeAudioPage : Page
{
    public YouTubeAudioPage(YouTubeAudioViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }
}
