using System.Text;
using System.Text.Json;
using Tap.Studio.Auth;
using Tap.Studio.Contracts;

namespace Tap.Studio.Endpoints;

/// <summary>
/// REST surface for the OAuth2 / auth-profile execution flow. The UI calls these to:
///   • inspect an OIDC discovery doc (used to auto-fill token / authorize endpoints),
///   • execute an auth profile (returning tokens directly or a popup loginUrl),
///   • poll the flow's status,
///   • receive the OAuth2 redirect callback and exchange the code for tokens.
///
/// The callback endpoint serves a tiny self-closing HTML page that posts a message to its
/// opener — combined with UI-side polling on <c>/api/auth/flows/{id}</c>, that's the
/// signal that the popup is done.
/// </summary>
public static class AuthFlowEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        // ----- Discovery -----
        // The UI hands us the raw authority field straight off the editor — which may still
        // contain {{var}} / ${{secret}} refs (e.g. `http://{{DEMO_API_URL}}`). Expand them
        // through the same resolver the runner uses so the .well-known fetch hits a real URL.
        app.MapGet("/api/auth/discovery", async (
            string authority, OidcDiscoveryClient client, WorkspaceService ws, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(authority))
                return Results.BadRequest(new { code = "missing-authority", message = "authority query parameter is required." });
            try
            {
                var workspace = ws.Current;
                Tap.Workspace.Model.EnvFile? env = null;
                if (workspace.Manifest?.DefaultEnv is { } defaultRef)
                    env = workspace.Resolve(defaultRef) as Tap.Workspace.Model.EnvFile;
                var resolver = new AuthFieldResolver(workspace, ws.CreateRegistry(), env);
                var resolved = await resolver.AllAsync(authority, ct).ConfigureAwait(false) ?? authority;

                var doc = await client.FetchAsync(resolved, ct).ConfigureAwait(false);
                return Results.Ok(new OidcDiscoveryDto(
                    doc.Issuer, doc.AuthorizationEndpoint, doc.TokenEndpoint,
                    doc.UserInfoEndpoint, doc.JwksUri, doc.EndSessionEndpoint,
                    doc.ScopesSupported, doc.GrantTypesSupported, doc.CodeChallengeMethodsSupported));
            }
            catch (Exception ex)
            {
                return Results.BadRequest(new { code = "discovery-failed", message = ex.Message });
            }
        });

        // ----- Execute -----
        app.MapPost("/api/auth/execute", async (AuthExecuteRequestDto body, AuthRunner runner, CancellationToken ct) =>
        {
            var result = await runner.ExecuteAsync(body.Path, body.ForceReauthenticate, ct).ConfigureAwait(false);
            return Results.Ok(ToDto(result));
        });

        // ----- Callback URI ------------
        // The redirect URI is derived from the current Studio base URL — not user-editable.
        // The UI fetches this to display it (and to remind the user to register it with the
        // identity provider's app registration).
        app.MapGet("/api/auth/callback-uri", (AuthRunner runner) =>
            Results.Ok(new { redirectUri = runner.ResolveCallbackUri() }));

        // ----- Poll flow -----
        app.MapGet("/api/auth/flows/{id}", (string id, AuthFlowStore store) =>
        {
            var f = store.Get(id);
            if (f is null) return Results.NotFound();

            return Results.Ok(new AuthExecuteResponseDto(
                Status: f.Status switch
                {
                    AuthFlowStatus.Pending => "pending",
                    AuthFlowStatus.Completed => "completed",
                    AuthFlowStatus.Failed => "failed",
                    _ => "pending",
                },
                FlowId: f.Id,
                LoginUrl: null,
                AccessToken: f.AccessToken,
                IdToken: f.IdToken,
                RefreshToken: f.RefreshToken,
                TokenType: f.TokenType,
                ExpiresAt: f.ExpiresAt,
                FromCache: false,
                Headers: null,
                Error: f.Error)
            {
                UserCode = f.UserCode,
                VerificationUri = f.VerificationUri,
                VerificationUriComplete = f.VerificationUriComplete,
            });
        });

        // ----- OAuth2 redirect callback -----
        // The identity provider redirects the user's browser here with `code` + `state`.
        // The browser is the popup; we exchange the code, write the result onto the flow,
        // and return a tiny page that messages the opener and closes itself.
        app.MapGet("/api/auth/callback", async (HttpContext ctx, AuthRunner runner, CancellationToken ct) =>
        {
            var q = ctx.Request.Query;
            var code = q["code"].ToString();
            var state = q["state"].ToString();
            var error = q["error"].ToString();
            var errorDesc = q["error_description"].ToString();

            string message;
            bool ok;

            if (!string.IsNullOrEmpty(error))
            {
                ok = false;
                message = string.IsNullOrEmpty(errorDesc) ? error : $"{error}: {errorDesc}";
            }
            else if (string.IsNullOrEmpty(code) || string.IsNullOrEmpty(state))
            {
                ok = false;
                message = "Missing code or state in callback.";
            }
            else
            {
                var result = await runner.CompleteAuthCodeAsync(state, code, ct).ConfigureAwait(false);
                ok = result.Error is null;
                message = ok ? "Authentication complete." : result.Error!;
            }

            ctx.Response.ContentType = "text/html; charset=utf-8";
            // Lock down referrer leakage and inline-script execution. We intentionally allow
            // `'unsafe-inline'` only for our own bootstrap script inside this page — it is the
            // mechanism the popup uses to signal its opener. No external scripts, frames, or
            // remote requests are permitted from this document.
            ctx.Response.Headers["Referrer-Policy"] = "no-referrer";
            ctx.Response.Headers["Content-Security-Policy"] =
                "default-src 'none'; style-src 'unsafe-inline'; script-src 'unsafe-inline'; base-uri 'none'; form-action 'none'";

            // Build the postMessage payload as a JSON value so user-controlled fields (state,
            // error messages from the IdP) can never break out of the script literal. The
            // payload origin is the Studio's own origin: we resolve it from the request and
            // hand it to the script so postMessage targets the parent's origin instead of "*".
            var origin = $"{ctx.Request.Scheme}://{ctx.Request.Host}";
            var payloadJson = JsonSerializer.Serialize(
                new AuthCallbackPayload("tap-auth-callback", ok, state),
                AuthCallbackJson.Default.AuthCallbackPayload);
            var originJson = JsonSerializer.Serialize(origin, AuthCallbackJson.Default.String);
            var safeMsg = System.Net.WebUtility.HtmlEncode(message);

            var html = $$"""
                <!doctype html>
                <html><head><meta charset="utf-8"><title>Tap Studio · Authentication</title>
                <style>body{font:14px system-ui;padding:32px;color:#1e293b}h1{font-size:18px}.{{(ok ? "ok" : "err")}}{color:{{(ok ? "#047857" : "#b91c1c")}}}</style>
                </head><body>
                <h1 class="{{(ok ? "ok" : "err")}}">{{(ok ? "✓ Signed in" : "✗ Authentication failed")}}</h1>
                <p>{{safeMsg}}</p>
                <p>This window will close automatically.</p>
                <script>
                  (function(){
                    var payload = {{payloadJson}};
                    var origin = {{originJson}};
                    try { if (window.opener) window.opener.postMessage(payload, origin); } catch (e) {}
                    setTimeout(function(){ window.close(); }, 800);
                  })();
                </script>
                </body></html>
                """;
            await ctx.Response.WriteAsync(html, Encoding.UTF8, ct).ConfigureAwait(false);
        });
    }

    /// <summary>Shape of the JSON payload posted to the popup opener. Kept as a record so the
    /// source-generated context can serialize it without reflection.</summary>
    internal sealed record AuthCallbackPayload(string type, bool ok, string state);

    private static AuthExecuteResponseDto ToDto(ExecuteAuthResult r)
    {
        var status = r.Error is not null ? "failed"
            : r.Pending ? "pending"
            : "completed";
        return new AuthExecuteResponseDto(
            Status: status,
            FlowId: r.FlowId,
            LoginUrl: r.LoginUrl,
            AccessToken: r.AccessToken,
            IdToken: r.IdToken,
            RefreshToken: r.RefreshToken,
            TokenType: r.TokenType,
            ExpiresAt: r.ExpiresAt,
            FromCache: r.FromCache,
            Headers: r.Headers,
            Error: r.Error)
        {
            UserCode = r.UserCode,
            VerificationUri = r.VerificationUri,
            VerificationUriComplete = r.VerificationUriComplete,
            DeviceCodeExpiresIn = r.DeviceCodeExpiresIn,
        };
    }
}
