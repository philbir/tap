namespace Tap.Workspace.Model;

/// <summary>
/// Which part of a response an extraction reads. Deliberately parallel to
/// <see cref="AssertSource"/> — the vocabulary a workspace author learns for assertions is the
/// one they use for extractions — with one addition: <see cref="Regex"/>, which assertions only
/// expose as sugar for <c>body</c> + <c>matches</c> but extraction needs as a source in its own
/// right so it can bind a capture group. See §10.3 of <c>docs/workspace-format.md</c>.
/// </summary>
public enum ExtractSource
{
    Status,
    Duration,
    Header,
    Body,
    JsonPath,
    XPath,

    /// <summary>A .NET regex over the body text; <see cref="ExtractSpec.Group"/> picks the
    /// capture group to bind.</summary>
    Regex,
}

/// <summary>
/// One binding of a response value to a variable name, declared on a flow step. The bound value
/// enters the run's variable bag and is read as an ordinary <c>{{name}}</c> token by every later
/// step — which is the whole mechanism behind "outputs of the previous response are inputs to
/// the next request".
/// </summary>
public sealed record ExtractSpec
{
    /// <summary>The variable name this binds. Required, and unique within a step.</summary>
    public required string Var { get; init; }

    public required ExtractSource Source { get; init; }

    /// <summary>Header name, path expression, or regex pattern. Null for the argument-less
    /// sources (status, duration, body).</summary>
    public string? Selector { get; init; }

    /// <summary>Capture group for <see cref="ExtractSource.Regex"/>. Null means "group 1 if the
    /// pattern declares any, otherwise the whole match".</summary>
    public int? Group { get; init; }

    /// <summary>Bound when the source matches nothing, instead of failing the step.</summary>
    public string? Default { get; init; }

    /// <summary>When false, a source that matches nothing binds nothing and the step carries on.
    /// Default true: a missing value is normally a real failure, because the next step is about
    /// to send the token unexpanded.</summary>
    public bool Required { get; init; } = true;

    /// <summary>Human-readable one-liner, used where a name is needed and none was given.</summary>
    public string Describe() => Source switch
    {
        ExtractSource.Status => $"{Var} ← status",
        ExtractSource.Duration => $"{Var} ← duration",
        ExtractSource.Body => $"{Var} ← body",
        ExtractSource.Header => $"{Var} ← header {Selector}",
        ExtractSource.Regex => $"{Var} ← regex {Selector}",
        _ => $"{Var} ← {Selector}",
    };

    /// <summary>Returns an error message when this extraction can never run, or null when it is
    /// well formed. Both the YAML parser and the Studio DTO mapper funnel through here so a file
    /// and an API call are held to the same rules.</summary>
    public static string? Validate(ExtractSpec spec)
    {
        if (string.IsNullOrWhiteSpace(spec.Var))
            return "'var' is required — an extraction has to say which variable it binds.";

        if (!IsUsableVariableName(spec.Var))
        {
            return $"'{spec.Var}' is not a usable variable name. Names may not contain '{{', '}}', " +
                   "or whitespace — a token that can't be written as {{name}} could never be read back.";
        }

        if (spec.Source.TakesSelector() && string.IsNullOrWhiteSpace(spec.Selector))
        {
            var what = spec.Source switch
            {
                ExtractSource.Header => "a header name",
                ExtractSource.Regex => "a pattern",
                _ => "an expression",
            };
            return $"'{spec.Source.ToWire()}' needs {what}.";
        }

        if (spec.Group is not null && spec.Source != ExtractSource.Regex)
            return "'group' applies to regex extractions only.";

        if (spec.Group is < 0)
            return $"'group' must be zero or positive, got {spec.Group}.";

        return null;
    }

    /// <summary>A name that survives a round trip through <c>{{name}}</c>. The interpolation
    /// regex stops at a brace and trims surrounding whitespace, so a name carrying either could
    /// be written but never read.</summary>
    private static bool IsUsableVariableName(string name)
    {
        foreach (var ch in name)
        {
            if (ch is '{' or '}' || char.IsWhiteSpace(ch)) return false;
        }
        return true;
    }
}

/// <summary>Wire names for <see cref="ExtractSource"/>. These strings appear in workspace files,
/// in the Studio JSON contracts, and in the TypeScript client — treat them as public API.</summary>
public static class ExtractWire
{
    public static string ToWire(this ExtractSource source) => source switch
    {
        ExtractSource.Status => "status",
        ExtractSource.Duration => "duration",
        ExtractSource.Header => "header",
        ExtractSource.Body => "body",
        ExtractSource.JsonPath => "jsonpath",
        ExtractSource.XPath => "xpath",
        ExtractSource.Regex => "regex",
        _ => "body",
    };

    public static ExtractSource? ParseSource(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "status" => ExtractSource.Status,
        "duration" => ExtractSource.Duration,
        "header" => ExtractSource.Header,
        "body" => ExtractSource.Body,
        "jsonpath" => ExtractSource.JsonPath,
        "xpath" => ExtractSource.XPath,
        "regex" => ExtractSource.Regex,
        _ => null,
    };

    /// <summary>Sources whose YAML key carries an argument (<c>header: etag</c>). The rest are
    /// bare markers (<c>body:</c>).</summary>
    public static bool TakesSelector(this ExtractSource source)
        => source is ExtractSource.Header or ExtractSource.JsonPath or ExtractSource.XPath or ExtractSource.Regex;
}
