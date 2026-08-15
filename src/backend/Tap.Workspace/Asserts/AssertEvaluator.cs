using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Tap.Workspace.Model;

namespace Tap.Workspace.Asserts;

/// <summary>
/// Evaluates a request's assertions against a captured response. Pure and synchronous —
/// no I/O, no clock, no ambient state — so the Studio's live "re-check against the last
/// response" path, the execute endpoints, and any future headless runner all share one
/// implementation and can never disagree about whether a request passed.
///
/// <para><b>Nothing here throws.</b> A malformed JSONPath, a body that isn't XML, a regex
/// that times out — each becomes a failed assertion carrying an explanation. Assertions
/// describe a response; they are not another way for a Send to blow up.</para>
/// </summary>
public static class AssertEvaluator
{
    /// <summary>Cap on the reported <see cref="AssertResult.Actual"/>. A failing assertion
    /// against a 2 MiB body should show enough to diagnose it, not re-send the body.</summary>
    public const int ActualPreviewLimit = 1024;

    public static IReadOnlyList<AssertResult> Evaluate(
        IReadOnlyList<ResolvedAssert> asserts, ResponseSnapshot response)
    {
        if (asserts.Count == 0) return [];

        var reader = new ResponseReader(response);
        var results = new List<AssertResult>(asserts.Count);
        for (var i = 0; i < asserts.Count; i++)
        {
            results.Add(EvaluateOne(i, asserts[i], reader));
        }
        return results;
    }

    /// <summary>Reports every assertion as skipped with one shared reason. Used for protocols
    /// whose responses assertions don't model yet (WebSocket), so the UI still lists the rows
    /// instead of silently dropping them.</summary>
    public static IReadOnlyList<AssertResult> SkipAll(IReadOnlyList<ResolvedAssert> asserts, string reason)
    {
        var results = new List<AssertResult>(asserts.Count);
        for (var i = 0; i < asserts.Count; i++)
        {
            results.Add(new AssertResult
            {
                Index = i,
                Name = Label(asserts[i]),
                Ok = true,
                Skipped = true,
                Expected = ExpectedText(asserts[i]),
                Message = reason,
            });
        }
        return results;
    }

    private static AssertResult EvaluateOne(int index, ResolvedAssert resolved, ResponseReader reader)
    {
        var spec = resolved.Spec;
        var name = Label(resolved);
        var expectedText = ExpectedText(resolved);

        if (resolved.RenderError is { } renderError)
        {
            return new AssertResult
            {
                Index = index,
                Name = name,
                Ok = false,
                Message = renderError,
            };
        }

        if (spec.Skip)
        {
            return new AssertResult
            {
                Index = index,
                Name = name,
                Ok = true,
                Skipped = true,
                Expected = expectedText,
                Message = "Skipped.",
            };
        }

        var extraction = reader.Read(spec.Source, spec.Selector);
        if (extraction.Error is { } extractionError)
        {
            return new AssertResult
            {
                Index = index,
                Name = name,
                Ok = false,
                Expected = expectedText,
                Message = extractionError,
            };
        }

        var (ok, message) = Apply(spec, extraction);
        return new AssertResult
        {
            Index = index,
            Name = name,
            Ok = ok,
            Actual = Preview(extraction.Describe()),
            Expected = expectedText,
            Message = message,
        };
    }

    private static string Label(ResolvedAssert resolved)
    {
        if (!string.IsNullOrWhiteSpace(resolved.Spec.Name)) return resolved.Spec.Name!;

        // A generated label quotes the expected value, so it has to be masked too — otherwise
        // the secret the Expected field carefully hides shows up in the row title next to it.
        var spec = resolved.ExpectedSecret
            ? resolved.Spec with { Expected = "***", ExpectedList = resolved.Spec.ExpectedList is null ? null : ["***"] }
            : resolved.Spec;
        return spec.Describe();
    }

    private static string? ExpectedText(ResolvedAssert resolved)
    {
        if (resolved.ExpectedSecret) return "***";
        var spec = resolved.Spec;
        if (spec.ExpectedList is { } list) return string.Join(", ", list);
        return spec.Expected;
    }

    // ---------------------------------------------------------------------------------
    // Matching
    // ---------------------------------------------------------------------------------

