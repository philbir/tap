namespace Tap.Workspace.Model;

/// <summary>
/// Whether, and how, Tap keeps a record of the exchanges it runs — the <c>history:</c>
/// frontmatter block, declarable on the workspace manifest, a collection, and a single
/// request.
///
/// <para>Every field is nullable and means "inherit". The three tiers are merged per key
/// (workspace &lt; collection &lt; request) by <see cref="Resolve"/>, which is the same
/// shape <see cref="RequestTransportSettings"/> already uses — a collection can turn history
/// on for everything under it while one noisy request opts out, or keeps fewer entries,
/// without restating the rest.</para>
///
/// <para>Off unless something says otherwise. Recording every response a workspace has ever
/// produced is a decision about someone's disk, and it is not one to make on their behalf.</para>
/// </summary>
public sealed record HistoryOptions
{
    /// <summary>Entries kept per request before the oldest are pruned.</summary>
    public const int DefaultMaxEntries = 25;

    /// <summary>256 KiB of response body per entry. Small on purpose: history is a folder that
    /// grows without anyone watching it, and a body big enough to matter is better re-fetched
    /// than hoarded.</summary>
    public const long DefaultMaxBodyBytes = 256 * 1024;

    /// <summary>Days an orphaned request's history survives — see the workspace-scope
    /// <see cref="OrphanRetentionDays"/>.</summary>
    public const int DefaultOrphanRetentionDays = 30;

    /// <summary>Hard ceiling on <see cref="MaxEntries"/>. Past this the folder stops being
    /// something a person browses.</summary>
    public const int AbsoluteMaxEntries = 1000;

    /// <summary>Hard ceiling on <see cref="MaxBodyBytes"/> — 64 MiB, matching what the response
    /// store is willing to retain for a single body.</summary>
    public const long AbsoluteMaxBodyBytes = 64L * 1024 * 1024;

    /// <summary>Record exchanges for this scope. Null inherits; false at any tier turns it off
    /// for everything below that doesn't turn it back on.</summary>
    public bool? Enabled { get; init; }

    /// <summary>How many entries to keep for one request. Null inherits, then
    /// <see cref="DefaultMaxEntries"/>.</summary>
    public int? MaxEntries { get; init; }

    /// <summary>
    /// Encrypt the entry at rest with the machine key, and — because it is then unreadable to
    /// anyone without that key — store it <b>unredacted</b>. This is the only way history shows
    /// the token that actually went on the wire.
    ///
    /// <para>Null inherits, then false, which redacts instead. There is no combination that
    /// writes secret material in the clear: encryption off means redaction on.</para>
    /// </summary>
    public bool? Encrypt { get; init; }

    /// <summary>Bytes of response body stored per entry. Null inherits, then
    /// <see cref="DefaultMaxBodyBytes"/>.</summary>
    public long? MaxBodyBytes { get; init; }

    /// <summary>
    /// How long the history of a request that no longer exists is kept, in days. Workspace
    /// scope only — an orphan by definition has no collection or request left to ask.
    /// Zero sweeps orphans on sight; null inherits <see cref="DefaultOrphanRetentionDays"/>.
    /// </summary>
    public int? OrphanRetentionDays { get; init; }

    /// <summary>True when nothing was declared — the emitter uses this to leave the
    /// <c>history:</c> block out of the file entirely.</summary>
    public bool IsEmpty =>
        Enabled is null && MaxEntries is null && Encrypt is null
        && MaxBodyBytes is null && OrphanRetentionDays is null;

    /// <summary>True when the only thing declared is <see cref="Enabled"/> — the emitter writes
    /// the <c>history: true</c> / <c>history: false</c> shorthand for these.</summary>
    public bool IsShorthand =>
        Enabled is not null && MaxEntries is null && Encrypt is null
        && MaxBodyBytes is null && OrphanRetentionDays is null;

    /// <summary>
    /// Merges the tiers a request sits in, nearest wins, per key. Nulls fall through, so a
    /// collection's <c>maxEntries</c> survives a request that only sets <c>encrypt</c>.
    /// </summary>
    public static HistoryOptions Resolve(HistoryOptions? workspace, HistoryOptions? collection, HistoryOptions? request)
        => new()
        {
            Enabled = request?.Enabled ?? collection?.Enabled ?? workspace?.Enabled,
            MaxEntries = request?.MaxEntries ?? collection?.MaxEntries ?? workspace?.MaxEntries,
            Encrypt = request?.Encrypt ?? collection?.Encrypt ?? workspace?.Encrypt,
            MaxBodyBytes = request?.MaxBodyBytes ?? collection?.MaxBodyBytes ?? workspace?.MaxBodyBytes,
            // Orphans have no owning request or collection by the time they matter, so this one
            // is read from the manifest only.
            OrphanRetentionDays = workspace?.OrphanRetentionDays,
        };

    public bool EffectiveEnabled => Enabled ?? false;

    public int EffectiveMaxEntries =>
        Math.Clamp(MaxEntries ?? DefaultMaxEntries, 1, AbsoluteMaxEntries);

    public bool EffectiveEncrypt => Encrypt ?? false;

    public long EffectiveMaxBodyBytes =>
        Math.Clamp(MaxBodyBytes ?? DefaultMaxBodyBytes, 0, AbsoluteMaxBodyBytes);

    public int EffectiveOrphanRetentionDays =>
        Math.Max(0, OrphanRetentionDays ?? DefaultOrphanRetentionDays);
}
