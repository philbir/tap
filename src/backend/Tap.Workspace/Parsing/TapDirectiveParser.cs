using Tap.Workspace.Model;

namespace Tap.Workspace.Parsing;

/// <summary>
/// Tap's own additions to the <c>.http</c> format, carried as comment directives:
/// <c># @tap-collection</c>, <c># @tap-auth</c>, <c># @tap-assert</c>, <c># @tap-secret</c>,
/// <c># @tap-protocol</c>, <c># @tap-tag</c>.
///
/// <para><b>Why comments.</b> A vendor-prefixed comment directive is established practice in this
/// ecosystem (kulala- and curl- prefixes do the same), and it degrades to an inert comment in all
/// five tools that read the format. A file carrying Tap assertions still opens, still sends, and
/// still looks normal in Visual Studio — which is the whole point of adopting a portable format
/// rather than inventing one.</para>
///
/// <para>Directives above the first request are file-wide defaults; the same directive inside a
/// request's comment block overrides the file default for that request.</para>
/// </summary>
public static class TapDirectiveParser
{
    public const string Prefix = "@tap-";

    /// <summary>Everything a directive block declared, before file and request scopes are merged.</summary>
    public sealed record Directives
    {
        public string? Collection { get; init; }
        public string? Auth { get; init; }
        public RequestProtocol? Protocol { get; init; }
        public IReadOnlyList<AssertSpec> Assertions { get; init; } = [];
        public IReadOnlyList<string> Tags { get; init; } = [];
        public IReadOnlyList<string> Secrets { get; init; } = [];

        /// <summary>Request scope wins over file scope, key by key. Assertions, tags, and secrets
        /// accumulate instead — a file-level assertion applying to every request is the reason to
        /// write one, so a request adding its own must not silently drop it.</summary>
        public Directives Merge(Directives request) => new()
        {
            Collection = request.Collection ?? Collection,
            Auth = request.Auth ?? Auth,
            Protocol = request.Protocol ?? Protocol,
            Assertions = [.. Assertions, .. request.Assertions],
            Tags = [.. Tags, .. request.Tags.Where(t => !Tags.Contains(t, StringComparer.OrdinalIgnoreCase))],
            Secrets = [.. Secrets, .. request.Secrets.Where(s => !Secrets.Contains(s, StringComparer.OrdinalIgnoreCase))],
        };
    }

    /// <summary>Accumulates directives from a block of comment lines.</summary>
    public sealed class Builder
    {
        private string? _collection;
        private string? _auth;
        private RequestProtocol? _protocol;
        private readonly List<AssertSpec> _assertions = [];
        private readonly List<string> _tags = [];
        private readonly List<string> _secrets = [];

        public bool IsEmpty => _collection is null && _auth is null && _protocol is null
            && _assertions.Count == 0 && _tags.Count == 0 && _secrets.Count == 0;

        /// <summary>
        /// Consumes one comment's text. Returns true when it was a <c>@tap-</c> directive (handled
        /// or rejected), so the caller knows not to treat it as anything else.
        /// </summary>
        public bool TryConsume(string comment, string relativePath, int line, List<WorkspaceError> errors)
        {
            if (!comment.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase)) return false;

            var space = comment.IndexOf(' ');
            var key = (space < 0 ? comment : comment[..space]).ToLowerInvariant();
            var value = space < 0 ? string.Empty : comment[(space + 1)..].Trim();

            void Warn(string message) => errors.Add(new WorkspaceError(
                WorkspaceErrorCode.W_HTTP_UNSUPPORTED_CONSTRUCT, message, relativePath, line,
                WorkspaceErrorSeverity.Warning));

            switch (key)
            {
                case "@tap-collection":
                    if (value.Length == 0) Warn("'@tap-collection' needs a collection slug.");
                    else _collection = value;
                    return true;

                case "@tap-auth":
                    if (value.Length == 0) Warn("'@tap-auth' needs an auth profile path or name.");
                    else _auth = value;
                    return true;

                case "@tap-protocol":
                    var protocol = RequestProtocolExtensions.TryParse(value);
                    if (protocol is null) Warn($"'@tap-protocol {value}' is not a known protocol. Expected one of: http, websocket.");
                    else _protocol = protocol;
                    return true;

                case "@tap-tag":
                    _tags.AddRange(value
                        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                        .Where(t => !_tags.Contains(t, StringComparer.OrdinalIgnoreCase)));
                    return true;

                case "@tap-secret":
                    _secrets.AddRange(value
                        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                        .Where(s => !_secrets.Contains(s, StringComparer.OrdinalIgnoreCase)));
                    return true;

                case "@tap-assert":
                    if (AssertExpression.TryParse(value, out var spec, out var assertError)) _assertions.Add(spec);
                    else
                    {
                        errors.Add(new WorkspaceError(
                            WorkspaceErrorCode.E_ASSERT_INVALID,
                            $"'@tap-assert {value}': {assertError}",
                            relativePath, line));
                    }
                    return true;

                default:
                    // A typo'd directive is silently inert otherwise, which is the worst outcome:
                    // the user believes the assertion is running.
                    Warn($"Unknown directive '{key}'. Known: @tap-collection, @tap-auth, @tap-assert, @tap-secret, @tap-protocol, @tap-tag.");
                    return true;
            }
        }

        public Directives Build() => new()
        {
            Collection = _collection,
            Auth = _auth,
            Protocol = _protocol,
            Assertions = _assertions,
            Tags = _tags,
            Secrets = _secrets,
        };
    }
}
