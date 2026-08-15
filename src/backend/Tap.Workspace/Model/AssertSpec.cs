namespace Tap.Workspace.Model;

/// <summary>
/// Which part of the response an assertion reads. Exactly one per assertion — see
/// §5.5 of <c>docs/workspace-format.md</c>.
/// </summary>
public enum AssertSource
{
    /// <summary>Response status code, as a number.</summary>
    Status,

    /// <summary>Total elapsed milliseconds, as a number.</summary>
    Duration,

    /// <summary>First value of a named response header. <see cref="AssertSpec.Selector"/> is the header name.</summary>
    Header,

    /// <summary>The decoded response body, as text.</summary>
    Body,

    /// <summary>A nodelist from the JSON-parsed body. <see cref="AssertSpec.Selector"/> is an RFC 9535 JSONPath.</summary>
    JsonPath,

    /// <summary>An XPath 1.0 evaluation over the XML-parsed body. <see cref="AssertSpec.Selector"/> is the expression.</summary>
    XPath,
}

/// <summary>
/// How the extracted value is compared against the expected value. The set of operators
/// valid for a given <see cref="AssertSource"/> is enforced at parse time by
/// <see cref="AssertSpec.ValidateCombination"/>.
/// </summary>
public enum AssertOp
{
    Equals,
    NotEquals,
    Contains,
    NotContains,
    StartsWith,
    EndsWith,
    Matches,
    NotMatches,
    LessThan,
    LessThanOrEqual,
    GreaterThan,
    GreaterThanOrEqual,
    Between,
    In,
    Exists,
    Count,
    Length,
    Type,
}

/// <summary>
/// One declared expectation about a response. Parsed from an entry under the request's
/// <c>assertions:</c> frontmatter key; emitted back by
/// <c>Tap.Studio.Specs.SpecYaml.SetAssertions</c>.
///
/// <para>An assertion is always an (extractor, matcher) pair. The YAML surface offers sugar
/// — a bare scalar means <see cref="AssertOp.Equals"/>, a bare selector means
/// <see cref="AssertOp.Exists"/>, and <c>regex:</c> is shorthand for body + matches — but
/// every form normalizes into this shape before it reaches the evaluator.</para>
/// </summary>
public sealed record AssertSpec
{
    /// <summary>Display label. When null the UI and the evaluator fall back to
    /// <see cref="Describe"/>, so a name is never required in a file.</summary>
    public string? Name { get; init; }

    public required AssertSource Source { get; init; }

    /// <summary>Header name for <see cref="AssertSource.Header"/>, the expression for
    /// <see cref="AssertSource.JsonPath"/> / <see cref="AssertSource.XPath"/>. Null for the
    /// argument-less sources (status, duration, body).</summary>
    public string? Selector { get; init; }

    public required AssertOp Op { get; init; }

    /// <summary>Scalar expected value. Null for <see cref="AssertOp.Between"/> and
    /// <see cref="AssertOp.In"/>, which use <see cref="ExpectedList"/>.</summary>
    public string? Expected { get; init; }

    /// <summary>Expected values for the list-shaped operators (<see cref="AssertOp.In"/>,
    /// <see cref="AssertOp.Between"/>).</summary>
    public IReadOnlyList<string>? ExpectedList { get; init; }

    /// <summary>Case-insensitive comparison for the string operators.</summary>
    public bool IgnoreCase { get; init; }

    /// <summary>Parsed and displayed, but not evaluated — reported as skipped.</summary>
    public bool Skip { get; init; }

