namespace Tap.Studio.Specs;

/// <summary>
/// Assigns the stable <c>id:</c> every workspace file carries.
///
/// <para>The format has always specified one — "auto-generated as a UUIDv7 on first save if
/// omitted" (§3.1) — but until request history nothing minted it outside the OpenAPI importer,
/// which left <c>id:</c> refs theoretical and gave history nothing durable to key on. A path is
/// not an identity: rename the file and every reference to what it was is gone.</para>
///
/// <para>UUIDv7 rather than v4 because the timestamp prefix makes ids sort by creation, which
/// costs nothing and occasionally answers "which of these came first" without opening either
/// file.</para>
///
/// <para><see cref="Ensure"/> is idempotent, so the endpoint that needs to echo the id back to
/// the client and the emitter that writes it into the file can both call it and agree.</para>
/// </summary>
public static class SpecIds
{
    public static string New() => Guid.CreateVersion7().ToString("d");

    /// <summary>The spec's id, or a fresh one when it has none. Whitespace counts as none —
    /// an <c>id: ""</c> left behind by a hand-edit is not an identity.</summary>
    public static string Ensure(string? id)
        => string.IsNullOrWhiteSpace(id) ? New() : id.Trim();
}

/// <summary>Maps the parsed <c>history:</c> block to and from its wire form. Null in, null out:
/// a scope that declares nothing must round-trip as declaring nothing, or saving a request would
/// silently pin whatever it happened to inherit at the time.</summary>
public static class HistoryOptionsMapper
{
    public static Tap.Studio.Contracts.HistoryOptionsDto? ToDto(Tap.Workspace.Model.HistoryOptions? options)
        => options is null || options.IsEmpty
            ? null
            : new(options.Enabled, options.MaxEntries, options.Encrypt, options.MaxBodyBytes, options.OrphanRetentionDays);

    /// <summary>The merged view — what recording will actually do — with every field filled in.
    /// Used for the "inherited" hints in the editors, never written back to a file.</summary>
    public static Tap.Studio.Contracts.HistoryOptionsDto Effective(Tap.Workspace.Model.HistoryOptions options)
        => new(options.EffectiveEnabled, options.EffectiveMaxEntries, options.EffectiveEncrypt,
            options.EffectiveMaxBodyBytes, options.EffectiveOrphanRetentionDays);

    public static Tap.Workspace.Model.HistoryOptions FromDto(Tap.Studio.Contracts.HistoryOptionsDto? dto)
        => dto is null
            ? new()
            : new()
            {
                Enabled = dto.Enabled,
                MaxEntries = dto.MaxEntries,
                Encrypt = dto.Encrypt,
                MaxBodyBytes = dto.MaxBodyBytes,
                OrphanRetentionDays = dto.OrphanRetentionDays,
            };
}
