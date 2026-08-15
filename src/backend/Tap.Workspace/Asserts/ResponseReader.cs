using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using System.Xml.XPath;
using Json.Path;
using Tap.Workspace.Model;

namespace Tap.Workspace.Asserts;

/// <summary>
/// What one extractor pulled out of a response, in the shapes its consumers need: a single text
/// value, a list of them, a node count, and (for JSONPath) the raw node so a type check can see
/// the JSON kind rather than a stringified approximation.
/// </summary>
public sealed record ReadValue
{
    /// <summary>Set when the read couldn't happen at all — a malformed selector, a body that
    /// isn't JSON, a truncated capture. Distinct from <see cref="Present"/>: "I couldn't look"
    /// is not "I looked and found nothing".</summary>
    public string? Error { get; init; }

    /// <summary>True when the source matched something.</summary>
    public bool Present { get; init; }

    /// <summary>The single matched value, when the read produced exactly one.</summary>
    public string? Text { get; init; }

    /// <summary>Every matched value, for the multi-node reads.</summary>
    public IReadOnlyList<string>? Values { get; init; }

    public int Count { get; init; }

    /// <summary>Length as an author means it: characters for text, elements for a JSON array,
    /// properties for an object, otherwise the node count.</summary>
    public int? NaturalLength { get; init; }

    public JsonNode? JsonValue { get; init; }
    public bool HasJsonValue { get; init; }

    /// <summary>The subject phrase used in messages ("$.items matched 3 nodes").</summary>
    public string Subject { get; init; } = "value";

    public string? Describe()
    {
        if (!Present) return null;
        if (Values is { Count: > 1 }) return string.Join(", ", Values);
        return Text;
    }
}

/// <summary>
/// Reads values out of a captured response. The single implementation of "what does
/// <c>$.order.id</c> mean against this body", shared by <see cref="AssertEvaluator"/> (which
/// compares the value) and the flow runner (which binds it to a variable) — so an assertion and
/// an extraction can never disagree about what a selector points at.
///
/// <para>Pure and synchronous, and <b>nothing here throws</b>: a bad selector, a body that isn't
/// XML, a regex that backtracks forever — each comes back as a <see cref="ReadValue.Error"/> for
/// the caller to report in its own vocabulary.</para>
///
/// <para>One instance per response: the body is parsed as JSON and as XML at most once each, on
/// demand, so a step with twelve JSONPath reads parses once and one with none never touches a
/// parser.</para>
/// </summary>
public sealed class ResponseReader(ResponseSnapshot response)
{
    /// <summary>Same 2 s ceiling <c>Interpolation</c> uses. Patterns come from workspace files,
    /// so a catastrophically backtracking one is a plausible accident.</summary>
    internal static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(2);

    private bool _jsonParsed;
    private JsonNode? _json;
    private string? _jsonError;

    private bool _xmlParsed;
    private XDocument? _xml;
    private string? _xmlError;

    /// <summary>Set when no body-family read can run at all, regardless of flavor.</summary>
    private string? BodyUnavailable => response.BodyTruncated
        ? "The response body was truncated at the 2 MiB capture cap, so body assertions were not evaluated."
        : null;

    /// <summary>Reads one of the assertion sources (§5.5). <paramref name="selector"/> is the
    /// header name or path expression; the argument-less sources ignore it.</summary>
    public ReadValue Read(AssertSource source, string? selector)
    {
        switch (source)
        {
            case AssertSource.Status:
                return new ReadValue
                {
                    Present = true,
                    Text = response.Status.ToString(CultureInfo.InvariantCulture),
                    Count = 1,
                    Subject = "status",
                };

            case AssertSource.Duration:
                return new ReadValue
                {
                    Present = true,
                    Text = response.DurationMs.ToString("0.###", CultureInfo.InvariantCulture),
                    Count = 1,
                    Subject = "duration",
                };

            case AssertSource.Header:
                return ReadHeader(selector ?? string.Empty);

            case AssertSource.Body:
                {
                    if (BodyUnavailable is { } reason) return new ReadValue { Error = reason };
                    var text = response.BodyText ?? string.Empty;
                    return new ReadValue
                    {
                        Present = response.BodyText is not null,
                        Text = text,
                        Count = 1,
                        NaturalLength = text.Length,
                        Subject = "body",
                    };
                }

            case AssertSource.JsonPath:
                return ReadJsonPath(selector ?? string.Empty);

            case AssertSource.XPath:
                return ReadXPath(selector ?? string.Empty);

            default:
                return new ReadValue { Error = "Unsupported assertion source." };
        }
    }

