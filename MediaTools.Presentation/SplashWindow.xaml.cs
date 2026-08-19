using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using MediaTools.Application.Abstractions;

namespace MediaTools.Presentation;

public partial class SplashWindow : Window
{
    public SplashWindow()
    {
        InitializeComponent();
        var v = Assembly.GetExecutingAssembly().GetName().Version;
        var versionLabel = v is null ? "v2.0.0" : $"v{v.Major}.{v.Minor}.{v.Build}";
        LargeVersionText.Text = versionLabel;
        VersionFooterText.Text = versionLabel;
    }

    /// <summary>Startup phases before FFmpeg/tools state is known (host build, DI, etc.).</summary>
    public void SetStartupStatus(string message, bool isBusy = true)
    {
        StatusMessageText.Text = message;
        BusyRing.IsActive = isBusy;
    }

    public void ApplyToolsState(IVideoCompressionService videoCompression)
    {
        if (videoCompression.IsToolsReady)
        {
            BusyRing.IsActive = false;
            StatusMessageText.Text = "Ready.";
            return;
        }

        if (videoCompression.IsToolsPreparing)
        {
            BusyRing.IsActive = true;
            StatusMessageText.Text =
                "Downloading media tools… First launch may take a minute (~100 MB).";
            return;
        }

        if (!string.IsNullOrWhiteSpace(videoCompression.ToolsPrepareError))
        {
            BusyRing.IsActive = false;
            StatusMessageText.Text =
                "Setup incomplete — use Retry on the next screen to try again.";
            return;
        }

        BusyRing.IsActive = true;
        StatusMessageText.Text = "Preparing workspace…";
    }

    /// <summary>Lets the user read the final status before the window closes.</summary>
    public async Task ShowReadyPauseAsync(int milliseconds = 320)
    {
        await Task.Delay(milliseconds).ConfigureAwait(true);
    }

    private void Window_Deactivated(object sender, System.EventArgs e)
    {
        // Force the splash window to stay focused and on top
        if (this.IsVisible)
        {
            this.Topmost = true;
            this.Activate();
            this.Focus();
        }
    }
}
