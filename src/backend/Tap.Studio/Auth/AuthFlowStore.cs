using System.Collections.Concurrent;

namespace Tap.Studio.Auth;

/// <summary>
/// In-memory store of in-progress OAuth2 authorization-code flows. A flow is short-lived —
/// the user opens the consent popup, signs in, gets redirected to <c>/api/auth/callback</c>,
/// the backend exchanges the code for a token, the UI's poll picks up the completion, and
/// then the flow is done. Persistence would be over-engineering at this stage.
///
/// Expired flows (older than <see cref="ExpiresAfter"/>) are pruned lazily on add.
/// </summary>
public sealed class AuthFlowStore
{
    private static readonly TimeSpan ExpiresAfter = TimeSpan.FromMinutes(10);
    private readonly ConcurrentDictionary<string, AuthFlow> _flows = new();

    public AuthFlow Create(AuthFlow flow)
    {
        PruneExpired();
        _flows[flow.Id] = flow;
        return flow;
    }

    public AuthFlow? Get(string id) => _flows.TryGetValue(id, out var f) ? f : null;

    public void Update(string id, Action<AuthFlow> mutate)
    {
        if (!_flows.TryGetValue(id, out var f)) return;
        mutate(f);
    }

    public void Remove(string id) => _flows.TryRemove(id, out _);

    private void PruneExpired()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var (id, flow) in _flows)
        {
            if (flow.CreatedAt + ExpiresAfter < now) _flows.TryRemove(id, out _);
        }
    }
}

/// <summary>
/// State for one in-progress flow. Stored in-memory; not persisted. Fields are settable so
/// the callback can write back the result without rebuilding the whole record.
/// </summary>
public sealed class AuthFlow
{
    public required string Id { get; init; }
    /// <summary>Workspace-relative path of the auth profile this flow is for.</summary>
    public required string AuthPath { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }

    /// <summary>PKCE code verifier — only kept until token exchange completes.</summary>
    public required string CodeVerifier { get; init; }
    /// <summary>Nonce sent on the authorize request; must match on the id_token.</summary>
    public required string Nonce { get; init; }
    public required string Authority { get; init; }
    public required string TokenEndpoint { get; init; }
    public required string ClientId { get; init; }
    public string? ClientSecret { get; init; }
    public required string RedirectUri { get; init; }
    public required IReadOnlyList<string> Scopes { get; init; }

    public AuthFlowStatus Status { get; set; } = AuthFlowStatus.Pending;
    public string? AccessToken { get; set; }
    public string? IdToken { get; set; }
    public string? RefreshToken { get; set; }
    public string? TokenType { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }
    public string? Error { get; set; }

    // Device-code only. Populated by the runner when it kicks off RFC 8628; the UI shows
    // these to the user while polling for completion. Null for every other grant.
    public string? UserCode { get; set; }
    public string? VerificationUri { get; set; }
    public string? VerificationUriComplete { get; set; }
}

public enum AuthFlowStatus
{
    /// <summary>Authorize URL handed to UI; awaiting callback.</summary>
    Pending,
    /// <summary>Callback received and token exchange succeeded.</summary>
    Completed,
    /// <summary>Identity provider returned an error, or token exchange failed.</summary>
    Failed,
}
