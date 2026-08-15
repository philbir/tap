using Tap.Studio.Contracts;

namespace Tap.Studio.Endpoints;

/// <summary>
/// <c>/api/browsers</c> — list the browsers + profiles installed on the host and launch a URL in
/// a chosen one. Powers the OAuth "open sign-in" picker so a token can be acquired under a
/// specific browser/profile (useful for testing auth against different accounts).
/// </summary>
public static class BrowserEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        app.MapGet("/api/browsers", () =>
            Results.Ok(BrowserLauncher.ListAvailableBrowsers()));

        app.MapPost("/api/browsers/open", (OpenBrowserRequestDto body) =>
        {
            if (string.IsNullOrWhiteSpace(body.Url))
                return Results.BadRequest(new WorkspaceErrorDto("invalid-url", "URL is required.", null, null));
            // The string becomes an operand of a real browser binary, so anything that isn't an
            // absolute http(s) URL — a leading-dash Chromium switch, a file:// path, a bare
            // executable path — is refused before it reaches a launcher.
            if (!BrowserLauncher.IsLaunchableUrl(body.Url))
                return Results.BadRequest(new WorkspaceErrorDto(
                    "invalid-url", "Only absolute http:// or https:// URLs can be opened in a browser.", null, null));
            try
            {
                BrowserLauncher.Open(body.Url, body.Browser, body.Profile);
                return Results.NoContent();
            }
            catch (Exception ex)
            {
                return Results.BadRequest(new WorkspaceErrorDto("browser-launch-failed", ex.Message, null, null));
            }
        });
    }
}
