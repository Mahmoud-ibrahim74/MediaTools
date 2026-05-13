using System.Runtime.InteropServices;

namespace MediaTools.Presentation.Services;

/// <summary>
/// Registers global screen-recorder shortcuts on the main window handle (system-wide while the app runs).
/// </summary>
internal static class ScreenRecorderHotkeyRegistration
{
    public const int IdStart = 1;
    public const int IdPause = 2;

    public static void RegisterAll(IntPtr hwnd, HotkeySetting start, HotkeySetting pause)
    {
        UnregisterAll(hwnd);
        TryRegister(hwnd, IdStart, start);
        TryRegister(hwnd, IdPause, pause);
    }

    public static void UnregisterAll(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        UnregisterHotKey(hwnd, IdStart);
        UnregisterHotKey(hwnd, IdPause);
    }

    private static void TryRegister(IntPtr hwnd, int id, HotkeySetting hk)
    {
        if (hk.IsEmpty || hwnd == IntPtr.Zero)
        {
            return;
        }

        uint mods = hk.Modifiers | HotkeySetting.ModNoRepeat;
        RegisterHotKey(hwnd, id, mods, hk.VirtualKey);
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}
