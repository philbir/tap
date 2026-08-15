namespace Tap.Studio;

/// <summary>
/// The running studio's own version, and the <c>tap-studio/{version}</c> User-Agent derived
/// from it. Anything the studio puts on the wire on the user's behalf identifies itself with
/// <see cref="UserAgent"/> so upstreams (and the user's own logs) see one consistent product
/// token instead of a per-call literal.
/// </summary>
internal static class StudioVersion
{
    /// <summary>Informational version with SourceLink's build metadata stripped — e.g.
    /// <c>0.5.0-beta.2</c>, not <c>0.5.0-beta.2+a8abb9cd…</c>.</summary>
    public static string Value { get; } = Resolve();

    /// <summary>Default User-Agent product token: <c>tap-studio/{version}</c>.</summary>
    public static string UserAgent { get; } = $"tap-studio/{Value}";

    private static string Resolve()
        => Tap.Execution.ExecutionIdentity.Resolve(typeof(StudioVersion).Assembly);
}