    /// <summary>Human-readable one-liner used when <see cref="Name"/> is absent. Generated
    /// server-side so the UI, the CLI, and any future report format all agree on the label.</summary>
    public string Describe()
    {
        var subject = Source switch
        {
            AssertSource.Status => "status",
            AssertSource.Duration => "duration",
            AssertSource.Body => "body",
            AssertSource.Header => $"header {Selector}",
            AssertSource.JsonPath => Selector ?? "$",
            AssertSource.XPath => Selector ?? "/",
            _ => "response",
        };

        var expected = Op switch
        {
            AssertOp.Between or AssertOp.In => ExpectedList is null ? "" : string.Join(", ", ExpectedList),
            _ => Expected ?? string.Empty,
        };

        return Op switch
        {
            AssertOp.Exists => string.Equals(Expected, "false", StringComparison.OrdinalIgnoreCase)
                ? $"{subject} does not exist"
                : $"{subject} exists",
            AssertOp.Equals => $"{subject} = {expected}",
            AssertOp.NotEquals => $"{subject} ≠ {expected}",
            AssertOp.Contains => $"{subject} contains {expected}",
            AssertOp.NotContains => $"{subject} does not contain {expected}",
            AssertOp.StartsWith => $"{subject} starts with {expected}",
            AssertOp.EndsWith => $"{subject} ends with {expected}",
            AssertOp.Matches => $"{subject} matches {expected}",
            AssertOp.NotMatches => $"{subject} does not match {expected}",
            AssertOp.LessThan => $"{subject} < {expected}",
            AssertOp.LessThanOrEqual => $"{subject} <= {expected}",
            AssertOp.GreaterThan => $"{subject} > {expected}",
            AssertOp.GreaterThanOrEqual => $"{subject} >= {expected}",
            AssertOp.Between => $"{subject} between {expected}",
            AssertOp.In => $"{subject} in [{expected}]",
            AssertOp.Count => $"{subject} count = {expected}",
            AssertOp.Length => $"{subject} length = {expected}",
            AssertOp.Type => $"{subject} is {expected}",
            _ => subject,
        };
    }

    /// <summary>
    /// Returns an error message when this assertion can never be evaluated, or null when it
    /// is well formed. Every path that produces an <see cref="AssertSpec"/> — the YAML
    /// parser and the Studio DTO mapper — funnels through here, so a file and an API call
    /// are held to exactly the same rules.
    /// </summary>
    public static string? Validate(AssertSpec spec)
        => ValidateCombination(spec.Source, spec.Op) ?? ValidateValues(spec.Op, spec.Expected, spec.ExpectedList);

    /// <summary>
    /// Returns an error message when this source/operator pairing can never be evaluated,
    /// or null when the combination is legal.
    /// </summary>
    public static string? ValidateCombination(AssertSource source, AssertOp op)
    {
        var nodeListSource = source is AssertSource.JsonPath or AssertSource.XPath;

        switch (op)
        {
            case AssertOp.Exists when source is AssertSource.Status or AssertSource.Duration or AssertSource.Body:
                return "'exists' applies to header, jsonpath, and xpath assertions only — status, duration, and body are always present.";
            case AssertOp.Count when !nodeListSource:
                return "'count' applies to jsonpath and xpath assertions only.";
            case AssertOp.Type when source != AssertSource.JsonPath:
                return "'type' applies to jsonpath assertions only.";
            case AssertOp.Length when source is AssertSource.Status or AssertSource.Duration:
                return "'length' does not apply to status or duration assertions.";
            case AssertOp.StartsWith or AssertOp.EndsWith or AssertOp.Matches or AssertOp.NotMatches
                when source is AssertSource.Duration:
                return "String operators do not apply to duration assertions — use lt/lte/gt/gte/between.";
        }

        return null;
    }

    /// <summary>
    /// Returns an error message when the expected value doesn't fit the operator's shape
    /// (a list where a scalar belongs, a non-number for a comparison, an unknown JSON type),
    /// or null when it does.
    /// </summary>
    public static string? ValidateValues(AssertOp op, string? expected, IReadOnlyList<string>? expectedList)
    {
        if (op.TakesList())
        {
            if (expectedList is null)
                return $"'{op.ToWire()}' takes a list — e.g. 'in: [200, 201]'.";

            if (op == AssertOp.Between)
            {
                if (expectedList.Count != 2)
                    return "'between' takes exactly two bounds — e.g. 'between: [200, 299]'.";
                foreach (var bound in expectedList)
                {
                    if (!IsNumber(bound)) return $"'between' bounds must be numbers, got '{bound}'.";
                }
            }
            else if (expectedList.Count == 0)
            {
                return "'in' needs at least one value.";
            }
            return null;
        }

        if (expectedList is not null)
            return $"'{op.ToWire()}' takes a single value, not a list.";

        if (op == AssertOp.Exists)
            return expected is null || bool.TryParse(expected, out _) ? null : $"'exists' takes true or false, not '{expected}'.";

        if (expected is null)
            return $"'{op.ToWire()}' needs a value.";

        var wantsNumber = op is AssertOp.LessThan or AssertOp.LessThanOrEqual or AssertOp.GreaterThan
            or AssertOp.GreaterThanOrEqual or AssertOp.Count or AssertOp.Length;
        if (wantsNumber && !IsNumber(expected))
            return $"'{op.ToWire()}' takes a number, got '{expected}'.";

        if (op == AssertOp.Type && AssertJsonType.Parse(expected) is null)
            return $"'type: {expected}' is not a JSON type. Expected one of: string, number, boolean, object, array, null.";

        return null;
    }

