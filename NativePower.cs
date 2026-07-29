using System.Runtime.InteropServices;

namespace WakeScope;

static class NativePower
{
    internal const int StatusSuccess        = 0;
    internal const int StatusBufferTooSmall = unchecked((int)0xC0000023);

    // Level 45: the PowerInformationWithPrivileges call level powercfg.exe uses internally.
    // Level 49 (GetPowerRequestList) requires SeTcbPrivilege and returns ACCESS_DENIED for administrators.
    internal const int PowerRequestListLevel = 45;

    [DllImport("powrprof.dll", ExactSpelling = true)]
    internal static extern int PowerInformationWithPrivileges(
        int    informationLevel,
        IntPtr inputBuffer,
        uint   inputBufferLength,
        IntPtr outputBuffer,
        uint   outputBufferLength);

    // ── Standby timeout (active power scheme) ────────────────────────────────

    private static readonly Guid SleepSubgroup   = new("238C9FA8-0AAD-41ED-83F4-97BE242C8F20");
    private static readonly Guid StandbyTimeout  = new("29F6C1DB-86DA-48C5-9FDB-F2B67B1F44DA");

    [DllImport("powrprof.dll")]
    private static extern uint PowerGetActiveScheme(IntPtr userRootPowerKey, out IntPtr activePolicyGuid);

    [DllImport("powrprof.dll")]
    private static extern uint PowerSetActiveScheme(IntPtr userRootPowerKey, ref Guid schemeGuid);

    [DllImport("powrprof.dll")]
    private static extern uint PowerReadACValueIndex(
        IntPtr rootPowerKey, ref Guid schemeGuid, ref Guid subGroupGuid, ref Guid settingGuid, out uint value);

    [DllImport("powrprof.dll")]
    private static extern uint PowerReadDCValueIndex(
        IntPtr rootPowerKey, ref Guid schemeGuid, ref Guid subGroupGuid, ref Guid settingGuid, out uint value);

    [DllImport("powrprof.dll")]
    private static extern uint PowerWriteACValueIndex(
        IntPtr rootPowerKey, ref Guid schemeGuid, ref Guid subGroupGuid, ref Guid settingGuid, uint value);

    [DllImport("powrprof.dll")]
    private static extern uint PowerWriteDCValueIndex(
        IntPtr rootPowerKey, ref Guid schemeGuid, ref Guid subGroupGuid, ref Guid settingGuid, uint value);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr mem);

    /// <summary>AC and DC standby timeout in seconds, 0 means never. Null when the scheme cannot be read.</summary>
    internal static (uint Ac, uint Dc)? GetStandbyTimeout()
    {
        if (!TryGetActiveScheme(out Guid scheme)) return null;

        var sub = SleepSubgroup;
        var setting = StandbyTimeout;
        if (PowerReadACValueIndex(IntPtr.Zero, ref scheme, ref sub, ref setting, out uint ac) != 0) return null;
        if (PowerReadDCValueIndex(IntPtr.Zero, ref scheme, ref sub, ref setting, out uint dc) != 0) return null;
        return (ac, dc);
    }

    /// <summary>Writes AC and DC standby timeout in seconds and applies the scheme. Returns a Win32 error code, 0 on success.</summary>
    internal static uint SetStandbyTimeout(uint seconds)
    {
        if (!TryGetActiveScheme(out Guid scheme)) return unchecked((uint)-1);

        var sub = SleepSubgroup;
        var setting = StandbyTimeout;
        uint status = PowerWriteACValueIndex(IntPtr.Zero, ref scheme, ref sub, ref setting, seconds);
        if (status != 0) return status;

        status = PowerWriteDCValueIndex(IntPtr.Zero, ref scheme, ref sub, ref setting, seconds);
        if (status != 0) return status;

        return PowerSetActiveScheme(IntPtr.Zero, ref scheme);
    }

    private static bool TryGetActiveScheme(out Guid scheme)
    {
        scheme = Guid.Empty;
        if (PowerGetActiveScheme(IntPtr.Zero, out IntPtr ptr) != 0 || ptr == IntPtr.Zero) return false;
        try
        {
            scheme = Marshal.PtrToStructure<Guid>(ptr);
            return true;
        }
        finally { LocalFree(ptr); }
    }

    // ── Privilege elevation ──────────────────────────────────────────────────

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
