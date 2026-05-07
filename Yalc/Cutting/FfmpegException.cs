using System;

namespace YetAnotherLosslessCutter.Cutting;

/// <summary>
/// Thrown when an ffmpeg invocation fails. Carries the exit code, the full command
/// that was run (joined for log inspection), and the captured stderr — gives callers
/// what they need to diagnose without separate plumbing.
/// </summary>
public sealed class FfmpegException : Exception
{
    public int ExitCode { get; }
    public string Command { get; }
    public string Stderr { get; }

    public FfmpegException(int exitCode, string command, string stderr)
        : base(BuildMessage(exitCode, stderr))
    {
        ExitCode = exitCode;
        Command = command;
        Stderr = stderr;
    }

    private static string BuildMessage(int exitCode, string stderr)
    {
        // Last few lines of stderr usually carry the actual error.
        var lines = stderr.Split('\n');
        var tail = lines.Length > 5 ? string.Join('\n', lines[^5..]) : stderr;
        return $"ffmpeg exited with code {exitCode}. Last output:\n{tail.Trim()}";
    }
}
