using System.Globalization;

namespace Aspire.Hosting;

/// <summary>
/// Opens a tap's captured traffic to a coding agent — the AppHost-facing switch for
/// <c>Inspector:Agent:*</c>.
/// </summary>
public static class TapAgentExtensions
{
    /// <summary>
    /// Lets an agent read this inspector's captured requests through <c>tap mcp</c> or the
    /// inspector's <c>/api/agent/*</c> surface. <b>Off unless you call this.</b>
    ///
    /// <para>What an agent gets is a redacted view: credentials are replaced with
    /// <c>[redacted:jwt #a91f3c2d …]</c>, and there is no way to ask for the real value back —
    /// not through a flag, not through an endpoint. Matching fingerprints let it reason about
    /// whether two requests carried the same token without seeing either. The inspector UI is
    /// unchanged and still shows you everything: redaction happens when an agent reads, not
    /// when traffic is captured.</para>
    ///
    /// <para>Captured bodies come from whoever is calling your tunnel, so treat what an agent
    /// reports from them as untrusted input — the tool results say as much, in the payload.</para>
    /// </summary>
    /// <param name="hosts">Restrict agent reads to these hostnames. Empty means every host this
    /// tap captures.</param>
    /// <param name="sinceAttach">Only show traffic captured after the agent connects, rather
    /// than the whole ring. Off by default: the request you need to debug is usually the one
    /// that already happened.</param>
    /// <param name="allowReplay">Also let an agent re-send a captured request. Off by default:
    /// reading traffic and acting on it are separate decisions. A replay carries the captured
    /// credential to the host it came from and nowhere else — the destination is not editable —
    /// so an agent can reproduce an authenticated call without ever holding the credential.</param>
    public static TapHandle WithAgentAccess(
        this TapHandle tap,
        string[]? hosts = null,
        bool sinceAttach = false,
        bool allowReplay = false)
    {
        tap.WithEnvironment("Inspector__Agent__Enabled", "true");
        tap.WithEnvironment("Inspector__Agent__Scope", sinceAttach ? "since-attach" : "all");
        if (allowReplay) tap.WithEnvironment("Inspector__Agent__AllowReplay", "true");

        if (hosts is { Length: > 0 })
        {
            tap.WithEnvironment("Inspector__Agent__AllowHosts", string.Join(',', hosts));
        }

        return tap;
    }

    /// <summary>
    /// Adds header names and JSON/form/query keys the redactor should treat as secret on top of
    /// its built-in lists — for a house-style header like <c>X-Acme-Session</c> that no generic
    /// rule would recognise.
    ///
    /// <para>These only ever hide more. There is no corresponding way to hide less.</para>
    /// </summary>
    public static TapHandle WithAgentRedaction(
        this TapHandle tap,
        string[]? headers = null,
        string[]? keys = null)
    {
        if (headers is { Length: > 0 })
        {
            tap.WithEnvironment("Inspector__Agent__ExtraSensitiveHeaders", string.Join(',', headers));
        }

        if (keys is { Length: > 0 })
        {
            tap.WithEnvironment("Inspector__Agent__ExtraSecretKeys", string.Join(',', keys));
        }

        return tap;
    }

    /// <summary>The inspector UI port an agent bridge should be pointed at —
    /// <c>tap mcp --url &lt;this&gt;</c>.</summary>
    public static string AgentBridgeUrl(this TapHandle tap)
        => string.Create(
            CultureInfo.InvariantCulture,
            $"http://localhost:{tap.Annotation.UiPort}");
}
