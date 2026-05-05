using System.Runtime.InteropServices;

namespace WakeScope;

static class NtPathConverter
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint QueryDosDevice(string lpDeviceName, char[] lpTargetPath, uint ucchMax);

    /// <summary>
    /// \Device\HarddiskVolumeX\... 形式の NT パスを Win32 パス (C:\...) に変換する。
    /// 変換できなければ null を返す。
    /// </summary>
    public static string? ToWin32Path(string ntPath)
    {
        var buf = new char[512];

        for (char c = 'A'; c <= 'Z'; c++)
        {
            string drive = $"{c}:";
            uint written = QueryDosDevice(drive, buf, (uint)buf.Length);
            if (written == 0) continue;

            // QueryDosDevice は複数の null 終端文字列を返す場合がある。先頭だけ使う。
            int nullIdx = Array.IndexOf(buf, '\0');
            string device = nullIdx >= 0 ? new string(buf, 0, nullIdx) : new string(buf);

            if (ntPath.StartsWith(device + @"\", StringComparison.OrdinalIgnoreCase))
                return drive + ntPath[device.Length..];
        }

        return null;
    }
}