    private static (bool Ok, string? Message) Apply(AssertSpec spec, ReadValue ex)
    {
        // Existence is the one matcher that is *about* absence, so it runs before the
        // present-check that guards everything else.
        if (spec.Op == AssertOp.Exists)
        {
            var wantPresent = !string.Equals(spec.Expected, "false", StringComparison.OrdinalIgnoreCase);
            if (ex.Present == wantPresent) return (true, null);
            return (false, wantPresent
                ? $"{Capitalize(ex.Subject)} did not match anything in the response."
                : $"{Capitalize(ex.Subject)} is present but was expected to be absent.");
        }

        // Count is defined on an empty nodelist (count: 0 is a legitimate assertion), so it
        // also runs before the present-check.
        if (spec.Op == AssertOp.Count)
        {
            var expectedCount = Number(spec.Expected);
            return expectedCount is not null && ex.Count == (int)expectedCount.Value
                ? (true, null)
                : (false, $"{Capitalize(ex.Subject)} matched {ex.Count} node(s), expected {spec.Expected}.");
        }

        if (!ex.Present)
        {
            return (false, spec.Source == AssertSource.Header
                ? $"{Capitalize(ex.Subject)} is not present in the response."
                : $"{Capitalize(ex.Subject)} did not match anything in the response.");
        }

        if (spec.Op == AssertOp.Length)
        {
            var expectedLength = Number(spec.Expected);
            return ex.NaturalLength is { } actualLength && expectedLength is not null
                   && actualLength == (int)expectedLength.Value
                ? (true, null)
                : (false, $"Length is {ex.NaturalLength?.ToString(CultureInfo.InvariantCulture) ?? "unknown"}, expected {spec.Expected}.");
        }

        if (spec.Op == AssertOp.Type)
        {
            if (!ex.HasJsonValue)
            {
                return (false, ex.Count > 1
                    ? $"{Capitalize(ex.Subject)} matched {ex.Count} nodes — 'type' needs exactly one."
                    : $"{Capitalize(ex.Subject)} did not yield a JSON value.");
            }
            var actualType = JsonTypeName(ex.JsonValue);
            return string.Equals(actualType, AssertJsonType.Parse(spec.Expected), StringComparison.Ordinal)
                ? (true, null)
                : (false, $"Type is {actualType}, expected {spec.Expected}.");
        }

        // `contains` over a multi-node result is membership, which is well defined for any
        // cardinality. Everything below it compares a single value, so a multi-node match is
        // ambiguous and reported as such rather than silently picking the first node.
        if (spec.Op is AssertOp.Contains or AssertOp.NotContains && ex.Values is { Count: > 1 })
        {
            var member = ex.Values.Any(v => ScalarEquals(v, spec.Expected, spec.IgnoreCase));
            var wantMember = spec.Op == AssertOp.Contains;
            return member == wantMember
                ? (true, null)
                : (false, wantMember
                    ? $"None of the {ex.Count} matched nodes equals {spec.Expected}."
                    : $"One of the {ex.Count} matched nodes equals {spec.Expected}.");
        }

        if (ex.Count > 1)
        {
            return (false,
                $"{Capitalize(ex.Subject)} matched {ex.Count} nodes — '{spec.Op.ToWire()}' compares a single value. " +
                "Narrow the expression, or use 'count', 'length', or 'contains'.");
        }

        var actual = ex.Text ?? string.Empty;
        var comparison = spec.IgnoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

        switch (spec.Op)
        {
            case AssertOp.Equals:
            case AssertOp.NotEquals:
                {
                    var equal = spec.Source == AssertSource.Status && IsStatusClassPattern(spec.Expected)
                        ? MatchesStatusClass(actual, spec.Expected!)
                        : ScalarEquals(actual, spec.Expected, spec.IgnoreCase);
                    return (equal == (spec.Op == AssertOp.Equals), null);
                }

            case AssertOp.Contains:
                return (actual.Contains(spec.Expected ?? string.Empty, comparison), null);

            case AssertOp.NotContains:
                return (!actual.Contains(spec.Expected ?? string.Empty, comparison), null);

            case AssertOp.StartsWith:
                return (actual.StartsWith(spec.Expected ?? string.Empty, comparison), null);

            case AssertOp.EndsWith:
                return (actual.EndsWith(spec.Expected ?? string.Empty, comparison), null);

            case AssertOp.Matches:
            case AssertOp.NotMatches:
                return ApplyRegex(spec, actual);

            case AssertOp.LessThan:
            case AssertOp.LessThanOrEqual:
            case AssertOp.GreaterThan:
            case AssertOp.GreaterThanOrEqual:
                return ApplyComparison(spec, actual);

            case AssertOp.Between:
                {
                    if (Number(actual) is not { } value)
                        return (false, $"'{Preview(actual)}' is not a number, so it cannot be range-checked.");
                    var low = Number(spec.ExpectedList?[0]);
                    var high = Number(spec.ExpectedList?[1]);
                    if (low is null || high is null) return (false, "'between' needs two numeric bounds.");
                    if (low > high) (low, high) = (high, low);
                    return (value >= low && value <= high, null);
                }

            case AssertOp.In:
                {
                    var members = spec.ExpectedList ?? [];
                    return (members.Any(m => ScalarEquals(actual, m, spec.IgnoreCase)), null);
                }

            default:
                return (false, $"Matcher '{spec.Op.ToWire()}' is not implemented.");
        }
    }

