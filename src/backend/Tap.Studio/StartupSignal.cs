using System.Diagnostics;
using System.Text.Json;

namespace Tap.Studio;

/// <summary>
/// The stdout handshake a desktop shell reads while Studio boots.
///
/// <para>Historically the only line was <c>studio.ready</c>, which meant a boot that stalled
/// before Kestrel bound looked identical to one that had crashed: the Tauri splash sat on
/// "Spawning sidecar" with nothing to say. So every startup step now announces itself, and
/// the shell renders the latest one — a launch that hangs names the phase it hung in
/// (almost always <c>workspace.loading</c>, with the folder it is scanning).</para>
///
/// <para>Gated on <c>TAP_STUDIO_EMIT_READY=1</c> so plain <c>dotnet run</c> / Aspire runs
/// don't get JSON mixed into their log stream. Lines are newline-delimited JSON, one event
/// per line, and every event carries <c>event</c> + <c>elapsedMs</c>:</para>
/// <code>
/// {"event":"studio.progress","phase":"workspace.loading","message":"…","elapsedMs":81}
/// {"event":"studio.error","phase":"workspace","message":"…","elapsedMs":142}
/// {"event":"studio.ready","url":"http://127.0.0.1:54123","pid":42,"elapsedMs":900}
/// </code>
/// The consumer is <c>src/desktop/src-tauri/src/lib.rs</c>; unknown events are logged and
/// ignored there, so adding one is backward compatible.
/// </summary>
internal static class StartupSignal
{
    private static readonly Stopwatch Clock = Stopwatch.StartNew();

    /// <summary>True when a desktop shell is listening for the handshake.</summary>
    public static bool Enabled { get; } =
        string.Equals(Environment.GetEnvironmentVariable("TAP_STUDIO_EMIT_READY"), "1", StringComparison.Ordinal);

    /// <summary>A startup milestone. <paramref name="phase"/> is a stable id;
    /// <paramref name="message"/> is shown to the user verbatim, so write it as a sentence.</summary>
    public static void Progress(string phase, string message)
        => Write(new { @event = "studio.progress", phase, message, elapsedMs = Clock.ElapsedMilliseconds });

    /// <summary>Startup failed in a way we can describe. The shell shows this instead of a
    /// generic timeout, which is the difference between "it hung" and "your workspace folder
    /// no longer exists".</summary>
    public static void Error(string phase, string message)
        => Write(new { @event = "studio.error", phase, message, elapsedMs = Clock.ElapsedMilliseconds });

    /// <summary>Kestrel is listening — the shell navigates the webview here.</summary>
    public static void Ready(string url)
        => Write(new { @event = "studio.ready", url, pid = Environment.ProcessId, elapsedMs = Clock.ElapsedMilliseconds });

    private static void Write(object payload)
    {
        if (!Enabled) return;
        // Declared as object, so STJ serializes the runtime (anonymous) type — the member
        // names above are already the wire names, no naming policy needed.
        Console.Out.WriteLine(JsonSerializer.Serialize(payload));
        Console.Out.Flush();
    }
}
