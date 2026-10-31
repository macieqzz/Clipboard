$ErrorActionPreference = 'Continue'
Add-Type @"
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
public static class W7 {
  public delegate bool CB(IntPtr h, IntPtr p);
  [DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
  [DllImport("user32.dll")] static extern bool EnumWindows(CB cb, IntPtr p);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] static extern int GetWindowText(IntPtr h, StringBuilder sb, int max);
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);
  [DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();
  [DllImport("user32.dll", SetLastError=true)] public static extern uint SendInput(uint n, INPUT[] p, int cb);
  [StructLayout(LayoutKind.Sequential)] public struct KEYBDINPUT { public ushort wVk; public ushort wScan; public uint dwFlags; public uint time; public UIntPtr dwExtraInfo; }
  [StructLayout(LayoutKind.Explicit)] public struct INPUTUNION { [FieldOffset(0)] public KEYBDINPUT ki; }
  [StructLayout(LayoutKind.Sequential)] public struct INPUT { public uint type; public INPUTUNION U; }
  public static List<IntPtr> EnumTop() { var l = new List<IntPtr>(); EnumWindows((h,p)=>{ l.Add(h); return true; }, IntPtr.Zero); return l; }
  public static uint PidOf(IntPtr h) { uint pid; GetWindowThreadProcessId(h, out pid); return pid; }
  public static bool Vis(IntPtr h) { return IsWindowVisible(h); }
  public static string Title(IntPtr h) { var sb = new StringBuilder(260); GetWindowText(h, sb, 260); return sb.ToString(); }
  public static bool Focus(IntPtr h) {
    uint cur = GetCurrentThreadId(); uint tgt; GetWindowThreadProcessId(h, out tgt);
    AttachThreadInput(tgt, cur, true); bool ok = SetForegroundWindow(h); AttachThreadInput(tgt, cur, false);
    return ok;
  }
  public static void PressKey(ushort vk) {
    var arr = new INPUT[2];
    arr[0].type = 1; arr[0].U.ki = new KEYBDINPUT { wVk = vk };
    arr[1].type = 1; arr[1].U.ki = new KEYBDINPUT { wVk = vk, dwFlags = 2 };
    SendInput(2, arr, Marshal.SizeOf(typeof(INPUT)));
  }
}
"@
Add-Type @"
using System;
using System.Runtime.InteropServices;
using System.Text;
public static class C7 {
  [StructLayout(LayoutKind.Sequential)] public struct COORD { public short X; public short Y; }
  [StructLayout(LayoutKind.Sequential)] public struct SMALLRECT { public short Left, Top, Right, Bottom; }
  [StructLayout(LayoutKind.Sequential)] public struct INFO { public COORD Size; public COORD Cursor; public ushort Attr; public SMALLRECT Win; public COORD MaxWindow; }
  [DllImport("kernel32.dll", SetLastError=true)] public static extern bool FreeConsole();
  [DllImport("kernel32.dll", SetLastError=true)] public static extern bool AttachConsole(uint pid);
  [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)] public static extern IntPtr CreateFile(string name, uint access, uint share, IntPtr sec, uint disp, uint flags, IntPtr tmpl);
  [DllImport("kernel32.dll")] public static extern bool GetConsoleScreenBufferInfo(IntPtr h, out INFO i);
  [DllImport("kernel32.dll", CharSet=CharSet.Unicode)] public static extern bool ReadConsoleOutputCharacter(IntPtr h, StringBuilder sb, uint len, COORD c, out uint read);
  public static string ReadCursorRow(uint pid) {
    FreeConsole();
    if (!AttachConsole(pid)) return "";
    try {
      IntPtr h = CreateFile("CONOUT$", 0xC0000000, 7, IntPtr.Zero, 3, 0, IntPtr.Zero);
      INFO info; GetConsoleScreenBufferInfo(h, out info);
      short cols = info.Size.X; short y = info.Cursor.Y;
      var sb = new StringBuilder(cols + 2);
      COORD c; c.X = 0; c.Y = y;
      uint got; ReadConsoleOutputCharacter(h, sb, (uint)cols, c, out got);
      return sb.ToString();
    } finally { FreeConsole(); }
  }
}
"@

