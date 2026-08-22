using Tap.Studio.Contracts;
using Tap.Studio.Specs;
using Tap.Workspace.Model;
using Tap.Workspace.Parsing;

namespace Tap.Tests.Format;

/// <summary>
/// Every file the Studio writes carries a stable <c>id:</c>. The format has always specified one
/// ("auto-generated as a UUIDv7 on first save if omitted", §3.1) but nothing minted it, which
/// left <c>id:</c> refs theoretical and gave request history nothing durable to key on — a path
/// is not an identity, because renaming the file destroys it.
/// </summary>
public class SpecIdTests
{
    [Fact]
    public void A_spec_saved_without_an_id_gets_one()
    {
        var source = RequestSpecEmitter.ToFileSource(new RequestSpecDto
        {
            Path = "collections/demo/one.req.tap",
            Name = "One",
            Method = "GET",
            Url = "/one",
        });

        var parsed = Assert.IsType<RequestFile>(FileParser.Parse("collections/demo/one.req.tap", source));
        Assert.False(string.IsNullOrWhiteSpace(parsed.Id));
        Assert.True(Guid.TryParse(parsed.Id, out _));
    }

    [Fact]
    public void An_existing_id_is_never_replaced()
    {
        // Replacing one would orphan every id-ref pointing at it — and every history entry
        // recorded under it.
        const string id = "0192aaaa-bbbb-7ccc-8ddd-eeeeffff0000";
        var source = RequestSpecEmitter.ToFileSource(new RequestSpecDto
        {
            Path = "collections/demo/one.req.tap",
            Id = id,
            Name = "One",
            Method = "GET",
            Url = "/one",
        });

        Assert.Equal(id, Assert.IsType<RequestFile>(
            FileParser.Parse("collections/demo/one.req.tap", source)).Id);
    }

    [Fact]
    public void Every_kind_gets_an_id_not_just_requests()
    {
        // Ids are minted in the emitters rather than per endpoint precisely so a kind cannot be
        // quietly left out.
        Assert.Contains("id: ", CollectionSpecEmitter.ToFileSource(new CollectionSpecDto { Slug = "demo", Name = "Demo" }));
        Assert.Contains("id: ", EnvSpecEmitter.ToFileSource(new EnvSpecDto { Path = "environments/dev.env.tap", Name = "dev" }));
        Assert.Contains("id: ", WorkspaceSpecEmitter.ToFileSource(new WorkspaceSpecDto { Name = "demo" }));
        Assert.Contains("id: ", AuthSpecEmitter.ToFileSource(new AuthSpecDto
        {
            Path = "auth/token.auth.tap",
            Name = "token",
            Type = "bearer",
        }));
    }

    [Fact]
    public void Ensure_is_idempotent_so_the_endpoint_and_the_emitter_agree()
    {
        // The endpoint assigns the id so it can echo it back on the response; the emitter assigns
        // it so no kind can be missed. Both call Ensure, and they have to reach the same answer.
        const string id = "0192aaaa-bbbb-7ccc-8ddd-eeeeffff0000";
        Assert.Equal(id, SpecIds.Ensure(id));
        Assert.Equal(id, SpecIds.Ensure(SpecIds.Ensure(id)));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Blank_is_not_an_identity(string? id)
    {
        // An `id: ""` left behind by a hand-edit would otherwise become the folder name every
        // recorded exchange in the workspace shares.
        Assert.False(string.IsNullOrWhiteSpace(SpecIds.Ensure(id)));
    }
}
