using Tap.Execution.Auth;
using Tap.Execution.Workspace;
using Tap.Workspace.Model;

namespace Tap.Studio.Auth;

/// <summary>
/// The Studio's answer to "where does a bearer token come from": the cache
/// <see cref="AuthTokenStore"/> fills when the user runs a profile's flow from the UI.
///
/// <para>Deliberately passive — it never mints anything. The Studio's flows are interactive
/// (a browser popup, a device code, an <c>az login</c>) and belong to a click, not to a
/// request being sent. A missing or lapsed token means the request goes out unauthenticated
/// and the upstream's 401 tells the user exactly what happened, which is more honest than a
/// popup appearing from under a Send button.</para>
///
/// <para>Freshness uses the same 30-second slack <see cref="AuthRunner"/> applies, so the Flow
/// tab's verdict and the executor's behaviour agree.</para>
/// </summary>
public sealed class CachedAuthTokenSource(AuthTokenStore store, Func<string> rootDirectory) : IAuthTokenSource
{
    private static readonly TimeSpan Slack = TimeSpan.FromSeconds(30);

    public ValueTask<AuthToken?> GetAsync(AuthFile profile, AuthProfileScope scope, CancellationToken ct)
    {
        var cached = store.Get(rootDirectory(), scope);
        if (cached is null || string.IsNullOrEmpty(cached.AccessToken))
            return ValueTask.FromResult<AuthToken?>(null);

        if (cached.ExpiresAt is not null && cached.ExpiresAt <= DateTimeOffset.UtcNow + Slack)
            return ValueTask.FromResult<AuthToken?>(null);

        return ValueTask.FromResult<AuthToken?>(new AuthToken(cached.AccessToken, cached.ExpiresAt));
    }
}
