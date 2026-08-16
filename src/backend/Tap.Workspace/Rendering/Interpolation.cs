using System.Text;
using System.Text.RegularExpressions;
using Tap.Workspace.Model;
using Tap.Workspace.Variables;

namespace Tap.Workspace.Rendering;

/// <summary>
/// Token expansion against the provider registry + scope cascade. A token can be either:
/// <list type="bullet">
///   <item><c>{{name}}</c> — unprefixed. Looked up first in the cascade (workspace/collection/api/stage/env/request),
///     then walked across providers in registration order. First non-null wins.</item>
///   <item><c>{{provider:name}}</c> — explicit. Resolved only against <c>provider</c>.
///     Throws if the provider doesn't have the name.</item>
/// </list>
///
/// <para>Resolution is async because providers can be remote (azkv). The output of each
/// expansion respects <see cref="VariableValue.IsSecret"/> only at the rendering boundary
/// — the resolved clear-text is interpolated into the URL/header/body because the request
/// needs to actually go out. The execution trace stored on
/// <see cref="ResolvedRequestMetadata.VariablesUsed"/> records the provider + name + IsSecret
/// (never the value) so history can show which secrets were touched without leaking them.</para>
///
/// <para>Escape: <c>\{{</c> emits a literal <c>{{</c>.</para>
/// </summary>
public static partial class Interpolation
{
    // Either `{{name}}` or `{{provider:name}}`. Provider must match the same shape as a
    // configured provider name (letters/digits/_-).
    //
    // The lazy `[^}]*?` next to `\s*` backtracks quadratically on a long unterminated `{{`, and the
    // text being scanned comes out of a workspace file — so the match is bounded by a 2 s timeout
    // rather than left to run for as long as the input allows. The pattern itself is unchanged:
    // rewriting it to remove the nested quantifier would change which spans it captures.
    [GeneratedRegex(@"(?<!\\)\{\{\s*(?:(?<provider>[a-zA-Z][a-zA-Z0-9_-]*)\s*:\s*)?(?<name>[^}\s][^}]*?)\s*\}\}",
        RegexOptions.None, 2000)]
    private static partial Regex TokenRegex();

    public static async ValueTask<string> ExpandAsync(
        string input,
        IReadOnlyDictionary<string, string> cascade,
        VariableProviderRegistry registry,
        CancellationToken ct)
    {
        List<Match> matches;
        try
        {
            // MatchCollection is lazy — materialize inside the try so a timeout surfaces here and
            // not from some later enumeration.
            matches = TokenRegex().Matches(input).ToList();
        }
        catch (RegexMatchTimeoutException)
        {
            throw new WorkspaceParseException(new WorkspaceError(
                WorkspaceErrorCode.E_HTTP_BLOCK_SYNTAX,
                "Timed out scanning for {{variable}} tokens. The text is too large or contains an unterminated '{{'."));
        }

        if (matches.Count == 0) return Unescape(input);

        // Resolve each unique token once.
        var resolved = new Dictionary<string, string>(matches.Count, StringComparer.Ordinal);
        foreach (Match m in matches)
        {
            var key = m.Value;
            if (resolved.ContainsKey(key)) continue;
            var providerName = m.Groups["provider"].Success ? m.Groups["provider"].Value : null;
            var name = m.Groups["name"].Value.Trim();

            if (providerName is not null)
            {
                var v = await registry.ResolveExplicitAsync(providerName, name, ct).ConfigureAwait(false);
                resolved[key] = v.Value;
            }
            else
            {
                // Cascade first — explicit per-scope vars override provider catalog entries.
                if (cascade.TryGetValue(name, out var cascadeValue))
                {
                    resolved[key] = cascadeValue;
                    continue;
                }

                // Generated tokens ({{$guid}}, {{$timestamp}}, …) come after the cascade so a
                // workspace can still define a variable of that name and win.
                if (DynamicVariables.TryResolve(name, out var generated))
                {
                    resolved[key] = generated;
                    continue;
                }

                var v = await registry.ResolveAnyAsync(name, ct).ConfigureAwait(false);
                if (v is null)
                {
                    throw new WorkspaceParseException(new WorkspaceError(
                        WorkspaceErrorCode.E_VAR_UNKNOWN,
                        $"Unknown variable '{{{{ {name} }}}}'. Not in the scope cascade and no provider resolved it."));
                }
                resolved[key] = v.Value;
            }
        }

        // Unescape the literal segments individually rather than the finished string: a resolved
        // value is emitted verbatim and never re-read, so nothing a provider returns can influence
        // the escaping of the text around it.
        var sb = new StringBuilder(input.Length);
        var cursor = 0;
        foreach (Match m in matches)
        {
            sb.Append(Unescape(input[cursor..m.Index]));
            sb.Append(resolved[m.Value]);
            cursor = m.Index + m.Length;
        }
        sb.Append(Unescape(input[cursor..]));
        return sb.ToString();
    }

    /// <summary>
    /// The unprefixed <c>{{name}}</c> tokens a template references, without resolving
    /// anything. Callers that need to reason about a template — "does this pull in a variable
    /// the workspace marked secret?" — use this rather than re-deriving the token syntax.
    /// Provider-prefixed tokens are excluded: those resolve through the registry, which
    /// reports their sensitivity on <see cref="ResolvedRequestMetadata.VariablesUsed"/>.
    /// </summary>
    public static IReadOnlyList<string> ReferencedNames(string input)
    {
        if (string.IsNullOrEmpty(input) || !input.Contains("{{", StringComparison.Ordinal)) return [];

        List<string> names = [];
        try
        {
            foreach (Match m in TokenRegex().Matches(input))
            {
                if (m.Groups["provider"].Success) continue;
                var name = m.Groups["name"].Value.Trim();
                // Generated tokens are not inputs the user has to supply, so listing them among
                // a request's variables would read as "you still need to set these".
                if (DynamicVariables.IsDynamic(name)) continue;
                names.Add(name);
            }
        }
        catch (RegexMatchTimeoutException)
        {
            // A template this pathological will fail loudly in ExpandAsync a moment later.
            // Reporting "no names" here only affects a secrecy hint, so don't pre-empt it.
            return [];
        }
        return names;
    }

    /// <summary><c>\{{</c> is the escape for a literal <c>{{</c>.</summary>
    private static string Unescape(string literal) => literal.Replace(@"\{{", "{{");
}
