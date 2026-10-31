$ErrorActionPreference = 'Continue'
Add-Type @"
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
public static class WVT {
  public delegate bool CB(IntPtr h, IntPtr p);
  [DllImport("user32.dll")] static extern bool EnumWindows(CB cb, IntPtr p);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] static extern int GetWindowText(IntPtr h, StringBuilder sb, int max);
  [DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte sc, uint fl, UIntPtr ex);
  public static List<IntPtr> Top() { var l = new List<IntPtr>(); EnumWindows((h,p)=>{ l.Add(h); return true; }, IntPtr.Zero); return l; }
  public static uint Pid(IntPtr h) { uint p; GetWindowThreadProcessId(h, out p); return p; }
  public static bool Vis(IntPtr h) { return IsWindowVisible(h); }
  public static string Title(IntPtr h) { var sb = new StringBuilder(260); GetWindowText(h, sb, 260); return sb.ToString(); }
  public static void WinV() {
    keybd_event(0x5B,0,0,UIntPtr.Zero);
    keybd_event(0x56,0,0,UIntPtr.Zero);
    keybd_event(0x56,0,2,UIntPtr.Zero);
    keybd_event(0x5B,0,2,UIntPtr.Zero);
  }
  public static void Esc() { keybd_event(0x1B,0,0,UIntPtr.Zero); keybd_event(0x1B,0,2,UIntPtr.Zero); }
}
"@

$exe = 'C:\Users\mcqz\AppData\Local\Temp\Clipwise\bin\Release\net9.0-windows10.0.19041.0\win-x64\publish\Clipwise.exe'
Remove-Item (Join-Path $env:APPDATA 'Clipwise\settings.json') -ErrorAction SilentlyContinue
Remove-Item (Join-Path $env:TEMP 'clipwise.log') -ErrorAction SilentlyContinue
taskkill /IM Clipwise.exe /F 2>$null | Out-Null
Start-Sleep 1
Start-Process $exe
Start-Sleep 3
$p = Get-Process Clipwise

function OverlayCount { @([WVT]::Top() | Where-Object { [WVT]::Pid($_) -eq $p.Id -and [WVT]::Vis($_) -and [WVT]::Title($_) -match '^Clipwise' }).Count }
function StartMenuOpen {
  $hosts = @(Get-Process -Name 'StartMenuExperienceHost' -ErrorAction SilentlyContinue | Select-Object -Expand Id)
  foreach ($h in [WVT]::Top()) {
    if ([WVT]::Pid($h) -in $hosts -and [WVT]::Vis($h)) { return $true }
  }
  return $false
}

"1 alive: $($null -ne $p)"
"2 initial overlay: $(OverlayCount) (expect 0)"
[WVT]::WinV(); Start-Sleep 1.5
"3 Win+V opens Clipwise: $((OverlayCount) -gt 0)"
"3b Start menu did NOT open: $(-not (StartMenuOpen))"
[WVT]::Esc(); Start-Sleep 1
"4 Esc closes: $((OverlayCount) -eq 0)"
[WVT]::WinV(); Start-Sleep 1.5
"5 Win+V reopens (toggle): $((OverlayCount) -gt 0)"
"5b Start menu still closed: $(-not (StartMenuOpen))"
[WVT]::Esc(); Start-Sleep 0.5
"--- app log ---"
if (Test-Path (Join-Path $env:TEMP 'clipwise.log')) { Get-Content (Join-Path $env:TEMP 'clipwise.log') }
taskkill /IM Clipwise.exe /F 2>$null | Out-Null
