using System.Windows;
using System.Windows.Controls;
using MahApps.Metro.Controls;

namespace MediaTools.Presentation.Views;

public partial class SupportUsWindow : MetroWindow
{
    public SupportUsWindow()
    {
        InitializeComponent();
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.CommandParameter is string textToCopy)
        {
            try
            {
                Clipboard.SetText(textToCopy);
                Helpers.MessageBoxHelper.ShowInformation(
                    "Copied to clipboard successfully!\n\nYour generosity truly touches my heart. Thank you so much for believing in me and supporting my hard work. Every little bit means the world to me and keeps this project alive! ❤️", 
                    "Thank You So Much! 🙏");
            }
            catch
            {
                // Ignore clipboard errors
            }
        }
    }
}
