using System.Globalization;

namespace Tap.Workspace.Parsing;

/// <summary>
/// Parses and formats the byte sizes that appear in frontmatter (<c>response.maxBytes</c>
/// today). Plain integers are bytes; a <c>kb</c> / <c>mb</c> / <c>gb</c> suffix multiplies
/// by 1024 and its powers, with the <c>b</c> optional and case ignored — <c>2mb</c>,
/// <c>2 MB</c> and <c>2m</c> are the same two mebibytes.
///
/// <para>Formatting is the inverse for exact multiples only: 2 MiB round-trips as
/// <c>2mb</c>, 1 500 000 stays <c>1500000</c>. A number the user wrote by hand should come
/// back looking like what they wrote.</para>
/// </summary>
public static class ByteSize
{
    private const long Kilo = 1024;
    private const long Mega = Kilo * 1024;
    private const long Giga = Mega * 1024;

    /// <summary>Parse a size. Returns false for anything not a non-negative number with an
    /// optional unit — including negatives, which are never a cap the caller meant.</summary>
    public static bool TryParse(string? text, out long bytes)
    {
        bytes = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;

        var s = text.Trim();
        var unit = 1L;
        // Longest suffix first: "kb" has to win over "b".
        foreach (var (suffix, multiplier) in Suffixes)
        {
            if (s.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                unit = multiplier;
                s = s[..^suffix.Length].TrimEnd();
                break;
            }
        }

        if (!long.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out var value)) return false;
        // Overflow means somebody typed a size no machine has; treat it as unparseable
        // rather than silently wrapping into a small cap.
        try { bytes = checked(value * unit); }
        catch (OverflowException) { return false; }
        return true;
    }

    /// <summary>Render a byte count back to frontmatter, using the largest unit that divides
    /// it exactly.</summary>
    public static string Format(long bytes)
    {
        if (bytes <= 0) return "0";
        if (bytes % Giga == 0) return $"{bytes / Giga}gb";
        if (bytes % Mega == 0) return $"{bytes / Mega}mb";
        if (bytes % Kilo == 0) return $"{bytes / Kilo}kb";
        return bytes.ToString(CultureInfo.InvariantCulture);
    }

    private static readonly (string Suffix, long Multiplier)[] Suffixes =
    [
        ("kib", Kilo), ("mib", Mega), ("gib", Giga),
        ("kb", Kilo), ("mb", Mega), ("gb", Giga),
        ("k", Kilo), ("m", Mega), ("g", Giga),
        ("b", 1),
    ];
}
