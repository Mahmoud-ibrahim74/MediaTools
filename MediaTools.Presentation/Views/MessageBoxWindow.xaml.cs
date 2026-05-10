using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using MahApps.Metro.Controls;
using MahApps.Metro.IconPacks;
using MediaTools.Presentation.Helpers;

namespace MediaTools.Presentation.Views;

public partial class MessageBoxWindow : MetroWindow
{
    private readonly MessageBoxButton _buttons;

    public MessageBoxResult Result { get; private set; } = MessageBoxResult.None;

    public MessageBoxWindow(string message, string caption, MessageDialogKind kind, MessageBoxButton buttons)
    {
        _buttons = buttons;
        InitializeComponent();
        DataContext = new DialogModel(caption, message);
        Title = caption;
        ApplyKind(kind);
        BuildButtons(buttons);
        Loaded += (_, _) =>
        {
            var first = ButtonPanel.Children.OfType<Button>().FirstOrDefault(b => b.IsDefault);
            first?.Focus();
        };
        PreviewKeyDown += OnPreviewKeyDown;
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape)
        {
            return;
        }

        switch (_buttons)
        {
            case MessageBoxButton.OK:
                CloseWith(MessageBoxResult.OK);
                e.Handled = true;
                break;
            case MessageBoxButton.OKCancel:
            case MessageBoxButton.YesNoCancel:
                CloseWith(MessageBoxResult.Cancel);
                e.Handled = true;
                break;
        }
    }

    private void ApplyKind(MessageDialogKind kind)
    {
        Brush accent;
        Brush iconFg;
        Brush badgeBg;
        Brush badgeBorder;
        PackIconMaterialKind iconKind;

        switch (kind)
        {
            case MessageDialogKind.Success:
                accent = (Brush)FindResource("StatusSuccessBrush");
                iconFg = accent;
                badgeBg = new SolidColorBrush(Color.FromArgb(0x14, 0x2E, 0x7D, 0x32));
                badgeBorder = new SolidColorBrush(Color.FromArgb(0x55, 0x2E, 0x7D, 0x32));
                iconKind = PackIconMaterialKind.CheckCircleOutline;
                break;
            case MessageDialogKind.Warning:
                accent = (Brush)FindResource("StatusWarningBrush");
                iconFg = (Brush)FindResource("StatusDangerBrush");
                badgeBg = new SolidColorBrush(Color.FromArgb(0x18, 0xF9, 0xA8, 0x25));
                badgeBorder = new SolidColorBrush(Color.FromArgb(0x55, 0xF9, 0xA8, 0x25));
                iconKind = PackIconMaterialKind.AlertOutline;
                break;
            case MessageDialogKind.Error:
                accent = (Brush)FindResource("StatusDangerBrush");
                iconFg = accent;
                badgeBg = new SolidColorBrush(Color.FromArgb(0x14, 0xC6, 0x28, 0x28));
                badgeBorder = new SolidColorBrush(Color.FromArgb(0x55, 0xC6, 0x28, 0x28));
                iconKind = PackIconMaterialKind.CloseCircleOutline;
                break;
            case MessageDialogKind.Question:
                accent = (Brush)FindResource("AccentPrimaryBrush");
                iconFg = accent;
                badgeBg = new SolidColorBrush(Color.FromArgb(0x14, 0x00, 0x78, 0xD7));
                badgeBorder = new SolidColorBrush(Color.FromArgb(0x33, 0x00, 0x78, 0xD7));
                iconKind = PackIconMaterialKind.HelpCircleOutline;
                break;
            case MessageDialogKind.Information:
                accent = (Brush)FindResource("AccentPrimaryBrush");
                iconFg = accent;
                badgeBg = new SolidColorBrush(Color.FromArgb(0x14, 0x00, 0x78, 0xD7));
                badgeBorder = new SolidColorBrush(Color.FromArgb(0x33, 0x00, 0x78, 0xD7));
                iconKind = PackIconMaterialKind.InformationOutline;
                break;
            default:
                accent = new SolidColorBrush(Color.FromRgb(0x5F, 0x6B, 0x76));
                iconFg = accent;
                badgeBg = new SolidColorBrush(Color.FromArgb(0x12, 0x5F, 0x6B, 0x76));
                badgeBorder = new SolidColorBrush(Color.FromArgb(0x33, 0x5F, 0x6B, 0x76));
                iconKind = PackIconMaterialKind.MessageTextOutline;
                break;
        }

        AccentStripe.Background = accent;
        GlowBrush = accent;
        IconBadge.Background = badgeBg;
        IconBadge.BorderBrush = badgeBorder;
        DialogIcon.Kind = iconKind;
        DialogIcon.Foreground = iconFg;
    }

    private void BuildButtons(MessageBoxButton buttons)
    {
        ButtonPanel.Children.Clear();

        switch (buttons)
        {
            case MessageBoxButton.OK:
                AddPrimary("OK", MessageBoxResult.OK, isDefault: true);
                break;
            case MessageBoxButton.OKCancel:
                AddOutlined("Cancel", MessageBoxResult.Cancel, isDefault: false);
                AddPrimary("OK", MessageBoxResult.OK, isDefault: true, marginLeft: 10);
                break;
            case MessageBoxButton.YesNoCancel:
                AddOutlined("Cancel", MessageBoxResult.Cancel, isDefault: false);
                AddOutlined("No", MessageBoxResult.No, isDefault: false, marginLeft: 10);
                AddPrimary("Yes", MessageBoxResult.Yes, isDefault: true, marginLeft: 10);
                break;
            case MessageBoxButton.YesNo:
                AddOutlined("No", MessageBoxResult.No, isDefault: false);
                AddPrimary("Yes", MessageBoxResult.Yes, isDefault: true, marginLeft: 10);
                break;
            default:
                AddPrimary("OK", MessageBoxResult.OK, isDefault: true);
                break;
        }
    }

    private void AddPrimary(string text, MessageBoxResult result, bool isDefault, double marginLeft = 0)
    {
        var b = new Button
        {
            Content = text,
            MinWidth = 92,
            Margin = new Thickness(marginLeft, 0, 0, 0),
            Style = (Style)FindResource("PrimaryButtonStyle"),
            IsDefault = isDefault
        };
        b.Click += (_, _) => CloseWith(result);
        ButtonPanel.Children.Add(b);
    }

    private void AddOutlined(string text, MessageBoxResult result, bool isDefault, double marginLeft = 0)
    {
        var b = new Button
        {
            Content = text,
            MinWidth = 92,
            Margin = new Thickness(marginLeft, 0, 0, 0),
            Style = (Style)FindResource("OutlinedButtonStyle"),
            IsCancel = result == MessageBoxResult.Cancel
        };
        if (isDefault)
        {
            b.IsDefault = true;
        }

        b.Click += (_, _) => CloseWith(result);
        ButtonPanel.Children.Add(b);
    }

    private void CloseWith(MessageBoxResult r)
    {
        Result = r;
        DialogResult = true;
    }

    private sealed record DialogModel(string Caption, string Message);
}
