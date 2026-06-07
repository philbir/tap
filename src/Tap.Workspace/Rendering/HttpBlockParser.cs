using Tap.Workspace.Model;

namespace Tap.Workspace.Rendering;

/// <summary>
/// Minimal parser for the fenced <c>http</c> block defined by spec §5.2 (VS Code REST Client
/// / JetBrains HTTP Client syntax — request-line, headers, blank line, body).
/// Interpolation tokens are preserved as-is; expansion happens later in the pipeline.
/// </summary>
public static class HttpBlockParser
{
    public sealed record Parsed(string Method, string Url, IReadOnlyList<(string Key, string Value)> Headers, string? Body);

    public static Parsed Parse(string block)
    {
        var lines = block.Split('\n');
        string? method = null;
        string? url = null;
        var headers = new List<(string, string)>();
        var bodyLines = new List<string>();
        var inBody = false;

        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i].TrimEnd('\r');

            if (method is null)
            {
                if (string.IsNullOrWhiteSpace(line)) continue; // skip leading blank lines
                if (line.StartsWith('#') || line.StartsWith("//")) continue; // comment lines

                var firstSpace = line.IndexOf(' ');
                if (firstSpace < 0)
                {
                    throw new WorkspaceParseException(new WorkspaceError(
                        WorkspaceErrorCode.E_HTTP_BLOCK_SYNTAX,
                        $"Expected 'METHOD URL [HTTP/version]' on first non-blank line; got '{line}'."));
                }
                method = line[..firstSpace].ToUpperInvariant();
                // URL is everything after the method up to an optional ' HTTP/<ver>' suffix.
                // We don't split on spaces because expanded variables may contain spaces.
                var rest = line[(firstSpace + 1)..].TrimStart();
                var httpMarker = rest.LastIndexOf(" HTTP/", StringComparison.OrdinalIgnoreCase);
                url = httpMarker > 0 ? rest[..httpMarker] : rest;
                continue;
            }

            if (!inBody)
            {
                if (string.IsNullOrWhiteSpace(line)) { inBody = true; continue; }

                var colon = line.IndexOf(':');
                if (colon <= 0)
                {
                    throw new WorkspaceParseException(new WorkspaceError(
                        WorkspaceErrorCode.E_HTTP_BLOCK_SYNTAX,
                        $"Expected 'Name: Value' header line; got '{line}'."));
                }
                headers.Add((line[..colon].Trim(), line[(colon + 1)..].Trim()));
            }
            else
            {
                bodyLines.Add(line);
            }
        }

        if (method is null || url is null)
        {
            throw new WorkspaceParseException(new WorkspaceError(
                WorkspaceErrorCode.E_HTTP_BLOCK_SYNTAX,
                "Empty http block — no request line found."));
        }

        var body = bodyLines.Count == 0 ? null : string.Join('\n', bodyLines).TrimEnd('\n', '\r');
        if (body is { Length: 0 }) body = null;
        return new Parsed(method, url, headers, body);
    }
}
