using System.Runtime.InteropServices;
using System.Text;

namespace WakeScope;

static class NativeResourceString
{
    private const uint LoadLibraryAsDatafile       = 0x00000002;
    private const uint LoadLibraryAsImageResource  = 0x00000020;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr LoadLibraryExW(string fileName, IntPtr file, uint flags);

    [DllImport("kernel32.dll")]
    private static extern bool FreeLibrary(IntPtr module);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int LoadStringW(IntPtr module, uint id, StringBuilder buffer, int bufferSize);

    private static readonly Dictionary<(string Module, uint Id), string?> Cache = [];

    /// <summary>Loads a localized resource string, null when the module or id cannot be resolved.</summary>
    internal static string? Load(string module, uint id)
    {
        if (string.IsNullOrWhiteSpace(module)) return null;

        lock (Cache)
        {
            if (Cache.TryGetValue((module, id), out string? cached)) return cached;

            string? text = LoadUncached(module, id);
            Cache[(module, id)] = text;
            return text;
        }
    }

    private static string? LoadUncached(string module, uint id)
    {
        IntPtr handle = LoadLibraryExW(module, IntPtr.Zero, LoadLibraryAsDatafile | LoadLibraryAsImageResource);
        if (handle == IntPtr.Zero) return null;

        try
        {
            var buffer = new StringBuilder(512);
            int length = LoadStringW(handle, id, buffer, buffer.Capacity);
            return length > 0 ? buffer.ToString(0, length) : null;
        }
        catch
        {
            return null;
        }
        finally
        {
            FreeLibrary(handle);
        }
    }
}
