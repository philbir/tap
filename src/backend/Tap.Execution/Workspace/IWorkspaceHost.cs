using Tap.Execution.Auth;
using Tap.Workspace;
using Tap.Workspace.Model;
using Tap.Workspace.Variables;

namespace Tap.Execution.Workspace;

/// <summary>
/// Everything the execution engine needs from whatever is hosting it, and nothing more.
///
/// <para>This is the seam between the engine and its front ends. The Studio's implementation
/// watches the filesystem, tracks a list of known workspaces, and reads a token cache the user
/// filled by clicking through a browser; the CLI's loads a folder once and refuses to do
/// anything interactive. Neither of those concerns belongs in a run, so neither is visible
/// here — which is what lets the same <see cref="Testing.TestRunner"/> serve both.</para>
/// </summary>
public interface IWorkspaceHost
{
    /// <summary>The loaded workspace. Implementations that reload (the Studio watches for file
    /// changes) return the current snapshot; a run reads it once at the start.</summary>
    LoadedWorkspace Workspace { get; }

    /// <summary>Absolute path to the workspace root. Used to resolve <c>&lt; ./file</c> body
    /// references, which are relative to the request's own directory inside it.</summary>
    string RootDirectory { get; }

    /// <summary>
    /// Builds a variable provider registry for one render. Fresh per call: providers carry a
    /// per-render resolution cache and trace, so sharing one across renders would report the
    /// wrong variables against the wrong request.
    /// </summary>
    /// <param name="env">The environment in effect, whose provider binding (default provider,
    /// aliases, strict flag) applies. <c>null</c> means no env layer — callers that want the
    /// manifest's <c>defaultEnv</c> resolve it before calling.</param>
    VariableProviderRegistry CreateRegistry(EnvFile? env);

    /// <summary>Where a runtime-acquired bearer token comes from — the one thing a CI run and
    /// a developer's Studio genuinely disagree about.</summary>
    IAuthTokenSource Tokens { get; }
}

/// <summary>A token minted for, or cached against, an auth profile.</summary>
/// <param name="AccessToken">The bearer value to stamp on the request.</param>
/// <param name="ExpiresAt">When it lapses, or null when the source doesn't say.</param>
public readonly record struct AuthToken(string AccessToken, DateTimeOffset? ExpiresAt);

/// <summary>
/// Supplies bearer tokens for auth profiles whose credentials come from a runtime exchange
/// (oauth2, azure-cli, jwt, github beyond PAT). Profiles whose headers the renderer can build
/// inline — bearer, basic, apiKey, custom, aws-sigv4 — never reach here.
///
/// <para>Implementations decide their own policy for what they will do to get one. The Studio
/// reads a cache the user filled interactively; a CI runner mints what it can non-interactively
/// and refuses the rest loudly, because the alternative is a pipeline blocked on a browser
/// prompt nobody can see.</para>
/// </summary>
public interface IAuthTokenSource
{
    /// <summary>
    /// The token for <paramref name="profile"/> under <paramref name="scope"/>, or null when
    /// there isn't one. Returning null is not an error — the request goes out without an
    /// Authorization header and, in all likelihood, fails with a 401 that says so far better
    /// than a synthesized message would.
    /// </summary>
    ValueTask<AuthToken?> GetAsync(AuthFile profile, AuthProfileScope scope, CancellationToken ct);
}

/// <summary>An <see cref="IAuthTokenSource"/> that never has a token. For hosts with no auth
/// story at all — tests, and any caller whose workspace only uses static profiles.</summary>
public sealed class NoAuthTokenSource : IAuthTokenSource
{
    public static readonly NoAuthTokenSource Instance = new();

    public ValueTask<AuthToken?> GetAsync(AuthFile profile, AuthProfileScope scope, CancellationToken ct)
        => ValueTask.FromResult<AuthToken?>(null);
}
