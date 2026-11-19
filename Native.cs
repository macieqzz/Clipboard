using System.Runtime.InteropServices;
using System.Text;

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

    public const int WM_NCHITTEST = 0x0084;
    public const int HTCLIENT = 1;
    public const int HTLEFT = 10;
    public const int HTRIGHT = 11;
    public const int HTTOP = 12;
    public const int HTTOPLEFT = 13;
    public const int HTTOPRIGHT = 14;
    public const int HTBOTTOM = 15;
    public const int HTBOTTOMLEFT = 16;
    public const int HTBOTTOMRIGHT = 17;

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
    public static extern IntPtr SetFocus(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr hWnd, uint gaFlags);

    [DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct GUITHREADINFO
    {
        public int cbSize;
        public int flags;
        public IntPtr hwndActive;
        public IntPtr hwndFocus;
        public IntPtr hwndCapture;
        public IntPtr hwndMenuOwner;
        public IntPtr hwndMoveSize;
        public IntPtr hwndCaret;
        public RECT rcCaret;
    }

    [DllImport("user32.dll")]
    private static extern bool GetGUIThreadInfo(uint idThread, ref GUITHREADINFO lpgui);

    private const uint GA_ROOT = 2;

    private static string ClassNameOf(IntPtr hWnd)
    {
        var sb = new StringBuilder(256);
        return GetClassName(hWnd, sb, sb.Capacity) > 0 ? sb.ToString() : "";
    }

    private static bool IsExplorer(uint pid)
    {
        try
        {
            using var p = System.Diagnostics.Process.GetProcessById((int)pid);
            return p.ProcessName.Equals("explorer", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// The HWND of the edit box the user is renaming a file/folder in (Explorer's
    /// in-place rename), or IntPtr.Zero when the foreground window is something else.
    /// </summary>
    public static IntPtr GetRenameEdit(IntPtr foreground)
    {
        try
        {
            if (foreground == IntPtr.Zero) return IntPtr.Zero;
            uint tid = GetWindowThreadProcessId(foreground, out uint pid);
            if (tid == 0) return IntPtr.Zero;

            var info = new GUITHREADINFO { cbSize = Marshal.SizeOf<GUITHREADINFO>() };
            if (!GetGUIThreadInfo(tid, ref info) || info.hwndFocus == IntPtr.Zero) return IntPtr.Zero;

            var focus = info.hwndFocus;
            if (!ClassNameOf(focus).Equals("Edit", StringComparison.OrdinalIgnoreCase)) return IntPtr.Zero;
            return IsExplorer(pid) ? focus : IntPtr.Zero;
        }
        catch
        {
            return IntPtr.Zero;
        }
    }

    /// <summary>Give keyboard focus back to a control in another process (the
    /// Explorer rename box after our overlay closed and took it).</summary>
    public static bool FocusChildWindow(IntPtr hWnd)
    {
        IntPtr root = GetAncestor(hWnd, GA_ROOT);
        if (root != IntPtr.Zero) RestoreForeground(root);

        uint cur = GetCurrentThreadId();
        GetWindowThreadProcessId(hWnd, out uint target);
        if (target == 0 || target == cur) return SetFocus(hWnd) != IntPtr.Zero;

        AttachThreadInput(target, cur, true);
        try { return SetFocus(hWnd) != IntPtr.Zero; }
        finally { AttachThreadInput(target, cur, false); }
    }

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
    public const byte VK_A = 0x41;
    public const byte VK_RETURN = 0x0D;
    public const byte VK_F2 = 0x71;

    [DllImport("user32.dll")]
    public static extern bool IsWindow(IntPtr hWnd);

    /// <summary>True when the window belongs to an explorer.exe process.</summary>
    public static bool IsExplorerProcess(IntPtr hWnd)
    {
        if (hWnd == IntPtr.Zero) return false;
        GetWindowThreadProcessId(hWnd, out uint pid);
        return pid != 0 && IsExplorer(pid);
    }
    public const uint KEYEVENTF_KEYUP = 0x0002;

    [DllImport("user32.dll")]
    public static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

    /// <summary>Press and release a key via keybd_event.</summary>
    public static void KeyTap(byte vk, byte scan)
    {
        keybd_event(vk, scan, 0, UIntPtr.Zero);
        keybd_event(vk, scan, KEYEVENTF_KEYUP, UIntPtr.Zero);
    }

    /// <summary>VK 0xE8 is unassigned: tapping it while Win is held makes the shell
    /// treat Win as "used in a combo", so releasing Win does not open Start.</summary>
    public static void SendMaskKey()
    {
        keybd_event(0xE8, 0, 0, HOOK_EVENT_MARK);
        keybd_event(0xE8, 0, KEYEVENTF_KEYUP, HOOK_EVENT_MARK);
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

    /// <summary>Ctrl+A via keybd_event (select all in the target edit).</summary>
    public static void SendCtrlA()
    {
        keybd_event(VK_CONTROL, 0, 0, UIntPtr.Zero);
        keybd_event(VK_A, 0, 0, UIntPtr.Zero);
        keybd_event(VK_A, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
        keybd_event(VK_CONTROL, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
    }

    public static void SendEnter() => KeyTap(VK_RETURN, 0x1C);

    /// <summary>F2 (start in-place rename in Explorer).</summary>
    public static void SendF2() => KeyTap(VK_F2, 0x3C);
}
