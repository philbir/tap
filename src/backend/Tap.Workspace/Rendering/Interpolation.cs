using System.Text;
using System.Text.RegularExpressions;
using Tap.Workspace.Model;
using Tap.Workspace.Variables;

namespace Tap.Workspace.Rendering;

/// <summary>
/// Token expansion against the provider registry + scope cascade. A token can be either:
/// <list type="bullet">
///   <item><c>{{name}}</c> — unprefixed. Looked up first in the cascade (workspace/collection/env/request),
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
///
/// <para><b>A declared variable may itself hold a template.</b> When a cascade hit names a
/// variable whose scope <em>declared</em> it (the <c>expandable</c> set), that value is
/// expanded in turn — which is what makes
/// <c>vars: { stripe.key: { default: '{{file:stripe.key}}', secret: true } }</c> resolve to
/// the value in the provider rather than going out as the literal token. Resolution walks the
/// author's template each time and never re-scans what a provider returned, and a chain that
/// closes on itself raises <c>E_VAR_CYCLE</c> rather than looping.</para>
///
/// <para>Everything else stays verbatim, deliberately: a per-run override and a value a flow
/// step bound with <c>extract:</c> are <em>data</em>, and re-scanning data would let a
/// response choose which secret the next request carries.</para>
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

    /// <param name="expandable">Names whose cascade value may itself carry <c>{{…}}</c> tokens
    /// — the ones a workspace file declared. Null (the default) keeps every cascade value
    /// verbatim, which is what a caller with no way to tell declaration from data wants.</param>
    public static ValueTask<string> ExpandAsync(
        string input,
        IReadOnlyDictionary<string, string> cascade,
        VariableProviderRegistry registry,
        CancellationToken ct,
        IReadOnlySet<string>? expandable = null)
        => ExpandCoreAsync(input, cascade, registry, expandable, visiting: null, ct);

    /// <param name="visiting">The chain of declared variables currently being resolved, innermost
    /// last. Carried through the recursion so a cycle is named rather than hung on.</param>
    private static async ValueTask<string> ExpandCoreAsync(
        string input,
        IReadOnlyDictionary<string, string> cascade,
        VariableProviderRegistry registry,
        IReadOnlySet<string>? expandable,
        List<string>? visiting,
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
                    resolved[key] = await ResolveDeclaredAsync(
                        name, cascadeValue, cascade, registry, expandable, visiting, ct).ConfigureAwait(false);
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
    /// One cascade value, resolved. A value whose scope declared it is a template in its own
    /// right and gets expanded; anything else is returned as it stands.
    ///
    /// <para>Each level starts from the author's template rather than from the text the level
    /// below produced, so a value a provider returned is never re-scanned however deep the
    /// chain goes.</para>
    /// </summary>
    private static async ValueTask<string> ResolveDeclaredAsync(
        string name,
        string value,
        IReadOnlyDictionary<string, string> cascade,
        VariableProviderRegistry registry,
        IReadOnlySet<string>? expandable,
        List<string>? visiting,
        CancellationToken ct)
    {
        if (expandable is null || !expandable.Contains(name)) return value;
        if (!value.Contains("{{", StringComparison.Ordinal)) return value;

        visiting ??= [];
        if (visiting.Contains(name))
        {
            throw new WorkspaceParseException(new WorkspaceError(
                WorkspaceErrorCode.E_VAR_CYCLE,
                $"Variable '{name}' resolves through itself: "
                + $"{string.Join(" → ", visiting)} → {name}. One of them has to hold a value."));
        }

        visiting.Add(name);
        try
        {
            return await ExpandCoreAsync(value, cascade, registry, expandable, visiting, ct).ConfigureAwait(false);
        }
        finally
        {
            visiting.RemoveAt(visiting.Count - 1);
        }
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
