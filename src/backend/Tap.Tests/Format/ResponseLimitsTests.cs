using Tap.Studio.Contracts;
using Tap.Studio.Specs;
using Tap.Workspace.Model;
using Tap.Workspace.Parsing;

namespace Tap.Tests.Format;

/// <summary>
/// <c>response.maxBytes</c> / <c>maxRetainedBytes</c> decide how much of a body reaches the
/// panel and how much stays reachable afterwards, so a size that parses as something other
/// than what the user wrote is a silently wrong cap — the class of bug the field exists to
/// remove. These cover the size grammar, the manifest round-trip, and the clamping rules the
/// executor relies on.
/// </summary>
public class ResponseLimitsTests
{
    [Theory]
    [InlineData("1024", 1024)]
    [InlineData("0", 0)]
    [InlineData("2mb", 2 * 1024 * 1024)]
    [InlineData("2MB", 2 * 1024 * 1024)]
    [InlineData("2 mb", 2 * 1024 * 1024)]
    [InlineData("2m", 2 * 1024 * 1024)]
    [InlineData("2mib", 2 * 1024 * 1024)]
    [InlineData("512kb", 512 * 1024)]
    [InlineData("1gb", 1024L * 1024 * 1024)]
    [InlineData("4096b", 4096)]
    public void Sizes_parse_with_or_without_a_unit(string text, long expected)
    {
        Assert.True(ByteSize.TryParse(text, out var bytes));
        Assert.Equal(expected, bytes);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("-1")]
    [InlineData("2.5mb")]
    [InlineData("lots")]
    [InlineData("mb")]
    [InlineData("9223372036854775807gb")] // overflows rather than wrapping into a tiny cap
    public void Anything_else_is_rejected(string text)
    {
        Assert.False(ByteSize.TryParse(text, out _));
    }

    [Theory]
    [InlineData(2 * 1024 * 1024, "2mb")]
    [InlineData(512 * 1024, "512kb")]
    [InlineData(1024L * 1024 * 1024, "1gb")]
    [InlineData(1_500_000, "1500000")]
    public void Formatting_uses_the_largest_unit_that_divides_exactly(long bytes, string expected)
    {
        Assert.Equal(expected, ByteSize.Format(bytes));
    }

    [Fact]
    public void A_manifest_without_a_response_block_keeps_the_defaults()
    {
        var manifest = ParseManifest("""
            ---
            kind: workspace
            name: demo
            ---
            """);

        Assert.True(manifest.Response.IsEmpty);
        Assert.Equal(ResponseLimits.DefaultMaxBytes, manifest.Response.EffectiveMaxBytes);
        Assert.Equal(ResponseLimits.DefaultMaxRetainedBytes, manifest.Response.EffectiveMaxRetainedBytes);
    }

    [Fact]
    public void Declared_caps_are_read()
    {
        var manifest = ParseManifest("""
            ---
            kind: workspace
            name: demo
            response:
              maxBytes: 8mb
              maxRetainedBytes: 256mb
            ---
            """);

        Assert.Equal(8 * 1024 * 1024, manifest.Response.MaxBytes);
        Assert.Equal(256 * 1024 * 1024, manifest.Response.MaxRetainedBytes);
    }

    [Fact]
    public void A_cap_that_is_not_a_size_is_an_error_rather_than_a_silent_default()
    {
        var ex = Assert.Throws<WorkspaceParseException>(() => ParseManifest("""
            ---
            kind: workspace
            name: demo
            response:
              maxBytes: plenty
            ---
            """));

        Assert.Equal(WorkspaceErrorCode.E_UNKNOWN_FIELD, ex.Error.Code);
        Assert.Contains("response.maxBytes", ex.Error.Message);
    }

    [Fact]
    public void Retaining_less_than_we_send_is_raised_to_what_we_send()
    {
        // Otherwise "show all" would offer less than the panel already displays.
        var limits = new ResponseLimits { MaxBytes = 8 * 1024 * 1024, MaxRetainedBytes = 1024 };
        Assert.Equal(8 * 1024 * 1024, limits.EffectiveMaxBytes);
        Assert.Equal(8 * 1024 * 1024, limits.EffectiveMaxRetainedBytes);
    }

    [Fact]
    public void Caps_are_clamped_to_the_absolute_ceiling()
    {
        var limits = new ResponseLimits { MaxBytes = long.MaxValue, MaxRetainedBytes = long.MaxValue };
        Assert.Equal(ResponseLimits.AbsoluteMaxBytes, limits.EffectiveMaxBytes);
        Assert.Equal(ResponseLimits.AbsoluteMaxBytes, limits.EffectiveMaxRetainedBytes);
    }

    [Fact]
    public void The_block_round_trips_through_the_emitter_in_human_sizes()
    {
        var source = WorkspaceSpecEmitter.ToFileSource(new WorkspaceSpecDto
        {
            Name = "demo",
            Response = new ResponseLimitsDto(8 * 1024 * 1024, 256 * 1024 * 1024),
        });

        Assert.Contains("maxBytes: 8mb", source);
        Assert.Contains("maxRetainedBytes: 256mb", source);

        var reparsed = ParseManifest(source);
        Assert.Equal(8 * 1024 * 1024, reparsed.Response.MaxBytes);
        Assert.Equal(256 * 1024 * 1024, reparsed.Response.MaxRetainedBytes);
    }

    [Fact]
    public void A_workspace_that_sets_no_cap_writes_no_response_block()
    {
        var source = WorkspaceSpecEmitter.ToFileSource(new WorkspaceSpecDto { Name = "demo" });
        Assert.DoesNotContain("response:", source);
    }

    [Fact]
    public void One_cap_on_its_own_is_written_alone()
    {
        var source = WorkspaceSpecEmitter.ToFileSource(new WorkspaceSpecDto
        {
            Name = "demo",
            Response = new ResponseLimitsDto(MaxBytes: null, MaxRetainedBytes: 128 * 1024 * 1024),
        });

        Assert.Contains("maxRetainedBytes: 128mb", source);
        Assert.DoesNotContain("maxBytes: ", source.Replace("maxRetainedBytes: ", string.Empty));
    }

    private static WorkspaceManifestFile ParseManifest(string source)
        => Assert.IsType<WorkspaceManifestFile>(FileParser.Parse("workspace.tap", source));
}
