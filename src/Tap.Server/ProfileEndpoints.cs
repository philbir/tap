using System.Text.Json;
using System.Text.Json.Serialization;
using Tap.Core.Profiles;

namespace Tap.Server;

/// <summary>
/// REST endpoints over <see cref="TunnelProfileStore"/> so the inspector UI can list,
/// edit, and delete the named tunnel profiles consumed by the <c>tap</c> CLI.
/// Bound to the UI port (local control plane), so values are returned as-is — including
/// credentials that already live on disk in plaintext under the profiles directory.
/// </summary>
internal static class ProfileEndpoints
{
    public static void Map(IEndpointRouteBuilder ep)
    {
        var store = new TunnelProfileStore();

        ep.MapGet("/api/profiles", () =>
        {
            var profiles = store.ListAll().ToArray();
            return Results.Json(profiles, TunnelProfileJson.Default.TunnelProfileArray);
        });

        ep.MapGet("/api/profiles/dir",
            () => Results.Json(new ProfileDirInfo(store.RootDirectory), ProfileEndpointsJsonContext.Default.ProfileDirInfo));

        ep.MapGet("/api/profiles/{name}", (string name) =>
        {
            var p = store.Load(name);
            return p is null
                ? Results.NotFound(new { error = $"profile '{name}' not found" })
                : Results.Json(p, TunnelProfileJson.Default.TunnelProfile);
        });

        ep.MapPut("/api/profiles/{name}", async (string name, HttpRequest req) =>
        {
            TunnelProfile? body;
            try
            {
                body = await JsonSerializer.DeserializeAsync(
                    req.Body, TunnelProfileJson.Default.TunnelProfile);
            }
            catch (Exception ex)
            {
                return Results.BadRequest(new { error = $"invalid json: {ex.Message}" });
            }

            if (body is null) return Results.BadRequest(new { error = "request body is required" });

            try
            {
                var profile = new TunnelProfile
                {
                    Name = name,
                    Upstream = body.Upstream,
                    ProxyPort = body.ProxyPort,
                    UiPort = body.UiPort,
                    TunnelMode = body.TunnelMode,
                    Token = body.Token,
                    ApiToken = body.ApiToken,
                    AccountId = body.AccountId,
                    ApiManagedTunnelName = body.ApiManagedTunnelName,
                    DynamicZone = body.DynamicZone,
                    Hostname = body.Hostname,
                    Docker = body.Docker,
                    AutoInstall = body.AutoInstall,
                    AuthHeader = body.AuthHeader,
                    AuthCidrs = body.AuthCidrs,
                    AuthCountries = body.AuthCountries,
                    OidcAuthority = body.OidcAuthority,
                    OidcClientId = body.OidcClientId,
                    OidcClientSecret = body.OidcClientSecret,
                };
                store.Save(profile);
                return Results.Json(profile, TunnelProfileJson.Default.TunnelProfile);
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        ep.MapDelete("/api/profiles/{name}", (string name) =>
        {
            try
            {
                return store.Delete(name) ? Results.NoContent() : Results.NotFound();
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });
    }
}

internal sealed record ProfileDirInfo(string root);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(ProfileDirInfo))]
internal sealed partial class ProfileEndpointsJsonContext : JsonSerializerContext;
