using System.Runtime.InteropServices;
using System.Reflection;

namespace WakeScope;

sealed class TrayApp : ApplicationContext
{
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    private readonly NotifyIcon _trayIcon;
    private readonly Icon _idleIcon;
    private readonly Icon _displayOnlyIcon;
    private readonly Icon _blockedIcon;
    private readonly Icon _fallbackIcon;
    private readonly PowerRequestMonitor _monitor;
    private readonly SynchronizationContext _syncContext;
    private readonly CancellationTokenSource _cts = new();
    private readonly ContextMenuStrip _menu = new();
    private List<PowerRequestEntry> _blockers = [];

    public TrayApp()
    {
        _idleIcon = CreateStatusIcon(Color.FromArgb(90, 90, 90));
        _displayOnlyIcon = CreateStatusIcon(Color.FromArgb(242, 153, 74));
        _blockedIcon = CreateStatusIcon(Color.FromArgb(224, 67, 54), true);
        _fallbackIcon = new Icon(SystemIcons.Application, 16, 16);

        _monitor = new PowerRequestMonitor(_fallbackIcon);
        _syncContext = SynchronizationContext.Current
            ?? throw new InvalidOperationException("SynchronizationContext is null on UI thread.");

        _menu.Font = new Font("Segoe UI", 9F, FontStyle.Regular, GraphicsUnit.Point);
        _menu.ImageScalingSize = new Size(16, 16);
        _menu.Opening += RebuildMenuOnOpening;
        _blockers = _monitor.GetBlockers();
        RebuildMenu();

        _trayIcon = new NotifyIcon
        {
            Icon = _idleIcon,
            Text = "WakeScope: no blockers",
            Visible = true,
            ContextMenuStrip = _menu,
        };
        UpdateTrayState();
        _trayIcon.MouseUp += (_, e) =>
        {
            if (e.Button != MouseButtons.Left) return;
            ShowTrayMenu();
        };

        _ = Task.Run(() => RunMonitorLoop(_cts.Token));
    }

    private static Icon CreateStatusIcon(Color fill, bool blocked = false)
    {
        using var bitmap = new Bitmap(16, 16);
        using Graphics graphics = Graphics.FromImage(bitmap);
        graphics.Clear(Color.Transparent);
        graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

        using var borderBrush = new SolidBrush(Color.FromArgb(34, 34, 34));
        using var fillBrush = new SolidBrush(fill);
        graphics.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Half;
        graphics.FillEllipse(borderBrush, 0, 0, 16, 16);
        graphics.FillEllipse(fillBrush, 1.5f, 1.5f, 13, 13);

        if (blocked)
        {
            using var glyphPen = new Pen(Color.White, 2)
            {
                StartCap = System.Drawing.Drawing2D.LineCap.Round,
                EndCap = System.Drawing.Drawing2D.LineCap.Round,
            };
            graphics.DrawLine(glyphPen, 8, 4.5f, 8, 9.2f);
            graphics.DrawLine(glyphPen, 8, 11.8f, 8, 11.9f);
        }
        IntPtr handle = bitmap.GetHicon();
        try
        {
            using var icon = Icon.FromHandle(handle);
            return (Icon)icon.Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }
    }

    private async Task RunMonitorLoop(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
        try
        {
            RefreshBlockers();
            while (await timer.WaitForNextTickAsync(ct))
            {
                RefreshBlockers();
            }
        }
        catch (OperationCanceledException) { }
    }

