using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Clipboard = System.Windows.Clipboard;
using DataObject = System.Windows.DataObject;
using FontFamily = System.Windows.Media.FontFamily;

namespace Clipwise;

public partial class MainWindow : Window
{
    private const int HotkeyId = 1;

    private readonly Store _store = new();
    private readonly Settings _settings = Settings.Load();
    private ListCollectionView? _view;
    private HwndSource? _host;
    private Native.LowLevelKeyboardProc? _llHookProc;
    private IntPtr _llHook;
    private bool _deferWinDown;   // holding a real Win-down until we know it's Win+V
    private uint _deferredVk;
    private uint _deferredScan;
    private bool _intercepted;     // a Win+V was consumed; await + eat the trailing Win-up
    private IntPtr _prevForeground;
    private bool _clipboardBusy;
    private bool _winvRefreshing;
    private DateTime _lastWinvRefresh = DateTime.MinValue;
    private readonly DispatcherTimer _saveTimer;
    private readonly DispatcherTimer _winvTimer;
    private static readonly string MarkerFile =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Clipwise", "first-run.txt");

    public static MainWindow? Instance { get; private set; }
    public bool HotkeyOk { get; private set; }

    /// <summary>"Ctrl+`" when it was free, "Ctrl+Shift+`" when another app owns Ctrl+` (e.g. Ditto).</summary>
    public string? HotkeyLabel { get; private set; }

    /// <summary>When true, Win+V opens this app instead of the Windows clipboard history.</summary>
    public bool WinVOverride { get; private set; }

    public MainWindow()
    {
        InitializeComponent();
        Instance = this;

        _store.Load();

        FontFamily = (FontFamily)FindResource("AppFont");
        FooterText.Text = "↵ Paste  ·  Del Remove  ·  P Pin  ·  Esc Close";

        _view = (ListCollectionView)CollectionViewSource.GetDefaultView(_store.Items);
        _view.Filter = FilterItem;
        ClipList.ItemsSource = _view;

        _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
        _saveTimer.Tick += (_, _) => { _saveTimer.Stop(); _store.Save(); };

        _winvTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(60) };
        _winvTimer.Tick += async (_, _) =>
        {
            _winvTimer.Stop();
            if (!IsVisible) await RefreshWinVAsync(); // keep history fresh while idle
        };

        WinVService.SubscribeToHistoryChanged(() =>
        {
            _winvTimer.Stop();
            _winvTimer.Start();
        });

        WinVOverride = _settings.WinVOverride;

        // A crashed previous session could have left a synthetic ALT stuck down
        // (system-wide). Clear it so arrow keys behave from the start.
        if ((Native.GetAsyncKeyState(0x12) & 0x8000) != 0)
        {
            try
            {
                Native.KeyTap(0x12, 0x38);
                LogStartup("unstuck leftover ALT");
            }
            catch { /* non-fatal */ }
        }

        InstallWinVHook();
        SetupHostWindow();
        RefreshWinVAsync().ContinueWith(_ => Dispatcher.InvokeAsync(UpdateStatus), TaskScheduler.Default);

