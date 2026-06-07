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
    public required ResolvedRequestMetadata Metadata { get; init; }
}

public sealed record ResolvedRequestMetadata
{
    public required string SourceRequestPath { get; init; }
    public string? EnvPath { get; init; }
    /// <summary>The stage that was used to resolve baseUrl / vars / headers, or <c>null</c> if no stage applied.</summary>
    public string? StageName { get; init; }

    /// <summary>Variables (including secrets) touched during render — by provider + name +
    /// IsSecret flag only. Never carries the resolved value; history surfaces this so
    /// operators can see what providers were consulted without storing secret material.</summary>
    public required IReadOnlyList<VariableResolution> VariablesUsed { get; init; }
}
