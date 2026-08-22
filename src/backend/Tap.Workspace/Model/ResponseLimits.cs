namespace Tap.Workspace.Model;

/// <summary>
/// How much of a response body Tap keeps, declared once per workspace in
/// <c>workspace.tap</c> under <c>response:</c>.
///
/// <para>Two caps, because a response has two audiences. <see cref="MaxBytes"/> is what
/// travels inline to whoever asked for the request — the Studio's body pane, the CLI's
/// <c>--json</c> document, the snapshot assertions read — and it is small on purpose: a
/// 200 MB body pasted into a code viewer is a frozen tab, not a diagnosis.
/// <see cref="MaxRetainedBytes"/> is how much the host is willing to hold on to beyond
/// that so the user can still ask for the rest: "show all" in the truncated banner, and
/// the full download.</para>
///
/// <para>Both are optional. A workspace that says nothing gets
/// <see cref="DefaultMaxBytes"/> and <see cref="DefaultMaxRetainedBytes"/>, which is the
/// behaviour every Tap workspace had before the fields existed — except that the retained
/// copy now makes a complete download possible.</para>
/// </summary>
public sealed record ResponseLimits
{
    /// <summary>2 MiB — what rides inline when the workspace doesn't say otherwise.</summary>
    public const long DefaultMaxBytes = 2 * 1024 * 1024;

    /// <summary>64 MiB — how much is held back for "show all" and the full download.</summary>
    public const long DefaultMaxRetainedBytes = 64 * 1024 * 1024;

    /// <summary>Hard ceiling on either cap. Past this the workspace is asking the host to
    /// buffer a quantity of somebody else's data that no interactive tool has a use for.</summary>
    public const long AbsoluteMaxBytes = 1024L * 1024 * 1024;

    /// <summary>Bytes of the body delivered inline. Null keeps <see cref="DefaultMaxBytes"/>.</summary>
    public long? MaxBytes { get; init; }

    /// <summary>Bytes the host retains for a later "show all" / download. Null keeps
    /// <see cref="DefaultMaxRetainedBytes"/>; a value below <see cref="EffectiveMaxBytes"/>
    /// is raised to it, since retaining less than we already sent would be a lie.</summary>
    public long? MaxRetainedBytes { get; init; }

    /// <summary>True when neither cap is set — the emitter uses this to leave the
    /// <c>response:</c> block out of the file entirely.</summary>
    public bool IsEmpty => MaxBytes is null && MaxRetainedBytes is null;

    public long EffectiveMaxBytes => Clamp(MaxBytes ?? DefaultMaxBytes);

    public long EffectiveMaxRetainedBytes =>
        Math.Max(EffectiveMaxBytes, Clamp(MaxRetainedBytes ?? DefaultMaxRetainedBytes));

    private static long Clamp(long value) => Math.Clamp(value, 0, AbsoluteMaxBytes);
}
