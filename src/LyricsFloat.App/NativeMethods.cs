using System.Runtime.InteropServices;

namespace LyricsFloat.App;

internal static partial class NativeMethods
{
    public const int GWL_EXSTYLE = -20;
    public const nint WS_EX_TRANSPARENT = 0x20;
    public const nint WS_EX_TOOLWINDOW = 0x80;
    public const nint WS_EX_NOACTIVATE = 0x08000000;

    public const int WM_HOTKEY = 0x0312;
    public const int WM_ENTERSIZEMOVE = 0x0231;
    public const int WM_EXITSIZEMOVE = 0x0232;
    public const uint MOD_ALT = 0x1;
    public const uint MOD_CONTROL = 0x2;
    public const uint MOD_NOREPEAT = 0x4000;
    public const int VK_SHIFT = 0x10;
    public const uint VK_K = 0x4B;
    public const uint VK_L = 0x4C;

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT
    {
        public int X, Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    [LibraryImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    public static partial nint GetWindowLongPtr(nint hWnd, int nIndex);

    [LibraryImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    public static partial nint SetWindowLongPtr(nint hWnd, int nIndex, nint dwNewLong);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool RegisterHotKey(nint hWnd, int id, uint fsModifiers, uint vk);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool UnregisterHotKey(nint hWnd, int id);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetCursorPos(out POINT point);

    [LibraryImport("user32.dll")]
    public static partial short GetAsyncKeyState(int vKey);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetWindowRect(nint hWnd, out RECT rect);

    /// <summary>
    /// Has the compositor blur whatever is behind the window, the effect behind the Windows taskbar.
    /// The API is undocumented but has been stable since Windows 10.
    /// </summary>
    public static unsafe bool EnableBackdropBlur(nint hWnd)
    {
        const int WCA_ACCENT_POLICY = 19;
        const int ACCENT_ENABLE_BLURBEHIND = 3;

        var accent = new AccentPolicy { AccentState = ACCENT_ENABLE_BLURBEHIND };
        var data = new WindowCompositionAttributeData
        {
            Attribute = WCA_ACCENT_POLICY,
            Data = (nint)(&accent),
            SizeOfData = sizeof(AccentPolicy),
        };
        return SetWindowCompositionAttribute(hWnd, ref data) != 0;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AccentPolicy
    {
        public int AccentState;
        public int AccentFlags;
        public uint GradientColor;
        public int AnimationId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowCompositionAttributeData
    {
        public int Attribute;
        public nint Data;
        public int SizeOfData;
    }

    /// <summary>Asks Windows 11 to round the window's corners; DWM clips everything it draws, blur included.</summary>
    public static bool RequestRoundedCorners(nint hWnd)
    {
        const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
        var round = 2; // DWMWCP_ROUND
        return DwmSetWindowAttribute(hWnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref round, sizeof(int)) == 0;
    }

    /// <summary>Removes the thin outline Windows 11 draws around windows.</summary>
    public static void HideWindowBorder(nint hWnd)
    {
        const int DWMWA_BORDER_COLOR = 34;
        var none = unchecked((int)0xFFFFFFFE); // DWMWA_COLOR_NONE
        DwmSetWindowAttribute(hWnd, DWMWA_BORDER_COLOR, ref none, sizeof(int));
    }

    [LibraryImport("dwmapi.dll")]
    private static partial int DwmSetWindowAttribute(nint hWnd, int attribute, ref int value, int size);

    [LibraryImport("user32.dll")]
    private static partial int SetWindowCompositionAttribute(nint hWnd, ref WindowCompositionAttributeData data);
}