    /// <summary>Reads one of the extraction sources (§10.3). Same vocabulary as
    /// <see cref="Read(AssertSource, string?)"/> plus <see cref="ExtractSource.Regex"/>, which
    /// only extraction exposes as a source in its own right.</summary>
    public ReadValue Read(ExtractSource source, string? selector, int? group = null)
        => source switch
        {
            ExtractSource.Status => Read(AssertSource.Status, null),
            ExtractSource.Duration => Read(AssertSource.Duration, null),
            ExtractSource.Header => Read(AssertSource.Header, selector),
            ExtractSource.Body => Read(AssertSource.Body, null),
            ExtractSource.JsonPath => Read(AssertSource.JsonPath, selector),
            ExtractSource.XPath => Read(AssertSource.XPath, selector),
            ExtractSource.Regex => ReadRegex(selector ?? string.Empty, group),
            _ => new ReadValue { Error = "Unsupported extraction source." },
        };

    private ReadValue ReadHeader(string wanted)
    {
        foreach (var (key, value) in response.Headers)
        {
            if (!string.Equals(key, wanted, StringComparison.OrdinalIgnoreCase)) continue;
            return new ReadValue
            {
                Present = true,
                Text = value,
                Count = 1,
                NaturalLength = value.Length,
                Subject = $"header '{wanted}'",
            };
        }
        return new ReadValue { Present = false, Count = 0, Subject = $"header '{wanted}'" };
    }

    private ReadValue ReadJsonPath(string expression)
    {
        if (BodyUnavailable is { } reason) return new ReadValue { Error = reason };

        if (!JsonPath.TryParse(expression, out var path))
            return new ReadValue { Error = $"'{expression}' is not a valid JSONPath expression." };

        EnsureJson();
        if (_jsonError is { } jsonError) return new ReadValue { Error = jsonError };

        var matches = path.Evaluate(_json).Matches;
        var values = new List<string>(matches.Count);
        foreach (var match in matches) values.Add(JsonText(match.Value));

        return new ReadValue
        {
            Present = matches.Count > 0,
            Count = matches.Count,
            Text = matches.Count == 1 ? values[0] : null,
            Values = values,
            NaturalLength = matches.Count == 1 ? JsonLength(matches[0].Value) : matches.Count,
            JsonValue = matches.Count == 1 ? matches[0].Value : null,
            HasJsonValue = matches.Count == 1,
            Subject = $"'{expression}'",
        };
    }

    private ReadValue ReadXPath(string expression)
    {
        if (BodyUnavailable is { } reason) return new ReadValue { Error = reason };

        EnsureXml();
        if (_xmlError is { } xmlError) return new ReadValue { Error = xmlError };

        object? evaluated;
        try
        {
            evaluated = _xml!.XPathEvaluate(expression);
        }
        catch (XPathException ex)
        {
            return new ReadValue { Error = $"'{expression}' is not a valid XPath expression: {ex.Message}" };
        }

        // XPath 1.0 evaluates to one of four types. The nodeset case is the interesting one;
        // the three scalar types collapse straight to a single value.
        switch (evaluated)
        {
            case string s:
                return Single(s, expression);
            case bool b:
                return Single(b ? "true" : "false", expression);
            case double d:
                return Single(d.ToString("0.###", CultureInfo.InvariantCulture), expression);
            case IEnumerable<object> nodes:
                {
                    var values = new List<string>();
                    foreach (var node in nodes) values.Add(XmlText(node));
                    return new ReadValue
                    {
                        Present = values.Count > 0,
                        Count = values.Count,
                        Text = values.Count == 1 ? values[0] : null,
                        Values = values,
                        NaturalLength = values.Count == 1 ? values[0].Length : values.Count,
                        Subject = $"'{expression}'",
                    };
                }
            default:
                return new ReadValue { Error = $"'{expression}' produced an XPath result Tap cannot compare." };
        }

        static ReadValue Single(string value, string expression) => new()
        {
            Present = true,
            Count = 1,
            Text = value,
            Values = [value],
            NaturalLength = value.Length,
            Subject = $"'{expression}'",
        };
    }

