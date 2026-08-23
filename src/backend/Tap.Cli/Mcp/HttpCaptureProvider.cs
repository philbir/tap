using System.Net.Http.Json;
using System.Text.Json;
using Tap.Core.Capture;
using Tap.Inspector.Mcp;

namespace Tap.Cli.Mcp;

/// <summary>
/// Reads captured traffic from a running inspector over HTTP, for the stdio MCP bridge.
///
/// <para>A separate process cannot read another process's memory — and here it must not. The
/// inspector's redacted <c>/api/agent/*</c> surface is the only thing this talks to, so what
/// crosses the process boundary is already projected: this class never holds a raw record, a
/// real header, or a live credential. That is the point. The shortcut of pulling raw
/// <c>/api/requests</c> (which <c>Tap.Cli</c>'s existing project reference would allow) and
/// redacting locally would put live credentials on the wire and make redaction the bridge's
/// job. Redaction happens at the source or it does not happen.</para>
///
/// <para>A consequence worth knowing: fingerprints come from the inspector's salt, so they
/// correlate across everything this bridge serves — but restarting the inspector renumbers
/// them, which is exactly the intended lifetime.</para>
/// </summary>
public sealed class HttpCaptureProvider(HttpClient http) : IMcpCaptureProvider
{
    private static readonly JsonSerializerOptions ReadOptions = new(JsonSerializerDefaults.Web);

    public async Task<CaptureListEnvelope> ListAsync(CaptureQuery query, CancellationToken cancellationToken)
    {
        var url = "api/agent/requests?limit=" + query.Limit
            + Param("host", query.Host)
            + Param("pathGlob", query.PathGlob)
            + Param("method", query.Method)
            + (query.Status is null ? "" : $"&status={query.Status}")
            + (query.OnlyErrors ? "&onlyErrors=true" : "");

        var envelope = await GetAsync<CaptureListEnvelope>(url, cancellationToken);
        return envelope ?? CaptureListEnvelope.For([], 0);
    }

    public async Task<CapturedRequestDetail?> GetAsync(
        string id, CaptureDetailOptions options, CancellationToken cancellationToken)
    {
        var url = $"api/agent/requests/{Uri.EscapeDataString(id)}"
            + $"?includeHeaders={options.IncludeHeaders}"
            + $"&includeBodies={options.IncludeBodies}"
            + $"&includeFrames={options.IncludeFrames}"
            + $"&maxBodyChars={options.MaxBodyChars}"
            + $"&maxFrames={options.MaxFrames}";

        using var response = await http.GetAsync(url, cancellationToken);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();

        var envelope = await response.Content.ReadFromJsonAsync<CaptureDetailEnvelope>(ReadOptions, cancellationToken);
        return envelope?.Request;
    }

    public async Task<CaptureWaitEnvelope> WaitAsync(
        CaptureQuery query, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var url = $"api/agent/wait?timeoutSeconds={(int)timeout.TotalSeconds}"
            + Param("host", query.Host)
            + Param("pathGlob", query.PathGlob)
            + Param("method", query.Method)
            + (query.OnlyErrors ? "&onlyErrors=true" : "");

        // The server holds the connection open for the whole wait, so the client must outlast
        // it — otherwise the bridge times out on a request that was about to succeed.
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout + TimeSpan.FromSeconds(15));

        var envelope = await GetAsync<CaptureWaitEnvelope>(url, deadline.Token);
        return envelope ?? CaptureWaitEnvelope.TimedOut(timeout);
    }

    private async Task<T?> GetAsync<T>(string url, CancellationToken cancellationToken)
    {
        using var response = await http.GetAsync(url, cancellationToken);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            // A 404 on the listing route means agent access is off, not that there is no
            // traffic — the inspector answers these paths either way, precisely so this is
            // distinguishable. Surface the inspector's own hint rather than guessing.
            var hint = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new InvalidOperationException(
                "The inspector refused /api/agent/*. Agent access is off by default: set " +
                "Inspector__Agent__Enabled=true on the inspector, or call .WithAgentAccess() in " +
                $"your AppHost.{Environment.NewLine}{hint}");
        }

        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<T>(ReadOptions, cancellationToken);
    }

    private static string Param(string name, string? value)
        => string.IsNullOrEmpty(value) ? string.Empty : $"&{name}={Uri.EscapeDataString(value)}";
}
