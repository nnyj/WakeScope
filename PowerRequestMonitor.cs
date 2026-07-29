using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace WakeScope;

public sealed class PowerRequestEntry : IDisposable
{
    public required string SourceType { get; init; }
    public required string NativePath { get; init; }
    public required string DisplayName { get; init; }
    public required string Reason { get; init; }
    public required List<string> Categories { get; init; }
    public uint ProcessId { get; set; }
    public string? ServiceName { get; init; }
    public Icon? Icon { get; init; }
    public string? ComClassName { get; set; }
    public string? CommandLine { get; set; }
    public List<ProcessCandidate> ProcessCandidates { get; } = [];

    private bool _disposed;

    public bool BlocksDisplay => Categories.Contains("DISPLAY");
    public bool BlocksSleep => Categories.Any(static x => x != "DISPLAY");

    public string CategoryText => string.Join(", ", Categories);

    public string DetailText
    {
        get
        {
            var parts = new List<string> { CategoryText };
            if (ProcessId != 0) parts.Add($"PID {ProcessId}");
            if (!string.IsNullOrWhiteSpace(ServiceName)) parts.Add($"svc: {ServiceName}");
            if (!string.IsNullOrWhiteSpace(ComClassName)) parts.Add(ComClassName);
            if (!string.IsNullOrWhiteSpace(Reason)) parts.Add(Reason);
            return string.Join(" | ", parts);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Icon?.Dispose();
        foreach (var candidate in ProcessCandidates)
            candidate.Dispose();
    }
}

public sealed class ProcessCandidate : IDisposable
{
    public required uint ProcessId { get; init; }
    public required string ProcessName { get; init; }
    public required string? CommandLine { get; init; }
    public required Icon? Icon { get; init; }

    public string Label => $"{ProcessName} PID {ProcessId}";
    public string CommandSummary => CommandLineFormatter.Summarize(CommandLine);
    public string? DecodedCommandSummary => CommandLineFormatter.DecodeEncodedPowerShell(CommandLine);

    public void Dispose()
    {
        Icon?.Dispose();
    }
}

static partial class CommandLineFormatter
{
    [GeneratedRegex(@"(?:-|/)e(?:ncodedcommand)?\s+(?<payload>[A-Za-z0-9+/=]+)", RegexOptions.IgnoreCase)]
    private static partial Regex EncodedCommandRegex();

    public static string Summarize(string? commandLine)
    {
        if (string.IsNullOrWhiteSpace(commandLine)) return "";

        string summary = commandLine;
        var encoded = EncodedCommandRegex().Match(commandLine);
        if (encoded.Success)
        {
            summary = commandLine[..encoded.Index].Trim() + " -EncodedCommand <base64>";
        }

        return TruncateMiddle(summary, 96);
    }

    public static string? DecodeEncodedPowerShell(string? commandLine)
    {
        if (string.IsNullOrWhiteSpace(commandLine)) return null;

        var match = EncodedCommandRegex().Match(commandLine);
        if (!match.Success) return null;

        try
        {
            string decoded = Encoding.Unicode.GetString(Convert.FromBase64String(match.Groups["payload"].Value));
            return TruncateMiddle(decoded.Replace("\r", " ").Replace("\n", " "), 140);
        }
        catch
        {
            return "Could not decode -EncodedCommand";
        }
    }

    private static string TruncateMiddle(string value, int maxLength)
    {
        if (value.Length <= maxLength) return value;

        int left = (maxLength - 3) / 2;
        int right = maxLength - 3 - left;
        return value[..left] + "..." + value[^right..];
    }
}

sealed partial class PowerRequestMonitor
{
    // Level 45 entry layout, verified on Windows 11 build 26100 against powercfg /requests.
    // entry+0x00 dword SupportedRequestMask, entry+0x04..0x18 six dword active counts
    //   (DISPLAY, SYSTEM, AWAYMODE, EXECUTION, PERFBOOST, ACTIVELOCKSCREEN).
    // DIAGNOSTIC_BUFFER at entry+0x20: qword size, dword caller type @+0x08
    //   (1 process, 2 shared service, else driver/kernel).
    //   Process/service: qword image name offset @+0x10, dword pid @+0x18, dword service tag @+0x1C.
    //   Driver: qword device description offset @+0x10, qword device path offset @+0x18.
    //   qword reason offset @+0x20. Diagnostic string offsets are relative to the diagnostic buffer base.
    // Reason struct: dword flags (1 simple string, 2 resource string), qword string/module offset @+0x08,
    //   ushort resource id @+0x10, dword substitution string count @+0x14.
    //   Reason string/module offsets are relative to the reason struct base, not the diagnostic buffer.
    private const int DiagOffset       = 0x20;
    private const int DiagHeaderSize   = 0x28;
    private const int ReasonHeaderSize = 0x18;

    private static readonly string[] BlockingCategories = ["DISPLAY", "SYSTEM", "AWAYMODE", "EXECUTION"];

    private readonly Icon _fallbackIcon;

    public PowerRequestMonitor(Icon fallbackIcon) => _fallbackIcon = fallbackIcon;

