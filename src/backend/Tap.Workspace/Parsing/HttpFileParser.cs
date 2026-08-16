using System.Text;
using Tap.Workspace.Model;

namespace Tap.Workspace.Parsing;

/// <summary>What one <c>.http</c> file produced: its requests, plus everything worth telling the
/// user about how it was read.</summary>
public sealed record HttpFileParseResult(
    IReadOnlyList<RequestFile> Requests,
    IReadOnlyList<WorkspaceError> Errors);

/// <summary>
/// Parses a <c>.http</c> file into <see cref="RequestFile"/> models — one per request in the file.
///
/// <para><b>Why the model is the seam.</b> Everything downstream of a <see cref="RequestFile"/> —
/// rendering, auth, assertions, execution, the CLI, MCP, the Studio — already works. Producing
/// <see cref="RequestFile"/>s from a second on-disk format is therefore the whole job; nothing
/// below this layer needs to know <c>.http</c> exists.</para>
///
/// <para><b>The dialect.</b> The format Visual Studio scaffolds into every new ASP.NET Core
/// project, shared in near-identical form by VS Code REST Client, JetBrains, httpyac, and Kulala:
/// <c>###</c> separators, a <c>METHOD URL [HTTP/version]</c> request line with optional indented
/// query continuation, headers, a blank line, then a body. Comments are <c>#</c> or <c>//</c>;
/// <c>@name = value</c> declares a file variable; <c>{{token}}</c> interpolation is syntactically
/// identical to Tap's own, which is what makes this cheap to support at all.</para>
///
/// <para><b>Constructs from other tools are recognized and skipped, not failed.</b> A JetBrains
/// pre-request script or an httpyac <c>??</c> assertion means the file was written for a tool Tap
/// isn't; dropping the whole file over it would be hostile, and silently ignoring it would be
/// worse. Each one produces a warning naming the Tap equivalent.</para>
///
/// <para>Errors isolate per request. A malformed request drops itself and keeps its line number;
/// the rest of the file still loads. The per-file granularity used for <c>.tap</c> files is too
/// coarse when one file holds twenty requests.</para>
/// </summary>
public static class HttpFileParser
{
    /// <summary>Separator between requests: three or more <c>#</c>, optionally followed by a title.</summary>
    private static bool IsSeparator(string line) =>
        line.StartsWith("###", StringComparison.Ordinal);

    public static HttpFileParseResult Parse(string relativePath, string content)
    {
        var errors = new List<WorkspaceError>();
        var lines = content.Replace("\r\n", "\n").Split('\n');

        // File variables are visible to every request in the file, matching REST Client. Collected
        // in a first pass so a request can use a variable declared below it — files commonly put
        // the @host block at the bottom.
        var fileVars = new Dictionary<string, VarSpec>(StringComparer.OrdinalIgnoreCase);
        foreach (var (line, number) in lines.Select((l, i) => (l, i + 1)))
        {
            if (TryParseVariable(line) is not { } assignment) continue;
            fileVars[assignment.Name] = VarSpec.FromLiteral(assignment.Value);
        }

        var requests = new List<RequestFile>();
        var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var sections = Split(lines);

        // Directives above the first request are the file's defaults. "Above the first request"
        // is the whole preamble, not just section 0 — a file may open with several separator-
        // delimited comment blocks before anything is sent.
        var fileScope = new TapDirectiveParser.Builder();
        foreach (var section in sections)
        {
            if (HasRequestLine(section)) break;
            CollectDirectives(section, relativePath, fileScope, errors);
        }
        var fileDirectives = fileScope.Build();

        foreach (var section in sections)
        {
            RequestFile request;
            try
            {
                if (TryParseSection(relativePath, section, fileVars, fileDirectives, requests.Count, usedNames, errors) is not { } parsed)
                    continue; // A section with no request line — a header block or trailing separator.
                request = parsed;
            }
            catch (WorkspaceParseException ex)
            {
                errors.Add(ex.Error);
                continue;
            }

            requests.Add(request);
        }

        if (requests.Count == 0 && errors.All(e => e.Severity == WorkspaceErrorSeverity.Warning))
        {
            errors.Add(new WorkspaceError(
                WorkspaceErrorCode.E_NO_REQUEST_BLOCK,
                "No requests found. A .http file needs at least one 'METHOD URL' request line.",
                relativePath));
        }

        return new HttpFileParseResult(requests, errors);
    }

