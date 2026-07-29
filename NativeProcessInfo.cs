using System.Runtime.InteropServices;

namespace WakeScope;

static class NativeProcessInfo
{
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint ProcessVmRead                  = 0x0010;

    // x64 layout: PEB->ProcessParameters, RTL_USER_PROCESS_PARAMETERS->CommandLine (UNICODE_STRING).
    private const int OffPebProcessParameters = 0x20;
    private const int OffParamsCommandLine    = 0x70;

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessBasicInformation
    {
        public IntPtr ExitStatus;
        public IntPtr PebBaseAddress;
        public IntPtr AffinityMask;
        public IntPtr BasePriority;
        public IntPtr UniqueProcessId;
        public IntPtr InheritedFromUniqueProcessId;
    }

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(
        IntPtr process, int infoClass, out ProcessBasicInformation info, int infoLength, out int returnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inheritHandle, uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ReadProcessMemory(
        IntPtr process, IntPtr baseAddress, byte[] buffer, int size, out IntPtr bytesRead);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);

    internal static string? GetCommandLine(uint processId)
    {
        if (processId == 0) return null;

        IntPtr process = OpenProcess(ProcessQueryLimitedInformation | ProcessVmRead, false, processId);
        if (process == IntPtr.Zero) return null;

        try
        {
            if (NtQueryInformationProcess(process, 0, out var info, Marshal.SizeOf<ProcessBasicInformation>(), out _) != 0)
                return null;
            if (info.PebBaseAddress == IntPtr.Zero) return null;

            IntPtr parameters = ReadPointer(process, IntPtr.Add(info.PebBaseAddress, OffPebProcessParameters));
            if (parameters == IntPtr.Zero) return null;

            var unicodeString = ReadBytes(process, IntPtr.Add(parameters, OffParamsCommandLine), 16);
            if (unicodeString is null) return null;

            int length = BitConverter.ToUInt16(unicodeString, 0);
            IntPtr buffer = (IntPtr)BitConverter.ToInt64(unicodeString, 8);
            if (length == 0 || buffer == IntPtr.Zero) return null;

            var text = ReadBytes(process, buffer, length);
            return text is null ? null : System.Text.Encoding.Unicode.GetString(text);
        }
        catch
        {
            return null;
        }
        finally
        {
            CloseHandle(process);
        }
    }

    // SC_SERVICE_TAG_QUERY, eServiceNameFromTagInformation = 1.
    private const int ServiceNameFromTag = 1;

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceTagQuery
    {
        public uint ProcessId;
        public uint ServiceTag;
        public uint Reserved;
        public IntPtr Buffer;
    }

    [DllImport("advapi32.dll")]
    private static extern uint I_QueryTagInformation(IntPtr unused, int infoClass, ref ServiceTagQuery query);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr mem);

    /// <summary>Service name owning a service tag inside a shared host process, null when unresolvable.</summary>
    internal static string? GetServiceNameFromTag(uint processId, uint serviceTag)
    {
        if (processId == 0 || serviceTag == 0) return null;

        var query = new ServiceTagQuery { ProcessId = processId, ServiceTag = serviceTag };
        try
        {
            if (I_QueryTagInformation(IntPtr.Zero, ServiceNameFromTag, ref query) != 0) return null;
            if (query.Buffer == IntPtr.Zero) return null;

            return Marshal.PtrToStringUni(query.Buffer);
        }
        catch
        {
            return null;
        }
        finally
        {
            if (query.Buffer != IntPtr.Zero) LocalFree(query.Buffer);
        }
    }

    private static IntPtr ReadPointer(IntPtr process, IntPtr address)
    {
        var bytes = ReadBytes(process, address, 8);
        return bytes is null ? IntPtr.Zero : (IntPtr)BitConverter.ToInt64(bytes, 0);
    }

    private static byte[]? ReadBytes(IntPtr process, IntPtr address, int size)
    {
        var buffer = new byte[size];
        if (!ReadProcessMemory(process, address, buffer, size, out IntPtr read) || (int)read != size)
            return null;
        return buffer;
    }
}
