using Tap.Studio.Contracts;
using Tap.Workspace.Model;
using Tap.Workspace.Rendering;

namespace Tap.Studio.Specs;

/// <summary>
/// Projects a parsed <see cref="RequestFile"/> back to the <see cref="RequestSpecDto"/> the
/// editors and emitters speak.
///
/// <para>Shared rather than inlined because two callers must agree exactly: the request editor
/// (which shows the user these fields) and the OpenAPI re-sync merge (which rewrites some of them
/// and must copy the rest through untouched). If those two disagreed about, say, where the
/// markdown body ends, re-sync would quietly rewrite documentation the editor thinks it owns.</para>
/// </summary>
public static class RequestSpecProjection
{
    public static RequestSpecDto ToSpec(RequestFile request)
    {
        var parsed = TryParseHttpBlock(request.HttpBlock);

        return new RequestSpecDto
        {
            Path = request.RelativePath,
            Id = request.Id,
            Name = request.Name ?? Path.GetFileNameWithoutExtension(request.RelativePath),
            Auth = request.Auth?.RelativePath,
            Tags = request.Tags.Count > 0 ? request.Tags : null,
            Vars = ToVarMap(request.Vars),
            Body = StripHttpFence(request.Body),
            Method = parsed?.Method ?? "GET",
            Url = parsed?.Url ?? string.Empty,
            Headers = parsed?.Headers.Select(h => new HttpHeaderSpecDto(h.Key, h.Value)).ToArray() ?? [],
            RequestBody = parsed?.Body,
            Protocol = request.Protocol.ToWire(),
            Transport = ToTransportDto(request.Transport),
            Assertions = AssertSpecMapper.ToDto(request.Assertions),
        };
    }

    public static HttpBlockParser.Parsed? TryParseHttpBlock(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        try { return HttpBlockParser.Parse(raw); }
        catch { return null; }
    }

    /// <summary>
    /// Strips the ```` ```http ```` fenced block out of the body so the markdown description
    /// doesn't double up. Only the <i>first</i> fence is removed — extra ones are documentation
    /// and pass through verbatim.
    /// </summary>
    public static string StripHttpFence(string body)
    {
        if (string.IsNullOrEmpty(body)) return string.Empty;
        var idx = body.IndexOf("```http", StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return body.Trim();
        var close = body.IndexOf("```", idx + 7, StringComparison.Ordinal);
        if (close < 0) return body.Trim();
        var before = body[..idx].TrimEnd();
        var after = body[(close + 3)..].TrimStart('\r', '\n');
        var combined = string.IsNullOrWhiteSpace(before)
            ? after
            : (string.IsNullOrWhiteSpace(after) ? before : before + "\n\n" + after);
        return combined.Trim();
    }

    public static RequestTransportSettingsDto? ToTransportDto(RequestTransportSettings settings)
        => settings.IgnoreTlsErrors is null && settings.TimeoutMs is null
            ? null
            : new RequestTransportSettingsDto(settings.IgnoreTlsErrors, settings.TimeoutMs);

    /// <summary>The spec DTO carries variables as a flat name→default map; the model keeps the
    /// richer <c>VarSpec</c>. Only the default survives the round trip, which is what the editor
    /// shows and what the emitter writes back.</summary>
    private static IReadOnlyDictionary<string, string>? ToVarMap(IReadOnlyDictionary<string, VarSpec> vars)
        => vars.Count == 0
            ? null
            : vars.ToDictionary(kv => kv.Key, kv => kv.Value.Default ?? string.Empty, StringComparer.Ordinal);
}
