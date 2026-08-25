using System.Text.RegularExpressions;
using Tap.Workspace.Model;

namespace Tap.Workspace.Variables;

/// <summary>
/// An optional regular expression bounding the names a provider exposes. A provider backed by
/// a store someone else fills — a Key Vault holding every secret a team owns — is otherwise
/// all-or-nothing: listing it floods the picker, and every bare <c>{{token}}</c> gets probed
/// against the whole vault. A filter narrows the provider to the slice a workspace actually
/// uses.
///
/// <para>The pattern is a .NET regex matched against the name as tokens spell it (after any
/// <c>prefix</c> has been stripped), and it is <b>unanchored</b> — <c>billing</c> matches
/// anywhere in the name, <c>^billing-</c> only at the start. That is what a regex normally
/// means, and anchoring silently would make half the patterns people write behave oddly.</para>
///
/// <para>A filter is a scope, not a display convenience: it holds for lookups and writes the
/// same way it holds for listings. Otherwise a token could reach past the filter and a
/// workspace could write a secret it can never read back.</para>
/// </summary>
public sealed class VariableNameFilter
{
    /// <summary>A user-supplied pattern can backtrack catastrophically. Matching one name is
    /// trivial work, so anything past this is a pathological pattern, not a slow one.</summary>
    private static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(250);

    /// <summary>The no-op filter: every name is in scope.</summary>
    public static VariableNameFilter None { get; } = new(null, null);

    private readonly Regex? _regex;

    private VariableNameFilter(Regex? regex, string? pattern)
    {
        _regex = regex;
        Pattern = pattern;
    }

    /// <summary>The configured pattern, or null when this filter admits everything.</summary>
    public string? Pattern { get; }

    /// <summary>True when nothing is filtered out.</summary>
    public bool IsEmpty => _regex is null;

    /// <summary>Compiles <paramref name="pattern"/>, treating blank as "no filter".
    /// <paramref name="providerName"/> only names the offender in the error message.</summary>
    /// <exception cref="WorkspaceParseException">The pattern is not a valid regex.</exception>
    public static VariableNameFilter Create(string? pattern, string providerName)
    {
        if (string.IsNullOrWhiteSpace(pattern)) return None;

        var trimmed = pattern.Trim();
        try
        {
            var regex = new Regex(trimmed, RegexOptions.CultureInvariant | RegexOptions.Compiled, MatchTimeout);
            return new VariableNameFilter(regex, trimmed);
        }
        catch (ArgumentException ex)
        {
            throw new WorkspaceParseException(new WorkspaceError(
                WorkspaceErrorCode.E_PROVIDER_CONFIG_INVALID,
                $"Variable provider '{providerName}' has an invalid 'filter' regex '{trimmed}': {ex.Message}"));
        }
    }

    /// <summary>Whether <paramref name="name"/> is inside the filter's scope.</summary>
    public bool IsMatch(string name)
    {
        if (_regex is null) return true;
        try
        {
            return _regex.IsMatch(name);
        }
        catch (RegexMatchTimeoutException)
        {
            // A filter decides what is reachable, so an inconclusive match has to fall
            // outside it — the alternative is a pathological pattern quietly widening scope.
            return false;
        }
    }
}
