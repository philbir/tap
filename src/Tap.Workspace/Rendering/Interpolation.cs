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
    [GeneratedRegex(@"(?<!\\)\{\{\s*(?:(?<provider>[a-zA-Z][a-zA-Z0-9_-]*)\s*:\s*)?(?<name>[^}\s][^}]*?)\s*\}\}")]
    private static partial Regex TokenRegex();

    public static async ValueTask<string> ExpandAsync(
        string input,
        IReadOnlyDictionary<string, string> cascade,
        VariableProviderRegistry registry,
        CancellationToken ct)
    {
        var matches = TokenRegex().Matches(input);
        if (matches.Count == 0) return input.Replace(@"\{{", "{{");

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

        var sb = new StringBuilder(input.Length);
        var cursor = 0;
        foreach (Match m in matches)
        {
            sb.Append(input, cursor, m.Index - cursor);
            sb.Append(resolved[m.Value]);
            cursor = m.Index + m.Length;
        }
        sb.Append(input, cursor, input.Length - cursor);
        return sb.ToString().Replace(@"\{{", "{{");
    }
}
