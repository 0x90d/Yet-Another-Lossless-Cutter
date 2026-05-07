using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;
using YetAnotherLosslessCutter.NativeDeps;

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

        var candidates = new List<string>();
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            candidates.Add("libmpv-2.dll");
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            candidates.Add("libmpv.2.dylib");
            candidates.Add("libmpv.dylib");
            // Homebrew installs to a prefix that's not in dyld's default search path.
            candidates.AddRange(HomebrewPaths.CandidatesFor("libmpv.2.dylib"));
            candidates.AddRange(HomebrewPaths.CandidatesFor("libmpv.dylib"));
        }
        else
        {
            candidates.Add("libmpv.so.2");
            candidates.Add("libmpv.so");
            candidates.AddRange(HomebrewPaths.CandidatesFor("libmpv.so.2"));
            candidates.AddRange(HomebrewPaths.CandidatesFor("libmpv.so"));
        }

        foreach (var candidate in candidates)
        {
            if (NativeLibrary.TryLoad(candidate, assembly, searchPath, out var handle))
                return handle;
        }

        return IntPtr.Zero;
    }
}
