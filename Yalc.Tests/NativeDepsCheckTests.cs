using System;
using System.IO;
using System.Runtime.InteropServices;
using FFmpeg.AutoGen;
using Xunit;
using YetAnotherLosslessCutter.NativeDeps;

namespace YetAnotherLosslessCutter.Tests;

public class NativeDepsCheckTests
{
    // Guards against the foot-gun where a future FFmpeg.AutoGen bump moves the avcodec
    // major (e.g. 62 → 63) but the dep-probe still hardcodes the old number — at which
    // point existing installs would pass the check, then AutoGen would throw
    // DllNotFoundException on first FFmpeg call. By asserting the probe name embeds
    // the current map value, any divergence between binding and probe breaks the test.
    [Fact]
    public void AvcodecProbeName_EmbedsAutoGenVersionMapMajor()
    {
        var major = ffmpeg.LibraryVersionMap["avcodec"].ToString();
        Assert.Contains(major, NativeDepsCheck.AvcodecDllName());
        Assert.Contains(major, NativeDepsCheck.AvcodecSoName());
    }

    [Fact]
    public void Windows_Detect_FlipsWithProbeFilePresence()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return;

        var stub = Path.Combine(AppContext.BaseDirectory, NativeDepsCheck.AvcodecDllName());
        File.Delete(stub);

        try
        {
            Assert.True(NativeDepsCheck.Detect().Ffmpeg,
                "Probe file absent — Detect must report FFmpeg missing");

            File.WriteAllBytes(stub, []);
            Assert.False(NativeDepsCheck.Detect().Ffmpeg,
                "Probe file present — Detect must report FFmpeg available");
        }
        finally
        {
            File.Delete(stub);
        }
    }
}
