using System.Collections.Concurrent;

namespace Tap.Studio.Auth;

/// <summary>
/// In-memory store of in-progress OAuth2 authorization-code flows. A flow is short-lived —
/// the user opens the consent popup, signs in, gets redirected to <c>/api/auth/callback</c>,
/// the backend exchanges the code for a token, the UI's poll picks up the completion, and
/// then the flow is done. Persistence would be over-engineering at this stage.
///
/// Expired flows (older than <see cref="ExpiresAfter"/>) are pruned lazily on add and are
/// invisible to <see cref="Get"/> from the moment they lapse, and the store is capped so a
/// client that abandons flows cannot grow it without bound.
/// </summary>
public sealed class AuthFlowStore
{
    private static readonly TimeSpan ExpiresAfter = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Ceiling on live flows. Every "execute" mints one and only the callback resolves it,
    /// so a client that starts flows and walks away would otherwise grow this dictionary
    /// without bound — each entry pinning a client secret and a PKCE verifier in memory.
    /// </summary>
    private const int MaxFlows = 64;

    private readonly ConcurrentDictionary<string, AuthFlow> _flows = new();
    private readonly Lock _claimLock = new();

    public AuthFlow Create(AuthFlow flow)
    {
        PruneExpired();
        EvictOldestOverCap();
        _flows[flow.Id] = flow;
        return flow;
    }

    /// <summary>
    /// Look up a flow, treating anything past <see cref="ExpiresAfter"/> as absent. Checking
    /// on read matters as much as pruning on write: an authorization-code <c>state</c> must
    /// stop being redeemable on time even if no later flow was ever created to trigger a prune.
    /// </summary>
    public AuthFlow? Get(string id)
    {
        if (!_flows.TryGetValue(id, out var f)) return null;
        if (f.CreatedAt + ExpiresAfter < DateTimeOffset.UtcNow)
        {
            _flows.TryRemove(id, out _);
            return null;
        }
        return f;
    }

    /// <summary>
    /// Take exclusive ownership of a still-pending flow, moving it to
    /// <see cref="AuthFlowStatus.Exchanging"/>. This is what makes an authorization-code
    /// <c>state</c> single-use: the first callback wins and every replay — including one
    /// racing on another thread — sees a non-pending flow and gets null.
    /// </summary>
    public AuthFlow? TryClaim(string id)
    {
        var flow = Get(id);
        if (flow is null) return null;
        lock (_claimLock)
        {
            if (flow.Status != AuthFlowStatus.Pending) return null;
            flow.Status = AuthFlowStatus.Exchanging;
            return flow;
        }
    }

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

    private void EvictOldestOverCap()
    {
        while (_flows.Count >= MaxFlows)
        {
            // Oldest-first: a flow that has been sitting unfinished the longest is the one
            // least likely to still have a browser waiting on it.
            var oldest = _flows.Values.MinBy(f => f.CreatedAt);
            if (oldest is null || !_flows.TryRemove(oldest.Id, out _)) return;
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
    /// <summary>Collection stage in effect when the flow started, or <c>null</c> for a
    /// workspace-scoped profile. Carried so the callback caches the resulting token under the
    /// same key <see cref="AuthRunner.ExecuteAsync"/> would have read it from.</summary>
    public string? Stage { get; init; }
    /// <summary>Env in effect when the flow started, or <c>null</c> when the workspace has
    /// none. Carried for the same reason as <see cref="Stage"/>: the callback runs minutes
    /// later on a different request, and the token it mints belongs to the env that produced
    /// the authorize URL — not to whatever env happens to be selected by then.</summary>
    public string? Env { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }

    /// <summary>PKCE code verifier — only kept until token exchange completes. Settable so
    /// the runner can wipe it afterwards rather than leave it live for the flow's whole TTL.</summary>
    public required string CodeVerifier { get; set; }
    /// <summary>Nonce sent on the authorize request; must match on the id_token.</summary>
    public required string Nonce { get; init; }
    public required string Authority { get; init; }
    public required string TokenEndpoint { get; init; }
    public required string ClientId { get; init; }
    /// <summary>Wiped alongside <see cref="CodeVerifier"/> once the exchange is done.</summary>
    public string? ClientSecret { get; set; }
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
    /// <summary>Callback claimed the flow and the token exchange is in flight. Reported to
    /// the UI as "pending" — it exists so a second callback carrying the same <c>state</c>
    /// can be told apart from the first one.</summary>
    Exchanging,
    /// <summary>Callback received and token exchange succeeded.</summary>
    Completed,
    /// <summary>Identity provider returned an error, or token exchange failed.</summary>
    Failed,
}
