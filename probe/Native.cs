using System.Runtime.InteropServices;

namespace TaskbarProbe;

internal static class Native
{
    // ---------- DPI ----------
    private static readonly IntPtr DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = new(-4);

    [DllImport("user32.dll")]
    private static extern bool SetProcessDpiAwarenessContext(IntPtr value);

    public static void EnablePerMonitorDpiAwareness()
    {
        try { SetProcessDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2); }
        catch (EntryPointNotFoundException) { /* < Win10 1703 */ }
    }

    // ---------- Windows ----------
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr FindWindow(string? lpClassName, string? lpWindowName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr FindWindowEx(IntPtr parent, IntPtr childAfter, string? className, string? windowName);

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")]
    public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);

    // ---------- Cursor / keyboard ----------
    [StructLayout(LayoutKind.Sequential)]
    public struct POINT { public int X, Y; }

    [DllImport("user32.dll")]
    public static extern bool GetCursorPos(out POINT p);

    [DllImport("user32.dll")]
    public static extern short GetAsyncKeyState(int vKey);

    public const int VK_ESCAPE = 0x1B;

    public static bool EscapePressed() => (GetAsyncKeyState(VK_ESCAPE) & 0x8000) != 0;

    // ---------- SendInput ----------
    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public MOUSEINPUT mi;
        // MOUSEINPUT is the largest of the union members on x64 (32 bytes), so
        // embedding it directly gives the correct INPUT size.
    }

    private const uint INPUT_MOUSE = 0;
    private const uint MOUSEEVENTF_MOVE = 0x0001;
    private const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    private const uint MOUSEEVENTF_LEFTUP = 0x0004;
    private const uint MOUSEEVENTF_ABSOLUTE = 0x8000;
    private const uint MOUSEEVENTF_VIRTUALDESK = 0x4000;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    private const int SM_XVIRTUALSCREEN = 76;
    private const int SM_YVIRTUALSCREEN = 77;
    private const int SM_CXVIRTUALSCREEN = 78;
    private const int SM_CYVIRTUALSCREEN = 79;

    private static void SendMouse(uint flags, int absX = 0, int absY = 0)
    {
        var input = new INPUT
        {
            type = INPUT_MOUSE,
            mi = new MOUSEINPUT { dx = absX, dy = absY, dwFlags = flags }
        };
        uint sent = SendInput(1, [input], Marshal.SizeOf<INPUT>());
        if (sent != 1)
            throw new InvalidOperationException($"SendInput 失败, Win32Error={Marshal.GetLastWin32Error()}");
    }

    /// <summary>把屏幕物理坐标移动到指定位置（绝对坐标，覆盖整个虚拟桌面）。</summary>
    public static void MoveMouseTo(int screenX, int screenY)
    {
        int vx = GetSystemMetrics(SM_XVIRTUALSCREEN);
        int vy = GetSystemMetrics(SM_YVIRTUALSCREEN);
        int vw = Math.Max(GetSystemMetrics(SM_CXVIRTUALSCREEN), 2);
        int vh = Math.Max(GetSystemMetrics(SM_CYVIRTUALSCREEN), 2);

        int nx = (int)Math.Round((screenX - vx) * 65535.0 / (vw - 1));
        int ny = (int)Math.Round((screenY - vy) * 65535.0 / (vh - 1));
        nx = Math.Clamp(nx, 0, 65535);
        ny = Math.Clamp(ny, 0, 65535);

        SendMouse(MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK, nx, ny);
    }

    public static void LeftDown() => SendMouse(MOUSEEVENTF_LEFTDOWN);
    public static void LeftUp() => SendMouse(MOUSEEVENTF_LEFTUP);
}
