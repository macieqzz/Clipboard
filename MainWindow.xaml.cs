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
    private bool _swallowingV;     // a Win+V V-down was eaten; eat its repeats and V-up too
    private IntPtr _prevForeground;
    private IntPtr _renameEdit;    // focused Explorer rename box when the overlay opened
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
        FooterText.Text = "Del Remove  ·  P Pin  ·  Esc Close";
        if (_settings.WindowWidth >= 340) Width = _settings.WindowWidth;
        if (_settings.WindowHeight >= 260) Height = _settings.WindowHeight;
        SizeChanged += (_, _) =>
        {
            if (!IsLoaded || WindowState != WindowState.Normal) return;
            _settings.WindowWidth = ActualWidth;
            _settings.WindowHeight = ActualHeight;
            _settings.Save();
        };


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
    /// Win+V override without ever holding back a key (same approach as Raycast /
    /// AutoHotkey). Win events always pass through untouched, so the system's
    /// key state can never get out of sync. Only the V press of a plain Win+V is
    /// eaten, and a no-op "mask" key is injected so the shell does not open Start
    /// when Win is released.
    /// </summary>
    private IntPtr LlHook(int nCode, UIntPtr wParam, IntPtr lParam)
    {
        if (nCode != 0 || !WinVOverride) return Native.CallNextHookEx(_llHook, nCode, wParam, lParam);

        var k = System.Runtime.InteropServices.Marshal.PtrToStructure<Native.KBDLLHOOKSTRUCT>(lParam);
        if (k.vkCode != Native.VK_V || k.dwExtraInfo == Native.HOOK_EVENT_MARK)
            return Native.CallNextHookEx(_llHook, nCode, wParam, lParam);

        bool isDown = wParam == Native.WM_KEYDOWN || wParam == Native.WM_SYSKEYDOWN;
        if (!isDown)
        {
            if (!_swallowingV) return Native.CallNextHookEx(_llHook, nCode, wParam, lParam);
            _swallowingV = false;
            return new IntPtr(1); // V-up of an eaten Win+V
        }

        if (_swallowingV) return new IntPtr(1); // auto-repeat while held

        bool winHeld = (Native.GetAsyncKeyState(Native.VK_LWIN) & 0x8000) != 0 ||
                       (Native.GetAsyncKeyState(Native.VK_RWIN) & 0x8000) != 0;
        bool ctrlOrAlt = (Native.GetAsyncKeyState(0x11) & 0x8000) != 0 ||
                         (Native.GetAsyncKeyState(0x12) & 0x8000) != 0;
        if (!winHeld || ctrlOrAlt) return Native.CallNextHookEx(_llHook, nCode, wParam, lParam);

        _swallowingV = true;
        Native.SendMaskKey();
        DeferOverlayOpen();
        return new IntPtr(1);
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
        if (msg == Native.WM_NCHITTEST && PresentationSource.FromVisual(this) != null)
        {
            int x = unchecked((short)(long)lParam);
            int y = unchecked((short)((long)lParam >> 16));
            var pt = PointFromScreen(new System.Windows.Point(x, y));

            const double border = 8.0;
            bool left = pt.X <= border;
            bool right = pt.X >= ActualWidth - border;
            bool top = pt.Y <= border;
            bool bottom = pt.Y >= ActualHeight - border;

            if (top && left) { handled = true; return (IntPtr)Native.HTTOPLEFT; }
            if (top && right) { handled = true; return (IntPtr)Native.HTTOPRIGHT; }
            if (bottom && left) { handled = true; return (IntPtr)Native.HTBOTTOMLEFT; }
            if (bottom && right) { handled = true; return (IntPtr)Native.HTBOTTOMRIGHT; }
            if (left) { handled = true; return (IntPtr)Native.HTLEFT; }
            if (right) { handled = true; return (IntPtr)Native.HTRIGHT; }
            if (top) { handled = true; return (IntPtr)Native.HTTOP; }
            if (bottom) { handled = true; return (IntPtr)Native.HTBOTTOM; }
        }
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
        // Renaming a file/folder? Remember the edit box so the paste can land there.
        _renameEdit = Native.GetRenameEdit(_prevForeground);

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

    public void ToggleOverlay()
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

        // Explorer's in-place rename: the overlay took the focus, so hand it back
        // to the name box, replace the whole name with the clip and confirm.
        if (_renameEdit != IntPtr.Zero && clip.Kind == ClipKind.Text)
        {
            if (Native.FocusChildWindow(_renameEdit))
            {
                await Task.Delay(60);
                Native.SendCtrlA();
                Native.SendCtrlV();
                await Task.Delay(120);
                Native.SendEnter();
                return;
            }
        }

        Native.SendCtrlV();
    }

    private void RemoveSelected()
    {
        if (ClipList.SelectedItem is not ClipItem clip) return;
        int idx = ClipList.SelectedIndex;
        _store.Remove(clip);
        if (clip.FromWinV && clip.HistoryId != null)
            _ = WinVService.DeleteAsync(clip);
        _saveTimer.Stop();
        _saveTimer.Start();
        UpdateCounts();

        // The next item slides into the removed slot; keep the cursor there
        // (or on the new last item) and keep typing focus in the search box.
        if (_view is { IsEmpty: false })
        {
            ClipList.SelectedIndex = Math.Min(idx, _view.Count - 1);
            ClipList.ScrollIntoView(ClipList.SelectedItem);
        }
        SearchBox.Focus();
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
        if (!HotkeyOk)
        {
            FooterText.Text = "Hotkey registration failed (Ctrl+` and Ctrl+Shift+` taken)";
            StatusText.Text = "";
            return;
        }

        FooterText.Text = "Del Remove  ·  P Pin  ·  Esc Close";

        if (!WinVService.IsSupported && !string.IsNullOrEmpty(WinVService.LastError))
        {
            StatusText.Text = "Win+V off";
            StatusText.ToolTip = WinVService.LastError;
        }
        else
        {
            StatusText.Text = "";
            StatusText.ToolTip = null;
        }
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
        if (e.OriginalSource is DependencyObject dep)
        {
            if (dep is Visual v && (SearchBox.IsAncestorOf(v) || v == SearchBox))
                return;
            // Clicks land on the Thumb/Track/RepeatButton parts, not the ScrollBar itself.
            for (var p = dep; p is Visual; p = VisualTreeHelper.GetParent(p))
                if (p is System.Windows.Controls.Primitives.ScrollBar) return;
            if (ItemsControl.ContainerFromElement(ClipList, dep) == null)
            {
                HideOverlay();
            }
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
