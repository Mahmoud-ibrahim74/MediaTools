namespace MediaTools.Presentation.Services;

/// <summary>Windows 10+ toast notifications (Action Center).</summary>
public interface IWindowsToastNotificationService
{
    /// <param name="isSuccess">Drives subtle styling (audio); both outcomes show a toast.</param>
    /// <param name="attribution">Shown as small caption (e.g. tool name tied to the page).</param>
    void ShowToolFinished(string title, string body, bool isSuccess, string? attribution = null);
}