        if (_store.Items.Count == 0 && !File.Exists(MarkerFile))
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(MarkerFile)!);
                File.WriteAllText(MarkerFile, DateTime.UtcNow.ToString("O"));
            }
            catch { /* non-fatal */ }
            Dispatcher.BeginInvoke(DispatcherPriority.Background, () => ShowOverlay());
        }
    }

    // ---------- plumbing ----------

    private void SetupHostWindow()
    {
        var p = new HwndSourceParameters("ClipwiseHost")
        {
            WindowStyle = unchecked((int)0x80000000), // WS_POPUP
            ExtendedWindowStyle = (int)(Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE),
            Width = 0,
            Height = 0,
            PositionX = -32000,
            PositionY = -32000,
        };
        _host = new HwndSource(p);
        _host.AddHook(Hook);

        HotkeyOk = Native.RegisterHotKey(_host.Handle, HotkeyId, Native.MOD_CONTROL | Native.MOD_NOREPEAT, Native.VK_OEM3);
        bool ctrlOnly = HotkeyOk;
        if (!HotkeyOk)
            HotkeyOk = Native.RegisterHotKey(_host.Handle, HotkeyId,
                Native.MOD_CONTROL | Native.MOD_SHIFT | Native.MOD_NOREPEAT, Native.VK_OEM3);

        HotkeyLabel = HotkeyOk ? (ctrlOnly ? "Ctrl+`" : "Ctrl+Shift+`") : null;
        LogStartup($"hotkey={HotkeyLabel ?? "FAILED"} host=0x{_host.Handle.ToInt64():X}");
        LogStartup("listener=" + Native.AddClipboardFormatListener(_host.Handle));
    }

    private static void LogStartup(string line)
    {
        try
        {
            System.IO.File.AppendAllText(
                System.IO.Path.Combine(System.IO.Path.GetTempPath(), "clipwise.log"),
                $"[{DateTime.Now:HH:mm:ss}] {line}" + Environment.NewLine);
        }
        catch { /* best effort */ }
    }

    private void InstallWinVHook()
    {
        if (!WinVOverride) return;
        try
        {
            _llHookProc = LlHook;
            // Installed on the UI thread so the callback runs on our message pump.
            _llHook = Native.SetWindowsHookEx(Native.WH_KEYBOARD_LL, _llHookProc,
                Native.GetModuleHandle(null), 0);
            if (_llHook == IntPtr.Zero)
                LogStartup("winv hook failed: " + System.Runtime.InteropServices.Marshal.GetLastWin32Error());
        }
        catch (Exception ex)
        {
            LogStartup("winv hook error: " + ex.Message);
        }
    }

    /// <summary>
    /// Open the overlay a beat after the injected Win+V key events so the shell
    /// is past its input processing; Activate() wins the foreground from a clean
    /// pump cycle. (Native Win+V also opens on release, so this feels identical.)
    /// </summary>
    private void DeferOverlayOpen()
    {
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background,
            new Action(async () =>
            {
                await System.Threading.Tasks.Task.Delay(80);
                if (WinVOverride) ToggleOverlay();
            }));
    }

    /// <summary>
    /// Swallows Win+V before the shell can open its clipboard history and toggles
    /// our overlay instead. WH_KEYBOARD_LL runs ahead of system hotkey processing.
    /// </summary>
    private IntPtr LlHook(int nCode, UIntPtr wParam, IntPtr lParam)
    {
        if (nCode != 0 || !WinVOverride) return Native.CallNextHookEx(_llHook, nCode, wParam, lParam);

        var k = System.Runtime.InteropServices.Marshal.PtrToStructure<Native.KBDLLHOOKSTRUCT>(lParam);
        bool isDown = wParam == Native.WM_KEYDOWN || wParam == Native.WM_SYSKEYDOWN;
        bool isUp = wParam == Native.WM_KEYUP || wParam == Native.WM_SYSKEYUP;
        if (Environment.GetEnvironmentVariable("CLIPWISE_HOOKLOG") == "1")
            LogStartup($"hook vk={k.vkCode:X2} msg={wParam:X4} inj={(k.flags & Native.LLKHF_INJECTED) != 0} def={_deferWinDown} int={_intercepted}");

        // Our own replayed events (tagged with the magic extra-info): pass through.
        if (k.dwExtraInfo == Native.HOOK_EVENT_MARK) return Native.CallNextHookEx(_llHook, nCode, wParam, lParam);

        bool isWinKey = k.vkCode == Native.VK_LWIN || k.vkCode == Native.VK_RWIN;

        // After an intercepted Win+V, eat the trailing V-up and Win-up so the
        // shell never learns a Win key was pressed (a swallowed Win-up alone
        // leaves the shell believing Win is held, breaking Win+arrow combos).
        if (_intercepted)
        {
            if (isWinKey && isUp || k.vkCode == Native.VK_V)
                return new IntPtr(1);
            _intercepted = false; // any other key ends the swallow window
        }

        if (isWinKey && isDown)
        {
            if (!_deferWinDown)
            {
                _deferWinDown = true;
                _deferredVk = k.vkCode;
                _deferredScan = k.scanCode;
            }
            return new IntPtr(1); // hold until we know the combo
        }

        if (_deferWinDown)
        {
            if (k.vkCode == Native.VK_V)
            {
                bool ctrlOrAlt =
                    (Native.GetAsyncKeyState(0x11) & 0x8000) != 0 ||
                    (Native.GetAsyncKeyState(0x12) & 0x8000) != 0;
                if (!ctrlOrAlt)
                {
                    if (isDown) DeferOverlayOpen();
                    _deferWinDown = false;
                    _intercepted = true;
                    return new IntPtr(1); // consume this V event
                }
            }

            // Not a plain Win+V: replay the held Win-down, and for a key press
            // re-inject the key itself right after it, so the shell sees the
            // combo in the correct order (Win-down, then key). Swallow the real
            // down to avoid a duplicated press; the real key-up still passes.
            ReplayDeferredWin(); // tagged Win-down
            _deferWinDown = false;
            if (isWinKey && isUp) return Native.CallNextHookEx(_llHook, nCode, wParam, lParam); // tap: real up completes it
            if (isDown)
            {
                Native.keybd_event((byte)k.vkCode, (byte)k.scanCode, 0, Native.HOOK_EVENT_MARK);
                return new IntPtr(1);
            }
            return Native.CallNextHookEx(_llHook, nCode, wParam, lParam);
        }

        return Native.CallNextHookEx(_llHook, nCode, wParam, lParam);
    }

    private void ReplayDeferredWin()
    {
        Native.keybd_event((byte)_deferredVk, (byte)_deferredScan, 0, Native.HOOK_EVENT_MARK);
    }

    /// <summary>Toggle the Win+V replacement from the tray menu.</summary>
    public void SetWinVOverride(bool on)
    {
        if (WinVOverride == on) return;
        WinVOverride = on;
        _settings.WinVOverride = on;
        _settings.Save();
        if (on) InstallWinVHook();
        else if (_llHook != IntPtr.Zero)
        {
            Native.UnhookWindowsHookEx(_llHook);
            _llHook = IntPtr.Zero;
        }
        UpdateStatus();
    }

    private IntPtr Hook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        switch (msg)
        {
            case Native.WM_HOTKEY when wParam.ToInt32() == HotkeyId:
                handled = true;
                ToggleOverlay();
                break;
            case Native.WM_CLIPBOARDUPDATE:
                handled = true;
                _ = CaptureClipboardAsync();
                break;
        }
        return IntPtr.Zero;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        if (PresentationSource.FromVisual(this) is HwndSource src)
            src.AddHook(WndProc);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        // Reserved for window-level messages (nothing needed today).
        return IntPtr.Zero;
    }

    private bool FilterItem(object o)
    {
        if (SearchBox.Text.Length == 0) return true;
        var clip = (ClipItem)o;
        var term = SearchBox.Text;
        return clip.Preview.Contains(term, StringComparison.OrdinalIgnoreCase) && !(clip.IsImage && !clip.IsPinned);
    }

    // ---------- clipboard capture ----------

    private async Task CaptureClipboardAsync()
    {
        if (_clipboardBusy) return;
        _clipboardBusy = true;
        try
        {
            await Task.Delay(60); // let the source app finish its writes
            ClipItem? clip = null;

            if (Clipboard.ContainsImage())
            {
                var img = Clipboard.GetImage();
                if (img != null)
                {
                    using var ms = new MemoryStream();
                    var enc = new PngBitmapEncoder();
                    enc.Frames.Add(BitmapFrame.Create(img));
                    enc.Save(ms);
                    var bytes = ms.ToArray();
                    clip = new ClipItem
                    {
                        Id = Guid.NewGuid().ToString("N"),
                        Kind = ClipKind.Image,
                        Hash = ClipItem.HashBytes(bytes),
                        Timestamp = DateTimeOffset.Now,
                        Size = bytes.Length,
                        Image = img,
                        ImageBytes = bytes,
                    };
                }
            }
            else if (Clipboard.ContainsText())
            {
                var text = Clipboard.GetText();
                if (!string.IsNullOrWhiteSpace(text))
                {
                    clip = new ClipItem
                    {
                        Id = Guid.NewGuid().ToString("N"),
                        Kind = ClipKind.Text,
                        Text = text,
                        Hash = ClipItem.HashText(text),
                        Timestamp = DateTimeOffset.Now,
                        Size = System.Text.Encoding.UTF8.GetByteCount(text),
                    };
                }
            }

            if (clip != null)
            {
                _store.Upsert(clip);
                _saveTimer.Stop();
                _saveTimer.Start();
                if (IsVisible)
                {
                    _view?.Refresh();
                    ClipList.SelectedIndex = 0; // newest capture becomes the paste target
                    UpdateCounts();
                }
            }
        }
        catch
        {
            // Clipboard races are normal (other apps holding it); drop the event.
        }
        finally
        {
            _clipboardBusy = false;
        }
    }

    // ---------- Win+V integration ----------

    private async Task RefreshWinVAsync()
    {
        if (_winvRefreshing) return;
        if ((DateTime.Now - _lastWinvRefresh).TotalSeconds < 30) return;
        _winvRefreshing = true;
        _lastWinvRefresh = DateTime.Now;
        try
        {
            await WinVService.RefreshAsync(_store);
            Dispatcher.Invoke(() =>
            {
                if (IsVisible) { _view?.Refresh(); UseLayoutRounding = true; UpdateCounts(); }
                UpdateStatus();
            });
        }
        finally
        {
            _winvRefreshing = false;
        }
    }

    // ---------- overlay lifecycle ----------

    public void ShowOverlay()
    {
        _prevForeground = Native.GetForegroundWindow();

        Native.GetCursorPos(out var p);
        var dpi = (double)Native.GetDpiForSystem() / 96.0;
        if (dpi <= 0) dpi = 1;

        double left = p.X / dpi - Width / 2;
        double top = p.Y / dpi - 24;
        var wa = SystemParameters.WorkArea;
        Left = Math.Clamp(left, wa.Left, wa.Right - Width);
        Top = Math.Clamp(top, wa.Top, wa.Bottom - Height);

        SearchBox.Clear();
        _view?.Refresh();
        UpdateCounts();
        _ = RefreshWinVAsync();

        Show();
        Activate();
        // The LL-hook path opens mid-injected-input where Activate can lose the
        // foreground lock; the ALT-tap inside ForceForeground reclaims it.
        Native.ForceForeground(new System.Windows.Interop.WindowInteropHelper(this).Handle);
        if (_view is { IsEmpty: false })
        {
            ClipList.SelectedIndex = 0;
            ClipList.ScrollIntoView(ClipList.SelectedItem);
        }
        SearchBox.Focus();
    }

    private void HideOverlay()
    {
        if (!IsVisible) return;
        Hide();
        if (_prevForeground != IntPtr.Zero)
            Native.RestoreForeground(_prevForeground);
        _winvTimer.Stop();
        _winvTimer.Start();
    }

    private void ToggleOverlay()
    {
        if (IsVisible) HideOverlay();
        else ShowOverlay();
    }

    // ---------- actions ----------

    private async Task PasteAsync(ClipItem? clip)
    {
        if (clip == null) return;
        HideOverlay();

        // Paste from the cached bytes when we have them (fast, exact). The Win+V
        // history API is slow (seconds per call), so only use it as a fallback
        // for items we never captured locally.
        var ok = false;
        try
        {
            if (clip.Kind == ClipKind.Text && !string.IsNullOrEmpty(clip.Text))
            {
                var pkg = new DataObject();
                pkg.SetText(clip.Text);
                Clipboard.SetDataObject(pkg, true);
                ok = true;
            }
            else if (clip.Image != null)
            {
                var pkg = new DataObject();
                pkg.SetImage(clip.Image);
                Clipboard.SetDataObject(pkg, true);
                ok = true;
            }
        }
        catch { /* fall through to the history API on next line */ }

        if (!ok && clip.FromWinV && clip.HistoryId != null)
            ok = await WinVService.RestoreAsync(clip);
        if (!ok) return;

        await Task.Delay(60);
        Native.SendCtrlV();
    }

    private void RemoveSelected()
    {
        if (ClipList.SelectedItem is not ClipItem clip) return;
        _store.Remove(clip);
        if (clip.FromWinV && clip.HistoryId != null)
            _ = WinVService.DeleteAsync(clip);
        _saveTimer.Stop();
        _saveTimer.Start();
        UpdateCounts();
    }

    private void TogglePinSelected()
    {
        if (ClipList.SelectedItem is not ClipItem clip) return;
        _store.TogglePin(clip);
        UpdateCounts();
    }

    private void MoveSelection(int delta)
    {
        if (_view == null || _view.IsEmpty) return;
        int idx = ClipList.SelectedIndex;
        idx = Math.Clamp(idx + delta, 0, _view.Count - 1);
        ClipList.SelectedIndex = idx;
        ClipList.ScrollIntoView(ClipList.SelectedItem);
    }

    // ---------- UI state ----------

    private void UpdateCounts()
    {
        var pinned = _store.Items.Count(i => i.IsPinned);
        CountChip.Text = $"{_view?.Count ?? 0} shown · {_store.Items.Count} total · {pinned} pinned";
    }

    private void UpdateStatus()
    {
        if (!WinVService.IsSupported)
        {
            StatusText.Text = "Win+V: unavailable — " + (WinVService.LastError ?? "unknown");
            return;
        }
        StatusText.Text = $"Win+V history: {WinVService.LastItemCount} items";
        if (WinVOverride) StatusText.Text += " · Win+V redirected";
        if (HotkeyOk) FooterText.Text = $"↵ Paste  ·  Del Remove  ·  P Pin  ·  Esc Close  ·  {HotkeyLabel} / Win+V open";
        else FooterText.Text = "hotkey registration failed (Ctrl+` and Ctrl+Shift+` both taken)";
    }

    // ---------- events ----------

    private void SearchBox_OnTextChanged(object sender, TextChangedEventArgs e)
    {
        _view?.Refresh();
        UpdateCounts();
        if (_view is { IsEmpty: false } && ClipList.SelectedIndex < 0)
            ClipList.SelectedIndex = 0;
    }

    private void ClipList_OnMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        if (e.OriginalSource is not DependencyObject dep) return;
        var item = ItemsControl.ContainerFromElement(ClipList, dep) as ListBoxItem;
        if (item?.DataContext is ClipItem clip)
            _ = PasteAsync(clip);
    }

    private void ClipList_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ClipList.SelectedItem is ClipItem clip)
            ClipList.ScrollIntoView(clip);
    }

    private void Window_OnPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        // Click outside the list (e.g. on the surface) also closes, like Win+V.
        if (e.OriginalSource is DependencyObject dep &&
            ItemsControl.ContainerFromElement(ClipList, dep) == null &&
            dep is not System.Windows.Controls.Primitives.ScrollBar)
        {
            HideOverlay();
        }
    }

    private void Window_OnPreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Down:
                MoveSelection(1);
                e.Handled = true;
                break;
            case Key.Up:
                MoveSelection(-1);
                e.Handled = true;
                break;
            case Key.Delete when SearchBox.Text.Length == 0:
                RemoveSelected();
                e.Handled = true;
                break;
            case Key.P when SearchBox.Text.Length == 0 && Keyboard.Modifiers == ModifierKeys.None:
                TogglePinSelected();
                e.Handled = true;
                break;
        }
    }

    private void Window_OnKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                HideOverlay();
                e.Handled = true;
                break;
            case Key.Enter when ClipList.SelectedItem is ClipItem clip:
                _ = PasteAsync(clip);
                e.Handled = true;
                break;
        }
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        // App quits via the tray menu (App.Shutdown), not by closing the overlay.
        e.Cancel = true;
        HideOverlay();
        base.OnClosing(e);
    }

    public void ShutdownHost()
    {
        if (_llHook != IntPtr.Zero)
        {
            Native.UnhookWindowsHookEx(_llHook);
            _llHook = IntPtr.Zero;
        }
        _store.Save();
        if (_host != null)
        {
            Native.RemoveClipboardFormatListener(_host.Handle);
            Native.UnregisterHotKey(_host.Handle, HotkeyId);
            _host.RemoveHook(Hook);
            _host.Dispose();
            _host = null;
        }
        Instance = null;
    }
}
