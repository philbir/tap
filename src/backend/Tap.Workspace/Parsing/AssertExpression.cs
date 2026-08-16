using Tap.Workspace.Model;

namespace Tap.Workspace.Parsing;

/// <summary>
/// The one-line assertion form used by <c># @tap-assert</c> directives:
/// <c>status == 200</c>, <c>header content-type contains json</c>, <c>body $.id exists</c>,
/// <c>duration &lt; 2000</c>.
///
/// <para>A <c>.http</c> file has no YAML, so the frontmatter assertion syntax can't be used
/// there. This is a different <em>spelling</em> of the same model, not a second model: it produces
/// the identical <see cref="AssertSpec"/>, keeps the identical sugar rules (a bare value means
/// equals, nothing at all means exists), and runs through the same
/// <see cref="AssertSpec.ValidateCombination"/>. An assertion means the same thing and reports the
/// same way whichever file it was written in.</para>
/// </summary>
public static class AssertExpression
{
    /// <summary>
    /// Operator spellings. Symbols and words both, because this line is read by people used to
    /// five different tools and there is no cost to accepting either.
    /// </summary>
    private static readonly Dictionary<string, AssertOp> Operators = new(StringComparer.OrdinalIgnoreCase)
    {
        ["=="] = AssertOp.Equals, ["="] = AssertOp.Equals, ["equals"] = AssertOp.Equals, ["is"] = AssertOp.Equals,
        ["!="] = AssertOp.NotEquals, ["not-equals"] = AssertOp.NotEquals,
        ["contains"] = AssertOp.Contains,
        ["not-contains"] = AssertOp.NotContains,
        ["starts-with"] = AssertOp.StartsWith, ["startswith"] = AssertOp.StartsWith,
        ["ends-with"] = AssertOp.EndsWith, ["endswith"] = AssertOp.EndsWith,
        ["matches"] = AssertOp.Matches, ["~="] = AssertOp.Matches,
        ["not-matches"] = AssertOp.NotMatches,
        ["<"] = AssertOp.LessThan, ["<="] = AssertOp.LessThanOrEqual,
        [">"] = AssertOp.GreaterThan, [">="] = AssertOp.GreaterThanOrEqual,
        ["exists"] = AssertOp.Exists,
        ["count"] = AssertOp.Count,
        ["length"] = AssertOp.Length,
        ["type"] = AssertOp.Type,
        ["in"] = AssertOp.In,
        ["between"] = AssertOp.Between,
    };

    public static bool TryParse(string expression, out AssertSpec spec, out string error)
    {
        spec = null!;
        error = string.Empty;

        var tokens = expression.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0)
        {
            error = "expected an expression, e.g. 'status == 200'.";
            return false;
        }

        var cursor = 0;
        AssertSource source;
        string? selector = null;

        switch (tokens[cursor].ToLowerInvariant())
        {
            case "status":
                source = AssertSource.Status;
                cursor++;
                break;

            case "duration":
                source = AssertSource.Duration;
                cursor++;
                break;

            case "header":
                cursor++;
                if (cursor >= tokens.Length)
                {
                    error = "'header' needs a header name, e.g. 'header content-type contains json'.";
                    return false;
                }
                source = AssertSource.Header;
                selector = tokens[cursor++];
                break;

            case "body":
                cursor++;
                // `body $.id exists` selects with JSONPath; bare `body contains x` reads the text.
                if (cursor < tokens.Length && IsJsonPath(tokens[cursor]))
                {
                    source = AssertSource.JsonPath;
                    selector = tokens[cursor++];
                }
                else
                {
                    source = AssertSource.Body;
                }
                break;

            case "xpath":
                cursor++;
                if (cursor >= tokens.Length)
                {
                    error = "'xpath' needs an expression, e.g. 'xpath /root/id exists'.";
                    return false;
                }
                source = AssertSource.XPath;
                selector = tokens[cursor++];
                break;

            default:
                // A bare JSONPath is the shorthand people reach for first.
                if (IsJsonPath(tokens[cursor]))
                {
                    source = AssertSource.JsonPath;
                    selector = tokens[cursor++];
                    break;
                }
                error = $"'{tokens[0]}' is not an extractor. Expected status, duration, header, body, xpath, or a $.jsonpath.";
                return false;
        }

        // Sugar, matching the YAML surface exactly: nothing left means "exists", and a bare value
        // with no operator means "equals".
        AssertOp op;
        string? expected;

        if (cursor >= tokens.Length)
        {
            op = AssertOp.Exists;
            expected = null;
        }
        else if (Operators.TryGetValue(tokens[cursor], out var matched))
        {
            op = matched;
            cursor++;
            expected = cursor < tokens.Length ? string.Join(' ', tokens[cursor..]) : null;
        }
        else
        {
            op = AssertOp.Equals;
            expected = string.Join(' ', tokens[cursor..]);
        }

        IReadOnlyList<string>? expectedList = null;
        if (op is AssertOp.In or AssertOp.Between && expected is not null)
        {
            expectedList = expected
                .Split(expected.Contains(',') ? ',' : ' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToArray();
            expected = null;
        }

        if (op == AssertOp.Exists && expected is not null
            && !bool.TryParse(expected, out _))
        {
            error = $"'exists' takes no value (or true/false), got '{expected}'.";
            return false;
        }

        var candidate = new AssertSpec
        {
            Source = source,
            Selector = selector,
            Op = op,
            Expected = expected,
            ExpectedList = expectedList,
        };

        // The same funnel the YAML parser and the Studio DTO mapper use, so an operator that can
        // never apply to an extractor is rejected identically in every spelling.
        if (AssertSpec.Validate(candidate) is { } invalid)
        {
            error = invalid;
            return false;
        }

        spec = candidate;
        return true;
    }

    private static bool IsJsonPath(string token)
        => token.StartsWith("$.", StringComparison.Ordinal)
        || token.StartsWith("$[", StringComparison.Ordinal)
        || token == "$";
}
