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
        var versionString = v is null ? "1.0" : $"{v.Major}.{v.Minor}.{v.Build}";
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
}
