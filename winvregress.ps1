$ErrorActionPreference = 'Continue'
Add-Type @"
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
public static class R4 {
  public delegate bool CB(IntPtr h, IntPtr p);
  [DllImport("user32.dll")] static extern bool EnumWindows(CB cb, IntPtr p);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll")] static extern bool IsWindow(IntPtr h);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] static extern int GetWindowText(IntPtr h, StringBuilder sb, int max);
  [DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte sc, uint fl, UIntPtr ex);
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern bool AttachThreadInput(uint a, uint b, bool c);
  [DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();
  [StructLayout(LayoutKind.Sequential)] public struct PNT { public int X; public int Y; }
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, Rt, B; }
  [StructLayout(LayoutKind.Sequential)] public struct WPLACE { public int length; public int flags; public int showCmd; public PNT minP; public PNT maxP; public RECT rc; }
  [DllImport("user32.dll")] public static extern bool GetWindowPlacement(IntPtr h, ref WPLACE wp);
  public static List<IntPtr> Top() { var l = new List<IntPtr>(); EnumWindows((h,p)=>{ l.Add(h); return true; }, IntPtr.Zero); return l; }
  public static uint Pid(IntPtr h) { uint p; GetWindowThreadProcessId(h, out p); return p; }
  public static bool Vis(IntPtr h) { return IsWindowVisible(h); }
  public static string Title(IntPtr h) { var sb = new StringBuilder(260); GetWindowText(h, sb, 260); return sb.ToString(); }
  public static bool Focus(IntPtr h) { uint cur = GetCurrentThreadId(); uint t; GetWindowThreadProcessId(h, out t); AttachThreadInput(t, cur, true); bool ok = SetForegroundWindow(h); AttachThreadInput(t, cur, false); return ok; }
  public static string Placement(IntPtr h) {
    if (!IsWindow(h)) return "gone";
    var wp = new WPLACE(); wp.length = Marshal.SizeOf(typeof(WPLACE));
    GetWindowPlacement(h, ref wp);
    return "show=" + wp.showCmd + " rc=(" + wp.rc.L + "," + wp.rc.T + ")";
  }
  public static void KeyDown(byte vk) { keybd_event(vk, 0, 0, UIntPtr.Zero); }
  public static void KeyUp(byte vk) { keybd_event(vk, 0, 2, UIntPtr.Zero); }
  public static void Tap(byte vk) { KeyDown(vk); KeyUp(vk); }
  public static void WinV() { KeyDown(0x5B); KeyDown(0x56); KeyUp(0x56); KeyUp(0x5B); }
  public static void WinLeft() { KeyDown(0x5B); Tap(0x25); KeyUp(0x5B); }
}
"@

$exe = 'C:\Users\mcqz\Desktop\Clipwise.exe'
taskkill /IM Clipwise.exe /F 2>$null | Out-Null
taskkill /IM notepad.exe /F 2>$null | Out-Null
Start-Sleep 1

$np = Start-Process notepad -PassThru
Start-Sleep 2
$npw = [IntPtr]::Zero
foreach ($h in [R4]::Top()) {
  if ([R4]::Pid($h) -in @(Get-Process -ErrorAction SilentlyContinue | ? { $_.ProcessName -match 'notepad' } | % Id)) { $npw = $h; break }
}
[R4]::Focus($npw) | Out-Null
Start-Sleep 0.5
$before = [R4]::Placement($npw)
"phaseA before: $before"

# baseline: injected Win+Left with Clipwise NOT running
[R4]::WinLeft(); Start-Sleep 1.2
$base = [R4]::Placement($npw)
$baseSnapped = $base -match 'rc=\(0,'
"phaseA baseline Win+Left snapped (no Clipwise): $baseSnapped | $base"
taskkill /IM Clipwise.exe /F 2>$null | Out-Null
Start-Sleep 0.5

Start-Process $exe
Start-Sleep 3
$p = Get-Process Clipwise
function OverlayCount { @([R4]::Top() | Where-Object { [R4]::Pid($_) -eq $p.Id -and [R4]::Vis($_) -and [R4]::Title($_) -match '^Clipwise' }).Count }

[R4]::WinV(); Start-Sleep 1.5
"1 Win+V opens Clipwise: $((OverlayCount) -gt 0)"
# make sure the overlay has focus before Esc
$ov = [IntPtr]::Zero
foreach ($h in [R4]::Top()) { if ([R4]::Pid($h) -eq $p.Id -and [R4]::Vis($h) -and [R4]::Title($h) -match '^Clipwise') { $ov = $h; break } }
if ($ov -ne [IntPtr]::Zero) { [R4]::Focus($ov) | Out-Null; Start-Sleep 0.2 }
[R4]::Tap(0x1B); Start-Sleep 1
"2 overlay closed: $((OverlayCount) -eq 0)"

$mid = [R4]::Placement($npw)
"3 notepad unchanged after Win+V+Esc: $($mid -eq $before) | $mid"

[R4]::Tap(0x26); [R4]::Tap(0x26); Start-Sleep 1      # plain Up arrows
$after = [R4]::Placement($npw)
"4 up-arrows left window alone: $($after -eq $before) | $after"

[R4]::Focus($npw) | Out-Null; Start-Sleep 0.3
[R4]::WinLeft(); Start-Sleep 1.2
$snap = [R4]::Placement($npw)
$snapped = $snap -match 'rc=\(0,'
"5 Win+Left snapped (non-V combo works): $snapped | $snap"

taskkill /IM notepad.exe /F 2>$null | Out-Null
taskkill /IM Clipwise.exe /F 2>$null | Out-Null
