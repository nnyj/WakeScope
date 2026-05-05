using System.Diagnostics;

namespace WakeScope;

public sealed class DisplayBlockerEntry : IDisposable
{
    public required uint   ProcessId  { get; init; }
    public required string FileName   { get; init; }
    public Icon?           Icon       { get; init; }

    private bool _disposed;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Icon?.Dispose();
    }
}

sealed class PowerRequestMonitor
{
    private readonly Icon _fallbackIcon;

    public PowerRequestMonitor(Icon fallbackIcon) => _fallbackIcon = fallbackIcon;

    // ── 公開 API ─────────────────────────────────────────────────────────

    public List<DisplayBlockerEntry> GetDisplayBlockers()
    {
        try
        {
            string output = RunPowercfg();
            return ParseDisplaySection(output);
        }
        catch
        {
            return [];
        }
    }

    // ── powercfg 実行 ────────────────────────────────────────────────────

    private static string RunPowercfg()
    {
        var psi = new ProcessStartInfo("powercfg", "/requests")
        {
            RedirectStandardOutput = true,
            UseShellExecute        = false,
            CreateNoWindow         = true,
        };

        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start powercfg.");

        string output = proc.StandardOutput.ReadToEnd();
        proc.WaitForExit(5000);
        return output;
    }

    // ── 出力パース ───────────────────────────────────────────────────────

    // powercfg /requests の出力例:
    //   DISPLAY:
    //   [PROCESS] \Device\HarddiskVolume3\...\chrome.exe
    //   Video Wake Lock
    //
    //   SYSTEM:
    //   None.
    //
    // カテゴリヘッダは "UPPERCASE:" 形式。[PROCESS] 行のみ抽出する。

    private List<DisplayBlockerEntry> ParseDisplaySection(string output)
    {
        var result = new List<DisplayBlockerEntry>();
        bool inDisplay = false;

        foreach (string rawLine in output.Split('\n'))
        {
            string line = rawLine.TrimEnd('\r', '\n');

            // カテゴリヘッダ判定: "DISPLAY:", "SYSTEM:", "AWAYMODE:" 等
            if (line.Length > 1 && line.EndsWith(':') &&
                line[..^1].All(c => char.IsLetterOrDigit(c)))
            {
                if (inDisplay) break; // DISPLAY セクションを抜けた
                inDisplay = line.Equals("DISPLAY:", StringComparison.OrdinalIgnoreCase);
                continue;
            }

            if (!inDisplay) continue;

            // [PROCESS] \Device\... の行だけ処理
            // [SERVICE] / [DRIVER] / [THREAD] / "None." / 理由行はスキップ
            if (!line.StartsWith("[PROCESS]", StringComparison.OrdinalIgnoreCase)) continue;

            string ntPath = line["[PROCESS]".Length..].TrimStart();
            if (ntPath.Length > 0)
                result.Add(CreateEntry(ntPath));
        }

        return result;
    }

    // ── エントリ生成 ─────────────────────────────────────────────────────

    private DisplayBlockerEntry CreateEntry(string ntPath)
    {
        string? win32Path = NtPathConverter.ToWin32Path(ntPath);
        string  fileName  = Path.GetFileName(win32Path ?? ntPath);
        if (string.IsNullOrEmpty(fileName)) fileName = ntPath;

        uint  pid  = win32Path is not null ? FindPid(win32Path) : 0;
        Icon? icon = TryExtractIcon(win32Path) ?? new Icon(_fallbackIcon, 16, 16);

        return new DisplayBlockerEntry
        {
            ProcessId = pid,
            FileName  = fileName,
            Icon      = icon,
        };
    }

    // プロセス名で絞り込んでから実行パスで照合するので GetProcesses() より高速
    private static uint FindPid(string win32Path)
    {
        try
        {
            string name = Path.GetFileNameWithoutExtension(win32Path);
            foreach (var proc in Process.GetProcessesByName(name))
            {
                try
                {
                    if (string.Equals(proc.MainModule?.FileName, win32Path,
                            StringComparison.OrdinalIgnoreCase))
                        return (uint)proc.Id;
                }
                catch { }
                finally { proc.Dispose(); }
            }
        }
        catch { }
        return 0;
    }

    private static Icon? TryExtractIcon(string? win32Path)
    {
        if (win32Path is null) return null;
        try
        {
            using var full = Icon.ExtractAssociatedIcon(win32Path);
            return full is null ? null : new Icon(full, 16, 16);
        }
        catch { return null; }
    }
}
