using System.Security.Principal;

namespace WakeScope;

static class Program
{
    [STAThread]
    static void Main()
    {
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
}