    private static (bool Ok, string? Message) ApplyRegex(AssertSpec spec, string actual)
    {
        var options = spec.IgnoreCase ? RegexOptions.IgnoreCase : RegexOptions.None;
        Regex regex;
        try
        {
            regex = new Regex(spec.Expected ?? string.Empty, options, ResponseReader.RegexTimeout);
        }
        catch (ArgumentException ex)
        {
            return (false, $"'{spec.Expected}' is not a valid regular expression: {ex.Message}");
        }

        try
        {
            var matched = regex.IsMatch(actual);
            return (matched == (spec.Op == AssertOp.Matches), null);
        }
        catch (RegexMatchTimeoutException)
        {
            return (false, "The regular expression took too long to evaluate (2 s limit) — it likely backtracks catastrophically.");
        }
    }

    private static (bool Ok, string? Message) ApplyComparison(AssertSpec spec, string actual)
    {
        if (Number(actual) is not { } left)
            return (false, $"'{Preview(actual)}' is not a number, so it cannot be compared with '{spec.Op.ToWire()}'.");
        if (Number(spec.Expected) is not { } right)
            return (false, $"'{spec.Expected}' is not a number.");

        var ok = spec.Op switch
        {
            AssertOp.LessThan => left < right,
            AssertOp.LessThanOrEqual => left <= right,
            AssertOp.GreaterThan => left > right,
            AssertOp.GreaterThanOrEqual => left >= right,
            _ => false,
        };
        return (ok, null);
    }

    // ---------------------------------------------------------------------------------
    // Value helpers
    // ---------------------------------------------------------------------------------

    /// <summary>
    /// Equality with the coercion that makes a YAML/variable world usable against JSON:
    /// expected values arrive as strings (a <c>{{var}}</c> can only expand to one), while
    /// the actual side may be a JSON number or boolean. Numbers compare as numbers, booleans
    /// as booleans, everything else as text.
    /// </summary>
    private static bool ScalarEquals(string? actual, string? expected, bool ignoreCase)
    {
        if (actual is null || expected is null) return actual is null && expected is null;
        if (Number(actual) is { } a && Number(expected) is { } b) return a.Equals(b);
        if (bool.TryParse(actual, out var ab) && bool.TryParse(expected, out var bb)) return ab == bb;
        return string.Equals(actual, expected, ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }

    private static double? Number(string? value)
        => double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;

    /// <summary><c>2xx</c> / <c>20x</c> — a three-character status pattern with at least one
    /// wildcard digit. Only honored for the status extractor; elsewhere 'x' is just a letter.</summary>
    private static bool IsStatusClassPattern(string? expected)
    {
        if (expected is not { Length: 3 }) return false;
        var wildcards = 0;
        foreach (var c in expected)
        {
            if (c is 'x' or 'X') wildcards++;
            else if (!char.IsAsciiDigit(c)) return false;
        }
        return wildcards > 0;
    }

    private static bool MatchesStatusClass(string actual, string pattern)
    {
        if (actual.Length != pattern.Length) return false;
        for (var i = 0; i < pattern.Length; i++)
        {
            if (pattern[i] is 'x' or 'X') continue;
            if (pattern[i] != actual[i]) return false;
        }
        return true;
    }

    private static string JsonTypeName(JsonNode? node)
    {
        if (node is null) return AssertJsonType.Null;
        return node switch
        {
            JsonArray => AssertJsonType.Array,
            JsonObject => AssertJsonType.Object,
            _ => node.GetValueKind() switch
            {
                JsonValueKind.String => AssertJsonType.String,
                JsonValueKind.Number => AssertJsonType.Number,
                JsonValueKind.True or JsonValueKind.False => AssertJsonType.Boolean,
                _ => AssertJsonType.Null,
            },
        };
    }

    private static string? Preview(string? value)
    {
        if (value is null) return null;
        return value.Length <= ActualPreviewLimit
            ? value
            : value[..ActualPreviewLimit] + $"… (+{value.Length - ActualPreviewLimit:N0} chars)";
    }

    private static string Capitalize(string subject)
        => subject.Length == 0 ? subject : char.ToUpperInvariant(subject[0]) + subject[1..];
}
