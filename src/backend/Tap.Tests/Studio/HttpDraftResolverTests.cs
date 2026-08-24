using Tap.Studio;
using Tap.Workspace.Model;
using Tap.Workspace.Rendering;

namespace Tap.Tests.Studio;

/// <summary>
/// Running the text in the editor rather than the text on disk. The interesting cases are all
/// about identity: a <c>.http</c> request is named by its own content, so the edits people make
/// while iterating — adding a request, changing a URL — are exactly the edits that move a
/// request's name. Resolving against the draft is what makes those edits runnable at all.
/// </summary>
public class HttpDraftResolverTests
{
    private const string Path = "collections/demo/orders.http";

    [Fact]
    public void A_request_that_exists_only_in_the_draft_can_be_run()
    {
        // The whole point: nothing here has ever been saved.
        var draft = "### Brand new\nGET /brand-new\n";

        var request = HttpDraftResolver.Resolve($"{Path}#brand-new", draft);

        Assert.Equal("Brand new", request.Name);
        Assert.Equal($"{Path}#brand-new", request.RelativePath);
    }

    [Fact]
    public void The_draft_wins_over_what_is_on_disk()
    {
        // Same fragment, different content: resolution never consults the workspace, so the
        // edited URL is the one that would be sent.
        var draft = "### Get order\nGET /orders/999\n";

        var request = HttpDraftResolver.Resolve($"{Path}#get-order", draft);

        Assert.Equal("/orders/999", HttpBlockParser.Parse(request.HttpBlock).Url);
    }

    [Fact]
    public void One_request_out_of_several_is_picked_by_fragment()
    {
        var draft = "### Get order\nGET /orders/1\n\n### Create order\nPOST /orders\n";

        var request = HttpDraftResolver.Resolve($"{Path}#create-order", draft);

        Assert.Equal("POST", HttpBlockParser.Parse(request.HttpBlock).Method);
    }

    [Fact]
    public void A_single_request_file_answers_to_its_bare_path()
    {
        var request = HttpDraftResolver.Resolve(Path, "GET /orders\n");

        Assert.Equal($"{Path}#get-orders", request.RelativePath);
    }

    [Fact]
    public void A_bare_path_with_several_requests_refuses_to_guess()
    {
        var draft = "### Get order\nGET /orders/1\n\n### Create order\nPOST /orders\n";

        var ex = Assert.Throws<WorkspaceParseException>(() => HttpDraftResolver.Resolve(Path, draft));

        Assert.Equal(WorkspaceErrorCode.E_DANGLING_REF, ex.Error.Code);
        Assert.Contains("holds 2 requests", ex.Error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_fragment_the_edit_renamed_away_reports_the_new_names()
    {
        // Editing `GET /orders` to `GET /customers` re-derives the name, so the list the user
        // clicked is one keystroke stale. Sending the wrong request would be far worse than
        // refusing, and the message has to say what to click instead.
        var ex = Assert.Throws<WorkspaceParseException>(
            () => HttpDraftResolver.Resolve($"{Path}#get-orders", "GET /customers\n"));

        Assert.Equal(WorkspaceErrorCode.E_DANGLING_REF, ex.Error.Code);
        Assert.Contains("#get-customers", ex.Error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_foreign_construct_warns_without_blocking_the_send()
    {
        // An httpyac assertion means the file is shared with another tool. Refusing to run the
        // request next to it would make Tap unusable on the files it exists to open.
        var draft = "### Get order\nGET /orders/1\n\n?? status == 200\n";

        var request = HttpDraftResolver.Resolve($"{Path}#get-order", draft);

        Assert.Equal("Get order", request.Name);
    }

    [Fact]
    public void A_malformed_draft_reports_the_parse_error_rather_than_sending_something_else()
    {
        var ex = Assert.Throws<WorkspaceParseException>(
            () => HttpDraftResolver.Resolve(Path, "### Broken\nnot-a-request-line\n"));

        Assert.NotEqual(WorkspaceErrorSeverity.Warning, ex.Error.Severity);
    }

    [Fact]
    public void Only_http_files_have_a_raw_draft()
    {
        // A .req.tap draft arrives as a spec; routing one through here would silently bypass
        // the emitter that is supposed to validate it.
        var ex = Assert.Throws<WorkspaceParseException>(
            () => HttpDraftResolver.Resolve("collections/demo/get.req.tap", "GET /orders\n"));

        Assert.Equal(WorkspaceErrorCode.E_KIND_MISMATCH, ex.Error.Code);
    }

    [Fact]
    public void The_owning_collection_is_still_reachable_from_a_draft_request()
    {
        // Attribution walks the directory part of the path, so a draft has to keep the file's
        // real path — otherwise baseUrl and inherited auth silently vanish on an unsaved edit.
        var request = HttpDraftResolver.Resolve($"{Path}#get-order", "### Get order\nGET /orders/1\n");

        Assert.Equal("collections/demo", System.IO.Path.GetDirectoryName(request.RelativePath)?.Replace('\\', '/'));
    }
}
