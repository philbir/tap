using System.Diagnostics;
using System.Text.Json;
using Tap.Execution.Auth;
using Tap.Execution.Workspace;
using Tap.Workspace;
using Tap.Workspace.Model;

namespace Tap.Studio.Cli.Auth;

/// <summary>Raised when a profile needs a human and there isn't one. Carries guidance rather
/// than a bare failure, because "auth failed" in a pipeline log costs somebody an afternoon.</summary>
public sealed class HeadlessAuthUnavailableException(string message) : Exception(message);

/// <summary>
/// Gets bearer tokens without a browser.
///
/// <para>Three rules, each chosen for what it prevents:</para>
/// <list type="number">
///   <item><b>Mint what can be minted.</b> client_credentials, ROPC, and <c>az</c> all work
///     without a human and cover the credentials a CI runner actually has.</item>
///   <item><b>Refuse the rest loudly.</b> An interactive grant fails immediately, naming the
///     profile and the alternatives. The failure mode this replaces — a pipeline blocked on a
///     sign-in prompt nobody can see, until the job times out — is far worse than an error.</item>
///   <item><b>Ignore the developer's token cache unless asked.</b> A CI run that passes because
///     someone's laptop had a warm token is not a passing test; it is a test that didn't run.
///     <c>--use-cached-tokens</c> opts in for local convenience.</item>
/// </list>
/// </summary>
public sealed class HeadlessAuthTokenSource(
    LoadedWorkspace workspace,
    Func<EnvFile?, Tap.Workspace.Variables.VariableProviderRegistry> registryFactory,
    HttpClient http,
    string rootDirectory,
    AuthTokenStore? cache) : IAuthTokenSource
{
    private readonly Dictionary<AuthProfileScope, AuthToken> _minted = [];

    public async ValueTask<AuthToken?> GetAsync(AuthFile profile, AuthProfileScope scope, CancellationToken ct)
    {
        // One exchange per profile+scope per run. A ten-step flow against one API should sign
        // in once, not ten times.
        if (_minted.TryGetValue(scope, out var cached) && !Expired(cached)) return cached;

        // Only consulted when --use-cached-tokens was passed. Off by default because a run
        // that passes on a token somebody minted by hand last Tuesday has not tested the
        // thing the pipeline believes it tested.
        if (cache?.Get(rootDirectory, scope) is { AccessToken.Length: > 0 } stored)
        {
            var fromCache = new AuthToken(stored.AccessToken, stored.ExpiresAt);
            if (!Expired(fromCache))
            {
                _minted[scope] = fromCache;
                return fromCache;
            }
        }

        var context = AuthScopeResolver.ContextFor(
            workspace, profile.RelativePath, requestPath: null, stageName: scope.Stage, envPath: scope.Env);
        var resolver = new AuthFieldResolver(workspace, registryFactory(context.Env), context);

        var token = profile.Type switch
        {
            "oauth2" => await OAuth2Async(profile, resolver, ct).ConfigureAwait(false),
            "azure-cli" => await AzureCliAsync(profile, resolver, ct).ConfigureAwait(false),
            "github" => Refuse(profile, $"github mode '{Mode(profile)}'", "a Personal Access Token (mode: pat), whose value can come from an env-backed variable"),
            "jwt" => null, // Minted by the renderer from profile fields; nothing to fetch.
            _ => Refuse(profile, $"auth type '{profile.Type}'", "one of the non-interactive types"),
        };

        if (token is { } minted) _minted[scope] = minted;
        return token;
    }

    private async ValueTask<AuthToken?> OAuth2Async(AuthFile profile, AuthFieldResolver resolver, CancellationToken ct)
    {
        var grant = (profile.Fields.GetValueOrDefault("flow")
                     ?? profile.Fields.GetValueOrDefault("grantType")
                     ?? "authorization_code_pkce").Trim();

        var form = new Dictionary<string, string>(StringComparer.Ordinal);
        switch (grant)
        {
            case "client_credentials":
                form["grant_type"] = "client_credentials";
                break;

            case "password" or "resource_owner" or "ropc":
                {
                    var username = await resolver.AllAsync(profile.Fields.GetValueOrDefault("username"), ct).ConfigureAwait(false);
                    var password = await resolver.AllAsync(profile.Fields.GetValueOrDefault("password"), ct).ConfigureAwait(false);
                    if (string.IsNullOrEmpty(username) || string.IsNullOrEmpty(password))
                    {
                        throw new HeadlessAuthUnavailableException(
                            $"Auth profile '{profile.RelativePath}' uses the password grant but has no username/password. "
                            + "Set them from variables your CI environment provides.");
                    }
                    form["grant_type"] = "password";
                    form["username"] = username!;
                    form["password"] = password!;
                    break;
                }

            default:
                return Refuse(
                    profile,
                    $"the '{grant}' grant",
                    "client_credentials — the grant designed for a machine with no user present");
        }

        var tokenUrl = await resolver.AllAsync(profile.Fields.GetValueOrDefault("tokenUrl"), ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(tokenUrl))
        {
            // Discovery would need a second round trip and an authority; a CI profile should
            // just say where its token endpoint is.
            throw new HeadlessAuthUnavailableException(
                $"Auth profile '{profile.RelativePath}' has no tokenUrl. Set it explicitly — "
                + "OIDC discovery is not performed headlessly.");
        }

        var clientId = await resolver.AllAsync(profile.Fields.GetValueOrDefault("clientId"), ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(clientId))
            throw new HeadlessAuthUnavailableException($"Auth profile '{profile.RelativePath}': clientId is required.");
        form["client_id"] = clientId!;

        var clientSecret = await resolver.AllAsync(profile.Fields.GetValueOrDefault("clientSecret"), ct).ConfigureAwait(false);
        if (!string.IsNullOrEmpty(clientSecret)) form["client_secret"] = clientSecret!;

        var audience = await resolver.AllAsync(profile.Fields.GetValueOrDefault("audience"), ct).ConfigureAwait(false);
        if (!string.IsNullOrEmpty(audience)) form["audience"] = audience!;

        var scopes = await resolver.AllAsync(profile.Scopes, ct).ConfigureAwait(false);
        if (scopes.Count > 0) form["scope"] = string.Join(' ', scopes);

        using var response = await http
            .PostAsync(tokenUrl, new FormUrlEncodedContent(form), ct)
            .ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            throw new HeadlessAuthUnavailableException(
                $"Auth profile '{profile.RelativePath}': the token endpoint returned {(int)response.StatusCode}. {Trim(body)}");
        }

        try
        {
            using var document = JsonDocument.Parse(body);
            if (!document.RootElement.TryGetProperty("access_token", out var accessToken)
                || accessToken.GetString() is not { Length: > 0 } value)
            {
                throw new HeadlessAuthUnavailableException(
                    $"Auth profile '{profile.RelativePath}': the token response carried no access_token.");
            }

            DateTimeOffset? expiresAt = document.RootElement.TryGetProperty("expires_in", out var expiresIn)
                && expiresIn.TryGetInt32(out var seconds)
                ? DateTimeOffset.UtcNow.AddSeconds(seconds)
                : null;

            return new AuthToken(value, expiresAt);
        }
        catch (JsonException ex)
        {
            throw new HeadlessAuthUnavailableException(
                $"Auth profile '{profile.RelativePath}': the token response was not JSON. {ex.Message}");
        }
    }

    /// <summary>
    /// Shells out to <c>az account get-access-token</c>. The best secretless option on a
    /// runner: Azure's own login action leaves the CLI authenticated with a federated
    /// credential, so nothing has to be stored in the workspace or the pipeline.
    /// </summary>
    private async ValueTask<AuthToken?> AzureCliAsync(AuthFile profile, AuthFieldResolver resolver, CancellationToken ct)
    {
        var flow = (profile.Fields.GetValueOrDefault("flow") ?? "direct").Trim();
        if (flow is not ("direct" or ""))
        {
            return Refuse(profile, $"azure-cli flow '{flow}'", "the 'direct' flow, which only needs a signed-in az");
        }

        var resource = await resolver.AllAsync(
            profile.Fields.GetValueOrDefault("resource") ?? profile.Fields.GetValueOrDefault("scope"), ct)
            .ConfigureAwait(false);

        var arguments = new List<string> { "account", "get-access-token", "--output", "json" };
        if (!string.IsNullOrWhiteSpace(resource))
        {
            arguments.Add("--resource");
            arguments.Add(resource!.EndsWith("/.default", StringComparison.Ordinal) ? resource[..^9] : resource);
        }
        if (await resolver.AllAsync(profile.Fields.GetValueOrDefault("tenantId"), ct).ConfigureAwait(false)
            is { Length: > 0 } tenant)
        {
            arguments.Add("--tenant");
            arguments.Add(tenant);
        }

        var (exitCode, stdout, stderr) = await RunAsync("az", arguments, ct).ConfigureAwait(false);
        if (exitCode != 0)
        {
            throw new HeadlessAuthUnavailableException(
                $"Auth profile '{profile.RelativePath}': `az account get-access-token` failed ({exitCode}). {Trim(stderr)} "
                + "On a runner, sign in first (azure/login with a federated credential).");
        }

        try
        {
            using var document = JsonDocument.Parse(stdout);
            var value = document.RootElement.GetProperty("accessToken").GetString();
            if (string.IsNullOrEmpty(value))
                throw new HeadlessAuthUnavailableException($"Auth profile '{profile.RelativePath}': az returned no accessToken.");

            DateTimeOffset? expiresAt = document.RootElement.TryGetProperty("expires_on", out var expiresOn)
                && expiresOn.TryGetInt64(out var epoch)
                ? DateTimeOffset.FromUnixTimeSeconds(epoch)
                : null;

            return new AuthToken(value!, expiresAt);
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            throw new HeadlessAuthUnavailableException(
                $"Auth profile '{profile.RelativePath}': could not read az's token output. {ex.Message}");
        }
    }

    private static AuthToken? Refuse(AuthFile profile, string what, string instead)
        => throw new HeadlessAuthUnavailableException(
            $"Auth profile '{profile.RelativePath}' uses {what}, which needs someone to complete a sign-in "
            + $"and so cannot run headlessly.\n"
            + $"  Use {instead}, or supply the token directly with --var and reference it from the profile.");

    private static bool Expired(AuthToken token)
        => token.ExpiresAt is { } at && at <= DateTimeOffset.UtcNow.AddSeconds(30);

    private static string Mode(AuthFile profile)
        => (profile.Fields.GetValueOrDefault("mode") ?? "pat").Trim();

    private static string Trim(string value)
    {
        var flat = value.Replace('\n', ' ').Replace('\r', ' ').Trim();
        return flat.Length <= 300 ? flat : flat[..300] + "…";
    }

    private static async Task<(int ExitCode, string Stdout, string Stderr)> RunAsync(
        string file, IReadOnlyList<string> arguments, CancellationToken ct)
    {
        var info = new ProcessStartInfo(file)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);

        try
        {
            using var process = Process.Start(info)
                ?? throw new HeadlessAuthUnavailableException($"Could not start '{file}'.");
            var stdout = process.StandardOutput.ReadToEndAsync(ct);
            var stderr = process.StandardError.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
            return (process.ExitCode, await stdout.ConfigureAwait(false), await stderr.ConfigureAwait(false));
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or FileNotFoundException)
        {
            throw new HeadlessAuthUnavailableException(
                $"'{file}' is not on PATH. Install it, or switch the profile to a grant that doesn't need it.");
        }
    }

    /// <summary>
    /// Builds the source for one run. <paramref name="useCachedTokens"/> is what decides
    /// whether <c>~/.tap/auth-tokens.json</c> — the cache the Studio fills when a user completes
    /// a sign-in — is allowed to satisfy a request.
    /// </summary>
    public static HeadlessAuthTokenSource Create(
        LoadedWorkspace workspace,
        Func<EnvFile?, Tap.Workspace.Variables.VariableProviderRegistry> registryFactory,
        HttpClient http,
        string rootDirectory,
        bool useCachedTokens)
    {
        var cache = useCachedTokens
            ? new AuthTokenStore(
                new AuthTokenStoreOptions { Directory = SystemDirectory() },
                Microsoft.Extensions.Logging.Abstractions.NullLogger<AuthTokenStore>.Instance)
            : null;
        return new HeadlessAuthTokenSource(workspace, registryFactory, http, rootDirectory, cache);
    }

    private static string SystemDirectory()
        => Tap.Execution.Variables.SystemSettingsOptions.DefaultSystemDir();
}
