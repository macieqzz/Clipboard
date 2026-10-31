using System.Runtime.InteropServices;

namespace Clipwise;

internal static class Native
{
    public const int WM_HOTKEY = 0x0312;
    public const int WM_CLIPBOARDUPDATE = 0x031D;
    public const int VK_OEM3 = 0xC0;                 // the ` key
    public const uint MOD_CONTROL = 0x0002;
    public const uint MOD_SHIFT = 0x0004;
    public const uint MOD_NOREPEAT = 0x4000;

    public const long WS_EX_TOOLWINDOW = 0x00000080;
    public const long WS_EX_NOACTIVATE = 0x08000000;

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT
    {
        public int X;
        public int Y;
    }

    public const int WH_KEYBOARD_LL = 13;
    public const UIntPtr HOOK_EVENT_MARK = (UIntPtr)0x434C5756; // our replays, recognized in LlHook
    public const uint WM_KEYDOWN = 0x0100;
    public const uint WM_KEYUP = 0x0101;
    public const uint WM_SYSKEYDOWN = 0x0104;
    public const uint WM_SYSKEYUP = 0x0105;
    public const uint LLKHF_INJECTED = 0x00000010;

    [StructLayout(LayoutKind.Sequential)]
    public struct KBDLLHOOKSTRUCT
    {
        public uint vkCode;
        public uint scanCode;
        public uint flags;
        public uint time;
        public UIntPtr dwExtraInfo;
    }

    public delegate IntPtr LowLevelKeyboardProc(int nCode, UIntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    public static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, UIntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr GetModuleHandle(string? lpModuleName);

    [DllImport("user32.dll")]
    public static extern short GetAsyncKeyState(int vKey);

    public const byte VK_LWIN = 0x5B;
    public const byte VK_RWIN = 0x5C;

    [DllImport("user32.dll")]
    public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll")]
    public static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [DllImport("user32.dll")]
    public static extern bool AddClipboardFormatListener(IntPtr hwnd);

    [DllImport("user32.dll")]
    public static extern bool RemoveClipboardFormatListener(IntPtr hwnd);

    [DllImport("user32.dll")]
    public static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    public static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll")]
    public static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

    [DllImport("kernel32.dll")]
    public static extern uint GetCurrentThreadId();

    /// <summary>SetForegroundWindow that works from a background process (thread-attach dance).</summary>
    public static bool RestoreForeground(IntPtr hWnd)
    {
        uint cur = GetCurrentThreadId();
        GetWindowThreadProcessId(hWnd, out uint target);
        if (cur == target) return SetForegroundWindow(hWnd);
        AttachThreadInput(target, cur, true);
        try { return SetForegroundWindow(hWnd); }
        finally { AttachThreadInput(target, cur, false); }
    }

    /// <summary>
    /// Take foreground for our own window even when the OS input lock is held by
    /// another process (e.g. right after an injected Win+V). Plain
    /// SetForegroundWindow covers the common case with no synthetic keys; the
    /// ALT-tap (which resets the foreground lock) is used only when that fails,
    /// and its release is verified so ALT can never end up stuck.
    /// </summary>
    public static bool ForceForeground(IntPtr hWnd)
    {
        IntPtr fg = GetForegroundWindow();
        uint cur = GetCurrentThreadId();
        if (fg == hWnd) return true;

        uint fgThread;
        GetWindowThreadProcessId(fg, out fgThread);
        bool attached = false;
        if (fgThread != 0 && fgThread != cur)
            attached = AttachThreadInput(fgThread, cur, true);
        try
        {
            bool ok = SetForegroundWindow(hWnd);
            if (!ok)
            {
                // Foreground lock still held (e.g. the user is actively typing):
                // a synthetic ALT press/release grants it. Send pairs and retry.
                for (int i = 0; i < 3 && !ok; i++)
                {
                    KeyTap(0x12, 0x38); // ALT down+up resets the foreground lock
                    System.Threading.Thread.Sleep(30);
                    ok = SetForegroundWindow(hWnd);
                }
                // Verify ALT really is up; if an up event got eaten, keep sending
                // pairs until the state clears (gets unstuck on the next pair).
                for (int i = 0; i < 5 && (GetAsyncKeyState(0x12) & 0x8000) != 0; i++)
                {
                    KeyTap(0x12, 0x38);
                    System.Threading.Thread.Sleep(20);
                }
            }
            return ok;
        }
        finally
        {
            if (attached) AttachThreadInput(fgThread, cur, false);
        }
    }

    [DllImport("user32.dll")]
    public static extern uint GetDpiForSystem();

    public const byte VK_CONTROL = 0x11;
    public const byte VK_V = 0x56;
    public const uint KEYEVENTF_KEYUP = 0x0002;

    [DllImport("user32.dll")]
    public static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

    /// <summary>Press and release a key via keybd_event.</summary>
    public static void KeyTap(byte vk, byte scan)
    {
        keybd_event(vk, scan, 0, UIntPtr.Zero);
        keybd_event(vk, scan, KEYEVENTF_KEYUP, UIntPtr.Zero);
    }

    /// <summary>Ctrl+V via keybd_event. Empirically the injection route that reaches
    /// target apps from a background session; SendInput is dropped in some
    /// remote/automation sessions.</summary>
    public static void SendCtrlV()
    {
        keybd_event(VK_CONTROL, 0, 0, UIntPtr.Zero);
        keybd_event(VK_V, 0, 0, UIntPtr.Zero);
        keybd_event(VK_V, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
        keybd_event(VK_CONTROL, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
    }
}
