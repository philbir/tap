using System.Net.Http.Headers;
using System.Text;
using Tap.Core.Capture;

namespace Tap.Server.Agent;

/// <summary>
/// Re-sends a captured exchange on an agent's behalf.
///
/// <para>The trick worth understanding: the request goes back out through the inspector's own
/// proxy port, carrying the headers as captured. So the original credential is presented to
/// the upstream, the agent that asked for it never sees that credential, and the replay is
/// captured like any other request — which means the agent can read the result straight back
/// with <c>describe_request</c>, redacted. Studio reaches the same "fully-authenticated
/// request, zero credentials in context" property from the workspace side; this is the
/// inspector's route to it.</para>
///
/// <para>That property is also exactly why the destination is not editable. An agent that
/// could choose where a replay goes would be choosing where somebody else's token goes.
/// <see cref="CaptureReplayRequest.Rejection"/> settles that before anything is sent.</para>
/// </summary>
public sealed class CaptureReplayer(IHttpClientFactory httpClientFactory, IRequestStore store, int proxyPort)
{
    /// <summary>How long to wait for the replay's own capture to land before answering without
    /// it. The request has already been sent by then; only the convenience of naming its record
    /// is at stake.</summary>
    private static readonly TimeSpan CaptureWindow = TimeSpan.FromSeconds(5);

    private static readonly string[] HeadersSetByTheReplay = ["Host", "Content-Length", "Content-Type"];

    public async Task<CaptureReplayEnvelope> ReplayAsync(
        RequestRecord record, CaptureReplayRequest request, CancellationToken cancellationToken)
    {
        if (request.Rejection() is { } rejection) return CaptureReplayEnvelope.Refused(rejection);

        if (!HttpMethods.IsGet(record.Method) && !TryMethod(record.Method, out _))
        {
            return CaptureReplayEnvelope.Refused($"'{record.Method}' is not a method this can replay.");
        }

        TryMethod(record.Method, out var method);
        var path = request.Path ?? record.Path;

        using var message = new HttpRequestMessage(method, $"http://localhost:{proxyPort}{path}");

        // The Host header is what the proxy routes on, so it is taken from the captured record
        // and never from the caller — see CaptureReplayRequest.UneditableHeaders.
        message.Headers.Host = record.Host;

        var body = request.Body ?? record.RequestBody;
        var contentType = request.ContentType ?? record.RequestContentType;
        if (!string.IsNullOrEmpty(body))
        {
            message.Content = new StringContent(body, Encoding.UTF8);
            message.Content.Headers.ContentType =
                MediaTypeHeaderValue.TryParse(contentType, out var parsed)
                    ? parsed
                    : new MediaTypeHeaderValue("application/octet-stream");
        }

        ApplyHeaders(message, record, request);

        // Subscribe before sending: the replay's own capture is a race otherwise, and the
        // whole point is to hand back a record the agent can immediately read.
        using var window = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var watching = WatchForCapture(path, window.Token);

        try
        {
            var client = httpClientFactory.CreateClient("replay");
            using var response = await client.SendAsync(
                message, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

            window.CancelAfter(CaptureWindow);
            return CaptureReplayEnvelope.Sent((int)response.StatusCode, await watching);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            await window.CancelAsync();
            return CaptureReplayEnvelope.Refused($"The replay could not be sent: {ex.Message}");
        }
    }

    private static void ApplyHeaders(
        HttpRequestMessage message, RequestRecord record, CaptureReplayRequest request)
    {
        var overrides = request.Headers ?? [];

        foreach (var (name, value) in record.RequestHeaders)
        {
            if (HeadersSetByTheReplay.Contains(name, StringComparer.OrdinalIgnoreCase)) continue;
            if (overrides.ContainsKey(name)) continue;

            Add(message, name, value);
        }

        // Caller edits last, so an explicit override wins over the captured value — including
        // replacing a credential with one the caller supplies, which is a legitimate way to
        // test "is it the token?" without ever learning the original.
        foreach (var (name, value) in overrides)
        {
            if (HeadersSetByTheReplay.Contains(name, StringComparer.OrdinalIgnoreCase)) continue;
            Add(message, name, value);
        }
    }

    private static void Add(HttpRequestMessage message, string name, string value)
    {
        if (!message.Headers.TryAddWithoutValidation(name, value) && message.Content is not null)
        {
            message.Content.Headers.TryAddWithoutValidation(name, value);
        }
    }

    /// <summary>The id of the record the replay produced, or null if it did not land in time.</summary>
    private async Task<string?> WatchForCapture(string path, CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var storeEvent in store.Stream(cancellationToken))
            {
                if (storeEvent is RecordEvent(var record) && record.Path == path) return record.Id.ToString();
            }
        }
        catch (OperationCanceledException)
        {
        }

        return null;
    }

    private static bool TryMethod(string method, out HttpMethod parsed)
    {
        parsed = HttpMethod.Get;
        if (string.IsNullOrWhiteSpace(method)) return false;

        foreach (var c in method)
        {
            if (!char.IsAsciiLetter(c)) return false;
        }

        parsed = new HttpMethod(method.ToUpperInvariant());
        return true;
    }
}