function Is-OverlayVisible([int]$clipPid) {
  foreach ($h in [W7]::EnumTop()) {
    if ([W7]::PidOf($h) -eq $clipPid -and [W7]::Vis($h) -and [W7]::Title($h) -match '^Clipwise') { return $true }
  }
  return $false
}
function Get-OverlayHwnd([int]$clipPid) {
  foreach ($h in [W7]::EnumTop()) {
    if ([W7]::PidOf($h) -eq $clipPid -and [W7]::Vis($h) -and [W7]::Title($h) -match '^Clipwise') { return $h }
  }
  return [IntPtr]::Zero
}
function Send-Hotkey {
  [W7]::keybd_event(0x11,0,0,[UIntPtr]::Zero)
  [W7]::keybd_event(0xC0,0,0,[UIntPtr]::Zero)
  [W7]::keybd_event(0xC0,0,2,[UIntPtr]::Zero)
  [W7]::keybd_event(0x11,0,2,[UIntPtr]::Zero)
}
function Send-Esc { [W7]::keybd_event(0x1B,0,0,[UIntPtr]::Zero); [W7]::keybd_event(0x1B,0,2,[UIntPtr]::Zero) }

$exe   = 'C:\Users\mcqz\AppData\Local\Temp\Clipwise\bin\Release\net9.0-windows10.0.19041.0\win-x64\publish\Clipwise.exe'
$store = Join-Path $env:APPDATA 'Clipwise\store.json'
$log   = Join-Path $env:TEMP 'clipwise.log'
Remove-Item $log   -ErrorAction SilentlyContinue
Remove-Item $store -ErrorAction SilentlyContinue
taskkill /IM Clipwise.exe /F 2>$null | Out-Null
taskkill /IM cmd.exe /F 2>$null | Out-Null
Start-Sleep 1

Start-Process $exe
Start-Sleep 3
$p = Get-Process Clipwise -ErrorAction SilentlyContinue
Write-Output "1 proc alive: $($null -ne $p)"

$token = 'clipwise-smoke-' + (Get-Date -Format HHmmss)
Set-Clipboard -Value $token
Start-Sleep 3
$json = if (Test-Path $store) { Get-Content $store -Raw } else { '' }
Write-Output "2 live capture in store: $($json.Contains($token)) | Win+V entries: $([regex]::Matches($json, '"FromWinV":true').Count)"

Send-Hotkey; Start-Sleep 1.5
Write-Output "3 overlay opens on hotkey: $(Is-OverlayVisible $p.Id)"
$ov = Get-OverlayHwnd $p.Id
if ($ov -ne [IntPtr]::Zero) { [W7]::Focus($ov) | Out-Null; Send-Esc; Start-Sleep 1 }
Write-Output "4 overlay closes on Esc: $(-not (Is-OverlayVisible $p.Id))"

function Type-Text([string]$s) {
  foreach ($ch in $s.ToCharArray()) {
    $vk = [int][char]::ToUpper($ch)
    if ($vk -ge 0x41 -and $vk -le 0x5A) {
      [W7]::keybd_event($vk,0,0,[UIntPtr]::Zero); [W7]::keybd_event($vk,0,2,[UIntPtr]::Zero)
    }
  }
}
$EXE5 = 'C:\Users\mcqz\AppData\Local\Temp\Clipwise\bin\Release\net9.0-windows10.0.19041.0\win-x64\publish\Clipwise.exe'

$cmd = Start-Process cmd -ArgumentList '/k title ClipwiseSmoke' -PassThru
Start-Sleep 2
$cwin = [IntPtr]::Zero
foreach ($h in [W7]::EnumTop()) { if ([W7]::Title($h) -match 'ClipwiseSmoke') { $cwin = $h; break } }
Write-Output "5 cmd window found: $($cwin -ne [IntPtr]::Zero) hwnd=0x$('{0:X}' -f $cwin.ToInt64())"
[W7]::Focus($cwin) | Out-Null
Start-Sleep 0.4
Set-Clipboard -Value 'clipwisecmdtarget'
Start-Sleep 1.2            # let the listener capture it before opening
Send-Hotkey; Start-Sleep 2
Write-Output "6 overlay for paste: $(Is-OverlayVisible $p.Id)"
$ov = Get-OverlayHwnd $p.Id
if ($ov -ne [IntPtr]::Zero) {
  for ($try = 1; $try -le 3; $try++) {
    [W7]::Focus($ov) | Out-Null
    Start-Sleep -Milliseconds 200
    Type-Text 'clipwisecmd'          # filter to the single target item
    Start-Sleep -Milliseconds 300
    [W7]::keybd_event(0x0D,0,0,[UIntPtr]::Zero)   # Enter down
    [W7]::keybd_event(0x0D,0,2,[UIntPtr]::Zero)   # Enter up
    Start-Sleep 1
    if (-not (Is-OverlayVisible $p.Id)) { Write-Output "   enter accepted on try $try"; break }
  }
}
Start-Sleep 2
$row = [C7]::ReadCursorRow([uint32]$cmd.Id)
Write-Output "7 cmd cursor line contains pasted: $($row.Contains('clipwisecmdtarget')) | got: '$($row.Trim())'"
taskkill /IM cmd.exe /F 2>$null | Out-Null
taskkill /IM Clipwise.exe /F 2>$null | Out-Null
