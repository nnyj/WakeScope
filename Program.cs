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

        ApplicationConfiguration.Initialize();

        // WindowsFormsSynchronizationContext を明示的に設定する。
        // TrayApp のコンストラクタは Application.Run() より先に実行されるため、
        // ここで設定しないと SynchronizationContext.Current が null になる。
        SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());

        using var app = new TrayApp();
        Application.Run(app);
    }
}
