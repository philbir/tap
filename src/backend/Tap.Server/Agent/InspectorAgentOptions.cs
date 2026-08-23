using Tap.Core.Redaction;

namespace Tap.Server.Agent;

/// <summary>
/// Whether, and how, a coding agent may read this inspector's captured traffic. Bound from
/// <c>Inspector:Agent:*</c>.
///
/// <para><b>Off by default.</b> Turning it on is a decision someone makes per inspector —
/// through <c>.WithAgentAccess()</c> in an AppHost, or the environment variable directly.</para>
///
/// <para>Two keys are deliberately absent. There is no <c>Reveal</c>: the agent surface has no
/// code path that emits a cleartext credential, which makes it auditable by absence rather
/// than by review. And there is no <c>RedactAtCapture</c>: redaction is read-time, so the ring
/// keeps the real values and a human can still read them off the inspector UI — which is the
/// only reason refusing to reveal anything is a tolerable rule rather than a crippling one.</para>
/// </summary>
public sealed class InspectorAgentOptions
{
    public bool Enabled { get; init; }

    /// <summary>
    /// <c>all</c> (default) or <c>since-attach</c>. <c>all</c> means an enabled agent sees this
    /// inspector's ring, on the theory that the request you need to debug is usually the one
    /// that already happened.
    /// </summary>
    public string Scope { get; init; } = "all";

    /// <summary>Hosts an agent may read, or empty for every host this inspector captures.</summary>
    public string[] AllowHosts { get; init; } = [];

    public string[] ExtraSensitiveHeaders { get; init; } = [];

    public string[] ExtraSecretKeys { get; init; } = [];

    /// <summary>
    /// Lets an agent re-send a captured request. A write, so it is off even when reads are on:
    /// enabling the agent surface is a decision about what may be <em>seen</em>, and this is a
    /// decision about what may be <em>done</em>.
    /// </summary>
    public bool AllowReplay { get; init; }

    public static InspectorAgentOptions Disabled { get; } = new();

    public static InspectorAgentOptions FromConfiguration(IConfiguration config)
    {
        var section = config.GetSection("Inspector:Agent");
        return new InspectorAgentOptions
        {
            Enabled = section.GetValue("Enabled", false),
            Scope = section["Scope"] ?? "all",
            AllowHosts = Split(section["AllowHosts"]),
            ExtraSensitiveHeaders = Split(section["ExtraSensitiveHeaders"]),
            ExtraSecretKeys = Split(section["ExtraSecretKeys"]),
            AllowReplay = section.GetValue("AllowReplay", false),
        };
    }

    public CaptureRedactionOptions ToRedactionOptions() => new()
    {
        ExtraSensitiveHeaders = ExtraSensitiveHeaders,
        ExtraSecretKeys = ExtraSecretKeys,
    };

    /// <summary>True when an agent may see traffic for this host at all. Applied before any
    /// projection, so a host outside the allowlist is not merely filtered out of a listing —
    /// it is never redacted, counted, or acknowledged.</summary>
    public bool AllowsHost(string host)
        => AllowHosts.Length == 0
            || AllowHosts.Contains(host, StringComparer.OrdinalIgnoreCase);

    private static string[] Split(string? value)
        => (value ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
