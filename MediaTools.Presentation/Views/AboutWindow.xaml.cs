using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using MahApps.Metro.Controls;

namespace MediaTools.Presentation.Views;

public partial class AboutWindow : MetroWindow
{
    public AboutWindow()
    {
        InitializeComponent();
        
        var v = Assembly.GetExecutingAssembly().GetName().Version;
        var versionString = v is null ? "2.0.0" : $"{v.Major}.{v.Minor}.{v.Build}";
        VersionText.Text = $"Media Tools v{versionString}";
    }

    private void OpenLink_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.CommandParameter is string url)
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = url,
                    UseShellExecute = true
                });
            }
            catch
            {
                // Ignore if browser fails to launch
            }
        }
    }

    private async void CheckUpdate_Click(object sender, RoutedEventArgs e)
    {
        CheckUpdateBtn.IsEnabled = false;
        UpdateProgress.Visibility = Visibility.Visible;
        UpdateProgress.IsActive = true;

        var result = await Helpers.GitHubUpdateHelper.CheckForUpdatesAsync();

        UpdateProgress.IsActive = false;
        UpdateProgress.Visibility = Visibility.Collapsed;
        CheckUpdateBtn.IsEnabled = true;

        if (result.IsUpdateAvailable)
        {
            var msgResult = Helpers.MessageBoxHelper.Show(
                $"A new version ({result.LatestVersion}) is available on GitHub!\n\nWould you like to download the update?",
                "Update Available",
                MessageBoxButton.YesNo,
                MessageBoxImage.Information);

            if (msgResult == MessageBoxResult.Yes)
            {
                try
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = result.DownloadUrl,
                        UseShellExecute = true
                    });
                }
                catch
                {
                    // Ignore
                }
            }
        }
        else
        {
            Helpers.MessageBoxHelper.ShowInformation("You have the latest version!", "Up to Date");
        }
    }
}
