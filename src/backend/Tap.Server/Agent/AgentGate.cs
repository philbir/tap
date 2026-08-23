using System.Net;
using System.Security.Cryptography;
using System.Text;
using Tap.Core.Capture;

namespace Tap.Server.Agent;

/// <summary>
/// The two things every agent-facing request has to satisfy before it reaches a tool or an
/// endpoint: it came from this machine, and it presented this run's token.
///
/// <para><b>Loopback.</b> Checked in code rather than by binding, because the UI port may well
/// be on a wildcard address — <c>WithTap</c> sets <c>Inspector__UiHost=0.0.0.0</c> in container
/// mode, and the inspector UI is something people deliberately reach from elsewhere. The agent
/// surface is not: nothing about it should follow the UI's exposure.</para>
///
/// <para><b>Token.</b> Because loopback is not an authorization boundary. That is already
/// written down in <c>ProfileEndpoints</c> for tunnel credentials, and it is at least as true
/// for captured traffic — without this, any other process running on the machine could read
/// every request your app has served. The token lives in a <c>0600</c> file whose permissions
/// are the actual boundary; see <see cref="AgentBridgeFile"/>.</para>
///
/// <para>Both failures answer 404, not 403. There is nothing useful to tell a caller that got
/// this far wrong, and an inspector should not confirm what it is holding.</para>
/// </summary>
internal sealed class AgentGate(string token)
{
    private readonly byte[] _token = Encoding.UTF8.GetBytes(token);

    public bool Allows(HttpContext context)
        => IsLoopback(context.Connection.RemoteIpAddress) && HasToken(context.Request);

    private bool HasToken(HttpRequest request)
    {
        var presented = request.Headers[AgentBridgeFile.HeaderName].ToString();
        if (string.IsNullOrEmpty(presented)) return false;

        // Fixed-time compare. The window is small and local, but a token that leaks one byte
        // per request to a patient local process is not a token.
        return CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(presented), _token);
    }

    private static bool IsLoopback(IPAddress? address)
    {
        if (address is null) return false;

        // A dual-stack socket reports IPv4 loopback as ::ffff:127.0.0.1, which IsLoopback
        // does not recognise on its own.
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        return IPAddress.IsLoopback(address);
    }

    public static async Task Reject(HttpContext context)
    {
        context.Response.StatusCode = StatusCodes.Status404NotFound;
        context.Response.Headers.CacheControl = "no-store";
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsync(
            CaptureJson.Error(
                "Not found.",
                "The agent surface requires a loopback connection and this inspector run's token. " +
                $"Present it as {AgentBridgeFile.HeaderName}; it is written to " +
                $"{AgentBridgeFile.DefaultRootDirectory}/<uiPort>.json when the inspector starts."));
    }
}
