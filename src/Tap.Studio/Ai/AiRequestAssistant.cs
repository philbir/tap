using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Tap.Studio.Contracts;

namespace Tap.Studio.Ai;

/// <summary>
/// Builds the system prompt that teaches the model Tap's request schema and parses the
/// <c>```tap-request</c> JSON block the model emits back into a <see cref="RequestSpecDto"/>.
/// The assistant never writes files — it proposes a spec the UI previews and the user applies.
/// </summary>
public static partial class AiRequestAssistant
{
    /// <summary>Context handed to the model so edits are incremental and reference real
    /// variables / auth profiles / collection settings instead of inventing them.</summary>
    public sealed record AssistContext(
        RequestSpecDto? CurrentSpec,
        CollectionInfo? Collection,
        IReadOnlyList<AuthProfileInfo> AuthProfiles,
        IReadOnlyList<string> EnvironmentNames,
        IReadOnlyList<VariableInfo> Variables);

    /// <summary>The collection that owns the current request — its base URL, default auth,
    /// shared headers, and stages all influence how a request should be crafted.</summary>
    public sealed record CollectionInfo(
        string Name,
        string Path,
        string? BaseUrl,
        string? DefaultAuth,
        IReadOnlyList<string> DefaultHeaderNames,
        IReadOnlyList<string> StageNames,
        string? DefaultStage);

    /// <summary>A reusable auth profile the request can point at via its <c>auth</c> field.</summary>
    public sealed record AuthProfileInfo(
        string Path,
        string? Name,
        string Type,
        IReadOnlyList<string> Scopes,
        IReadOnlyList<string> HeaderNames);

    /// <summary>A variable the model may reference with <c>{{name}}</c>, tagged with the
    /// cascade scope it resolves from and whether it carries a secret (never inline its value).</summary>
    public sealed record VariableInfo(
        string Name,
        string Scope,
        bool Secret,
        string? Description,
        string? Example);

