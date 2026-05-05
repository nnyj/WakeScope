using System.Runtime.InteropServices;

namespace WakeScope;

static class NativePower
{
    internal const int StatusSuccess        = 0;
    internal const int StatusBufferTooSmall = unchecked((int)0xC0000023);

    // Level 45: powercfg.exe が内部で使用する PowerInformationWithPrivileges の呼び出しレベル。
    // Level 49 (GetPowerRequestList) は SeTcbPrivilege が必要で管理者権限では ACCESS_DENIED になる。
    internal const int PowerRequestListLevel = 45;

    [DllImport("powrprof.dll", ExactSpelling = true)]
    internal static extern int PowerInformationWithPrivileges(
        int    informationLevel,
        IntPtr inputBuffer,
        uint   inputBufferLength,
        IntPtr outputBuffer,
        uint   outputBufferLength);

    // ── 権限昇格 ─────────────────────────────────────────────────────────────

    private const uint TokenAdjustPrivileges = 0x0020;
    private const uint TokenQuery            = 0x0008;
    private const uint SePrivilegeEnabled    = 0x00000002;

    [StructLayout(LayoutKind.Sequential)]
    private struct Luid { public uint LowPart; public int HighPart; }

    [StructLayout(LayoutKind.Sequential)]
    private struct LuidAndAttributes { public Luid Luid; public uint Attributes; }

    [StructLayout(LayoutKind.Sequential)]
    private struct TokenPrivileges { public uint PrivilegeCount; public LuidAndAttributes Privileges0; }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
    private static extern bool LookupPrivilegeValue(string? system, string name, out Luid luid);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool AdjustTokenPrivileges(
        IntPtr token, bool disableAll, ref TokenPrivileges newState,
        uint bufLen, IntPtr prev, IntPtr retLen);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr h);

    internal static void EnablePrivilege(string name)
    {
        if (!OpenProcessToken(GetCurrentProcess(), TokenAdjustPrivileges | TokenQuery, out IntPtr token))
            return;
        try
        {
            if (!LookupPrivilegeValue(null, name, out Luid luid)) return;
            var tp = new TokenPrivileges
            {
                PrivilegeCount = 1,
                Privileges0    = new LuidAndAttributes { Luid = luid, Attributes = SePrivilegeEnabled },
            };
            AdjustTokenPrivileges(token, false, ref tp, 0, IntPtr.Zero, IntPtr.Zero);
        }
        finally { CloseHandle(token); }
    }
}
