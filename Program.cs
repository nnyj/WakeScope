using System.Security.Principal;

namespace WakeScope;

static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        if (args.Length == 2 && args[0] == "--dump")
        {
            DumpBlockers(args[1]);
            return;
        }

        using var mutex = new Mutex(true, @"Global\WakeScope_SingleInstance", out bool created);
        if (!created)
        {
            MessageBox.Show("WakeScope is already running.", "WakeScope",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        using var identity = WindowsIdentity.GetCurrent();
        if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
        {
            MessageBox.Show("WakeScope requires administrator privileges.", "WakeScope",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        NativePower.EnablePrivilege("SeShutdownPrivilege");
        NativePower.EnablePrivilege("SeDebugPrivilege");

        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        ApplicationConfiguration.Initialize();
        SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());

        using var app = new TrayApp();
        Application.Run(app);
    }

    private static void DumpBlockers(string outputPath)
    {
        NativePower.EnablePrivilege("SeShutdownPrivilege");
        NativePower.EnablePrivilege("SeDebugPrivilege");

        using var fallbackIcon = new Icon(SystemIcons.Application, 16, 16);
        var blockers = new PowerRequestMonitor(fallbackIcon).GetBlockers();
        File.WriteAllLines(outputPath, blockers.Select(static x => string.Join(" | ",
            x.SourceType, x.CategoryText, x.ProcessId, x.DisplayName, x.NativePath, x.Reason, x.ServiceName ?? "")));

        foreach (var entry in blockers) entry.Dispose();
    }
}
