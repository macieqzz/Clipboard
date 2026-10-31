using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows;
using System.Windows.Threading;
using WinForms = System.Windows.Forms;
using Application = System.Windows.Application;

namespace Clipwise;

public partial class App : Application
{
    private System.Threading.Mutex? _mutex;
    private WinForms.NotifyIcon? _tray;
    private DispatcherTimer? _trayTimer;

    protected override void OnStartup(StartupEventArgs e)
    {
        _mutex = new System.Threading.Mutex(true, "Clipwise.SingleInstance", out bool createdNew);
        if (!createdNew)
        {
            Shutdown();
            return;
        }

        base.OnStartup(e);
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        DispatcherUnhandledException += (_, args) =>
        {
            try { Log(args.Exception); } catch { /* best effort */ }
            args.Handled = true;
        };

        var window = new MainWindow();
        // Never shown at startup: lives in the tray until Ctrl+`.
        _ = window;

        SetupTray();
    }

    private void SetupTray()
    {
        var hk = Clipwise.MainWindow.Instance?.HotkeyLabel ?? "Ctrl+`";
        var menu = new WinForms.ContextMenuStrip();
        menu.Items.Add($"Open  ({hk})", null, (_, _) => Clipwise.MainWindow.Instance?.ShowOverlay());
        var winVItem = new WinForms.ToolStripMenuItem
        {
            Text = "Replace Win+V (open Clipwise instead)",
            CheckOnClick = true,
            Checked = Clipwise.MainWindow.Instance?.WinVOverride ?? false,
        };
        winVItem.CheckedChanged += (_, _) => Clipwise.MainWindow.Instance?.SetWinVOverride(winVItem.Checked);
        menu.Items.Add(winVItem);
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => Shutdown());

        _tray = new WinForms.NotifyIcon
        {
            Text = $"Clipwise — {hk} / Win+V",
            Icon = MakeIcon(),
            ContextMenuStrip = menu,
            Visible = true,
        };
        // Prefer the app's own embedded icon (crisper + consistent with the exe).
        try
        {
            using var ext = System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath!);
            if (ext != null) _tray.Icon = ext;
        }
        catch { /* keep the drawn glyph */ }
        _tray.DoubleClick += (_, _) => Clipwise.MainWindow.Instance?.ShowOverlay();

        // Tray icons are sometimes slow to appear; ping the shell once.
        _trayTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _trayTimer.Tick += (_, _) =>
        {
            _trayTimer.Stop();
            try { _tray.Visible = true; } catch { /* non-fatal */ }
        };
        _trayTimer.Start();
    }

    private static System.Drawing.Icon MakeIcon()
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            using var pen = new Pen(Color.FromArgb(240, 240, 240), 2.4f);
            using var brush = new SolidBrush(Color.FromArgb(240, 240, 240));
            using var dark = new SolidBrush(Color.FromArgb(32, 32, 32));
            // clipboard board
            var rect = new RectangleF(5, 4, 22, 25);
            g.FillRoundedRectangle(dark, rect, 3);
            g.DrawRoundedRectangle(pen, rect, 3);
            // clip tab
            g.FillRectangle(brush, 13, 2, 6, 5);
            // a line of 'text'
            g.DrawLine(pen, 11, 14, 21, 14);
            g.DrawLine(new Pen(Color.FromArgb(240, 240, 240), 2f), 11, 20, 18, 20);
        }
        IntPtr hIcon = bmp.GetHicon();
        try { return System.Drawing.Icon.FromHandle(hIcon); }
        catch { return System.Drawing.SystemIcons.Application; }
    }

    private static void Log(Exception ex)
    {
        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "clipwise.log");
        System.IO.File.AppendAllText(path,
            $"[{DateTime.Now:HH:mm:ss}] {ex}\n");
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Clipwise.MainWindow.Instance?.ShutdownHost();
        if (_tray != null)
        {
            _tray.Visible = false;
            _tray.Dispose();
        }
        _mutex?.Dispose();
        base.OnExit(e);
    }
}

internal static class GraphicsExtensions
{
    public static void FillRoundedRectangle(this Graphics g, Brush brush, RectangleF r, float radius)
    {
        using var path = RoundedPath(r, radius);
        g.FillPath(brush, path);
    }

    public static void DrawRoundedRectangle(this Graphics g, Pen pen, RectangleF r, float radius)
    {
        using var path = RoundedPath(r, radius);
        g.DrawPath(pen, path);
    }

    private static System.Drawing.Drawing2D.GraphicsPath RoundedPath(RectangleF r, float radius)
    {
        float d = radius * 2;
        var p = new System.Drawing.Drawing2D.GraphicsPath();
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }
}