    public List<PowerRequestEntry> GetBlockers()
    {
        try
        {
            var nativeEntries = QueryNativeEntries();
            var merged = new Dictionary<string, PowerRequestEntry>(StringComparer.OrdinalIgnoreCase);

            foreach (var native in nativeEntries)
            {
                if (native.Categories.Count == 0) continue;

                string key = $"{native.SourceType}|{native.NativePath}|{native.Reason}|{native.ServiceName}";

                if (!merged.TryGetValue(key, out var entry))
                {
                    string? win32Path = NtPathConverter.ToWin32Path(native.NativePath);

                    entry = new PowerRequestEntry
                    {
                        SourceType = native.SourceType,
                        NativePath = native.NativePath,
                        DisplayName = native.DisplayName
                            ?? GetDisplayName(native.SourceType, native.NativePath, win32Path),
                        Reason = native.Reason,
                        Categories = [],
                        ProcessId = native.ProcessId,
                        ServiceName = native.ServiceName,
                        Icon = TryExtractIcon(win32Path) ?? new Icon(_fallbackIcon, 16, 16),
                    };
                    merged.Add(key, entry);
                }

                foreach (var cat in native.Categories)
                {
                    if (!entry.Categories.Contains(cat))
                        entry.Categories.Add(cat);
                }

                if (entry.ProcessId == 0 && native.ProcessId != 0)
                    entry.ProcessId = native.ProcessId;
            }

            foreach (var entry in merged.Values)
                EnrichFromProcess(entry);

            return merged.Values
                .OrderByDescending(static x => x.BlocksSleep)
                .ThenBy(static x => x.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch
        {
            return [];
        }
    }

    private static string GetDisplayName(string sourceType, string nativePath, string? win32Path)
    {
        if (sourceType == "PROCESS")
        {
            string fileName = Path.GetFileName(win32Path ?? nativePath);
            return string.IsNullOrWhiteSpace(fileName) ? nativePath : fileName;
        }

        return nativePath;
    }

    private List<NativeRequestEntry> QueryNativeEntries()
    {
        uint size = 16384;
        while (true)
        {
            IntPtr buf = Marshal.AllocHGlobal((int)size);
            try
            {
                int status = NativePower.PowerInformationWithPrivileges(
                    NativePower.PowerRequestListLevel,
                    IntPtr.Zero, 0, buf, size);

                if (status == NativePower.StatusBufferTooSmall)
                {
                    size *= 2;
                    continue;
                }
                if (status != NativePower.StatusSuccess) return [];

                return ParseNativeEntries(buf, size);
            }
            finally
            {
                Marshal.FreeHGlobal(buf);
            }
        }
    }

    private static List<NativeRequestEntry> ParseNativeEntries(IntPtr buf, uint bufSize)
    {
        long count = Marshal.ReadInt64(buf, 0);
        var result = new List<NativeRequestEntry>();

        for (long i = 0; i < count; i++)
        {
            int headerOff = 8 + (int)i * 8;
            if (headerOff + 8 > (int)bufSize) break;

            long elemOff = Marshal.ReadInt64(buf, headerOff);
            if (elemOff < 0 || elemOff + DiagOffset + DiagHeaderSize > bufSize) continue;

            var entry = ParseEntry(buf, bufSize, (int)elemOff);
            if (entry is not null) result.Add(entry);
        }

        return result;
    }

    private static NativeRequestEntry? ParseEntry(IntPtr buf, uint bufSize, int elemOff)
    {
        var categories = ReadCategories(buf, elemOff);
        if (categories.Count == 0) return null;

        int diag = elemOff + DiagOffset;
        long diagSize = Marshal.ReadInt64(buf, diag);
        if (diagSize < DiagHeaderSize || diag + diagSize > bufSize) return null;

        int diagLimit = diag + (int)diagSize;
        int callerType = Marshal.ReadInt32(buf, diag + 0x08);
        string reason = ReadReason(buf, diag, diagLimit);

        if (callerType is 1 or 2)
        {
            string imagePath = ReadStringAt(buf, diag, Marshal.ReadInt64(buf, diag + 0x10), diagLimit);
            if (imagePath.Length == 0) return null;

            uint processId = (uint)Marshal.ReadInt32(buf, diag + 0x18);
            uint serviceTag = (uint)Marshal.ReadInt32(buf, diag + 0x1C);

            return new NativeRequestEntry
            {
                SourceType = "PROCESS",
                ProcessId = processId,
                NativePath = imagePath,
                DisplayName = null,
                Reason = reason,
                ServiceName = NativeProcessInfo.GetServiceNameFromTag(processId, serviceTag),
                Categories = categories,
            };
        }

        string description = ReadStringAt(buf, diag, Marshal.ReadInt64(buf, diag + 0x10), diagLimit);
        string devicePath = ReadStringAt(buf, diag, Marshal.ReadInt64(buf, diag + 0x18), diagLimit);
        if (description.Length == 0 && devicePath.Length == 0) return null;

        return new NativeRequestEntry
        {
            SourceType = "DRIVER",
            ProcessId = 0,
            NativePath = devicePath.Length > 0 ? devicePath : description,
            DisplayName = description.Length > 0 ? description : devicePath,
            Reason = reason,
            ServiceName = null,
            Categories = categories,
        };
    }

    private static List<string> ReadCategories(IntPtr buf, int elemOff)
    {
        var categories = new List<string>();
        for (int i = 0; i < BlockingCategories.Length; i++)
        {
            if (Marshal.ReadInt32(buf, elemOff + 0x04 + i * 4) > 0)
                categories.Add(BlockingCategories[i]);
        }
        return categories;
    }

    private static string ReadReason(IntPtr buf, int diag, int diagLimit)
    {
        long reasonOff = Marshal.ReadInt64(buf, diag + 0x20);
        if (reasonOff <= 0 || diag + reasonOff + ReasonHeaderSize > diagLimit) return "";

        int reason = diag + (int)reasonOff;
        int flags = Marshal.ReadInt32(buf, reason);
        if (flags is not (1 or 2)) return "";

        string text = ReadStringAt(buf, reason, Marshal.ReadInt64(buf, reason + 0x08), diagLimit);
        if (flags == 1 || text.Length == 0) return text;

        ushort resourceId = (ushort)Marshal.ReadInt16(buf, reason + 0x10);
        return NativeResourceString.Load(text, resourceId) ?? $"{text}: {resourceId}";
    }

    private static string ReadStringAt(IntPtr buf, int diag, long relativeOffset, int diagLimit)
    {
        if (relativeOffset <= 0 || diag + relativeOffset + 2 > diagLimit) return "";

        var sb = new StringBuilder();
        for (int p = diag + (int)relativeOffset; p + 2 <= diagLimit; p += 2)
        {
            short w = Marshal.ReadInt16(buf, p);
            if (w == 0) break;
            sb.Append((char)w);
        }
        return sb.ToString();
    }

    private static void EnrichFromProcess(PowerRequestEntry entry)
    {
        if (entry.SourceType != "PROCESS") return;

        if (entry.ProcessId == 0)
        {
            var candidates = FindProcessesByPath(NtPathConverter.ToWin32Path(entry.NativePath));
            if (candidates.Count == 1)
                entry.ProcessId = candidates[0].ProcessId;
            else
                entry.ProcessCandidates.AddRange(candidates);
        }

        if (entry.ProcessId == 0) return;

        entry.CommandLine = NativeProcessInfo.GetCommandLine(entry.ProcessId);
        entry.ComClassName = TryGetComClassName(entry.CommandLine);
    }

    private static List<ProcessCandidate> FindProcessesByPath(string? win32Path)
    {
        if (string.IsNullOrWhiteSpace(win32Path)) return [];

        try
        {
            string name = Path.GetFileNameWithoutExtension(win32Path);
            var matches = new List<ProcessCandidate>();

            foreach (var proc in Process.GetProcessesByName(name))
            {
                try
                {
                    string? modulePath = proc.MainModule?.FileName;
                    if (string.IsNullOrWhiteSpace(modulePath)) continue;

                    if (string.Equals(modulePath, win32Path, StringComparison.OrdinalIgnoreCase))
                    {
                        matches.Add(new ProcessCandidate
                        {
                            ProcessId = (uint)proc.Id,
                            ProcessName = Path.GetFileName(modulePath),
                            CommandLine = NativeProcessInfo.GetCommandLine((uint)proc.Id),
                            Icon = TryExtractIcon(modulePath),
                        });
                    }
                }
                catch { }
                finally { proc.Dispose(); }
            }

            return matches;
        }
        catch
        {
            return [];
        }
    }

    [GeneratedRegex(@"/Processid:\{(?<id>[0-9a-fA-F\-]+)\}", RegexOptions.IgnoreCase)]
    private static partial Regex ComProcessIdRegex();

    private static string? TryGetComClassName(string? commandLine)
    {
        if (string.IsNullOrWhiteSpace(commandLine)) return null;

        var match = ComProcessIdRegex().Match(commandLine);
        if (!match.Success) return null;

        string clsid = "{" + match.Groups["id"].Value + "}";
        try
        {
            using var key = Microsoft.Win32.Registry.ClassesRoot.OpenSubKey($@"CLSID\{clsid}");
            return key?.GetValue(null)?.ToString();
        }
        catch
        {
            return null;
        }
    }

    private static Icon? TryExtractIcon(string? win32Path)
    {
        if (win32Path is null) return null;
        try
        {
            using var full = Icon.ExtractAssociatedIcon(win32Path);
            return full is null ? null : new Icon(full, 16, 16);
        }
        catch
        {
            return null;
        }
    }

    private sealed class NativeRequestEntry
    {
        public required string SourceType { get; init; }
        public required uint ProcessId { get; init; }
        public required string NativePath { get; init; }
        public required string? DisplayName { get; init; }
        public required string Reason { get; init; }
        public required string? ServiceName { get; init; }
        public required List<string> Categories { get; init; }
    }
}
