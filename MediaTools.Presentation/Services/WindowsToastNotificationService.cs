using Microsoft.Toolkit.Uwp.Notifications;

namespace MediaTools.Presentation.Services;

public sealed class WindowsToastNotificationService(IUserPreferencesService userPreferences) : IWindowsToastNotificationService
{
    public void ShowToolFinished(string title, string body, bool isSuccess, string? attribution = null)
    {
        if (string.IsNullOrWhiteSpace(title) || !userPreferences.ToastNotificationsEnabled)
        {
            return;
        }

        try
        {
            var line2 = string.IsNullOrWhiteSpace(body) ? " " : body.Trim();
            var builder = new ToastContentBuilder()
                .AddHeader("mediatools", "MediaTools", "home");

            if (!string.IsNullOrWhiteSpace(attribution))
            {
                builder.AddAttributionText(attribution.Trim());
            }

            builder.AddText(title.Trim())
                .AddText(line2);

            if (isSuccess)
            {
                builder.AddAudio(new ToastAudio { Silent = false });
            }
            else
            {
                builder.AddAudio(new ToastAudio { Silent = true });
            }

            builder.Show();
        }
        catch
        {
            // Toasts require a supported OS, notification permissions, and compatible shell.
        }
    }
}
