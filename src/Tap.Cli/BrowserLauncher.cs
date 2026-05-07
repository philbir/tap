using System.Diagnostics;

namespace Tap.Cli;

/// <summary>
/// Cross-platform "open this URL in the user's default browser" helper.
/// </summary>
internal static class BrowserLauncher
{
    public static bool TryOpen(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            return true;
        }
        catch { /* fall through */ }

        try
        {
            if (OperatingSystem.IsMacOS())
            {
                Process.Start("open", url);
                return true;
            }
            if (OperatingSystem.IsLinux())
            {
                Process.Start("xdg-open", url);
                return true;
            }
            if (OperatingSystem.IsWindows())
            {
                Process.Start(new ProcessStartInfo("cmd", $"/c start \"\" \"{url}\"") { CreateNoWindow = true });
                return true;
            }
        }
        catch { }

        return false;
    }
}
