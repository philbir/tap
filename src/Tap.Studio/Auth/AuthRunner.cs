using System.Diagnostics;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Tap.Workspace.Model;

namespace Tap.Studio.Auth;

/// <summary>
/// Executes an auth profile end-to-end. Field values (urls, client ids, scopes…) go through
/// <see cref="AuthFieldResolver"/> first so workspace <c>{{var}}</c> + <c>${{secret}}</c>
/// refs resolve before any HTTP call.
///
/// Grant matrix:
/// <list type="bullet">
///   <item><b>client_credentials</b> — m2m, sync. Posts to the token endpoint.</item>
///   <item><b>password</b> (ROPC) — username/password, sync. Useful for smoke tests.</item>
///   <item><b>authorization_code</b> / <b>authorization_code_pkce</b> — interactive popup
///     hand-off via <see cref="AuthFlowStore"/>; the callback finishes the exchange.</item>
///   <item><b>device_code</b> — polls the token endpoint after the user authenticates on
///     a second device. <see cref="ExecuteAuthResult.UserCode"/> + <see cref="ExecuteAuthResult.VerificationUri"/>
///     are surfaced for the UI to display.</item>
///   <item><b>refresh_token</b> — opportunistic; <see cref="RefreshIfPossibleAsync"/> swaps a
///     near-expiry token for a fresh one without re-prompting.</item>
///   <item><b>azure-cli</b> — shells out to <c>az account get-access-token</c>; no HTTP
///     credentials kept in the profile.</item>
///   <item><b>azure-cli-obo</b> — On-Behalf-Of: an upstream API exchanges the user's
///     az-cli token (or any inbound JWT) for a downstream token via the
///     <c>urn:ietf:params:oauth:grant-type:jwt-bearer</c> grant.</item>
/// </list>
/// </summary>
public sealed class AuthRunner
{
    private readonly HttpClient _http;
    private readonly OidcDiscoveryClient _discovery;
    private readonly AuthFlowStore _flows;
    private readonly AuthTokenStore _tokens;
    private readonly WorkspaceService _ws;
    private readonly IHttpContextAccessor _httpCtx;
    private readonly ILogger<AuthRunner> _logger;

    public AuthRunner(
        HttpClient http,
        OidcDiscoveryClient discovery,
        AuthFlowStore flows,
        AuthTokenStore tokens,
        WorkspaceService ws,
        IHttpContextAccessor httpCtx,
        ILogger<AuthRunner> logger)
    {
        _http = http;
        _discovery = discovery;
        _flows = flows;
        _tokens = tokens;
        _ws = ws;
        _httpCtx = httpCtx;
        _logger = logger;
    }

    /// <summary>
    /// Start the auth flow for the given profile. Returns either a completed result (sync
    /// flows + cache hits) or a <see cref="ExecuteAuthResult.LoginUrl"/> /
    /// <see cref="ExecuteAuthResult.UserCode"/> for the UI to drive an interactive step.
    /// </summary>
    public async Task<ExecuteAuthResult> ExecuteAsync(string authPath, bool forceReauthenticate, CancellationToken ct)
    {
        var workspace = _ws.Current;
        if (workspace.FindByPath(authPath) is not AuthFile auth)
            return ExecuteAuthResult.Failed($"Auth profile '{authPath}' not in workspace.");

        if (!forceReauthenticate)
        {
            var cached = _tokens.Get(_ws.RootDirectory, authPath);
            if (cached is not null && (cached.ExpiresAt is null || cached.ExpiresAt > DateTimeOffset.UtcNow + TimeSpan.FromSeconds(30)))
            {
                return ExecuteAuthResult.FromTokens(cached, fromCache: true);
            }

            // Token cached but stale — try to refresh silently before re-prompting.
            if (cached is { RefreshToken: not null } && auth.Type == "oauth2")
            {
                var refreshed = await RefreshIfPossibleAsync(auth, authPath, cached, ct).ConfigureAwait(false);
                if (refreshed is not null) return ExecuteAuthResult.FromTokens(refreshed, fromCache: false);
            }
        }

        var resolver = CreateResolver();
        try
        {
            return auth.Type switch
            {
                "oauth2" => await ExecuteOAuth2Async(auth, authPath, resolver, ct).ConfigureAwait(false),
                "azure-cli" => await ExecuteAzureAsync(auth, authPath, resolver, ct).ConfigureAwait(false),
                "jwt" => await ExecuteJwtAsync(auth, authPath, resolver, ct).ConfigureAwait(false),
                "github" => await ExecuteGithubAsync(auth, authPath, resolver, ct).ConfigureAwait(false),
                "basic" or "bearer" or "apiKey" or "custom" or "none" =>
                    ExecuteAuthResult.SyntheticHeaders(await BuildHeadersForAsync(auth, resolver, ct).ConfigureAwait(false)),
                _ => ExecuteAuthResult.Failed($"Auth type '{auth.Type}' is not supported by the runner yet."),
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Auth profile '{Path}' execution failed", authPath);
            return ExecuteAuthResult.Failed(ex.Message);
        }
    }

    public async Task<ExecuteAuthResult> CompleteAuthCodeAsync(string flowId, string code, CancellationToken ct)
    {
        var flow = _flows.Get(flowId);
        if (flow is null) return ExecuteAuthResult.Failed("Flow not found or expired.");

        try
        {
            var token = await ExchangeCodeAsync(flow, code, ct).ConfigureAwait(false);
            flow.AccessToken = token.AccessToken;
            flow.IdToken = token.IdToken;
            flow.RefreshToken = token.RefreshToken;
            flow.TokenType = token.TokenType;
            flow.ExpiresAt = token.ExpiresIn is { } s ? DateTimeOffset.UtcNow.AddSeconds(s) : null;
            flow.Status = AuthFlowStatus.Completed;

            var entry = new AuthTokenEntry
            {
                AccessToken = token.AccessToken,
                IdToken = token.IdToken,
                RefreshToken = token.RefreshToken,
                TokenType = token.TokenType,
                ExpiresAt = flow.ExpiresAt,
                ObtainedAt = DateTimeOffset.UtcNow,
                TokenEndpoint = flow.TokenEndpoint,
                ClientId = flow.ClientId,
                Scopes = flow.Scopes,
            };
            _tokens.Save(_ws.RootDirectory, flow.AuthPath, entry);
            return ExecuteAuthResult.FromTokens(entry, fromCache: false);
        }
        catch (Exception ex)
        {
            flow.Status = AuthFlowStatus.Failed;
            flow.Error = ex.Message;
            return ExecuteAuthResult.Failed(ex.Message);
        }
    }

    private AuthFieldResolver CreateResolver()
    {
        var ws = _ws.Current;
        EnvFile? env = null;
        if (ws.Manifest?.DefaultEnv is { } defaultRef)
            env = ws.Resolve(defaultRef) as EnvFile;
        return new AuthFieldResolver(ws, _ws.CreateRegistry(), env);
    }

    // ---- OAuth2 ----------------------------------------------------------------------

    private async Task<ExecuteAuthResult> ExecuteOAuth2Async(AuthFile auth, string authPath, AuthFieldResolver resolver, CancellationToken ct)
    {
        var grantType = (auth.Fields.GetValueOrDefault("flow")
                         ?? auth.Fields.GetValueOrDefault("grantType")
                         ?? "authorization_code_pkce").Trim();
        var useDiscovery = string.Equals(auth.Fields.GetValueOrDefault("useDiscovery"), "true", StringComparison.OrdinalIgnoreCase);

        // Resolve every templated field up-front. tokenUrl/authorizeUrl/etc. may carry
        // {{DEMO_API_URL}} placeholders or ${{env:…}} secret refs.
        var authority = await resolver.AllAsync(auth.Fields.GetValueOrDefault("authority"), ct).ConfigureAwait(false);
        var tokenEndpoint = await resolver.AllAsync(auth.Fields.GetValueOrDefault("tokenUrl"), ct).ConfigureAwait(false) ?? string.Empty;
        var authorizeEndpoint = await resolver.AllAsync(auth.Fields.GetValueOrDefault("authorizeUrl"), ct).ConfigureAwait(false) ?? string.Empty;
        var deviceEndpoint = await resolver.AllAsync(auth.Fields.GetValueOrDefault("deviceAuthorizationUrl"), ct).ConfigureAwait(false) ?? string.Empty;

        if (useDiscovery && !string.IsNullOrWhiteSpace(authority))
        {
            try
            {
                var doc = await _discovery.FetchAsync(authority!, ct).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(tokenEndpoint)) tokenEndpoint = doc.TokenEndpoint;
                if (string.IsNullOrWhiteSpace(authorizeEndpoint)) authorizeEndpoint = doc.AuthorizationEndpoint;
                // device_authorization_endpoint isn't on our DTO; pull it lazily from the well-known
                // when the user picks device_code without an explicit URL.
                if (string.IsNullOrWhiteSpace(deviceEndpoint) && grantType == "device_code")
                    deviceEndpoint = await TryDiscoverDeviceEndpointAsync(authority!, ct).ConfigureAwait(false) ?? string.Empty;
            }
            catch (Exception ex)
            {
                return ExecuteAuthResult.Failed($"OIDC discovery failed: {ex.Message}");
            }
        }

        if (string.IsNullOrWhiteSpace(tokenEndpoint) && grantType != "device_code")
            return ExecuteAuthResult.Failed("OAuth2: tokenUrl is missing (set it explicitly or enable Use Discovery with an authority).");

        var clientId = await resolver.AllAsync(auth.Fields.GetValueOrDefault("clientId"), ct).ConfigureAwait(false);
        var clientSecret = await resolver.AllAsync(auth.Fields.GetValueOrDefault("clientSecret"), ct).ConfigureAwait(false);
        var audience = await resolver.AllAsync(auth.Fields.GetValueOrDefault("audience"), ct).ConfigureAwait(false);
        var username = await resolver.AllAsync(auth.Fields.GetValueOrDefault("username"), ct).ConfigureAwait(false);
        var password = await resolver.AllAsync(auth.Fields.GetValueOrDefault("password"), ct).ConfigureAwait(false);
        // Redirect URI is owned by the runtime, not the auth profile — Aspire allocates a
        // fresh port every boot, so any value pinned in the workspace file is stale by the
        // next restart. We always derive it from the inbound HttpContext; anything written
        // to the file is ignored. The UI shows the live value read-only so the user knows
        // what to register with their identity provider.
        var redirectUri = ResolveCallbackUri();
        var scopes = await ResolveScopesAsync(auth, resolver, ct).ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(clientId))
            return ExecuteAuthResult.Failed("OAuth2: clientId is required.");