    /// <summary>
    /// Matches <paramref name="pattern"/> against the body and reads back one capture group.
    /// <paramref name="group"/> defaults to 1 when the pattern declares any groups and 0 (the
    /// whole match) when it doesn't — writing <c>regex: 'session=([^;]+)'</c> and getting the
    /// whole <c>session=…</c> back would surprise everyone, and so would being forced to write
    /// <c>group: 0</c> for a pattern with nothing to capture.
    /// </summary>
    private ReadValue ReadRegex(string pattern, int? group)
    {
        if (BodyUnavailable is { } reason) return new ReadValue { Error = reason };

        var subject = $"regex '{pattern}'";
        Regex regex;
        try
        {
            regex = new Regex(pattern, RegexOptions.None, RegexTimeout);
        }
        catch (ArgumentException ex)
        {
            return new ReadValue { Error = $"'{pattern}' is not a valid regular expression: {ex.Message}" };
        }

        var body = response.BodyText;
        if (body is null)
            return new ReadValue { Present = false, Count = 0, Subject = subject };

        Match match;
        try
        {
            match = regex.Match(body);
        }
        catch (RegexMatchTimeoutException)
        {
            return new ReadValue
            {
                Error = $"'{pattern}' took too long to evaluate (2 s limit) — it likely backtracks catastrophically.",
            };
        }

        if (!match.Success)
            return new ReadValue { Present = false, Count = 0, Subject = subject };

        var wanted = group ?? (regex.GetGroupNumbers().Length > 1 ? 1 : 0);
        if (wanted >= match.Groups.Count)
        {
            return new ReadValue
            {
                Error = $"'{pattern}' has no capture group {wanted} — it declares {match.Groups.Count - 1}.",
            };
        }

        var captured = match.Groups[wanted];
        if (!captured.Success)
            return new ReadValue { Present = false, Count = 0, Subject = $"{subject} group {wanted}" };

        return new ReadValue
        {
            Present = true,
            Count = 1,
            Text = captured.Value,
            Values = [captured.Value],
            NaturalLength = captured.Value.Length,
            Subject = subject,
        };
    }

    private void EnsureJson()
    {
        if (_jsonParsed) return;
        _jsonParsed = true;

        if (string.IsNullOrWhiteSpace(response.BodyText))
        {
            _jsonError = "The response has no body to evaluate as JSON.";
            return;
        }

        try
        {
            _json = JsonNode.Parse(response.BodyText!);
            if (_json is null) _jsonError = "The response body is not valid JSON.";
        }
        catch (JsonException ex)
        {
            _jsonError = $"The response body is not valid JSON: {ex.Message}";
        }
    }

    private void EnsureXml()
    {
        if (_xmlParsed) return;
        _xmlParsed = true;

        if (string.IsNullOrWhiteSpace(response.BodyText))
        {
            _xmlError = "The response has no body to evaluate as XML.";
            return;
        }

        try
        {
            // DTD processing stays off (LoadOptions has no DTD switch; XDocument.Parse never
            // resolves external entities), so a hostile response can't turn an assertion into
            // an entity-expansion or file-read primitive.
            _xml = XDocument.Parse(response.BodyText!, LoadOptions.PreserveWhitespace);
        }
        catch (System.Xml.XmlException ex)
        {
            _xmlError = $"The response body is not valid XML: {ex.Message}";
        }
    }

    internal static string JsonText(JsonNode? node)
    {
        if (node is null) return "null";
        // A JSON string compares as its contents, not as its quoted literal — `equals: ok`
        // should match `"ok"`.
        if (node is JsonValue value && value.TryGetValue<string>(out var s)) return s;
        return node.ToJsonString();
    }

    /// <summary>Length as a JSON author means it: elements for an array, properties for an
    /// object, characters for a string, digits/characters of the literal otherwise.</summary>
    private static int JsonLength(JsonNode? node) => node switch
    {
        null => 0,
        JsonArray array => array.Count,
        JsonObject obj => obj.Count,
        _ => JsonText(node).Length,
    };

    private static string XmlText(object node) => node switch
    {
        XElement element => element.Value,
        XAttribute attribute => attribute.Value,
        XText text => text.Value, // also covers XCData, which derives from XText
        XComment comment => comment.Value,
        _ => node.ToString() ?? string.Empty,
    };
}
