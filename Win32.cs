using System.Runtime.InteropServices;

namespace MousePollingMonitor;

/// <summary>
/// P/Invoke layer — only what Raw Input capture needs.
/// </summary>
internal static class Win32
{
    // ── Message IDs ───────────────────────────────────────────────────────────
    public const uint WM_INPUT   = 0x00FF;
    public const uint WM_DESTROY = 0x0002;

    // ── Raw Input constants ───────────────────────────────────────────────────
    public const uint RID_INPUT       = 0x10000003;
    public const uint RIDEV_INPUTSINK = 0x00000100;   // receive even when not focused
    public const uint RIM_TYPEMOUSE   = 0;

    // HWND_MESSAGE: message-only window parent sentinel
    public static readonly IntPtr HWND_MESSAGE = new(-3);

    // Native size of RAWINPUTHEADER:
    //   dwType(4) + dwSize(4) + hDevice(ptr) + wParam(ptr)
    public static readonly uint RawInputHeaderSize = (uint)(8 + IntPtr.Size * 2);

    // ── Delegate for WndProc ──────────────────────────────────────────────────
    public delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    // ── Structures ────────────────────────────────────────────────────────────

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct WNDCLASSW
    {
        public uint    style;
        public IntPtr  lpfnWndProc;   // stored as IntPtr — avoids delegate GC edge-cases
        public int     cbClsExtra;
        public int     cbWndExtra;
        public IntPtr  hInstance;
        public IntPtr  hIcon;
        public IntPtr  hCursor;
        public IntPtr  hbrBackground;
        public string? lpszMenuName;
        public string  lpszClassName;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MSG
    {
        public IntPtr hwnd;
        public uint   message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint   time;
        public int    ptX, ptY;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct RAWINPUTDEVICE
    {
        public ushort usUsagePage;
        public ushort usUsage;
        public uint   dwFlags;
        public IntPtr hwndTarget;
    }

    // ── User32 imports ────────────────────────────────────────────────────────

    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    public static extern ushort RegisterClassW(ref WNDCLASSW lpWndClass);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    public static extern IntPtr CreateWindowExW(
        uint dwExStyle, string lpClassName, string lpWindowName,
        uint dwStyle, int x, int y, int nWidth, int nHeight,
        IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

    [DllImport("user32.dll", ExactSpelling = true)]
    public static extern IntPtr DefWindowProcW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", ExactSpelling = true)]
    public static extern int GetMessageW(out MSG lpMsg, IntPtr hWnd, uint min, uint max);

    [DllImport("user32.dll", ExactSpelling = true)]
    public static extern bool TranslateMessage(ref MSG lpMsg);

    [DllImport("user32.dll", ExactSpelling = true)]
    public static extern IntPtr DispatchMessageW(ref MSG lpMsg);

    [DllImport("user32.dll", ExactSpelling = true)]
    public static extern void PostQuitMessage(int nExitCode);

    [DllImport("user32.dll", ExactSpelling = true)]
    public static extern bool RegisterRawInputDevices(
        [In] RAWINPUTDEVICE[] pRawInputDevices,
        uint uiNumDevices,
        uint cbSize);

    [DllImport("user32.dll", ExactSpelling = true)]
    public static extern uint GetRawInputData(
        IntPtr hRawInput, uint uiCommand,
        IntPtr pData, ref uint pcbSize, uint cbSizeHeader);

    [DllImport("kernel32.dll", ExactSpelling = true)]
    public static extern IntPtr GetModuleHandleW(string? lpModuleName);

    [DllImport("kernel32.dll", ExactSpelling = true)]
    public static extern IntPtr GetStdHandle(int nStdHandle);

    [DllImport("kernel32.dll", ExactSpelling = true)]
    public static extern bool GetConsoleMode(IntPtr hConsoleHandle, out uint lpMode);

    [DllImport("kernel32.dll", ExactSpelling = true)]
    public static extern bool SetConsoleMode(IntPtr hConsoleHandle, uint dwMode);

    // Enables ANSI/VT100 escape codes on Windows 10+ console.
    public static void EnableAnsiConsole()
    {
        IntPtr handle = GetStdHandle(-11); // STD_OUTPUT_HANDLE
        if (GetConsoleMode(handle, out uint mode))
            SetConsoleMode(handle, mode | 0x0004); // ENABLE_VIRTUAL_TERMINAL_PROCESSING
    }
}
