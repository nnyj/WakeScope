using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

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
    // バッファ構造 (Windows 11 x64 で実測確認済み):
    //   Header:  [+0x00] uint64 Count
    //            [+0x08] uint64[Count] Offsets  (バッファ先頭からの各要素オフセット)
    //   Element: [+0x00] uint64 TypeMarker
    //            [+0x08] uint64 f1
    //            [+0x10] uint64 f2   ← DISPLAY アクティブ要求数 (0x3F 型)
    //            [+0x18] uint64 f3
    //            [+0x20] uint64 f4
    //            [+0x28] uint64 f5
    //            [+0x30] uint64 f6
    //            [+0x38] uint64 f7   ← PID (経験的に確認・未公式)
    //            [+0x40] uint64 f8
    //            [+0x48] WCHAR[] name    (null 終端 UTF-16LE)
    //                    WCHAR[] reason  (name の直後、null 終端)

    private const ulong TypeMarkerProcess = 0x3F; // ユーザーモードプロセス (DISPLAY/SYSTEM/AwayMode)
    private const int   OffF2             = 0x10; // DISPLAY アクティブフラグ
    private const int   OffF7             = 0x38; // PID
    private const int   OffStrings        = 0x48; // 文字列開始位置

    private readonly Icon _fallbackIcon;

    public PowerRequestMonitor(Icon fallbackIcon) => _fallbackIcon = fallbackIcon;

    // ── 公開 API ─────────────────────────────────────────────────────────────

    public List<DisplayBlockerEntry> GetDisplayBlockers()
    {
        try { return QueryDisplayBlockers(); }
        catch { return []; }
    }

    // ── API 呼び出し ─────────────────────────────────────────────────────────

    private List<DisplayBlockerEntry> QueryDisplayBlockers()
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

                if (status == NativePower.StatusBufferTooSmall) { size *= 2; continue; }
                if (status != NativePower.StatusSuccess) return [];

                return ParseDisplayEntries(buf, size);
            }
            finally
            {
                Marshal.FreeHGlobal(buf);
            }
        }
    }

    // ── バッファ解析 ─────────────────────────────────────────────────────────

    private List<DisplayBlockerEntry> ParseDisplayEntries(IntPtr buf, uint bufSize)
    {
        ulong count = (ulong)Marshal.ReadInt64(buf, 0);
        var result  = new List<DisplayBlockerEntry>();

        for (ulong i = 0; i < count; i++)
        {
            int headerOff = 8 + (int)i * 8;
            if (headerOff + 8 > (int)bufSize) break;

            ulong elemOff = (ulong)Marshal.ReadInt64(buf, headerOff);
            if (elemOff + OffStrings + 2 > bufSize) continue;

            IntPtr elem = IntPtr.Add(buf, (int)elemOff);

            ulong typeMarker = (ulong)Marshal.ReadInt64(elem, 0x00);
            if (typeMarker != TypeMarkerProcess) continue;

            ulong f2 = (ulong)Marshal.ReadInt64(elem, OffF2);
            if (f2 == 0) continue; // DISPLAY 要求がアクティブでない

            ulong f7 = (ulong)Marshal.ReadInt64(elem, OffF7);

            int maxBytes    = (int)(bufSize - elemOff) - OffStrings;
            var (ntPath, _) = ReadStringPair(elem, OffStrings, maxBytes);

            string? win32Path = NtPathConverter.ToWin32Path(ntPath);
            string  fileName  = Path.GetFileName(win32Path ?? ntPath);
            if (string.IsNullOrEmpty(fileName)) fileName = ntPath;

            uint  pid  = ResolvePid(f7, win32Path);
            Icon? icon = TryExtractIcon(win32Path) ?? new Icon(_fallbackIcon, 16, 16);

            result.Add(new DisplayBlockerEntry
            {
                ProcessId = pid,
                FileName  = fileName,
                Icon      = icon,
            });
        }

        return result;
    }

    // ── PID 解決 ─────────────────────────────────────────────────────────────

    // f7 に PID が格納されていることを経験的に確認済み (未公式)。
    // プロセスが実在しパスが一致すれば採用し、そうでなければ名前マッチングにフォールバックする。
    private static uint ResolvePid(ulong f7Candidate, string? win32Path)
    {
        if (f7Candidate > 0 && f7Candidate <= uint.MaxValue)
        {
            uint candidate = (uint)f7Candidate;
            try
            {
                using var proc = Process.GetProcessById((int)candidate);
                if (win32Path is null ||
                    string.Equals(proc.MainModule?.FileName, win32Path,
                        StringComparison.OrdinalIgnoreCase))
                    return candidate;
            }
            catch { }
        }

        return win32Path is not null ? FindPidByPath(win32Path) : 0;
    }

    private static uint FindPidByPath(string win32Path)
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

    // ── 文字列読み取り ───────────────────────────────────────────────────────

    private static (string first, string second) ReadStringPair(
        IntPtr elem, int startOffset, int maxBytes)
    {
        int limit  = startOffset + maxBytes;
        int offset = startOffset;
        string first  = ReadNullTermWChar(elem, ref offset, limit);
        string second = ReadNullTermWChar(elem, ref offset, limit);
        return (first, second);
    }

    // byteOffset を更新しながら null 終端 WCHAR 文字列を読む
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

    // ── アイコン取得 ─────────────────────────────────────────────────────────

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
