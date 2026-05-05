using System.Reflection;
using System.Runtime.InteropServices;

namespace WakeScope;

sealed class TrayApp : ApplicationContext
{
    // ── P/Invoke ─────────────────────────────────────────────────────────

    [DllImport("shell32.dll", CharSet = CharSet.Auto)]
    private static extern uint ExtractIconEx(
        string szFileName, int nIconIndex,
        IntPtr[]? phiconLarge, IntPtr[] phiconSmall, uint nIcons);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    // ── フィールド ───────────────────────────────────────────────────────

    private readonly NotifyIcon             _trayIcon;
    private readonly Icon                   _idleIcon;
    private readonly Icon                   _activeIcon;
    private readonly Icon                   _fallbackIcon;
    private readonly PowerRequestMonitor    _monitor;
    private readonly SynchronizationContext _syncContext;
    private readonly CancellationTokenSource _cts = new();
    private List<DisplayBlockerEntry>       _blockers = [];

    private readonly ContextMenuStrip _menu = new();

    // ── 初期化 ───────────────────────────────────────────────────────────

    public TrayApp()
    {
        _idleIcon    = LoadEmbeddedIcon("WakeScope.icons.tray_idle.ico");
        _activeIcon  = LoadEmbeddedIcon("WakeScope.icons.tray_active.ico");
        _fallbackIcon = LoadShell32Icon(2);

        _monitor = new PowerRequestMonitor(_fallbackIcon);
        _syncContext = SynchronizationContext.Current
            ?? throw new InvalidOperationException("SynchronizationContext is null on the UI thread.");

        _menu.Opening += (_, _) => RebuildMenu();

        _trayIcon = new NotifyIcon
        {
            Icon             = _idleIcon,
            Text             = "WakeScope",
            Visible          = true,
            ContextMenuStrip = _menu,
        };

        _ = Task.Run(() => RunMonitorLoop(_cts.Token));
    }

    private static Icon LoadEmbeddedIcon(string resourceName)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Embedded resource not found: {resourceName}");
        return new Icon(stream);
    }

    private static Icon LoadShell32Icon(int index)
    {
        string shell32 = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System), "shell32.dll");

        var hSmall = new IntPtr[1];
        ExtractIconEx(shell32, index, null, hSmall, 1);

        if (hSmall[0] == IntPtr.Zero)
            return SystemIcons.WinLogo;

        try
        {
            return new Icon(Icon.FromHandle(hSmall[0]), 16, 16);
        }
        finally
        {
            DestroyIcon(hSmall[0]);
        }
    }

    // ── 監視ループ（バックグラウンドスレッド）───────────────────────────

    private async Task RunMonitorLoop(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                try
                {
                    var blockers = _monitor.GetDisplayBlockers();
                    _syncContext.Post(_ => ApplyBlockers(blockers), null);
                }
                catch (Exception ex) when (ex is not OperationCanceledException) { }
            }
        }
        catch (OperationCanceledException) { }
    }

    // ── UI スレッド更新 ──────────────────────────────────────────────────

    private void ApplyBlockers(List<DisplayBlockerEntry> incoming)
    {
        foreach (var e in _blockers) e.Dispose();
        _blockers = incoming;
        _trayIcon.Icon = _blockers.Count > 0 ? _activeIcon : _idleIcon;
    }

    // ── メニュー構築 ─────────────────────────────────────────────────────

    private void RebuildMenu()
    {
        foreach (ToolStripItem item in _menu.Items)
        {
            item.Image?.Dispose();
            item.Image = null;
        }
        _menu.Items.Clear();

        if (_blockers.Count == 0)
        {
            _menu.Items.Add(new ToolStripMenuItem("Nothing...") { Enabled = false });
        }
        else
        {
            foreach (var entry in _blockers)
            {
                _menu.Items.Add(new ToolStripMenuItem(
                    $"{entry.FileName} (PID: {entry.ProcessId})")
                {
                    Image        = entry.Icon?.ToBitmap(),
                    ImageScaling = ToolStripItemImageScaling.None,
                });
            }
        }

        _menu.Items.Add(new ToolStripSeparator());
        var exitItem = new ToolStripMenuItem("終了");
        exitItem.Click += (_, _) => Application.Exit();
        _menu.Items.Add(exitItem);
    }

    // ── クリーンアップ ───────────────────────────────────────────────────

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _cts.Cancel();
            _cts.Dispose();
            _trayIcon.Visible = false;
            _trayIcon.Dispose();

            foreach (ToolStripItem item in _menu.Items)
                item.Image?.Dispose();
            _menu.Dispose();

            foreach (var e in _blockers) e.Dispose();
            _idleIcon.Dispose();
            _activeIcon.Dispose();
            _fallbackIcon.Dispose();
        }
        base.Dispose(disposing);
    }
}