        return grantType switch
        {
            "client_credentials" =>
                await ClientCredentialsAsync(authPath, tokenEndpoint, clientId!, clientSecret, scopes, audience, ct).ConfigureAwait(false),
            "password" or "resource_owner" or "ropc" =>
                await PasswordAsync(authPath, tokenEndpoint, clientId!, clientSecret, username, password, scopes, audience, ct).ConfigureAwait(false),
            "device_code" or "device" =>
                await DeviceCodeAsync(authPath, deviceEndpoint, tokenEndpoint, clientId!, clientSecret, scopes, audience, ct).ConfigureAwait(false),
            "authorization_code" or "authorization_code_pkce" =>
                StartAuthCodeFlow(authPath, authorizeEndpoint, tokenEndpoint, authority, clientId!, clientSecret, redirectUri!, scopes, audience),
            _ => ExecuteAuthResult.Failed($"OAuth2 grant '{grantType}' is not supported yet."),
        };
    }

    private async Task<ExecuteAuthResult> ClientCredentialsAsync(
        string authPath, string tokenEndpoint, string clientId, string? clientSecret,
        IReadOnlyList<string> scopes, string? audience, CancellationToken ct)
    {
        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "client_credentials",
            ["client_id"] = clientId,
        };
        if (!string.IsNullOrEmpty(clientSecret)) form["client_secret"] = clientSecret;
        if (scopes.Count > 0) form["scope"] = string.Join(' ', scopes);
        if (!string.IsNullOrEmpty(audience)) form["audience"] = audience!;

        var token = await PostTokenAsync(tokenEndpoint, form, ct).ConfigureAwait(false);
        return SaveAndReturn(authPath, tokenEndpoint, clientId, scopes, token);
    }

    private async Task<ExecuteAuthResult> PasswordAsync(
        string authPath, string tokenEndpoint, string clientId, string? clientSecret,
        string? username, string? password, IReadOnlyList<string> scopes, string? audience, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(username) || string.IsNullOrEmpty(password))
            return ExecuteAuthResult.Failed("OAuth2 password grant: username + password are required (set on the auth profile).");

        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "password",
            ["client_id"] = clientId,
            ["username"] = username!,
            ["password"] = password!,
        };
        if (!string.IsNullOrEmpty(clientSecret)) form["client_secret"] = clientSecret;
        if (scopes.Count > 0) form["scope"] = string.Join(' ', scopes);
        if (!string.IsNullOrEmpty(audience)) form["audience"] = audience!;

        var token = await PostTokenAsync(tokenEndpoint, form, ct).ConfigureAwait(false);
        return SaveAndReturn(authPath, tokenEndpoint, clientId, scopes, token);
    }

    private async Task<ExecuteAuthResult> DeviceCodeAsync(
        string authPath, string deviceEndpoint, string tokenEndpoint, string clientId, string? clientSecret,
        IReadOnlyList<string> scopes, string? audience, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(deviceEndpoint))
            return ExecuteAuthResult.Failed("OAuth2 device_code: deviceAuthorizationUrl is missing (set it or enable Use Discovery).");

        var form = new Dictionary<string, string> { ["client_id"] = clientId };
        if (scopes.Count > 0) form["scope"] = string.Join(' ', scopes);
        if (!string.IsNullOrEmpty(audience)) form["audience"] = audience!;

        using var resp = await _http.PostAsync(deviceEndpoint, new FormUrlEncodedContent(form), ct).ConfigureAwait(false);
        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
            return ExecuteAuthResult.Failed($"Device authorization endpoint returned {(int)resp.StatusCode}: {body}");

        var dev = JsonSerializer.Deserialize(body, TokenJson.Default.DeviceCodeResponse);
        if (dev is null || string.IsNullOrEmpty(dev.DeviceCode))
            return ExecuteAuthResult.Failed("Device endpoint returned an unrecognized response.");

        // Stand up a flow record so the UI can poll. Polling the token endpoint happens on
        // the same flow id — keeps the contract identical with the auth-code popup case.
        var flowId = Guid.NewGuid().ToString("N");
        var flow = _flows.Create(new AuthFlow
        {
            Id = flowId,
            AuthPath = authPath,
            CreatedAt = DateTimeOffset.UtcNow,
            CodeVerifier = string.Empty,
            Nonce = string.Empty,
            Authority = string.Empty,
            TokenEndpoint = tokenEndpoint,
            ClientId = clientId,
            ClientSecret = clientSecret,
            RedirectUri = string.Empty,
            Scopes = scopes,
        });
        flow.UserCode = dev.UserCode;
        flow.VerificationUri = dev.VerificationUri;
        flow.VerificationUriComplete = dev.VerificationUriComplete;

        // Background poll loop. Best-effort — if the runner shuts down the user can
        // re-execute the profile and it'll start over.
        _ = Task.Run(async () =>
        {
            var pollInterval = TimeSpan.FromSeconds(Math.Max(dev.Interval ?? 5, 1));
            var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(Math.Max(dev.ExpiresIn ?? 600, 60));
            while (DateTimeOffset.UtcNow < deadline)
            {
                try { await Task.Delay(pollInterval, CancellationToken.None).ConfigureAwait(false); } catch { return; }
                try
                {
                    var pollForm = new Dictionary<string, string>
                    {
                        ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code",
                        ["device_code"] = dev.DeviceCode!,
                        ["client_id"] = clientId,
                    };
                    if (!string.IsNullOrEmpty(clientSecret)) pollForm["client_secret"] = clientSecret;

                    using var pollResp = await _http.PostAsync(tokenEndpoint, new FormUrlEncodedContent(pollForm), CancellationToken.None).ConfigureAwait(false);
                    var pollBody = await pollResp.Content.ReadAsStringAsync(CancellationToken.None).ConfigureAwait(false);
                    if (pollResp.IsSuccessStatusCode)
                    {
                        var tok = JsonSerializer.Deserialize(pollBody, TokenJson.Default.TokenResponse);
                        if (tok is not null)
                        {
                            flow.AccessToken = tok.AccessToken;
                            flow.IdToken = tok.IdToken;
                            flow.RefreshToken = tok.RefreshToken;
                            flow.TokenType = tok.TokenType;
                            flow.ExpiresAt = tok.ExpiresIn is { } s ? DateTimeOffset.UtcNow.AddSeconds(s) : null;
                            flow.Status = AuthFlowStatus.Completed;
                            _tokens.Save(_ws.RootDirectory, authPath, new AuthTokenEntry
                            {
                                AccessToken = tok.AccessToken,
                                IdToken = tok.IdToken,
                                RefreshToken = tok.RefreshToken,
                                TokenType = tok.TokenType,
                                ExpiresAt = flow.ExpiresAt,
                                ObtainedAt = DateTimeOffset.UtcNow,
                                TokenEndpoint = tokenEndpoint,
                                ClientId = clientId,
                                Scopes = scopes,
                            });
                        }
                        return;
                    }

                    // RFC 8628 standard pending/slow_down/expired_token signalling.
                    using var doc = JsonDocument.Parse(pollBody);
                    var err = doc.RootElement.TryGetProperty("error", out var ee) ? ee.GetString() : null;
                    if (err == "authorization_pending") continue;
                    if (err == "slow_down") { pollInterval = pollInterval.Add(TimeSpan.FromSeconds(5)); continue; }
                    if (err == "expired_token" || err == "access_denied")
                    {
                        flow.Status = AuthFlowStatus.Failed;
                        flow.Error = err;
                        return;
                    }
                    flow.Status = AuthFlowStatus.Failed;
                    flow.Error = pollBody;
                    return;
                }
                catch (Exception ex)
                {
                    flow.Status = AuthFlowStatus.Failed;
                    flow.Error = ex.Message;
                    return;
                }
            }
            flow.Status = AuthFlowStatus.Failed;
            flow.Error = "Device code expired before the user completed authentication.";
        }, CancellationToken.None);

        return new ExecuteAuthResult
        {
            FlowId = flowId,
            UserCode = dev.UserCode,
            VerificationUri = dev.VerificationUriComplete ?? dev.VerificationUri,
            VerificationUriComplete = dev.VerificationUriComplete,
            DeviceCodeExpiresIn = dev.ExpiresIn,
            Pending = true,
        };
    }

    private ExecuteAuthResult StartAuthCodeFlow(
        string authPath, string authorizeEndpoint, string tokenEndpoint, string? authority,
        string clientId, string? clientSecret, string redirectUri, IReadOnlyList<string> scopes, string? audience)
    {
        if (string.IsNullOrWhiteSpace(authorizeEndpoint))
            return ExecuteAuthResult.Failed("OAuth2: authorizeUrl is missing.");

        var flowId = Guid.NewGuid().ToString("N");
        var codeVerifier = RandomUrlSafe(64);
        var codeChallenge = Base64UrlSha256(codeVerifier);
        var nonce = RandomUrlSafe(32);

        var flow = _flows.Create(new AuthFlow
        {
            Id = flowId,
            AuthPath = authPath,
            CreatedAt = DateTimeOffset.UtcNow,
            CodeVerifier = codeVerifier,
            Nonce = nonce,
            Authority = authority ?? string.Empty,
            TokenEndpoint = tokenEndpoint,
            ClientId = clientId,
            ClientSecret = clientSecret,
            RedirectUri = redirectUri,
            Scopes = scopes,
        });

        var query = new Dictionary<string, string?>
        {
            ["response_type"] = "code",
            ["client_id"] = clientId,
            ["redirect_uri"] = redirectUri,
            ["state"] = flowId,
            ["nonce"] = nonce,
            ["code_challenge"] = codeChallenge,
            ["code_challenge_method"] = "S256",
        };
        if (scopes.Count > 0) query["scope"] = string.Join(' ', scopes);
        if (!string.IsNullOrEmpty(audience)) query["audience"] = audience;
        var loginUrl = AppendQuery(authorizeEndpoint, query);

        return new ExecuteAuthResult
        {
            FlowId = flow.Id,
            LoginUrl = loginUrl,
            Pending = true,
        };
    }

    private async Task<TokenResponse> ExchangeCodeAsync(AuthFlow flow, string code, CancellationToken ct)
    {
        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = flow.RedirectUri,
            ["client_id"] = flow.ClientId,
            ["code_verifier"] = flow.CodeVerifier,
        };
        if (!string.IsNullOrEmpty(flow.ClientSecret)) form["client_secret"] = flow.ClientSecret;
        return await PostTokenAsync(flow.TokenEndpoint, form, ct).ConfigureAwait(false);
    }

    private async Task<AuthTokenEntry?> RefreshIfPossibleAsync(AuthFile auth, string authPath, AuthTokenEntry stale, CancellationToken ct)
    {
        if (stale.RefreshToken is null || stale.TokenEndpoint is null) return null;
        var resolver = CreateResolver();
        var clientId = await resolver.AllAsync(auth.Fields.GetValueOrDefault("clientId"), ct).ConfigureAwait(false);
        var clientSecret = await resolver.AllAsync(auth.Fields.GetValueOrDefault("clientSecret"), ct).ConfigureAwait(false);
        if (string.IsNullOrEmpty(clientId)) return null;

        try
        {
            var form = new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = stale.RefreshToken,
                ["client_id"] = clientId!,
            };
            if (!string.IsNullOrEmpty(clientSecret)) form["client_secret"] = clientSecret;
            if (stale.Scopes is { Count: > 0 } s) form["scope"] = string.Join(' ', s);

            var token = await PostTokenAsync(stale.TokenEndpoint, form, ct).ConfigureAwait(false);
            var entry = new AuthTokenEntry
            {
                AccessToken = token.AccessToken,
                IdToken = token.IdToken ?? stale.IdToken,
                RefreshToken = token.RefreshToken ?? stale.RefreshToken,
                TokenType = token.TokenType ?? stale.TokenType,
                ExpiresAt = token.ExpiresIn is { } sec ? DateTimeOffset.UtcNow.AddSeconds(sec) : null,
                ObtainedAt = DateTimeOffset.UtcNow,
                TokenEndpoint = stale.TokenEndpoint,
                ClientId = stale.ClientId,
                Scopes = stale.Scopes,
            };
            _tokens.Save(_ws.RootDirectory, authPath, entry);
            return entry;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Silent refresh failed for {Path}; falling through to interactive.", authPath);
            return null;
        }
    }

    // ---- Azure CLI ------------------------------------------------------------------

    /// <summary>
    /// Dispatches on the profile's <c>flow</c> field — mirrors how the OAuth2 entry point
    /// picks between client_credentials / password / authorization_code / etc. Default
    /// flow is <c>direct</c> (plain az-cli token); <c>on_behalf_of</c> chains the az-cli
    /// step with a JWT-bearer exchange against AAD.
    /// </summary>
    private Task<ExecuteAuthResult> ExecuteAzureAsync(AuthFile auth, string authPath, AuthFieldResolver resolver, CancellationToken ct)
    {
        var flow = (auth.Fields.GetValueOrDefault("flow") ?? "direct").Trim();
        return flow switch
        {
            "on_behalf_of" or "obo" => ExecuteAzureCliOboAsync(auth, authPath, resolver, ct),
            "direct" or "" => ExecuteAzureCliAsync(auth, authPath, resolver, ct),
            _ => Task.FromResult(ExecuteAuthResult.Failed($"Azure CLI flow '{flow}' is not supported (use 'direct' or 'on_behalf_of').")),
        };
    }

    private async Task<ExecuteAuthResult> ExecuteAzureCliAsync(AuthFile auth, string authPath, AuthFieldResolver resolver, CancellationToken ct)
    {
        // Either `resource` (v1 audience) or `scope` (v2). Pass through whichever the
        // user provided — `az` accepts both.
        var resource = await resolver.AllAsync(auth.Fields.GetValueOrDefault("resource"), ct).ConfigureAwait(false);
        var scope = await resolver.AllAsync(auth.Fields.GetValueOrDefault("scope"), ct).ConfigureAwait(false);
        var tenant = await resolver.AllAsync(auth.Fields.GetValueOrDefault("tenant") ?? auth.Fields.GetValueOrDefault("tenantId"), ct).ConfigureAwait(false);
        var subscription = await resolver.AllAsync(auth.Fields.GetValueOrDefault("subscription"), ct).ConfigureAwait(false);

        var result = await RunAzCliAsync(resource, scope, tenant, subscription, ct).ConfigureAwait(false);
        if (result.Error is not null) return ExecuteAuthResult.Failed(result.Error);

        var entry = new AuthTokenEntry
        {
            AccessToken = result.Token!,
            TokenType = "Bearer",
            ExpiresAt = result.ExpiresAt,
            ObtainedAt = DateTimeOffset.UtcNow,
        };
        _tokens.Save(_ws.RootDirectory, authPath, entry);
        return ExecuteAuthResult.FromTokens(entry, fromCache: false);
    }

    private async Task<ExecuteAuthResult> ExecuteAzureCliOboAsync(AuthFile auth, string authPath, AuthFieldResolver resolver, CancellationToken ct)
    {
        // OBO = Azure-CLI user token → downstream API token via the JWT-bearer grant.
        // The middle-tier client (clientId/clientSecret) presents the user assertion to the
        // identity provider and gets back a new access token whose audience is the
        // downstream API (scopes/audience).
        var resource = await resolver.AllAsync(auth.Fields.GetValueOrDefault("userResource"), ct).ConfigureAwait(false);
        var userScope = await resolver.AllAsync(auth.Fields.GetValueOrDefault("userScope"), ct).ConfigureAwait(false);
        var tenant = await resolver.AllAsync(auth.Fields.GetValueOrDefault("tenant") ?? auth.Fields.GetValueOrDefault("tenantId"), ct).ConfigureAwait(false);
        var subscription = await resolver.AllAsync(auth.Fields.GetValueOrDefault("subscription"), ct).ConfigureAwait(false);

        var userToken = await RunAzCliAsync(resource, userScope, tenant, subscription, ct).ConfigureAwait(false);
        if (userToken.Error is not null) return ExecuteAuthResult.Failed("Azure CLI step failed: " + userToken.Error);

        var clientId = await resolver.AllAsync(auth.Fields.GetValueOrDefault("clientId"), ct).ConfigureAwait(false);
        var clientSecret = await resolver.AllAsync(auth.Fields.GetValueOrDefault("clientSecret"), ct).ConfigureAwait(false);
        var tokenEndpoint = await resolver.AllAsync(auth.Fields.GetValueOrDefault("tokenUrl"), ct).ConfigureAwait(false);

        // Default to the Microsoft v2 endpoint when only a tenant is given — saves the user
        // from spelling out the URL when targeting AAD.
        if (string.IsNullOrWhiteSpace(tokenEndpoint) && !string.IsNullOrWhiteSpace(tenant))
            tokenEndpoint = $"https://login.microsoftonline.com/{tenant}/oauth2/v2.0/token";

        if (string.IsNullOrWhiteSpace(tokenEndpoint))
            return ExecuteAuthResult.Failed("Azure-CLI OBO: tokenUrl is required (or set tenant to default to the AAD v2 endpoint).");
        if (string.IsNullOrWhiteSpace(clientId))
            return ExecuteAuthResult.Failed("Azure-CLI OBO: clientId (middle-tier app) is required.");

        var scopes = await ResolveScopesAsync(auth, resolver, ct).ConfigureAwait(false);
        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "urn:ietf:params:oauth:grant-type:jwt-bearer",
            ["client_id"] = clientId!,
            ["assertion"] = userToken.Token!,
            ["requested_token_use"] = "on_behalf_of",
        };
        if (!string.IsNullOrEmpty(clientSecret)) form["client_secret"] = clientSecret;
        if (scopes.Count > 0) form["scope"] = string.Join(' ', scopes);

        var token = await PostTokenAsync(tokenEndpoint!, form, ct).ConfigureAwait(false);
        return SaveAndReturn(authPath, tokenEndpoint!, clientId!, scopes, token);
    }

    private async Task<AzCliResult> RunAzCliAsync(string? resource, string? scope, string? tenant, string? subscription, CancellationToken ct)
    {
        var args = new List<string> { "account", "get-access-token", "--output", "json" };
        if (!string.IsNullOrEmpty(resource)) { args.Add("--resource"); args.Add(resource!); }
        if (!string.IsNullOrEmpty(scope)) { args.Add("--scope"); args.Add(scope!); }
        if (!string.IsNullOrEmpty(tenant)) { args.Add("--tenant"); args.Add(tenant!); }
        if (!string.IsNullOrEmpty(subscription)) { args.Add("--subscription"); args.Add(subscription!); }

        // `az` on Windows is a cmd shim — wrap accordingly. On Unix the binary is on PATH.
        var psi = new ProcessStartInfo
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        if (OperatingSystem.IsWindows())
        {
            psi.FileName = "cmd.exe";
            psi.ArgumentList.Add("/c");
            psi.ArgumentList.Add("az");
            foreach (var a in args) psi.ArgumentList.Add(a);
        }
        else
        {
            psi.FileName = "az";
            foreach (var a in args) psi.ArgumentList.Add(a);
        }

        try
        {
            using var proc = Process.Start(psi)
                ?? throw new InvalidOperationException("Failed to start `az`. Is the Azure CLI installed and on PATH?");
            var stdoutTask = proc.StandardOutput.ReadToEndAsync(ct);
            var stderrTask = proc.StandardError.ReadToEndAsync(ct);
            await proc.WaitForExitAsync(ct).ConfigureAwait(false);
            var stdout = await stdoutTask.ConfigureAwait(false);
            var stderr = await stderrTask.ConfigureAwait(false);
            if (proc.ExitCode != 0)
                return new AzCliResult { Error = $"az exited with {proc.ExitCode}: {stderr.Trim()}" };

            var parsed = JsonSerializer.Deserialize(stdout, TokenJson.Default.AzCliTokenResponse);
            if (parsed?.AccessToken is null)
                return new AzCliResult { Error = "az returned no accessToken." };

            DateTimeOffset? expires = null;
            if (DateTimeOffset.TryParse(parsed.ExpiresOn, out var parsedExpires))
                expires = parsedExpires;
            else if (parsed.ExpiresOnTimestamp is { } unix)
                expires = DateTimeOffset.FromUnixTimeSeconds(unix);

            return new AzCliResult { Token = parsed.AccessToken, ExpiresAt = expires };
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return new AzCliResult { Error = "Could not run `az` — install the Azure CLI and `az login` first." };
        }
    }

    // ---- JWT mint -------------------------------------------------------------------

    /// <summary>
    /// Build a signed JWT from auth-profile fields and return it as a bearer header.
    /// Mirrors dreamr's <c>JwtIssuer.CreateToken(CreateRawJwtTokenRequest)</c>: the user
    /// supplies algorithm + issuer + audience + expiresIn + signing key + an optional
    /// JSON blob of custom claims, and we emit <c>header.payload.signature</c>. No identity
    /// provider involved — useful when an upstream accepts a self-signed JWT (e.g. service-to-service
    /// "client assertion" patterns, dev-mode JWT bearer auth, or signed webhook callbacks).
    ///
    /// Supports HS256/HS384/HS512 (symmetric HMAC, key = UTF-8 bytes of the field), and
    /// RS256/RS384/RS512 + ES256/ES384/ES512 / PS256/PS384/PS512 (asymmetric, key = PEM-
    /// encoded private key). The standard <c>iss/exp/iat/jti/sub/aud</c> claims are auto-filled;
    /// anything in <c>payload</c> overrides them.
    /// </summary>
    private async Task<ExecuteAuthResult> ExecuteJwtAsync(AuthFile auth, string authPath, AuthFieldResolver resolver, CancellationToken ct)
    {
        var algorithm = ((await resolver.AllAsync(auth.Fields.GetValueOrDefault("algorithm"), ct).ConfigureAwait(false)) ?? "HS256").Trim().ToUpperInvariant();
        var issuer = await resolver.AllAsync(auth.Fields.GetValueOrDefault("issuer"), ct).ConfigureAwait(false);
        var audience = await resolver.AllAsync(auth.Fields.GetValueOrDefault("audience"), ct).ConfigureAwait(false);
        var subject = await resolver.AllAsync(auth.Fields.GetValueOrDefault("subject"), ct).ConfigureAwait(false);
        var keyId = await resolver.AllAsync(auth.Fields.GetValueOrDefault("keyId") ?? auth.Fields.GetValueOrDefault("kid"), ct).ConfigureAwait(false);
        var key = await resolver.AllAsync(auth.Fields.GetValueOrDefault("key"), ct).ConfigureAwait(false);
        var payloadJson = await resolver.AllAsync(auth.Fields.GetValueOrDefault("payload"), ct).ConfigureAwait(false);
        var expiresInRaw = auth.Fields.GetValueOrDefault("expiresIn");
        var expiresIn = int.TryParse(expiresInRaw, out var seconds) ? seconds : 3600;

        if (string.IsNullOrEmpty(key))
            return ExecuteAuthResult.Failed("JWT: a signing 'key' is required (HMAC secret or PEM-encoded private key).");

        try
        {
            var jwt = JwtMinter.Mint(new JwtMintRequest
            {
                Algorithm = algorithm,
                Issuer = issuer,
                Audience = audience,
                Subject = subject,
                KeyId = keyId,
                Key = key!,
                PayloadJson = payloadJson,
                ExpiresIn = expiresIn,
            });

            var entry = new AuthTokenEntry
            {
                AccessToken = jwt,
                TokenType = "Bearer",
                ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(expiresIn),
                ObtainedAt = DateTimeOffset.UtcNow,
            };
            _tokens.Save(_ws.RootDirectory, authPath, entry);
            return ExecuteAuthResult.FromTokens(entry, fromCache: false);
        }
        catch (Exception ex)
        {
            return ExecuteAuthResult.Failed($"JWT minting failed: {ex.Message}");
        }
    }

    // ---- GitHub --------------------------------------------------------------------

    private const string GithubApiVersion = "2022-11-28";
    private const string GithubAuthorize = "https://github.com/login/oauth/authorize";
    private const string GithubToken = "https://github.com/login/oauth/access_token";

    /// <summary>
    /// Multi-mode GitHub auth. Dispatches on the profile's <c>mode</c> field. Mirrors how
    /// the Azure CLI entry point picks between direct + OBO — the front door is one auth
    /// type with several sub-flows, so users don't drown in a dozen near-identical types.
    /// </summary>
    private async Task<ExecuteAuthResult> ExecuteGithubAsync(AuthFile auth, string authPath, AuthFieldResolver resolver, CancellationToken ct)
    {
        var mode = (auth.Fields.GetValueOrDefault("mode") ?? "pat").Trim();
        return mode switch
        {
            "pat"   => await ExecuteGithubPatAsync(auth, authPath, resolver, ct).ConfigureAwait(false),
            "gh-cli" or "ghcli" or "cli"
                    => await ExecuteGithubCliAsync(authPath, ct).ConfigureAwait(false),
            "app"   => await ExecuteGithubAppAsync(auth, authPath, resolver, ct).ConfigureAwait(false),
            "oauth" => await ExecuteGithubOAuthAsync(auth, authPath, resolver, ct).ConfigureAwait(false),
            _ => ExecuteAuthResult.Failed($"GitHub mode '{mode}' is not supported (use pat, gh-cli, app, or oauth)."),
        };
    }

    private async Task<ExecuteAuthResult> ExecuteGithubPatAsync(AuthFile auth, string authPath, AuthFieldResolver resolver, CancellationToken ct)
    {
        var token = await resolver.AllAsync(auth.Fields.GetValueOrDefault("token"), ct).ConfigureAwait(false);
        if (string.IsNullOrEmpty(token))
            return ExecuteAuthResult.Failed("GitHub PAT: 'token' is required.");

        var entry = new AuthTokenEntry
        {
            AccessToken = token!,
            TokenType = "Bearer",
            ObtainedAt = DateTimeOffset.UtcNow,
        };
        _tokens.Save(_ws.RootDirectory, authPath, entry);
        return WithGithubExtras(ExecuteAuthResult.FromTokens(entry, fromCache: false));
    }

    private async Task<ExecuteAuthResult> ExecuteGithubCliAsync(string authPath, CancellationToken ct)
    {
        var (token, error) = await RunGhCliAsync(ct).ConfigureAwait(false);
        if (error is not null) return ExecuteAuthResult.Failed(error);

        var entry = new AuthTokenEntry
        {
            AccessToken = token!,
            TokenType = "Bearer",
            ObtainedAt = DateTimeOffset.UtcNow,
        };
        _tokens.Save(_ws.RootDirectory, authPath, entry);
        return WithGithubExtras(ExecuteAuthResult.FromTokens(entry, fromCache: false));
    }

    /// <summary>
    /// GitHub App installation token: mint a 9-minute RS256 JWT signed with the App's PEM
    /// private key, POST it to /app/installations/{id}/access_tokens with a Bearer header,
    /// cache the returned <c>ghs_*</c> installation token. The token typically expires after
    /// an hour — we honor whatever <c>expires_at</c> GitHub returns.
    /// </summary>
    private async Task<ExecuteAuthResult> ExecuteGithubAppAsync(AuthFile auth, string authPath, AuthFieldResolver resolver, CancellationToken ct)
    {
        var appId = await resolver.AllAsync(auth.Fields.GetValueOrDefault("appId"), ct).ConfigureAwait(false);
        var installationId = await resolver.AllAsync(auth.Fields.GetValueOrDefault("installationId"), ct).ConfigureAwait(false);
        var privateKey = await resolver.AllAsync(auth.Fields.GetValueOrDefault("privateKey"), ct).ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(appId))
            return ExecuteAuthResult.Failed("GitHub App: 'appId' is required.");
        if (string.IsNullOrWhiteSpace(installationId))
            return ExecuteAuthResult.Failed("GitHub App: 'installationId' is required.");
        if (string.IsNullOrWhiteSpace(privateKey))
            return ExecuteAuthResult.Failed("GitHub App: 'privateKey' (PEM) is required.");

        string assertion;
        try
        {
            assertion = JwtMinter.Mint(new JwtMintRequest
            {
                Algorithm = "RS256",
                Issuer = appId!,
                Key = privateKey!,
                ExpiresIn = 540, // GH caps the App JWT at 10 minutes; 9 leaves clock-skew room.
            });
        }
        catch (Exception ex)
        {
            return ExecuteAuthResult.Failed($"GitHub App JWT minting failed: {ex.Message}");
        }

        using var req = new HttpRequestMessage(HttpMethod.Post,
            $"https://api.github.com/app/installations/{Uri.EscapeDataString(installationId!)}/access_tokens");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", assertion);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        req.Headers.Add("X-GitHub-Api-Version", GithubApiVersion);
        req.Headers.UserAgent.Add(new System.Net.Http.Headers.ProductInfoHeaderValue("tap-studio", "1.0"));

        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
            return ExecuteAuthResult.Failed($"GitHub App installation token endpoint returned {(int)resp.StatusCode}: {body}");

        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            var token = root.TryGetProperty("token", out var t) ? t.GetString() : null;
            if (string.IsNullOrEmpty(token))
                return ExecuteAuthResult.Failed("GitHub App installation token response missing 'token'.");

            DateTimeOffset? expiresAt = null;
            if (root.TryGetProperty("expires_at", out var ex) && ex.ValueKind == JsonValueKind.String
                && DateTimeOffset.TryParse(ex.GetString(), out var parsed))
                expiresAt = parsed;

            var entry = new AuthTokenEntry
            {
                AccessToken = token!,
                TokenType = "Bearer",
                ExpiresAt = expiresAt,
                ObtainedAt = DateTimeOffset.UtcNow,
            };
            _tokens.Save(_ws.RootDirectory, authPath, entry);
            return WithGithubExtras(ExecuteAuthResult.FromTokens(entry, fromCache: false));
        }
        catch (JsonException ex)
        {
            return ExecuteAuthResult.Failed($"GitHub App installation token response was not valid JSON: {ex.Message}");
        }
    }

    /// <summary>
    /// GitHub OAuth App: delegate to <see cref="StartAuthCodeFlow"/> with github.com endpoints
    /// preset. PKCE is enabled by default — GitHub supports it for confidential and public
    /// clients alike.
    /// </summary>
    private async Task<ExecuteAuthResult> ExecuteGithubOAuthAsync(AuthFile auth, string authPath, AuthFieldResolver resolver, CancellationToken ct)
    {
        var clientId = await resolver.AllAsync(auth.Fields.GetValueOrDefault("clientId"), ct).ConfigureAwait(false);
        var clientSecret = await resolver.AllAsync(auth.Fields.GetValueOrDefault("clientSecret"), ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(clientId))
            return ExecuteAuthResult.Failed("GitHub OAuth: clientId is required.");

        var scopes = await ResolveScopesAsync(auth, resolver, ct).ConfigureAwait(false);
        var redirectUri = ResolveCallbackUri();
        return StartAuthCodeFlow(authPath, GithubAuthorize, GithubToken,
            authority: "https://github.com", clientId!, clientSecret, redirectUri, scopes, audience: null);
    }

    /// <summary>Attach the bearer Authorization header for a GitHub token result so every
    /// mode (PAT, gh-cli, App, OAuth) produces the same request shape.</summary>
    private static ExecuteAuthResult WithGithubExtras(ExecuteAuthResult result)
    {
        if (result.AccessToken is null) return result;
        var extras = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Authorization"] = "Bearer " + result.AccessToken,
        };
        return result with { Headers = extras };
    }

    private static async Task<(string? Token, string? Error)> RunGhCliAsync(CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        if (OperatingSystem.IsWindows())
        {
            psi.FileName = "cmd.exe";
            psi.ArgumentList.Add("/c");
            psi.ArgumentList.Add("gh");
            psi.ArgumentList.Add("auth");
            psi.ArgumentList.Add("token");
        }
        else
        {
            psi.FileName = "gh";
            psi.ArgumentList.Add("auth");
            psi.ArgumentList.Add("token");
        }

        try
        {
            using var proc = Process.Start(psi)
                ?? throw new InvalidOperationException("Failed to start `gh`. Is the GitHub CLI installed and on PATH?");
            var stdoutTask = proc.StandardOutput.ReadToEndAsync(ct);
            var stderrTask = proc.StandardError.ReadToEndAsync(ct);
            await proc.WaitForExitAsync(ct).ConfigureAwait(false);
            var stdout = (await stdoutTask.ConfigureAwait(false)).Trim();
            var stderr = (await stderrTask.ConfigureAwait(false)).Trim();
            if (proc.ExitCode != 0)
                return (null, $"gh exited with {proc.ExitCode}: {stderr}");
            if (string.IsNullOrEmpty(stdout))
                return (null, "gh auth token returned no output — run `gh auth login` first.");
            return (stdout, null);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return (null, "Could not run `gh` — install the GitHub CLI and `gh auth login` first.");
        }
    }

    // ---- Token endpoint helpers -----------------------------------------------------

    private static async Task<string?> TryDiscoverDeviceEndpointAsync(string authority, CancellationToken ct)
    {
        // Cheap one-off — fetch the raw discovery doc and pick out device_authorization_endpoint
        // since our OidcDiscoveryDocument doesn't surface it (yet).
        try
        {
            using var http = new HttpClient();
            var url = authority.TrimEnd('/') + "/.well-known/openid-configuration";
            using var stream = await http.GetStreamAsync(url, ct).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
            return doc.RootElement.TryGetProperty("device_authorization_endpoint", out var de) ? de.GetString() : null;
        }
        catch
        {
            return null;
        }
    }

    private ExecuteAuthResult SaveAndReturn(string authPath, string tokenEndpoint, string clientId,
        IReadOnlyList<string> scopes, TokenResponse token)
    {
        var entry = new AuthTokenEntry
        {
            AccessToken = token.AccessToken,
            IdToken = token.IdToken,
            RefreshToken = token.RefreshToken,
            TokenType = token.TokenType,
            ExpiresAt = token.ExpiresIn is { } s ? DateTimeOffset.UtcNow.AddSeconds(s) : null,
            ObtainedAt = DateTimeOffset.UtcNow,
            TokenEndpoint = tokenEndpoint,
            ClientId = clientId,
            Scopes = scopes,
        };
        _tokens.Save(_ws.RootDirectory, authPath, entry);
        return ExecuteAuthResult.FromTokens(entry, fromCache: false);
    }

    private async Task<TokenResponse> PostTokenAsync(string tokenEndpoint, Dictionary<string, string> form, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(tokenEndpoint) || !Uri.TryCreate(tokenEndpoint, UriKind.Absolute, out _))
            throw new InvalidOperationException($"Token endpoint URL is invalid: '{tokenEndpoint}'.");

        using var req = new HttpRequestMessage(HttpMethod.Post, tokenEndpoint)
        {
            Content = new FormUrlEncodedContent(form),
        };
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"Token endpoint returned {(int)resp.StatusCode}: {body}");

        var parsed = JsonSerializer.Deserialize(body, TokenJson.Default.TokenResponse)
                     ?? throw new InvalidOperationException("Token endpoint returned empty body.");
        return parsed;
    }

    private static async ValueTask<IReadOnlyList<string>> ResolveScopesAsync(AuthFile auth, AuthFieldResolver resolver, CancellationToken ct)
    {
        IReadOnlyList<string> raw;
        if (auth.Scopes.Count > 0) raw = auth.Scopes;
        else
        {
            var combined = auth.Fields.GetValueOrDefault("scopes");
            raw = string.IsNullOrWhiteSpace(combined)
                ? []
                : combined.Split([' ', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        }
        return await resolver.AllAsync(raw, ct).ConfigureAwait(false);
    }

    private async ValueTask<IReadOnlyDictionary<string, string>> BuildHeadersForAsync(AuthFile auth, AuthFieldResolver resolver, CancellationToken ct)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        switch (auth.Type)
        {
            case "bearer":
                {
                    var t = await resolver.AllAsync(auth.Fields.GetValueOrDefault("token"), ct).ConfigureAwait(false);
                    if (!string.IsNullOrEmpty(t)) headers["Authorization"] = "Bearer " + t;
                    break;
                }
            case "basic":
                {
                    var u = await resolver.AllAsync(auth.Fields.GetValueOrDefault("username"), ct).ConfigureAwait(false);
                    var p = await resolver.AllAsync(auth.Fields.GetValueOrDefault("password"), ct).ConfigureAwait(false);
                    if (!string.IsNullOrEmpty(u) && !string.IsNullOrEmpty(p))
                        headers["Authorization"] = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"{u}:{p}"));
                    break;
                }
            case "apiKey":
                {
                    var location = auth.Fields.GetValueOrDefault("in") ?? "header";
                    if (string.Equals(location, "header", StringComparison.OrdinalIgnoreCase))
                    {
                        var name = await resolver.AllAsync(auth.Fields.GetValueOrDefault("apiKeyName") ?? auth.Fields.GetValueOrDefault("name"), ct).ConfigureAwait(false);
                        var val = await resolver.AllAsync(auth.Fields.GetValueOrDefault("apiKeyValue") ?? auth.Fields.GetValueOrDefault("value"), ct).ConfigureAwait(false);
                        if (!string.IsNullOrEmpty(name) && val is not null) headers[name!] = val;
                    }
                    break;
                }
            case "custom":
                foreach (var (k, v) in auth.Headers)
                {
                    var expanded = await resolver.AllAsync(v, ct).ConfigureAwait(false);
                    if (expanded is not null) headers[k] = expanded;
                }
                break;
        }
        return headers;
    }

    /// <summary>Fallback redirect URI when there's no live HttpContext (e.g. background
    /// callers / tests). The Studio's actual base URL is preferred — see <see cref="ResolveCallbackUri"/>.</summary>
    public const string DefaultRedirectUri = "http://localhost:5298/api/auth/callback";

    /// <summary>
    /// Build the OAuth redirect URI from whichever URL the user is currently hitting Studio at.
    /// Aspire reroutes through a dynamic port per run, so the redirect must follow that —
    /// the alternative (hard-coding) breaks every other restart. The result is the absolute
    /// URL of <c>/api/auth/callback</c> on the same scheme+host the inbound request used.
    /// Falls back to <see cref="DefaultRedirectUri"/> when no HttpContext is available.
    ///
    /// When running under the desktop shell (<c>TAP_STUDIO_DESKTOP=1</c>) the Tauri sidecar
    /// binds to a random localhost port, so the OS-registered <c>tap-studio://callback</c>
    /// scheme is used instead — stable across launches and registerable with any IdP.
    /// </summary>
    public string ResolveCallbackUri()
    {
        if (string.Equals(Environment.GetEnvironmentVariable("TAP_STUDIO_DESKTOP"), "1", StringComparison.Ordinal))
        {
            return "tap-studio://callback";
        }
        var req = _httpCtx.HttpContext?.Request;
        if (req is null || !req.Host.HasValue) return DefaultRedirectUri;
        return $"{req.Scheme}://{req.Host.Value}/api/auth/callback";
    }

    private static string RandomUrlSafe(int bytes)
    {
        Span<byte> buf = stackalloc byte[bytes];
        RandomNumberGenerator.Fill(buf);
        return Convert.ToBase64String(buf).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private static string Base64UrlSha256(string input)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private static string AppendQuery(string url, IDictionary<string, string?> query)
    {
        var sb = new StringBuilder(url);
        sb.Append(url.Contains('?') ? '&' : '?');
        var first = true;
        foreach (var (k, v) in query)
        {
            if (v is null) continue;
            if (!first) sb.Append('&');
            sb.Append(Uri.EscapeDataString(k)).Append('=').Append(Uri.EscapeDataString(v));
            first = false;
        }
        return sb.ToString();
    }

    private readonly record struct AzCliResult
    {
        public string? Token { get; init; }
        public DateTimeOffset? ExpiresAt { get; init; }
        public string? Error { get; init; }
    }
}