    private void RefreshBlockers()
    {
        try
        {
            var blockers = _monitor.GetBlockers();
            _syncContext.Post(_ => ApplyBlockers(blockers), null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { }
    }

    private void ApplyBlockers(List<PowerRequestEntry> incoming)
    {
        foreach (var entry in _blockers) entry.Dispose();
        _blockers = incoming;
        UpdateTrayState();
    }

    private void UpdateTrayState()
    {
        bool display = _blockers.Any(static x => x.BlocksDisplay);
        bool sleep = _blockers.Any(static x => x.BlocksSleep);

        _trayIcon.Icon = (display, sleep) switch
        {
            (_, true) => _blockedIcon,
            (true, false) => _displayOnlyIcon,
            _ => _idleIcon,
        };

        _trayIcon.Text = GetTooltip(display, sleep);
    }

    private string GetTooltip(bool display, bool sleep)
    {
        if (!display && !sleep) return "WakeScope: no blockers";

        int displayCount = _blockers.Count(static x => x.BlocksDisplay);
        int sleepCount = _blockers.Count(static x => x.BlocksSleep);
        string text = $"WakeScope: display {displayCount}, sleep {sleepCount}";
        return text.Length <= 63 ? text : text[..63];
    }

    private void RebuildMenu()
    {
        foreach (ToolStripItem item in _menu.Items)
            DisposeMenuItemImages(item);
        _menu.Items.Clear();

        if (_blockers.Count == 0)
        {
            _menu.Items.Add(new ToolStripMenuItem("No blockers") { Enabled = false });
        }
        else
        {
            AddSection("Display", _blockers.Where(static x => x.BlocksDisplay));
            AddSection("Sleep", _blockers.Where(static x => x.BlocksSleep));
        }

        _menu.Items.Add(new ToolStripSeparator());

        var refreshItem = new ToolStripMenuItem("Refresh");
        refreshItem.Click += (_, _) => Task.Run(RefreshBlockers);
        _menu.Items.Add(refreshItem);

        var exitItem = new ToolStripMenuItem("Exit");
        exitItem.Click += (_, _) => Application.Exit();
        _menu.Items.Add(exitItem);
    }

    private void AddSection(string title, IEnumerable<PowerRequestEntry> entries)
    {
        var list = entries.ToList();
        if (list.Count == 0) return;

        if (_menu.Items.Count > 0) _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(new ToolStripMenuItem(title) { Enabled = false });

        foreach (var entry in list)
        {
            var item = new ToolStripMenuItem($"{entry.DisplayName} ({entry.DetailText})")
            {
                Image = ToMenuBitmap(entry.Icon),
                ImageScaling = ToolStripItemImageScaling.SizeToFit,
            };

            if (!string.IsNullOrWhiteSpace(entry.Reason))
                item.DropDownItems.Add(new ToolStripMenuItem(entry.Reason) { Enabled = false });

            if (!string.IsNullOrWhiteSpace(entry.ComClassName))
                item.DropDownItems.Add(new ToolStripMenuItem(entry.ComClassName) { Enabled = false });

            if (!string.IsNullOrWhiteSpace(entry.CommandLine))
                item.DropDownItems.Add(new ToolStripMenuItem(CommandLineFormatter.Summarize(entry.CommandLine)) { Enabled = false });

            item.DropDownItems.Add(new ToolStripSeparator());

            if (entry.ProcessCandidates.Count > 0)
            {
                item.DropDownItems.Add(new ToolStripMenuItem("Matching processes") { Enabled = false });
                foreach (var candidate in entry.ProcessCandidates)
                {
                    var candidateItem = new ToolStripMenuItem(candidate.Label)
                    {
                        Image = ToMenuBitmap(candidate.Icon),
                        ImageScaling = ToolStripItemImageScaling.SizeToFit,
                    };
                    if (!string.IsNullOrWhiteSpace(candidate.CommandLine))
                    {
                        candidateItem.DropDownItems.Add(new ToolStripMenuItem(candidate.CommandSummary) { Enabled = false });
                        if (!string.IsNullOrWhiteSpace(candidate.DecodedCommandSummary))
                            candidateItem.DropDownItems.Add(new ToolStripMenuItem("Decoded: " + candidate.DecodedCommandSummary) { Enabled = false });
                    }

                    var killCandidateItem = new ToolStripMenuItem("Kill process");
                    killCandidateItem.Click += (_, _) => KillProcess(candidate.ProcessId, candidate.ProcessName);
                    candidateItem.DropDownItems.Add(killCandidateItem);

                    item.DropDownItems.Add(candidateItem);
                }
            }

            if (entry.ProcessId != 0)
            {
                var killItem = new ToolStripMenuItem("Kill process");
                killItem.Click += (_, _) => KillProcess(entry.ProcessId, entry.DisplayName);
                item.DropDownItems.Add(killItem);
            }
            else if (entry.ProcessCandidates.Count == 0)
            {
                item.DropDownItems.Add(new ToolStripMenuItem("No process to kill") { Enabled = false });
            }

            _menu.Items.Add(item);
        }
    }

    private static Bitmap? ToMenuBitmap(Icon? icon)
    {
        if (icon is null) return null;

        var bitmap = new Bitmap(16, 16);
        using Graphics graphics = Graphics.FromImage(bitmap);
        graphics.Clear(Color.Transparent);
        graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
        graphics.DrawIcon(icon, new Rectangle(0, 0, 16, 16));
        return bitmap;
    }

    private void KillProcess(uint processId, string displayName)
    {
        if (processId == 0) return;

        try
        {
            using var proc = System.Diagnostics.Process.GetProcessById((int)processId);
            proc.Kill();
            proc.WaitForExit(2000);
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"Could not kill {displayName} PID {processId}.\n\n{ex.Message}",
                "WakeScope",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }

        Task.Run(RefreshBlockers);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _cts.Cancel();
            _cts.Dispose();
            _trayIcon.Visible = false;
            _trayIcon.Dispose();

            foreach (ToolStripItem item in _menu.Items)
                DisposeMenuItemImages(item);
            _menu.Dispose();

            foreach (var entry in _blockers) entry.Dispose();
            _idleIcon.Dispose();
            _displayOnlyIcon.Dispose();
            _blockedIcon.Dispose();
            _fallbackIcon.Dispose();
        }

        base.Dispose(disposing);
    }

    private static void DisposeMenuItemImages(ToolStripItem item)
    {
        item.Image?.Dispose();
        item.Image = null;

        if (item is not ToolStripMenuItem menuItem) return;

        foreach (ToolStripItem child in menuItem.DropDownItems)
            DisposeMenuItemImages(child);
    }

    private void ShowTrayMenu()
    {
        ApplyBlockers(_monitor.GetBlockers());
        RebuildMenu();

        var method = typeof(NotifyIcon)
            .GetMethod("ShowContextMenu", BindingFlags.Instance | BindingFlags.NonPublic);
        if (method is not null)
        {
            method.Invoke(_trayIcon, null);
            return;
        }

        _menu.Show(Cursor.Position);
    }

    private void RebuildMenuOnOpening(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        ApplyBlockers(_monitor.GetBlockers());
        RebuildMenu();
    }
}
