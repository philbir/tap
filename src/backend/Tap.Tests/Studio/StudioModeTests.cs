using Microsoft.Extensions.Configuration;
using Tap.Studio;

namespace Tap.Tests.Studio;

/// <summary>
/// Aspire mode. The behaviour that matters is a precedence change, and it is invisible unless
/// you know the trap: <c>WorkspaceService</c> normally boots from the *known-workspace list*,
/// not from the configured root. That is right for a tool the user owns and wrong for one an
/// AppHost launched — without the pin, a developer who once opened a different workspace on
/// this machine would get that one instead, and the AppHost's <c>WithWorkspaceFolder</c> would
/// silently do nothing.
/// </summary>
public class StudioModeTests
{
    private static StudioOptions Options(params (string Key, string Value)[] settings)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(settings.Select(s => new KeyValuePair<string, string?>(s.Key, s.Value)))
            .Build();
        return StudioOptions.FromConfiguration(config);
    }

    [Fact]
    public void The_default_mode_is_normal_and_nothing_is_pinned()
    {
        var options = Options();
        Assert.Equal(StudioMode.Normal, options.Mode);
        Assert.False(options.IsWorkspacePinned);
    }

    [Theory]
    [InlineData("aspire")]
    [InlineData("Aspire")]
    [InlineData("ASPIRE")]
    public void Aspire_mode_is_recognized_case_insensitively(string value)
    {
        // The AppHost writes this as an env var (Studio__Mode); casing there is not worth a
        // support ticket.
        var options = Options(("Studio:Mode", value));
        Assert.Equal(StudioMode.Aspire, options.Mode);
        Assert.True(options.IsWorkspacePinned);
    }

    [Fact]
    public void An_unrecognized_mode_falls_back_to_normal_rather_than_failing_startup()
    {
        // A typo in an AppHost should not stop Studio from coming up at all.
        var options = Options(("Studio:Mode", "asprie"));
        Assert.Equal(StudioMode.Normal, options.Mode);
    }

    [Fact]
    public void Aspire_mode_keeps_the_configured_workspace_root()
    {
        var root = Path.Combine(Path.GetTempPath(), "tap-aspire-root");
        var options = Options(("Studio:Mode", "aspire"), ("Studio:WorkspaceRoot", root));

        Assert.True(options.IsWorkspacePinned);
        Assert.Equal(Path.GetFullPath(root), options.WorkspaceRoot);
    }
}