    private readonly record struct Section(IReadOnlyList<(string Text, int Number)> Lines, string? Title, int StartLine);

    /// <summary>Splits the file on <c>###</c> lines, carrying each separator's trailing text as the
    /// following section's title.</summary>
    private static List<Section> Split(string[] lines)
    {
        var sections = new List<Section>();
        var current = new List<(string, int)>();
        string? title = null;
        var start = 1;

        for (var i = 0; i < lines.Length; i++)
        {
            var text = lines[i];
            if (!IsSeparator(text))
            {
                current.Add((text, i + 1));
                continue;
            }

            sections.Add(new Section(current, title, start));
            current = [];
            title = text.TrimStart('#').Trim();
            if (title.Length == 0) title = null;
            start = i + 2;
        }

        sections.Add(new Section(current, title, start));
        return sections;
    }

    private readonly record struct Assignment(string Name, string Value);

    /// <summary>A bare <c>@name = value</c> line. Comment lines carrying <c>@directives</c> are
    /// handled separately — those start with <c>#</c> or <c>//</c>.</summary>
    private static Assignment? TryParseVariable(string line)
    {
        var trimmed = line.Trim();
        if (trimmed.Length < 2 || trimmed[0] != '@') return null;

        var equals = trimmed.IndexOf('=');
        if (equals < 2) return null;

        var name = trimmed[1..equals].Trim();
        if (name.Length == 0 || !name.All(ch => char.IsLetterOrDigit(ch) || ch is '_' or '-' or '.')) return null;

        return new Assignment(name, trimmed[(equals + 1)..].Trim());
    }

    /// <summary>True when this section actually sends something, as opposed to being a comment or
    /// variable block between separators.</summary>
    private static bool HasRequestLine(Section section)
    {
        foreach (var (text, _) in section.Lines)
        {
            var trimmed = text.Trim();
            if (trimmed.Length == 0) continue;
            if (TryComment(trimmed) is not null) continue;
            if (TryParseVariable(trimmed) is not null) continue;
            return true;
        }
        return false;
    }

    /// <summary>Feeds a section's comment lines to a directive builder, ignoring everything else.</summary>
    private static void CollectDirectives(
        Section section, string relativePath, TapDirectiveParser.Builder builder, List<WorkspaceError> errors)
    {
        foreach (var (text, number) in section.Lines)
        {
            if (TryComment(text.Trim()) is { } comment)
                builder.TryConsume(comment, relativePath, number, errors);
        }
    }

