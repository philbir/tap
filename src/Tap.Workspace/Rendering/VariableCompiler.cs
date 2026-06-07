using System.Text;
using System.Text.RegularExpressions;

namespace Tap.Workspace.Rendering;

/// <summary>
/// Soft preview helper for the Variables panel's "Test it" pane. Unlike
/// <see cref="Interpolation"/> (which throws on unknown variables and resolves provider
/// values), this renders what it can against the already-built variable view from
/// <see cref="VariableViewBuilder"/> and annotates each token with provenance + sensitivity.
///
/// <para>Both unprefixed (<c>{{name}}</c>) and explicit (<c>{{provider:name}}</c>) tokens
/// are accepted. Unprefixed tokens hit the merged-by-name dictionary (cascade-wins, last
/// provider-set wins among equals). Explicit tokens search the original layered sets
/// because the merged dictionary's dedupe-by-name loses the entry that was overwritten —
/// callers can still ask for a specific provider's value via the explicit form.</para>
/// </summary>
public static partial class VariableCompiler
{
    [GeneratedRegex(@"(?<!\\)\{\{\s*(?:(?<provider>[a-zA-Z][a-zA-Z0-9_-]*)\s*:\s*)?(?<name>[^}\s][^}]*?)\s*\}\}")]
    private static partial Regex TokenRegex();

    public static CompileResult Compile(string template, IReadOnlyDictionary<string, Variable> scope)
        => Compile(template, scope, sets: null);

    public static CompileResult Compile(string template, IReadOnlyDictionary<string, Variable> scope, IReadOnlyList<VariableSet>? sets)
    {
        var replacements = new List<Replacement>();
        var sb = new StringBuilder(template.Length);
        var cursor = 0;

        foreach (Match m in TokenRegex().Matches(template))
        {
            sb.Append(template, cursor, m.Index - cursor);
            var providerName = m.Groups["provider"].Success ? m.Groups["provider"].Value : null;
            var name = m.Groups["name"].Value.Trim();

            Variable? v = null;
            if (providerName is not null)
            {
                // Explicit lookup — scan the layered sets so a same-named variable in a
                // different set isn't shadowed by the merged-by-name dedupe.
                if (sets is not null)
                {
                    foreach (var set in sets)
                    {
                        if (set.ProviderName is null
                            || !string.Equals(set.ProviderName, providerName, StringComparison.OrdinalIgnoreCase)) continue;
                        var hit = set.Variables.FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.Ordinal));
                        if (hit is not null) { v = hit; break; }
                    }
                }
                if (v is null
                    && scope.TryGetValue(name, out var candidate)
                    && string.Equals(candidate.ProviderName, providerName, StringComparison.OrdinalIgnoreCase))
                {
                    v = candidate;
                }
            }
            else if (scope.TryGetValue(name, out var candidate))
            {
                v = candidate;
            }

            if (v is not null)
            {
                var rendered = v.IsSensitive ? "***" : (v.Value ?? string.Empty);
                sb.Append(rendered);
                replacements.Add(new Replacement(
                    Token: m.Value,
                    Name: name,
                    StartIndex: m.Index,
                    Length: m.Length,
                    Resolved: true,
                    IsSensitive: v.IsSensitive,
                    Scope: v.Scope,
                    SourcePath: v.SourcePath,
                    ProviderName: v.ProviderName));
            }
            else
            {
                sb.Append(m.Value);
                replacements.Add(new Replacement(
                    Token: m.Value,
                    Name: name,
                    StartIndex: m.Index,
                    Length: m.Length,
                    Resolved: false,
                    IsSensitive: false,
                    Scope: null,
                    SourcePath: null,
                    ProviderName: providerName));
            }
            cursor = m.Index + m.Length;
        }
        sb.Append(template, cursor, template.Length - cursor);

        return new CompileResult(sb.ToString(), replacements);
    }
}

public sealed record CompileResult(string Value, IReadOnlyList<Replacement> Replacements);

public sealed record Replacement(
    string Token,
    string Name,
    int StartIndex,
    int Length,
    bool Resolved,
    bool IsSensitive,
    VariableScope? Scope,
    string? SourcePath,
    string? ProviderName);