    public static string BuildSystemPrompt(AssistContext ctx)
    {
        var sb = new StringBuilder();
        sb.AppendLine("You are Tap Studio's request assistant — you help craft and edit HTTP requests for the Tap workbench.");
        sb.AppendLine("Tap stores each request as a `*.req.md` file. You never write files; instead you propose a structured request that the user previews and applies.");
        sb.AppendLine();
        sb.AppendLine("## How to respond");
        sb.AppendLine("Write a short, friendly markdown explanation of what you changed and why (1-3 sentences).");
        sb.AppendLine("When the user wants to create or change the request, ALSO emit exactly ONE fenced code block tagged `tap-request` containing a JSON object the UI can apply.");
        sb.AppendLine("Omit the block entirely when the user only asks a question and no change is needed.");
        sb.AppendLine();
        sb.AppendLine("## The tap-request JSON shape");
        sb.AppendLine("```");
        sb.AppendLine("{");
        sb.AppendLine("  \"name\": \"string — short human title\",");
        sb.AppendLine("  \"method\": \"GET | POST | PUT | PATCH | DELETE | HEAD | OPTIONS\",");
        sb.AppendLine("  \"url\": \"string — may contain {{variables}}; absolute or collection-relative\",");
        sb.AppendLine("  \"headers\": [ { \"name\": \"Header-Name\", \"value\": \"value, may use {{vars}}\" } ],");
        sb.AppendLine("  \"requestBody\": \"string | null — raw request body (JSON/text/etc.)\",");
        sb.AppendLine("  \"auth\": \"string | null — relative path to a .auth.md profile, or 'none' to opt out\",");
        sb.AppendLine("  \"protocol\": \"http | websocket — omit or 'http' for normal requests\",");
        sb.AppendLine("  \"tags\": [ \"optional\", \"labels\" ],");
        sb.AppendLine("  \"body\": \"markdown documentation shown under the request — see the Docs rule below\"");
        sb.AppendLine("}");
        sb.AppendLine("```");
        sb.AppendLine("Rules: use `{{variableName}}` for values that should come from variables (never hardcode secrets like tokens or API keys — reference a variable instead). Set a `Content-Type` header when you set a JSON/form body. Only include fields you are changing plus the required `name`, `method`, `url`. Keep existing values you aren't changing.");
        sb.AppendLine();
        sb.AppendLine("## Docs (the `body` field)");
        sb.AppendLine("Whenever you create a request — or change what it does in a meaningful way — ALWAYS set `body` to concise markdown documentation. The body is plain markdown WITHOUT any frontmatter and WITHOUT a fenced `http` block (Tap renders the request itself); just write prose.");
        sb.AppendLine("Good docs cover: a one-line summary of what the endpoint does, notable path/query params and what they mean, the request body shape when there is one, which auth/variables it relies on, and the expected response. Keep it tight (a short paragraph plus optional bullet list). When you're only making a tiny tweak and docs already exist, refine them rather than rewriting from scratch — and preserve any existing notes the user wrote.");
        sb.AppendLine();

        if (ctx.CurrentSpec is { } cur)
        {
            sb.AppendLine("## Current request (edit this — keep what the user didn't ask to change)");
            sb.AppendLine("```json");
            sb.AppendLine(JsonSerializer.Serialize(new
            {
                name = cur.Name,
                method = cur.Method,
                url = cur.Url,
                headers = cur.Headers?.Select(h => new { name = h.Name, value = h.Value }),
                requestBody = cur.RequestBody,
                auth = cur.Auth,
                protocol = cur.Protocol,
                tags = cur.Tags,
                body = cur.Body,
            }, new JsonSerializerOptions { WriteIndented = true }));
            sb.AppendLine("```");
            sb.AppendLine();
        }
        else
        {
            sb.AppendLine("There is no current request — you are helping author a new one.");
            sb.AppendLine();
        }

        if (ctx.Collection is { } col)
        {
            sb.AppendLine("## Collection this request belongs to");
            sb.AppendLine($"Name: {col.Name} ({col.Path}).");
            if (!string.IsNullOrWhiteSpace(col.BaseUrl))
                sb.AppendLine($"Base URL: {col.BaseUrl} — prefer a path relative to this base (e.g. `/users/{{{{id}}}}`) over repeating the host; an absolute `url` overrides it.");
            else
                sb.AppendLine("Base URL: none — requests in this collection use absolute URLs.");
            if (!string.IsNullOrWhiteSpace(col.DefaultAuth))
                sb.AppendLine($"Default auth: {col.DefaultAuth} is applied automatically — only set the request `auth` to override it or set `none` to opt out.");
            if (col.DefaultHeaderNames.Count > 0)
                sb.AppendLine($"Collection headers added to every request (don't duplicate these unless overriding): {string.Join(", ", col.DefaultHeaderNames)}.");
            if (col.StageNames.Count > 0)
                sb.AppendLine($"Stages (environments) available: {string.Join(", ", col.StageNames)}{(col.DefaultStage is { } ds ? $" (default: {ds})" : "")}. Vary host/values across stages with stage-scoped variables, not by hardcoding.");
            sb.AppendLine();
        }

        if (ctx.AuthProfiles.Count > 0)
        {
            sb.AppendLine("## Auth profiles (set the request `auth` to one of these relative paths)");
            foreach (var a in ctx.AuthProfiles)
            {
                var bits = new List<string> { $"type {a.Type}" };
                if (a.Scopes.Count > 0) bits.Add($"scopes: {string.Join(' ', a.Scopes)}");
                if (a.HeaderNames.Count > 0) bits.Add($"sets headers: {string.Join(", ", a.HeaderNames)}");
                var label = string.IsNullOrWhiteSpace(a.Name) ? a.Path : $"{a.Name} ({a.Path})";
                sb.AppendLine($"- {label} — {string.Join("; ", bits)}");
            }
            sb.AppendLine("Reference a profile by `auth` instead of hand-writing Authorization headers.");
            sb.AppendLine();
        }

        if (ctx.EnvironmentNames.Count > 0)
            sb.AppendLine($"Workspace environments: {string.Join(", ", ctx.EnvironmentNames)}.");

        if (ctx.Variables.Count > 0)
        {
            sb.AppendLine("## Variables you can reference with `{{name}}` (don't invent others; never inline a secret's value)");
            foreach (var v in ctx.Variables)
            {
                var bits = new List<string> { v.Scope };
                if (v.Secret) bits.Add("secret");
                if (!string.IsNullOrWhiteSpace(v.Description)) bits.Add(v.Description!.Trim());
                else if (!string.IsNullOrWhiteSpace(v.Example)) bits.Add($"e.g. {v.Example!.Trim()}");
                sb.AppendLine($"- {{{{{v.Name}}}}} — {string.Join("; ", bits)}");
            }
        }

        return sb.ToString();
    }

