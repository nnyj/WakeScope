using System.Diagnostics;
using System.Management;
using System.Runtime.InteropServices;
using System.Text;

namespace WakeScope;

public sealed class PowerRequestEntry : IDisposable
{
    public required string SourceType { get; init; }
    public required string NativePath { get; init; }
    public required string DisplayName { get; init; }
    public required string Reason { get; init; }
    public required List<string> Categories { get; init; }
    public uint ProcessId { get; set; }
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

static class CommandLineFormatter
{
    public static string Summarize(string? commandLine)
    {
        if (string.IsNullOrWhiteSpace(commandLine)) return "";

        string summary = commandLine;
        var encoded = System.Text.RegularExpressions.Regex.Match(
            commandLine,
            @"(?i)(?:-|/)e(?:ncodedcommand)?\s+(?<payload>[A-Za-z0-9+/=]+)");
        if (encoded.Success)
        {
            summary = commandLine[..encoded.Index].Trim() + " -EncodedCommand <base64>";
        }

        return TruncateMiddle(summary, 96);
    }

    public static string? DecodeEncodedPowerShell(string? commandLine)
    {
        if (string.IsNullOrWhiteSpace(commandLine)) return null;

        var match = System.Text.RegularExpressions.Regex.Match(
            commandLine,
            @"(?i)(?:-|/)e(?:ncodedcommand)?\s+(?<payload>[A-Za-z0-9+/=]+)");
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

sealed class PowerRequestMonitor
{
    private const ulong TypeMarkerKernel = 0x12;
    private const ulong TypeMarkerLegacy = 0x1E;
    private const ulong TypeMarkerProcess = 0x3F;
    private const ulong TypeMarkerExecutionProcess = 0x1000003F;
    private const int OffProcessId = 0x38;
    private const int OffStrings = 0x48;
    private const int OffAltStrings = 0x68;

    private readonly Icon _fallbackIcon;

    public PowerRequestMonitor(Icon fallbackIcon) => _fallbackIcon = fallbackIcon;

    public List<PowerRequestEntry> GetBlockers()
    {
        try
        {
            var nativeEntries = QueryNativeEntries();
            var entries = QueryPowercfgEntries();

            foreach (var entry in entries)
            {
                EnrichFromNative(entry, nativeEntries);
                EnrichFromProcess(entry);
            }

            return entries
                .OrderByDescending(static x => x.BlocksSleep)
                .ThenBy(static x => x.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch
        {
            return [];
        }
    }

    private List<PowerRequestEntry> QueryPowercfgEntries()
    {
        using var proc = new Process();
        proc.StartInfo = new ProcessStartInfo
        {
            FileName = "powercfg.exe",
            ArgumentList = { "/requests" },
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        proc.Start();
        string output = proc.StandardOutput.ReadToEnd();
        proc.WaitForExit(5000);

        var entries = new Dictionary<string, PowerRequestEntry>(StringComparer.OrdinalIgnoreCase);
        string category = "";
        string sourceType = "";
        string nativePath = "";

        foreach (string rawLine in output.Replace("\r", "").Split('\n'))
        {
            string line = rawLine.Trim();
            if (line.Length == 0 || line == "None.") continue;

            if (line.EndsWith(':'))
            {
                AddPowercfgEntry(entries, category, sourceType, nativePath, "");
                category = line.TrimEnd(':');
                sourceType = "";
                nativePath = "";
                continue;
            }

            if (line.StartsWith('['))
            {
                AddPowercfgEntry(entries, category, sourceType, nativePath, "");
                int close = line.IndexOf(']');
                if (close <= 1) continue;

                sourceType = line[1..close];
                nativePath = line[(close + 1)..].Trim();
                continue;
            }

            if (category.Length == 0 || sourceType.Length == 0 || nativePath.Length == 0)
                continue;

            AddPowercfgEntry(entries, category, sourceType, nativePath, line);
            sourceType = "";
            nativePath = "";
        }

        AddPowercfgEntry(entries, category, sourceType, nativePath, "");

        return entries.Values.ToList();
    }

    private void AddPowercfgEntry(
        Dictionary<string, PowerRequestEntry> entries,
        string category,
        string sourceType,
        string nativePath,
        string reason)
    {
        if (category.Length == 0 || sourceType.Length == 0 || nativePath.Length == 0)
            return;

        string key = $"{sourceType}|{nativePath}|{reason}";
        if (!entries.TryGetValue(key, out var entry))
        {
            string? win32Path = NtPathConverter.ToWin32Path(nativePath);
            string displayName = GetDisplayName(sourceType, nativePath, win32Path);

            entry = new PowerRequestEntry
            {
                SourceType = sourceType,
                NativePath = nativePath,
                DisplayName = displayName,
                Reason = reason,
                Categories = [],
                Icon = TryExtractIcon(win32Path) ?? new Icon(_fallbackIcon, 16, 16),
            };
            entries.Add(key, entry);
        }

        if (!entry.Categories.Contains(category))
            entry.Categories.Add(category);
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
        ulong count = (ulong)Marshal.ReadInt64(buf, 0);
        var result = new List<NativeRequestEntry>();

        for (ulong i = 0; i < count; i++)
        {
            int headerOff = 8 + (int)i * 8;
            if (headerOff + 8 > (int)bufSize) break;

            ulong elemOff = (ulong)Marshal.ReadInt64(buf, headerOff);
            if (elemOff + OffStrings + 2 > bufSize) continue;

            IntPtr elem = IntPtr.Add(buf, (int)elemOff);
            ulong typeMarker = (ulong)Marshal.ReadInt64(elem, 0x00);
            int stringOffset = typeMarker == TypeMarkerKernel ? OffAltStrings : OffStrings;
            if (elemOff + (ulong)stringOffset + 2 > bufSize) continue;

            int maxBytes = (int)(bufSize - elemOff) - stringOffset;
            var (nativePath, reason) = ReadStringPair(elem, stringOffset, maxBytes);
            if (string.IsNullOrWhiteSpace(nativePath)) continue;

            result.Add(new NativeRequestEntry
            {
                TypeMarker = typeMarker,
                ProcessId = ReadProcessId(typeMarker, elem),
                NativePath = nativePath,
                Reason = reason,
                Categories = ReadNativeCategories(typeMarker, elem),
            });
        }

        return result;
    }

    private static uint ReadProcessId(ulong typeMarker, IntPtr elem)
    {
        if (typeMarker is not TypeMarkerProcess and not TypeMarkerExecutionProcess)
            return 0;

        ulong value = (ulong)Marshal.ReadInt64(elem, OffProcessId);
        return value > 0 && value <= uint.MaxValue ? (uint)value : 0;
    }

    private static List<string> ReadNativeCategories(ulong typeMarker, IntPtr elem)
    {
        ulong f1 = (ulong)Marshal.ReadInt64(elem, 0x08);
        ulong f2 = (ulong)Marshal.ReadInt64(elem, 0x10);
        ulong f5 = (ulong)Marshal.ReadInt64(elem, 0x28);
        var categories = new List<string>();

        if (typeMarker == TypeMarkerProcess)
        {
            if (f2 != 0) categories.Add("DISPLAY");
            if (f1 != 0) categories.Add("SYSTEM");
        }
        else if (typeMarker == TypeMarkerExecutionProcess)
        {
            if (f5 != 0) categories.Add("EXECUTION");
        }
        else if (typeMarker == TypeMarkerKernel || typeMarker == TypeMarkerLegacy)
        {
            if (f1 != 0 || f5 != 0) categories.Add("SYSTEM");
        }

        return categories;
    }

    private static (string first, string second) ReadStringPair(
        IntPtr elem, int startOffset, int maxBytes)
    {
        int limit = startOffset + maxBytes;
        int offset = startOffset;
        string first = ReadNullTermWChar(elem, ref offset, limit);
        string second = ReadNullTermWChar(elem, ref offset, limit);
        return (first, second);
    }

    private static string ReadNullTermWChar(IntPtr elem, ref int byteOffset, int limit)
    {
        var sb = new StringBuilder();
        while (byteOffset + 1 < limit)
        {
            short w = Marshal.ReadInt16(elem, byteOffset);
            byteOffset += 2;
            if (w == 0) break;
            sb.Append((char)w);
        }
        return sb.ToString();
    }

    private static void EnrichFromNative(PowerRequestEntry entry, List<NativeRequestEntry> nativeEntries)
    {
        var candidates = nativeEntries
            .Where(x => string.Equals(x.NativePath, entry.NativePath, StringComparison.OrdinalIgnoreCase))
            .Where(x => string.IsNullOrWhiteSpace(entry.Reason) ||
                string.Equals(x.Reason, entry.Reason, StringComparison.OrdinalIgnoreCase))
            .ToList();

        var exact = candidates.FirstOrDefault(x => entry.Categories.Any(x.Categories.Contains));
        var match = exact ?? candidates.FirstOrDefault();
        if (match is null || match.ProcessId == 0) return;

        entry.ProcessId = match.ProcessId;
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

        entry.CommandLine = TryGetCommandLine(entry.ProcessId);
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
                            CommandLine = TryGetCommandLine((uint)proc.Id),
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

    private static string? TryGetCommandLine(uint processId)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                $"SELECT CommandLine FROM Win32_Process WHERE ProcessId = {processId}");
            using ManagementObjectCollection results = searcher.Get();
            return results.Cast<ManagementObject>()
                .Select(static x => x["CommandLine"]?.ToString())
                .FirstOrDefault(static x => !string.IsNullOrWhiteSpace(x));
        }
        catch
        {
            return null;
        }
    }

    private static string? TryGetComClassName(string? commandLine)
    {
        if (string.IsNullOrWhiteSpace(commandLine)) return null;

        var match = System.Text.RegularExpressions.Regex.Match(
            commandLine,
            @"/Processid:\{(?<id>[0-9a-fA-F\-]+)\}",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
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
        public required ulong TypeMarker { get; init; }
        public required uint ProcessId { get; init; }
        public required string NativePath { get; init; }
        public required string Reason { get; init; }
        public required List<string> Categories { get; init; }
    }
}
