using System.Reflection;

namespace Tap.Execution;

/// <summary>
/// The product token this process puts on the wire, and the engine's own version.
///
/// <para>Anything Tap sends on a user's behalf identifies itself with one consistent
/// <c>User-Agent</c> rather than a per-call literal — an upstream's logs should be able to
/// tell a request from the Studio apart from one fired by a CI run. Each front end sets
/// <see cref="UserAgent"/> once at startup; the default names the engine, which is the honest
/// answer for a host that never bothered.</para>
/// </summary>
public static class ExecutionIdentity
{
    /// <summary>Informational version with SourceLink's build metadata stripped — e.g.
    /// <c>0.5.0-beta.2</c>, not <c>0.5.0-beta.2+a8abb9cd…</c>.</summary>
    public static string Version { get; } = Resolve(typeof(ExecutionIdentity).Assembly);

    /// <summary>Product token stamped on requests that don't set their own <c>User-Agent</c>.
    /// Assign once at startup, before anything is sent.</summary>
    public static string UserAgent { get; set; } = $"tap-execution/{Version}";

    /// <summary>The informational version of <paramref name="assembly"/>, for a host deriving
    /// its own token (<c>tap-studio/1.2.3</c>).</summary>
    public static string Resolve(Assembly assembly)
    {
        var info = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (string.IsNullOrWhiteSpace(info))
            return assembly.GetName().Version?.ToString(3) ?? "0.0.0";

        // "<version>+<commit sha>" — the sha is useful in logs, noise in a User-Agent.
        var plus = info.IndexOf('+');
        return plus >= 0 ? info[..plus] : info;
    }
}