    private static RequestFile? TryParseSection(
        string relativePath,
        Section section,
        IReadOnlyDictionary<string, VarSpec> fileVars,
        TapDirectiveParser.Directives fileDirectives,
        int ordinal,
        HashSet<string> usedNames,
        List<WorkspaceError> errors)
    {
        string? explicitName = null;
        int? timeoutMs = null;
        var requestLineIndex = -1;
        var scope = new TapDirectiveParser.Builder();

        // Pass 1: directives and the request line. Variables were already collected file-wide.
        for (var i = 0; i < section.Lines.Count; i++)
        {
            var (text, number) = section.Lines[i];
            var trimmed = text.Trim();
            if (trimmed.Length == 0) continue;

            if (TryComment(trimmed) is { } comment)
            {
                if (scope.TryConsume(comment, relativePath, number, errors)) continue;
                if (StartsWithDirective(comment, "@name", out var value)) explicitName = value;
                else if (StartsWithDirective(comment, "@timeout", out var seconds)
                         && double.TryParse(seconds, System.Globalization.CultureInfo.InvariantCulture, out var s))
                    timeoutMs = (int)(s * 1000);
                else WarnForeign(relativePath, comment, number, errors);
                continue;
            }

            if (TryParseVariable(trimmed) is not null) continue;
            if (WarnForeign(relativePath, trimmed, number, errors)) continue;

            requestLineIndex = i;
            break;
        }

        if (requestLineIndex < 0) return null;

        // Pass 2: request line (+ folded query continuation), headers, body.
        var (requestLine, requestLineNumber) = section.Lines[requestLineIndex];
        var url = new StringBuilder(requestLine.Trim());
        var cursor = requestLineIndex + 1;

        // A query continuation is an indented line starting with ? or & — the dialect's way of
        // wrapping a long URL. It folds into the request line with no separator.
        while (cursor < section.Lines.Count)
        {
            var text = section.Lines[cursor].Text;
            if (text.Length == 0 || !char.IsWhiteSpace(text[0])) break;
            var trimmed = text.Trim();
            if (trimmed.Length == 0 || (trimmed[0] != '?' && trimmed[0] != '&')) break;
            url.Append(trimmed);
            cursor++;
        }

        var block = new StringBuilder(url.ToString());
        var inBody = false;

        for (; cursor < section.Lines.Count; cursor++)
        {
            var (text, number) = section.Lines[cursor];
            var trimmed = text.Trim();

            if (!inBody)
            {
                if (trimmed.Length == 0)
                {
                    inBody = true;
                    block.Append('\n');
                    continue;
                }
                // Comments and variable declarations are interleaved freely with headers.
                if (TryComment(trimmed) is { } headerComment)
                {
                    if (!scope.TryConsume(headerComment, relativePath, number, errors))
                        WarnForeign(relativePath, headerComment, number, errors);
                    continue;
                }
                if (TryParseVariable(trimmed) is not null) continue;
                if (WarnForeign(relativePath, trimmed, number, errors)) continue;

                block.Append('\n').Append(trimmed);
                continue;
            }

            // Inside the body everything is literal except foreign constructs, which are dropped
            // so they never reach the wire.
            if (WarnForeign(relativePath, trimmed, number, errors)) continue;
            block.Append('\n').Append(text);
        }

        // HttpBlockParser is the single authority on request-line/header syntax — reusing it here
        // means a .http request and a ```http fence cannot drift apart in what they accept. It
        // raises errors without position, because a fence knows only its own offset; here we do
        // know where the request starts, and an editor marker needs that.
        var blockText = block.ToString().TrimEnd('\n');
        Rendering.HttpBlockParser.Parsed parsed;
        try
        {
            parsed = Rendering.HttpBlockParser.Parse(blockText);
        }
        catch (WorkspaceParseException ex)
        {
            throw new WorkspaceParseException(ex.Error with
            {
                RelativePath = relativePath,
                Line = ex.Error.Line ?? requestLineNumber,
            });
        }

        var name = explicitName
            ?? section.Title
            ?? DeriveName(parsed.Method, parsed.Url)
            ?? $"request-{ordinal + 1}";

        // Names address requests from the CLI and MCP, so two requests in one file cannot share
        // one. A collision falls back to the ordinal rather than silently shadowing.
        if (!usedNames.Add(name))
        {
            name = $"{name} ({ordinal + 1})";
            usedNames.Add(name);
        }

        var directives = fileDirectives.Merge(scope.Build());

        // A `# @tap-secret` name marks an existing file variable as secret so the redactor
        // covers it. Marking a name that isn't declared would be a silent no-op, so it warns.
        var vars = fileVars;
        if (directives.Secrets.Count > 0)
        {
            var copy = new Dictionary<string, VarSpec>(fileVars, StringComparer.OrdinalIgnoreCase);
            foreach (var secret in directives.Secrets)
            {
                if (copy.TryGetValue(secret, out var existing)) copy[secret] = existing with { Secret = true };
                else copy[secret] = new VarSpec { Secret = true };
            }
            vars = copy;
        }

        return new RequestFile
        {
            Kind = WorkspaceKind.Request,
            RelativePath = HttpFragment.Compose(relativePath, name),
            Name = name,
            Body = string.Empty,
            HttpBlock = blockText,
            HttpBlockStartLine = requestLineNumber,
            // Portable, not request-scoped: a `@name = value` line is what this file resolves to
            // when it is opened outside Tap, so inside Tap every workspace scope outranks it.
            PortableVars = vars,
            Tags = directives.Tags,
            Protocol = directives.Protocol ?? RequestProtocol.Http,
            Auth = directives.Auth is { } authRef ? ParseRef(authRef) : null,
            CollectionRef = directives.Collection,
            Assertions = directives.Assertions,
            Transport = new RequestTransportSettings { TimeoutMs = timeoutMs },
        };
    }

    /// <summary>Same ref grammar as the <c>auth:</c> frontmatter key — a relative path, or
    /// <c>id:&lt;uuid&gt;</c>.</summary>
    private static WorkspaceRef ParseRef(string raw)
        => raw.StartsWith("id:", StringComparison.OrdinalIgnoreCase)
            ? WorkspaceRef.FromId(raw[3..])
            : WorkspaceRef.FromPath(raw);

