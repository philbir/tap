using Tap.Studio;

namespace Tap.Tests.Studio;

/// <summary>
/// The Windows white screen, as a unit test.
///
/// <para>Tauri resolves bundled resources relative to <c>current_exe()</c>, which it
/// canonicalizes to defeat symlink attacks — and on Windows that returns a verbatim
/// <c>\\?\</c> path. Verbatim paths reach Win32 without normalization, so a forward slash
/// inside one is an ordinary filename character rather than a separator.</para>
///
/// <para>The failure that produces is deceptively partial. <c>PhysicalFileProvider</c> joins its
/// root to the request's own <c>/</c>-separated subpath, so under a <c>\\?\</c> root a
/// single-segment request like <c>/tap-studio-icon.svg</c> serves fine while every nested one —
/// <c>/assets/index-&lt;hash&gt;.js</c>, the entire SPA bundle — asks for a filename containing a
/// slash and 404s. index.html loads, its script does not, and Studio 0.7.4 shipped a Windows
/// build that opened to an empty window with no way to find out why.</para>
/// </summary>
public class WebRootPathTests
{
    [Fact]
    public void Strips_the_verbatim_prefix_from_a_drive_rooted_path()
    {
        Assert.Equal(
            @"C:\Users\p7e\AppData\Local\Tap Studio\binaries\wwwroot",
            StudioHost.StripVerbatimPrefix(@"\\?\C:\Users\p7e\AppData\Local\Tap Studio\binaries\wwwroot"));
    }

    [Fact]
    public void Leaves_an_ordinary_path_alone()
    {
        const string path = @"C:\Program Files\Tap Studio\binaries\wwwroot";
        Assert.Equal(path, StudioHost.StripVerbatimPrefix(path));
        Assert.Equal("/Applications/Tap Studio.app", StudioHost.StripVerbatimPrefix("/Applications/Tap Studio.app"));
    }

    /// <summary>
    /// A UNC verbatim path is not a usable path with the prefix removed — <c>UNC\server\share</c>
    /// is relative — so it keeps it, and takes the 404s over a path that resolves somewhere else
    /// entirely.
    /// </summary>
    [Fact]
    public void Leaves_a_verbatim_UNC_path_alone()
    {
        const string path = @"\\?\UNC\server\share\wwwroot";
        Assert.Equal(path, StudioHost.StripVerbatimPrefix(path));
    }

    [Fact]
    public void Passes_null_and_empty_through()
    {
        Assert.Null(StudioHost.StripVerbatimPrefix(null));
        Assert.Equal(string.Empty, StudioHost.StripVerbatimPrefix(string.Empty));
    }

    /// <summary>
    /// The prefix is only meaningful at the front. A path that merely contains the character
    /// sequence is left as it is.
    /// </summary>
    [Fact]
    public void Only_strips_a_leading_prefix()
    {
        const string path = @"C:\weird\\?\C:\nested";
        Assert.Equal(path, StudioHost.StripVerbatimPrefix(path));
    }
}
