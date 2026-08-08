using System.Text.Json;
using System.Text.Json.Serialization;
using Tap.Core.Profiles;

namespace Tap.Server;

/// <summary>
/// REST endpoints over <see cref="TunnelProfileStore"/> so the inspector UI can list,
/// edit, and delete the named tunnel profiles consumed by the <c>tap</c> CLI.
///
/// <para>Bound to the UI port (local control plane), but "local" is not an authorization
/// boundary: a browser will happily talk to loopback on behalf of any page, so these
/// responses are treated as if they could reach a hostile reader. Credentials
/// (<see cref="TunnelProfile.SecretFieldNames"/>) are therefore replaced with
/// <see cref="RedactedValue"/> on the way out, and only <c>POST /api/profiles/{name}/reveal</c>
/// returns them in the clear.</para>
/// </summary>
internal static class ProfileEndpoints
{
    /// <summary>
    /// Placeholder substituted for a stored credential. A PUT that echoes it back keeps
    /// whatever is already on disk, so the editor can round-trip a profile it was never shown
    /// the secrets for — without the mask overwriting the real value.
    /// </summary>
    internal const string RedactedValue = "__tap_redacted__";

    public static void Map(IEndpointRouteBuilder ep)
    {
        var store = new TunnelProfileStore();

        ep.MapGet("/api/profiles", () =>
        {
            var profiles = store.ListAll().Select(Redact).ToArray();
            return Results.Json(profiles, TunnelProfileJson.Default.TunnelProfileArray);
        });

        ep.MapGet("/api/profiles/dir",
            () => Results.Json(new ProfileDirInfo(store.RootDirectory), ProfileEndpointsJsonContext.Default.ProfileDirInfo));

        ep.MapGet("/api/profiles/{name}", (string name) =>
        {
            var p = store.Load(name);
            return p is null
                ? Results.NotFound(new { error = $"profile '{name}' not found" })
                : Results.Json(Redact(p), TunnelProfileJson.Default.TunnelProfile);
        });

        // Deliberately POST, not GET. This is the one route that emits cleartext credentials,
        // so it should also be covered by the unsafe-method Origin/Referer check in
        // TapInspectorHost — and never be reachable by a bare <img>/<script>/EventSource.
        ep.MapPost("/api/profiles/{name}/reveal", (string name) =>
        {
            TunnelProfile? p;
            try
            {
                p = store.Load(name);
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }

            return p is null
                ? Results.NotFound(new { error = $"profile '{name}' not found" })
                : Results.Json(
                    new ProfileSecrets(p.Token, p.ApiToken, p.TailscaleAuthKey, p.OidcClientSecret),
                    ProfileEndpointsJsonContext.Default.ProfileSecrets);
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
                // A field left at the redaction placeholder means "unchanged" — resolve it
                // against what is already on disk rather than writing the mask over the secret.
                var existing = store.Load(name);
                var profile = body
                    .WithName(name)
                    .WithSecrets(
                        Unmask(body.Token, existing?.Token),
                        Unmask(body.ApiToken, existing?.ApiToken),
                        Unmask(body.TailscaleAuthKey, existing?.TailscaleAuthKey),
                        Unmask(body.OidcClientSecret, existing?.OidcClientSecret));

                store.Save(profile);
                return Results.Json(Redact(profile), TunnelProfileJson.Default.TunnelProfile);
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

    /// <summary>
    /// Swaps every credential for <see cref="RedactedValue"/>. Absent stays absent, so the UI
    /// can still tell "no token configured" from "a token is set but hidden".
    /// </summary>
    private static TunnelProfile Redact(TunnelProfile p) => p.WithSecrets(
        Mask(p.Token), Mask(p.ApiToken), Mask(p.TailscaleAuthKey), Mask(p.OidcClientSecret));

    private static string? Mask(string? value) => string.IsNullOrEmpty(value) ? value : RedactedValue;

    private static string? Unmask(string? incoming, string? stored) =>
        string.Equals(incoming, RedactedValue, StringComparison.Ordinal) ? stored : incoming;
}

internal sealed record ProfileDirInfo(string root);

internal sealed record ProfileSecrets(
    string? Token, string? ApiToken, string? TailscaleAuthKey, string? OidcClientSecret);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(ProfileDirInfo))]
[JsonSerializable(typeof(ProfileSecrets))]
internal sealed partial class ProfileEndpointsJsonContext : JsonSerializerContext;
