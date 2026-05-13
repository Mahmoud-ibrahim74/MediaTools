using System.Text;
using System.Windows.Input;

namespace MediaTools.Presentation.Services;

/// <summary>
/// Win32-style hotkey for <see cref="RegisterHotKey"/> (modifiers + virtual-key).
/// </summary>
public readonly record struct HotkeySetting(uint Modifiers, uint VirtualKey)
{
    /// <summary>Reserved / unset — will not be registered globally.</summary>
    public static HotkeySetting Empty => new(0, 0);

    public bool IsEmpty => VirtualKey == 0;

    /// <summary>MOD_* flags only (no MOD_NOREPEAT).</summary>
    public static HotkeySetting FromWpfKey(Key key, ModifierKeys modifierKeys)
    {
        var vk = (uint)KeyInterop.VirtualKeyFromKey(key);
        return new HotkeySetting(ToWin32Modifiers(modifierKeys), vk);
    }

    /// <summary>
    /// Maps WPF modifier keys to Win32 MOD_* bits used by RegisterHotKey.
    /// </summary>
    public static uint ToWin32Modifiers(ModifierKeys modifierKeys)
    {
        uint m = 0;
        if (modifierKeys.HasFlag(ModifierKeys.Control))
        {
            m |= ModControl;
        }

        if (modifierKeys.HasFlag(ModifierKeys.Alt))
        {
            m |= ModAlt;
        }

        if (modifierKeys.HasFlag(ModifierKeys.Shift))
        {
            m |= ModShift;
        }

        if (modifierKeys.HasFlag(ModifierKeys.Windows))
        {
            m |= ModWin;
        }

        return m;
    }

    public string ToDisplayString()
    {
        if (IsEmpty)
        {
            return "(none)";
        }

        var sb = new StringBuilder();
        var m = Modifiers;
        if ((m & ModControl) != 0)
        {
            Append(sb, "Ctrl");
        }

        if ((m & ModAlt) != 0)
        {
            Append(sb, "Alt");
        }

        if ((m & ModShift) != 0)
        {
            Append(sb, "Shift");
        }

        if ((m & ModWin) != 0)
        {
            Append(sb, "Win");
        }

        var keyName = VkToDisplayName(VirtualKey);
        Append(sb, keyName);
        return sb.ToString();
    }

    private static void Append(StringBuilder sb, string part)
    {
        if (sb.Length > 0)
        {
            sb.Append(" + ");
        }

        sb.Append(part);
    }

    private static string VkToDisplayName(uint vk)
    {
        try
        {
            var key = KeyInterop.KeyFromVirtualKey((int)vk);
            return key switch
            {
                Key.LeftShift or Key.RightShift => "Shift",
                Key.LeftCtrl or Key.RightCtrl => "Ctrl",
                Key.LeftAlt or Key.RightAlt => "Alt",
                Key.LWin or Key.RWin => "Win",
                >= Key.F1 and <= Key.F24 => "F" + ((int)key - (int)Key.F1 + 1),
                Key.Space => "Space",
                Key.Return => "Enter",
                Key.Escape => "Esc",
                Key.Back => "Backspace",
                Key.Tab => "Tab",
                _ => key.ToString()
            };
        }
        catch
        {
            return $"0x{vk:X}";
        }
    }

    // Win32 MOD_* (RegisterHotKey)
    public const uint ModAlt = 0x0001;
    public const uint ModControl = 0x0002;
    public const uint ModShift = 0x0004;
    public const uint ModWin = 0x0008;

    /// <summary>Avoid repeated WM_HOTKEY while key held.</summary>
    public const uint ModNoRepeat = 0x4000;
}