    private static bool IsNumber(string? value)
        => double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out _);
}

/// <summary>
/// The JSON type names accepted by the <see cref="AssertOp.Type"/> matcher. Kept as strings
/// rather than mapped onto <c>JsonValueKind</c> so the vocabulary in a workspace file stays
/// the one a JSON author expects (<c>number</c>, not <c>Number</c>; one <c>boolean</c>
/// instead of <c>True</c>/<c>False</c>).
/// </summary>
public static class AssertJsonType
{
    public const string String = "string";
    public const string Number = "number";
    public const string Boolean = "boolean";
    public const string Object = "object";
    public const string Array = "array";
    public const string Null = "null";

    public static string? Parse(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        String => String,
        Number => Number,
        Boolean => Boolean,
        Object => Object,
        Array => Array,
        Null => Null,
        _ => null,
    };
}

/// <summary>Wire names for the assertion enums. These strings appear in workspace files,
/// in the Studio JSON contracts, and in the TypeScript client — treat them as public API.</summary>
public static class AssertWire
{
    public static string ToWire(this AssertSource source) => source switch
    {
        AssertSource.Status => "status",
        AssertSource.Duration => "duration",
        AssertSource.Header => "header",
        AssertSource.Body => "body",
        AssertSource.JsonPath => "jsonpath",
        AssertSource.XPath => "xpath",
        _ => "status",
    };

    public static AssertSource? ParseSource(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "status" => AssertSource.Status,
        "duration" => AssertSource.Duration,
        "header" => AssertSource.Header,
        "body" => AssertSource.Body,
        "jsonpath" => AssertSource.JsonPath,
        "xpath" => AssertSource.XPath,
        _ => null,
    };

    public static string ToWire(this AssertOp op) => op switch
    {
        AssertOp.Equals => "equals",
        AssertOp.NotEquals => "notEquals",
        AssertOp.Contains => "contains",
        AssertOp.NotContains => "notContains",
        AssertOp.StartsWith => "startsWith",
        AssertOp.EndsWith => "endsWith",
        AssertOp.Matches => "matches",
        AssertOp.NotMatches => "notMatches",
        AssertOp.LessThan => "lt",
        AssertOp.LessThanOrEqual => "lte",
        AssertOp.GreaterThan => "gt",
        AssertOp.GreaterThanOrEqual => "gte",
        AssertOp.Between => "between",
        AssertOp.In => "in",
        AssertOp.Exists => "exists",
        AssertOp.Count => "count",
        AssertOp.Length => "length",
        AssertOp.Type => "type",
        _ => "equals",
    };

    public static AssertOp? ParseOp(string? value) => value?.Trim() switch
    {
        "equals" => AssertOp.Equals,
        "notEquals" => AssertOp.NotEquals,
        "contains" => AssertOp.Contains,
        "notContains" => AssertOp.NotContains,
        "startsWith" => AssertOp.StartsWith,
        "endsWith" => AssertOp.EndsWith,
        "matches" => AssertOp.Matches,
        "notMatches" => AssertOp.NotMatches,
        "lt" => AssertOp.LessThan,
        "lte" => AssertOp.LessThanOrEqual,
        "gt" => AssertOp.GreaterThan,
        "gte" => AssertOp.GreaterThanOrEqual,
        "between" => AssertOp.Between,
        "in" => AssertOp.In,
        "exists" => AssertOp.Exists,
        "count" => AssertOp.Count,
        "length" => AssertOp.Length,
        "type" => AssertOp.Type,
        _ => null,
    };

    /// <summary>Sources that take an argument on the YAML key (<c>header: content-type</c>).
    /// The rest take their expected value there instead (<c>status: 200</c>).</summary>
    public static bool TakesSelector(this AssertSource source)
        => source is AssertSource.Header or AssertSource.JsonPath or AssertSource.XPath;

    /// <summary>Operators whose expected value is a list rather than a scalar.</summary>
    public static bool TakesList(this AssertOp op)
        => op is AssertOp.In or AssertOp.Between;
}
