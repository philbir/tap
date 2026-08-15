using LibGit2Sharp;
using Tap.Studio.Contracts;

namespace Tap.Studio.Endpoints;

/// <summary>
/// <c>/api/git/*</c> — git operations against the repository enclosing the active workspace.
/// Returns <c>409</c> when the workspace is not inside a git repo so the UI can hide the tab.
/// Network operations (fetch/pull/push) return the raw <c>git</c> stdout/stderr so the user
/// can see exactly what happened, including credential prompts and merge advice.
/// </summary>
public static class GitEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/git");

        g.MapGet("/", (GitService git) =>
        {
            var status = git.GetStatus();
            return status is null
                ? Results.StatusCode(StatusCodes.Status409Conflict)
                : Results.Ok(status);
        });

        g.MapGet("/changes", (GitService git) =>
        {
            if (git.Root is null) return Results.StatusCode(StatusCodes.Status409Conflict);
            return Results.Ok(git.GetChanges());
        });

        g.MapGet("/branches", (GitService git) =>
        {
            if (git.Root is null) return Results.StatusCode(StatusCodes.Status409Conflict);
            return Results.Ok(git.GetBranches());
        });

        g.MapGet("/diff", (string path, bool? staged, GitService git) =>
        {
            if (git.Root is null) return Results.StatusCode(StatusCodes.Status409Conflict);
            try
            {
                var content = git.Diff(path, staged ?? false);
                return Results.Text(content, "text/plain; charset=utf-8");
            }
            catch (Exception ex)
            {
                return Results.BadRequest(new { code = "git-error", message = ex.Message });
            }
        });

        g.MapPost("/stage", (GitStagePathsDto body, GitService git) => Run(() => git.Stage(body.Paths ?? [])));
        g.MapPost("/unstage", (GitStagePathsDto body, GitService git) => Run(() => git.Unstage(body.Paths ?? [])));
        g.MapPost("/discard", (GitStagePathsDto body, GitService git) => Run(() => git.Discard(body.Paths ?? [])));

        g.MapPost("/commit", (GitCommitRequestDto body, GitService git) =>
        {
            try { return Results.Ok(git.Commit(body.Message ?? string.Empty)); }
            catch (EmptyCommitException ex) { return Results.BadRequest(new { code = "empty-commit", message = ex.Message }); }
            catch (InvalidOperationException ex) { return Results.BadRequest(new { code = "git-error", message = ex.Message }); }
            catch (LibGit2SharpException ex) { return Results.BadRequest(new { code = "git-error", message = ex.Message }); }
        });

        g.MapPost("/branches", (GitCreateBranchDto body, GitService git) =>
        {
            try { git.CreateBranch(body.Name ?? string.Empty, body.Checkout); return Results.NoContent(); }
            catch (InvalidOperationException ex) { return Results.BadRequest(new { code = "git-error", message = ex.Message }); }
            catch (LibGit2SharpException ex) { return Results.BadRequest(new { code = "git-error", message = ex.Message }); }
        });

        g.MapPost("/checkout", (GitCheckoutDto body, GitService git) =>
        {
            try { git.Checkout(body.Name ?? string.Empty); return Results.NoContent(); }
            catch (CheckoutConflictException ex)
            { return Results.BadRequest(new { code = "checkout-conflict", message = ex.Message }); }
            catch (InvalidOperationException ex) { return Results.BadRequest(new { code = "git-error", message = ex.Message }); }
            catch (LibGit2SharpException ex) { return Results.BadRequest(new { code = "git-error", message = ex.Message }); }
        });

        g.MapPost("/fetch", async (GitService git, CancellationToken ct) =>
        {
            try { return Results.Ok(await git.FetchAsync(ct)); }
            catch (InvalidOperationException ex) { return Results.BadRequest(new { code = "git-error", message = ex.Message }); }
        });

        g.MapPost("/pull", async (GitService git, CancellationToken ct) =>
        {
            try { return Results.Ok(await git.PullAsync(ct)); }
            catch (InvalidOperationException ex) { return Results.BadRequest(new { code = "git-error", message = ex.Message }); }
        });

        g.MapPost("/push", async (GitPushRequestDto? body, GitService git, CancellationToken ct) =>
        {
            try { return Results.Ok(await git.PushAsync(body?.SetUpstream ?? true, ct)); }
            catch (InvalidOperationException ex) { return Results.BadRequest(new { code = "git-error", message = ex.Message }); }
        });

        g.MapPost("/init", (GitService git) =>
        {
            try { return Results.Ok(git.InitRepository()); }
            catch (InvalidOperationException ex) { return Results.BadRequest(new { code = "git-error", message = ex.Message }); }
            catch (LibGit2SharpException ex) { return Results.BadRequest(new { code = "git-error", message = ex.Message }); }
        });

        g.MapPost("/remote", (GitSetRemoteDto body, GitService git) =>
        {
            try { return Results.Ok(git.SetRemote(body.Name, body.Url)); }
            catch (InvalidOperationException ex) { return Results.BadRequest(new { code = "git-error", message = ex.Message }); }
            catch (LibGit2SharpException ex) { return Results.BadRequest(new { code = "git-error", message = ex.Message }); }
        });
    }

    private static IResult Run(Action action)
    {
        try { action(); return Results.NoContent(); }
        catch (InvalidOperationException ex) { return Results.BadRequest(new { code = "git-error", message = ex.Message }); }
        catch (LibGit2SharpException ex) { return Results.BadRequest(new { code = "git-error", message = ex.Message }); }
    }
}
