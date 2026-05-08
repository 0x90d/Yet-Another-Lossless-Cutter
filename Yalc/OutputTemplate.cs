using System;
using System.Globalization;
using System.IO;
using System.Text;

namespace YetAnotherLosslessCutter;

/// <summary>
/// Pure renderer for output-filename templates. The user supplies a template with
/// curly-brace tokens (<c>{name}</c>, <c>{start}</c>, etc.); we substitute and the
/// caller composes the result with an output directory.
/// </summary>
public static class OutputTemplate
{
    /// <summary>
    /// The default template. Matches the pre-templating hardcoded format exactly so
    /// existing users see no change unless they edit Settings.
    /// </summary>
    public const string Default = "{name}-{start}-{end}{ext}";

    /// <summary>
    /// Inputs to a template render. All fields are pre-formatted strings — the
    /// renderer doesn't do its own time math or path parsing, so it stays trivially
    /// testable.
    /// </summary>
    public readonly record struct Context(
        string Name,
        string Ext,
        TimeSpan StartTime,
        TimeSpan EndTime,
        DateTime Now,
        int Index);

    /// <summary>
    /// Render <paramref name="template"/> against <paramref name="ctx"/>. Unknown
    /// tokens that aren't resolved by <paramref name="resolveCustom"/> are left in
    /// place (visible to the user as a hint that the token name is wrong). An
    /// empty / null template falls back to <see cref="Default"/>.
    /// </summary>
    /// <param name="resolveCustom">Optional fallback for tokens the core doesn't
    /// recognize. Called once per unknown token; non-null/non-empty return is used,
    /// otherwise the token is left in place. Used by plugin-fed tokens.</param>
    public static string Render(string? template, in Context ctx, Func<string, string?>? resolveCustom = null)
    {
        if (string.IsNullOrWhiteSpace(template)) template = Default;

        var sb = new StringBuilder(template.Length + 32);
        var i = 0;
        while (i < template.Length)
        {
            var c = template[i];
            if (c != '{')
            {
                sb.Append(c);
                i++;
                continue;
            }

            var close = template.IndexOf('}', i + 1);
            if (close < 0)
            {
                // Unclosed brace — pass through unchanged.
                sb.Append(template, i, template.Length - i);
                break;
            }

            var token = template.Substring(i + 1, close - i - 1);
            sb.Append(Resolve(token, ctx, resolveCustom));
            i = close + 1;
        }

        return sb.ToString();
    }

    private static string Resolve(string token, in Context ctx, Func<string, string?>? resolveCustom)
    {
        // Tokens are case-insensitive so {Name} and {name} both work.
        var builtIn = token.ToLowerInvariant() switch
        {
            "name"     => ctx.Name,
            "ext"      => ctx.Ext,
            "start"    => FormatSpan(ctx.StartTime),
            "end"      => FormatSpan(ctx.EndTime),
            "duration" => FormatSpan(ctx.EndTime - ctx.StartTime),
            "date"     => ctx.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            "time"     => ctx.Now.ToString("HH-mm-ss", CultureInfo.InvariantCulture),
            "datetime" => ctx.Now.ToString("yyyy-MM-dd_HH-mm-ss", CultureInfo.InvariantCulture),
            "index"    => ctx.Index.ToString("000", CultureInfo.InvariantCulture),
            _ => null,
        };
        if (builtIn != null) return builtIn;

        // Defer to a plugin-supplied resolver before giving up.
        var custom = resolveCustom?.Invoke(token);
        if (!string.IsNullOrEmpty(custom)) return custom;

        return "{" + token + "}"; // unknown — leave in place so the user notices
    }

    /// <summary>
    /// Filesystem-safe span format: <c>hh.mm.ss.fff</c>. Dots instead of colons so
    /// the result drops cleanly into a filename without further escaping.
    /// </summary>
    private static string FormatSpan(TimeSpan ts)
    {
        if (ts < TimeSpan.Zero) ts = TimeSpan.Zero;
        return ts.ToString(@"hh\.mm\.ss\.fff", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Strip characters that can't appear in a filename on the current platform.
    /// Used as a final safety net so a stray <c>:</c> or <c>?</c> the user typed
    /// directly into the template doesn't blow up the cut.
    /// </summary>
    public static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        if (name.IndexOfAny(invalid) < 0) return name;
        var sb = new StringBuilder(name.Length);
        foreach (var c in name)
            sb.Append(Array.IndexOf(invalid, c) >= 0 ? '_' : c);
        return sb.ToString();
    }
}
