using Tap.Studio.Contracts;
using YamlDotNet.RepresentationModel;

namespace Tap.Studio.Specs;

/// <summary>
/// Converts an <see cref="AuthSpecDto"/> into the canonical workspace-file string.
/// Field order matches the on-disk convention: <c>kind → id → name → type</c>, then
/// type-specific block, then <c>tags</c>.
/// </summary>
public static class AuthSpecEmitter
{
    public static string ToFileSource(AuthSpecDto spec)
    {
        var fm = new YamlMappingNode();
        fm.Set("kind", "auth");
        fm.Set("id", SpecIds.Ensure(spec.Id));
        fm.Set("name", spec.Name);
        fm.Set("type", spec.Type);

        switch (spec.Type)
        {
            case "basic":
                fm.SetIfNotEmpty("username", spec.Username);
                fm.SetIfNotEmpty("password", spec.Password);
                break;

            case "bearer":
                fm.SetIfNotEmpty("token", spec.Token);
                break;

            case "apiKey":
                fm.SetIfNotEmpty("in", spec.In);
                fm.SetIfNotEmpty("apiKeyName", spec.ApiKeyName);
                fm.SetIfNotEmpty("apiKeyValue", spec.ApiKeyValue);
                break;

            case "oauth2":
                fm.SetIfNotEmpty("flow", spec.Flow);
                fm.SetIfTrue("useDiscovery", spec.UseDiscovery);
                fm.SetIfNotEmpty("authority", spec.Authority);
                fm.SetIfNotEmpty("authorizeUrl", spec.AuthorizeUrl);
                fm.SetIfNotEmpty("tokenUrl", spec.TokenUrl);
                fm.SetIfNotEmpty("deviceAuthorizationUrl", spec.DeviceAuthorizationUrl);
                fm.SetIfNotEmpty("clientId", spec.ClientId);
                fm.SetIfNotEmpty("clientSecret", spec.ClientSecret);
                fm.SetStringList("scopes", spec.Scopes);
                fm.SetIfNotEmpty("audience", spec.Audience);
                // redirectUri is no longer emitted: Studio owns it (derived from its own
                // base URL on each run). Keeping it in the file would freeze a stale port.
                // ROPC bits — only meaningful when flow == password. Keyed under
                // "username"/"password" inside the auth block to match the form the runner reads.
                fm.SetIfNotEmpty("username", spec.OauthUsername);
                fm.SetIfNotEmpty("password", spec.OauthPassword);
                break;

            case "aws-sigv4":
                fm.SetIfNotEmpty("region", spec.Region);
                fm.SetIfNotEmpty("service", spec.Service);
                fm.SetIfNotEmpty("accessKeyId", spec.AccessKeyId);
                fm.SetIfNotEmpty("secretAccessKey", spec.SecretAccessKey);
                fm.SetIfNotEmpty("sessionToken", spec.SessionToken);
                break;

            case "azure-cli":
                // OBO chains an az-cli step (userResource/userScope) with a JWT-bearer
                // exchange (clientId/clientSecret/scopes/audience/tokenUrl). Direct mode
                // uses resource/scope to ask az for a downstream token in one step. We
                // emit only the fields the chosen flow needs, so toggling between flows
                // doesn't leave dead fields behind.
                {
                    var flow = string.IsNullOrEmpty(spec.AzureFlow) ? "direct" : spec.AzureFlow;
                    if (!string.Equals(flow, "direct", StringComparison.Ordinal))
                        fm.SetIfNotEmpty("flow", flow);
                    fm.SetIfNotEmpty("tenant", spec.TenantId);
                    fm.SetIfNotEmpty("subscription", spec.Subscription);

                    if (string.Equals(flow, "on_behalf_of", StringComparison.Ordinal))
                    {
                        fm.SetIfNotEmpty("userResource", spec.UserResource);
                        fm.SetIfNotEmpty("userScope", spec.UserScope);
                        fm.SetIfNotEmpty("tokenUrl", spec.TokenUrl);
                        fm.SetIfNotEmpty("clientId", spec.ClientId);
                        fm.SetIfNotEmpty("clientSecret", spec.ClientSecret);
                        fm.SetStringList("scopes", spec.Scopes);
                        fm.SetIfNotEmpty("audience", spec.Audience);
                    }
                    else
                    {
                        fm.SetIfNotEmpty("resource", spec.Resource);
                        fm.SetIfNotEmpty("scope", spec.Scope);
                    }
                    break;
                }

            case "jwt":
                // No issuer/audience/subject keys: those are claims like any other and live
                // in `payload`. Files still carrying them keep working (the runner reads them),
                // but a save from Studio rewrites them into the payload JSON.
                fm.SetIfNotEmpty("algorithm", spec.JwtAlgorithm);
                fm.SetIfNotEmpty("keyId", spec.JwtKeyId);
                fm.SetIfNotEmpty("key", spec.JwtKey);
                if (spec.JwtExpiresIn is { } secs) fm.Set("expiresIn", secs.ToString());
                fm.SetIfNotEmpty("payload", spec.JwtPayload);
                break;

            case "github":
                // Mirrors azure-cli's pattern: a single `mode:` field selects the sub-flow
                // (pat / gh-cli / app / oauth) and only the fields that flow needs are emitted.
                {
                    var mode = string.IsNullOrEmpty(spec.GithubMode) ? "pat" : spec.GithubMode;
                    fm.SetIfNotEmpty("mode", mode);
                    switch (mode)
                    {
                        case "pat":
                            fm.SetIfNotEmpty("token", spec.Token);
                            break;
                        case "gh-cli":
                            // Nothing to persist — the runner shells out to `gh auth token`.
                            break;
                        case "app":
                            fm.SetIfNotEmpty("appId", spec.GithubAppId);
                            fm.SetIfNotEmpty("installationId", spec.GithubInstallationId);
                            fm.SetIfNotEmpty("privateKey", spec.GithubPrivateKey);
                            break;
                        case "oauth":
                            fm.SetIfNotEmpty("clientId", spec.ClientId);
                            fm.SetIfNotEmpty("clientSecret", spec.ClientSecret);
                            fm.SetStringList("scopes", spec.Scopes);
                            break;
                    }
                    break;
                }

            case "custom":
                fm.SetStringMap("headers", spec.Headers);
                fm.SetStringMap("query", spec.Query);
                break;
        }

        fm.SetStringList("tags", spec.Tags);
        return SpecYaml.ToFrontmatter(fm, spec.Body);
    }
}