    [GeneratedRegex("```tap-request\\s*\\n(.*?)```", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex TapRequestBlock();

    /// <summary>Pulls the first <c>```tap-request</c> block out of the reply and merges it
    /// onto <paramref name="current"/> (so untouched fields survive). Returns null when the
    /// reply has no block or the JSON is unusable.</summary>
    public static RequestSpecDto? TryParseProposal(string reply, string path, RequestSpecDto? current)
    {
        var match = TapRequestBlock().Match(reply ?? string.Empty);
        if (!match.Success) return null;
        var json = match.Groups[1].Value.Trim();
        if (json.Length == 0) return null;

        ProposedRequest? p;
        try { p = JsonSerializer.Deserialize<ProposedRequest>(json, Options); }
        catch { return null; }
        if (p is null) return null;

        var headers = p.Headers?
            .Where(h => !string.IsNullOrWhiteSpace(h.Name))
            .Select(h => new HttpHeaderSpecDto(h.Name!.Trim(), h.Value ?? string.Empty))
            .ToArray();

        var protocol = string.Equals(p.Protocol, "websocket", StringComparison.OrdinalIgnoreCase) ? "websocket" : null;

        return new RequestSpecDto
        {
            Path = path,
            Id = current?.Id,
            Name = !string.IsNullOrWhiteSpace(p.Name) ? p.Name!.Trim() : (current?.Name ?? "New request"),
            Method = !string.IsNullOrWhiteSpace(p.Method) ? p.Method!.Trim().ToUpperInvariant() : (current?.Method ?? "GET"),
            Url = p.Url ?? current?.Url ?? string.Empty,
            Headers = headers is { Length: > 0 } ? headers : (p.Headers is null ? current?.Headers : null),
            RequestBody = p.RequestBody ?? (p.HasRequestBody ? null : current?.RequestBody),
            Auth = p.Auth ?? current?.Auth,
            Protocol = protocol ?? current?.Protocol,
            Tags = p.Tags ?? current?.Tags,
            Vars = current?.Vars,
            Secrets = current?.Secrets,
            Body = p.Body ?? current?.Body,
        };
    }

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private sealed record ProposedRequest
    {
        public string? Name { get; init; }
        public string? Method { get; init; }
        public string? Url { get; init; }
        public List<ProposedHeader>? Headers { get; init; }
        public string? RequestBody { get; init; }
        public string? Auth { get; init; }
        public string? Protocol { get; init; }
        public List<string>? Tags { get; init; }
        public string? Body { get; init; }

        // Distinguish "requestBody omitted" from "requestBody: null (clear it)" isn't possible
        // with System.Text.Json defaults, so a present-but-null is treated as "leave as-is".
        public bool HasRequestBody => RequestBody is not null;
    }

    private sealed record ProposedHeader
    {
        public string? Name { get; init; }
        public string? Value { get; init; }
    }
}
