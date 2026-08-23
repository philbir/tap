using System.Text.RegularExpressions;

namespace Tap.Core.Capture;

/// <summary>
/// Which captured exchanges a reader wants. Shared by the inspector's REST surface, the MCP
/// tools, and the stdio bridge, so "what does this filter mean" has one answer.
///
/// <para>Matching runs against the <b>redacted</b> summary, never the raw record. That is not
/// an implementation detail: matching on raw text would turn a filter into an oracle — an
/// agent could probe a masked value one character at a time and read the answer off the
/// result count.</para>
/// </summary>
public sealed record CaptureQuery
{
    /// <summary>Exact host match, case-insensitive.</summary>
    public string? Host { get; init; }

    /// <summary>Glob over the path, <c>*</c> and <c>?</c> supported: <c>/webhooks/*</c>.
    /// Matched against the path only, never the query string.</summary>
    public string? PathGlob { get; init; }

    public string? Method { get; init; }

    public int? Status { get; init; }

    public DateTimeOffset? Since { get; init; }

    /// <summary>Only exchanges that failed — 4xx, 5xx, or a proxy-level error.</summary>
    public bool OnlyErrors { get; init; }

    /// <summary>How many to return, newest first. The default is small on purpose: a listing
    /// an agent cannot afford to read is a listing it will summarise badly.</summary>
    public int Limit { get; init; } = 20;

    public static CaptureQuery All { get; } = new();

    public bool Matches(CapturedRequestSummary summary)
    {
        if (Host is not null && !string.Equals(Host, summary.Host, StringComparison.OrdinalIgnoreCase)) return false;
        if (Method is not null && !string.Equals(Method, summary.Method, StringComparison.OrdinalIgnoreCase)) return false;
        if (Status is not null && Status != summary.Status) return false;
        if (Since is not null && summary.At < Since) return false;
        if (OnlyErrors && summary.Status < 400 && summary.Error is null) return false;

        if (PathGlob is not null)
        {
            var path = summary.Path;
            var mark = path.IndexOf('?');
            if (mark >= 0) path = path[..mark];
            if (!Glob.IsMatch(PathGlob, path)) return false;
        }

        return true;
    }
}

/// <summary>Shell-style globbing, the only pattern language the filters accept. Deliberately
/// not regular expressions: a caller-supplied regex over captured traffic is a denial-of-service
/// hole, and nobody debugging a webhook wanted one.</summary>
internal static class Glob
{
    private static readonly Dictionary<string, Regex> Cache = new(StringComparer.Ordinal);
    private static readonly Lock Sync = new();

    public static bool IsMatch(string pattern, string value)
    {
        Regex regex;
        lock (Sync)
        {
            if (!Cache.TryGetValue(pattern, out regex!))
            {
                regex = Compile(pattern);

                // Patterns come from callers, so the cache is unbounded input. It only ever
                // holds what a session actually asked for, and a debugging session asks for a
                // handful — but clear rather than grow without limit.
                if (Cache.Count > 256) Cache.Clear();
                Cache[pattern] = regex;
            }
        }

        return regex.IsMatch(value);
    }

    private static Regex Compile(string pattern)
    {
        var translated = new System.Text.StringBuilder("^");
        foreach (var c in pattern)
        {
            translated.Append(c switch
            {
                '*' => ".*",
                '?' => ".",
                _ => Regex.Escape(c.ToString()),
            });
        }

        translated.Append('$');
        return new Regex(
            translated.ToString(),
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
            TimeSpan.FromMilliseconds(250));
    }
}