internal sealed record TokenResponse
{
    [JsonPropertyName("access_token")] public string AccessToken { get; init; } = string.Empty;
    [JsonPropertyName("id_token")] public string? IdToken { get; init; }
    [JsonPropertyName("refresh_token")] public string? RefreshToken { get; init; }
    [JsonPropertyName("token_type")] public string? TokenType { get; init; }
    [JsonPropertyName("expires_in")] public int? ExpiresIn { get; init; }
    [JsonPropertyName("scope")] public string? Scope { get; init; }
}

internal sealed record DeviceCodeResponse
{
    [JsonPropertyName("device_code")] public string? DeviceCode { get; init; }
    [JsonPropertyName("user_code")] public string? UserCode { get; init; }
    [JsonPropertyName("verification_uri")] public string? VerificationUri { get; init; }
    [JsonPropertyName("verification_uri_complete")] public string? VerificationUriComplete { get; init; }
    [JsonPropertyName("expires_in")] public int? ExpiresIn { get; init; }
    [JsonPropertyName("interval")] public int? Interval { get; init; }
}

/// <summary>Shape of <c>az account get-access-token --output json</c>.</summary>
internal sealed record AzCliTokenResponse
{
    [JsonPropertyName("accessToken")] public string? AccessToken { get; init; }
    [JsonPropertyName("expiresOn")] public string? ExpiresOn { get; init; }
    [JsonPropertyName("expires_on")] public long? ExpiresOnTimestamp { get; init; }
    [JsonPropertyName("tokenType")] public string? TokenType { get; init; }
    [JsonPropertyName("subscription")] public string? Subscription { get; init; }
    [JsonPropertyName("tenant")] public string? Tenant { get; init; }
}

