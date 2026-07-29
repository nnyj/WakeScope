using System.Runtime.InteropServices;

namespace WakeScope;

static class NtPathConverter
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint QueryDosDevice(string lpDeviceName, char[] lpTargetPath, uint ucchMax);

    /// <summary>
    /// Converts an NT path of the form \Device\HarddiskVolumeX\... to a Win32 path (C:\...).
    /// Returns null when no conversion is possible.
    /// </summary>
    public static string? ToWin32Path(string ntPath)
    {
        var buf = new char[512];

        for (char c = 'A'; c <= 'Z'; c++)
        {
            string drive = $"{c}:";
            uint written = QueryDosDevice(drive, buf, (uint)buf.Length);
            if (written == 0) continue;

            // QueryDosDevice can return several null-terminated strings; only the first one is used.
            int nullIdx = Array.IndexOf(buf, '\0');
            string device = nullIdx >= 0 ? new string(buf, 0, nullIdx) : new string(buf);

            if (ntPath.StartsWith(device + @"\", StringComparison.OrdinalIgnoreCase))
                return drive + ntPath[device.Length..];
        }

        return null;
    }
}
