using Tap.Workspace.Asserts;
using Tap.Workspace.Model;
using Tap.Workspace.Variables;

namespace Tap.Workspace.Rendering;

/// <summary>
/// A fully-rendered HTTP request ready to be executed. The output of
/// <see cref="WorkspaceRenderer.RenderAsync"/>.
/// </summary>
public sealed record ResolvedRequest
{
    public required string Method { get; init; }
    public required string Url { get; init; }
    public required IReadOnlyDictionary<string, string> Headers { get; init; }
    public string? Body { get; init; }
    /// <summary>Raw bytes to send as the request body — set when the rendered <see cref="Body"/>
    /// was a <c>&lt; ./path</c> file reference and the host resolved the ref to actual file
    /// contents. When non-null, the executor sends these bytes via <c>ByteArrayContent</c>
    /// instead of UTF-8-encoding <see cref="Body"/>. The string <see cref="Body"/> still
    /// holds the ref text so the UI's request-capture view can show what was referenced.</summary>
    public byte[]? BinaryBody { get; init; }
    /// <summary>Wire protocol picked up from the source request. <c>Http</c> = standard
    /// request/response; <c>WebSocket</c> = the executor opens a ws connection at <see cref="Url"/>.</summary>
    public RequestProtocol Protocol { get; init; } = RequestProtocol.Http;
    public RequestTransportSettings Transport { get; init; } = new();

    /// <summary>Whether this exchange gets recorded, and how — the <c>history:</c> block from
    /// the request, its collection, and the manifest, already merged per key. Sits here rather
    /// than in <see cref="Metadata"/> because it is policy the host acts on, not a record of
    /// what the render resolved.</summary>
    public HistoryOptions History { get; init; } = new();

    /// <summary>Assertions to evaluate once the response lands, with their selectors and
    /// expected values already expanded through the same cascade as the request itself.</summary>
    public IReadOnlyList<ResolvedAssert> Assertions { get; init; } = [];

    public required ResolvedRequestMetadata Metadata { get; init; }

    /// <summary>Removes this render's secrets from output meant for callers that must not see
    /// them (agent transcripts, CLI JSON, MCP results). Lives on the request rather than in
    /// <see cref="Metadata"/> because metadata is persisted into execution history and must
    /// stay value-free; the redactor holds values, privately, for exactly as long as the
    /// rendered request itself is alive.</summary>
    public SecretRedactor Redactor { get; init; } = SecretRedactor.None;
}

public sealed record ResolvedRequestMetadata
{
    public required string SourceRequestPath { get; init; }
    public string? EnvPath { get; init; }

    /// <summary>Variables (including secrets) touched during render — by provider + name +
    /// IsSecret flag only. Never carries the resolved value; history surfaces this so
    /// operators can see what providers were consulted without storing secret material.</summary>
    public required IReadOnlyList<VariableResolution> VariablesUsed { get; init; }

    /// <summary>The fully-expanded base URL the request's relative path was joined onto, or
    /// <c>null</c> when the URL was already absolute and no join happened. Callers that only
    /// permit collection-scoped execution (dynamic agent requests) use this to verify the
    /// final URL actually went through the collection's baseUrl.</summary>
    public string? ResolvedBaseUrl { get; init; }
}
