using System.Reflection;

namespace Tap.Tests.Studio;

/// <summary>
/// The container integration's default image tag. This is the one piece of
/// <c>AddTapStudioContainer</c> that can silently strand a consumer: the publish workflow only
/// tags <c>latest</c> for stable releases, so a preview that defaulted to it would fail to pull.
/// </summary>
public class TapStudioContainerTests
{
    /// <summary>Mirrors Aspire.Hosting.TapStudioExtensions.ResolveDefaultImageTag. Tap.Tests does
    /// not reference Tap.Hosting (no test project does), so the rule is pinned here rather than
    /// left entirely unguarded.</summary>
    private static string Resolve(string? informational)
    {
        if (string.IsNullOrWhiteSpace(informational)) return "latest";
        var plus = informational.IndexOf('+');
        var version = plus >= 0 ? informational[..plus] : informational;
        return version.EndsWith("-local", StringComparison.Ordinal) || version.Length == 0
            ? "latest"
            : version;
    }

    /// <summary>A released build names the image published from the same git tag.</summary>
    [Theory]
    [InlineData("0.7.0-p.2", "0.7.0-p.2")]
    [InlineData("0.7.0", "0.7.0")]
    [InlineData("0.7.0-p.2+abc123def", "0.7.0-p.2")]
    [InlineData("0.6.1+deadbeef", "0.6.1")]
    public void A_released_build_pins_the_matching_image_tag(string informational, string expected)
        => Assert.Equal(expected, Resolve(informational));

    /// <summary>A local build names no published image, so there is nothing better than latest.</summary>
    [Theory]
    [InlineData("0.1.0-local")]
    [InlineData("0.1.0-local+abc123")]
    [InlineData("")]
    [InlineData(null)]
    public void A_local_build_falls_back_to_latest(string? informational)
        => Assert.Equal("latest", Resolve(informational));

    /// <summary>The fallback only exists for local builds; a real version must never resolve to
    /// latest, because latest is not published for previews.</summary>
    [Fact]
    public void A_preview_version_never_resolves_to_latest()
        => Assert.NotEqual("latest", Resolve("0.7.0-p.2"));

    /// <summary>Guards the assumption the rule is built on: Directory.Build.props' fallback
    /// version is the "-local" shape the resolver keys off.</summary>
    [Fact]
    public void The_local_fallback_version_still_has_the_shape_the_resolver_expects()
    {
        var props = File.ReadAllText(RepoFile("Directory.Build.props"));
        Assert.Contains("0.1.0-local", props);
    }

    private static string RepoFile(string relative)
    {
        var dir = AppContext.BaseDirectory;
        while (dir is { Length: > 0 } && !File.Exists(Path.Combine(dir, "Tap.slnx")))
            dir = Path.GetDirectoryName(dir);
        Assert.False(string.IsNullOrEmpty(dir), "Could not locate the repository root.");
        return Path.Combine(dir!, relative);
    }
}
