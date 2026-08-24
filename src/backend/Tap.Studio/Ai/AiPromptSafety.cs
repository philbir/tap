using System.Text;

namespace Tap.Studio.Ai;

/// <summary>
/// Sanitizes everything that reaches a prompt from outside the code.
///
/// <para>Shared by every assistant rather than copied into each one. The hardening only works if
/// it is applied to <i>all</i> untrusted text, and a second copy is a second place to forget a
/// call site.</para>
/// </summary>
internal static class AiPromptSafety
{
    /// <summary>
    /// Marks the untrusted section of a prompt. <see cref="Clean"/> strips it out of every
    /// interpolated value, so nothing read off disk — or fetched from a URL — can forge the
    /// closing marker and continue as if it were our own instructions.
    /// </summary>
    public const string FenceToken = "UNTRUSTED-WORKSPACE-DATA";

    /// <summary>
    /// Flattens one untrusted string into something safe to interpolate: no control characters
    /// (a newline plus a <c>##</c> heading is all it takes to look like a new section), no
    /// backticks (which would break out of a fenced block), no fence marker, and a hard length
    /// cap so a single description can't crowd out the real instructions.
    /// </summary>
    public static string Clean(string? value, int max = 200)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;

        var sb = new StringBuilder(Math.Min(value.Length, max));
        foreach (var ch in value)
        {
            if (sb.Length >= max) break;
            if (char.IsControl(ch))
            {
                if (sb.Length > 0 && sb[^1] != ' ') sb.Append(' ');
                continue;
            }
            sb.Append(ch == '`' ? '\'' : ch);
        }

        var cleaned = sb.ToString().Replace(FenceToken, "", StringComparison.OrdinalIgnoreCase).Trim();
        return value.Length > max ? cleaned + "…" : cleaned;
    }

    public static string CleanJoin(string separator, IReadOnlyList<string> values)
        => string.Join(separator, values.Take(40).Select(v => Clean(v, 120)));
}