    /// <summary>The comment text after a <c>#</c> or <c>//</c> marker, or null when not a comment.</summary>
    private static string? TryComment(string trimmed)
    {
        if (trimmed.StartsWith("//", StringComparison.Ordinal)) return trimmed[2..].Trim();
        if (trimmed.StartsWith('#')) return trimmed[1..].Trim();
        return null;
    }

    private static bool StartsWithDirective(string comment, string directive, out string value)
    {
        if (comment.StartsWith(directive, StringComparison.OrdinalIgnoreCase)
            && (comment.Length == directive.Length || char.IsWhiteSpace(comment[directive.Length])))
        {
            value = comment[directive.Length..].Trim();
            return true;
        }
        value = string.Empty;
        return false;
    }

    /// <summary>
    /// A readable name from the request line when the file didn't give one: <c>GET /orders/{id}</c>
    /// becomes <c>get-orders</c>. Prefers the last meaningful path segment, which is what a human
    /// would have called it.
    /// </summary>
    private static string? DeriveName(string method, string url)
    {
        var path = url;
        var scheme = path.IndexOf("://", StringComparison.Ordinal);
        if (scheme >= 0)
        {
            var afterHost = path.IndexOf('/', scheme + 3);
            path = afterHost < 0 ? string.Empty : path[afterHost..];
        }

        var query = path.IndexOf('?');
        if (query >= 0) path = path[..query];

        var segments = path
            .Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Where(s => !s.Contains("{{", StringComparison.Ordinal) && !s.StartsWith('{'))
            .ToArray();

        var stem = segments.Length == 0 ? null : segments[^1];
        var slug = Slug(stem is null ? method : $"{method} {stem}");
        return slug.Length == 0 ? null : slug;
    }

    private static string Slug(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var ch in text.ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(ch)) sb.Append(ch);
            else if (sb.Length > 0 && sb[^1] != '-') sb.Append('-');
        }
        return sb.ToString().Trim('-');
    }

    /// <summary>
    /// Recognizes a construct that belongs to another tool's dialect, warns with the Tap
    /// equivalent, and reports whether the line should be skipped.
    /// </summary>
    private static bool WarnForeign(string relativePath, string text, int line, List<WorkspaceError> errors)
    {
        void Warn(string message) => errors.Add(new WorkspaceError(
            WorkspaceErrorCode.W_HTTP_UNSUPPORTED_CONSTRUCT, message, relativePath, line,
            WorkspaceErrorSeverity.Warning));

        if (text.StartsWith("< {%", StringComparison.Ordinal) || text.StartsWith("> {%", StringComparison.Ordinal))
        {
            Warn("JetBrains pre-request/response scripts are not executed. Use a flow (*.flow.tap) to pass values between requests.");
            return true;
        }

        if (text.StartsWith("??", StringComparison.Ordinal))
        {
            Warn("httpyac '??' assertions are not evaluated. Use '# @tap-assert' for the same checks.");
            return true;
        }

        if (text.StartsWith(">>", StringComparison.Ordinal))
        {
            Warn("Response redirects ('>> file') are ignored. Capture responses with the CLI's --output instead.");
            return true;
        }

        if (StartsWithWord(text, "run") || StartsWithWord(text, "import"))
        {
            Warn($"'{text.Split(' ')[0]}' is not supported. Compose requests with a flow (*.flow.tap) or a test set (*.test.tap).");
            return true;
        }

        // Request chaining reads a previous response — the one construct with a real Tap answer
        // that isn't a drop-in, so the message points at it explicitly.
        if (text.Contains(".response.", StringComparison.OrdinalIgnoreCase) && text.Contains("{{", StringComparison.Ordinal))
        {
            Warn("Request chaining ({{name.response...}}) is not resolved. A flow (*.flow.tap) extracts values from one step's response into the next.");
            return false; // Keep the line: the token is reported as an unknown variable at render.
        }

        return false;
    }

    private static bool StartsWithWord(string text, string word) =>
        text.StartsWith(word, StringComparison.OrdinalIgnoreCase)
        && text.Length > word.Length
        && char.IsWhiteSpace(text[word.Length]);
}
