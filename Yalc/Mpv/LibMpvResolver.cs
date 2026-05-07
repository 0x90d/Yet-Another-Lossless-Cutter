using System;
using System.Reflection;
using System.Runtime.InteropServices;

namespace YetAnotherLosslessCutter.Mpv;

internal static class LibMpvResolver
{
    private static bool _registered;

    public static void Register()
    {
        if (_registered) return;
        _registered = true;
        NativeLibrary.SetDllImportResolver(typeof(LibMpv).Assembly, Resolve);
    }

    private static IntPtr Resolve(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (!string.Equals(libraryName, "libmpv-2", StringComparison.Ordinal))
            return IntPtr.Zero;

        string[] candidates;
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            candidates = new[] { "libmpv-2.dll" };
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            candidates = new[] { "libmpv.2.dylib", "libmpv.dylib" };
        else
            candidates = new[] { "libmpv.so.2", "libmpv.so" };

        foreach (var candidate in candidates)
        {
            if (NativeLibrary.TryLoad(candidate, assembly, searchPath, out var handle))
                return handle;
        }

        return IntPtr.Zero;
    }
}
