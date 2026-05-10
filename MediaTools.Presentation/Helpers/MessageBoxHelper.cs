using System.Windows;
using MediaTools.Presentation.Views;

namespace MediaTools.Presentation.Helpers;

/// <summary>Replacement for <see cref="MessageBox"/> using the app-styled dialog.</summary>
public static class MessageBoxHelper
{
    public static MessageBoxResult Show(
        string message,
        string caption = "MediaTools",
        MessageBoxButton button = MessageBoxButton.OK,
        MessageBoxImage icon = MessageBoxImage.None)
    {
        var owner = global::System.Windows.Application.Current?.MainWindow;
        var kind = MapImage(icon);
        var w = new MessageBoxWindow(message, caption, kind, button)
        {
            Owner = owner
        };
        w.ShowDialog();
        return w.Result;
    }

    public static void ShowInformation(string message, string caption = "MediaTools") =>
        Show(message, caption, MessageBoxButton.OK, MessageBoxImage.Information);

    public static void ShowWarning(string message, string caption = "MediaTools") =>
        Show(message, caption, MessageBoxButton.OK, MessageBoxImage.Warning);

    public static void ShowError(string message, string caption = "MediaTools") =>
        Show(message, caption, MessageBoxButton.OK, MessageBoxImage.Error);

    private static MessageDialogKind MapImage(MessageBoxImage image) =>
        image switch
        {
            MessageBoxImage.None => MessageDialogKind.Neutral,
            MessageBoxImage.Question => MessageDialogKind.Question,
            MessageBoxImage.Error or MessageBoxImage.Hand or MessageBoxImage.Stop => MessageDialogKind.Error,
            MessageBoxImage.Warning or MessageBoxImage.Exclamation => MessageDialogKind.Warning,
            MessageBoxImage.Information or MessageBoxImage.Asterisk => MessageDialogKind.Information,
            _ => MessageDialogKind.Neutral
        };
}