[JsonSerializable(typeof(TokenResponse))]
[JsonSerializable(typeof(DeviceCodeResponse))]
[JsonSerializable(typeof(AzCliTokenResponse))]
internal partial class TokenJson : JsonSerializerContext
{
}

/// <summary>
/// Either the populated tokens (sync grants and cached entries), the popup hand-off info
/// (auth-code grant), or the device-code prompt (device flow). <see cref="Error"/> is set
/// when execution failed.
/// </summary>
public sealed record ExecuteAuthResult
{
    public string? AccessToken { get; init; }
    public string? IdToken { get; init; }
    public string? RefreshToken { get; init; }
    public string? TokenType { get; init; }
    public DateTimeOffset? ExpiresAt { get; init; }
    public bool FromCache { get; init; }

    public string? FlowId { get; init; }
    public string? LoginUrl { get; init; }
    public bool Pending { get; init; }

    // Device-code fields. Surfaced when the runner chose the RFC 8628 grant — UI displays
    // these to the user and polls the flow id for completion.
    public string? UserCode { get; init; }
    public string? VerificationUri { get; init; }
    public string? VerificationUriComplete { get; init; }
    public int? DeviceCodeExpiresIn { get; init; }

    /// <summary>Headers an auth profile would inject directly (basic/bearer/apiKey/custom).</summary>
    public IReadOnlyDictionary<string, string>? Headers { get; init; }

    public string? Error { get; init; }

    public static ExecuteAuthResult FromTokens(AuthTokenEntry entry, bool fromCache) => new()
    {
        AccessToken = entry.AccessToken,
        IdToken = entry.IdToken,
        RefreshToken = entry.RefreshToken,
        TokenType = entry.TokenType,
        ExpiresAt = entry.ExpiresAt,
        FromCache = fromCache,
    };

    public static ExecuteAuthResult SyntheticHeaders(IReadOnlyDictionary<string, string> headers) => new()
    {
        Headers = headers,
    };

    public static ExecuteAuthResult Failed(string message) => new() { Error = message };
}
