namespace Tap.Studio.OpenApi;

/// <summary>
/// One OpenAPI operation, reduced to exactly what Tap needs to write a request.
///
/// <para><b>This type is the seam.</b> Nothing downstream of the mapper — neither emitter, nor the
/// import planner, nor the re-sync differ, nor the wire DTOs — references a
/// <c>Microsoft.OpenApi</c> type. That keeps the library's object model (which has changed shape
/// twice across major versions) contained in one file, and it means the two output formats and the
/// preview UI all read the same normalized view of an operation.</para>
/// </summary>
public sealed record MappedOperation
{
    /// <summary>Upstream identity, stable across re-syncs. <c>operationId</c> when the document
    /// declares one and it is unique; otherwise <c>"{METHOD} {path}"</c> with <c>{param}</c> braces
    /// intact, so renaming a path parameter reads as the real change it is.</summary>
    public required string OpKey { get; init; }

    /// <summary>As declared. Null is common — plenty of real specs omit it.</summary>
    public string? OperationId { get; init; }

    public required string Method { get; init; }

    /// <summary>Templated, e.g. <c>/pets/{petId}</c>. Braces are Tap's variable syntax too, so the
    /// URL written into a request substitutes <c>{{petId}}</c> at this position.</summary>
    public required string Path { get; init; }

    public string? Summary { get; init; }
    public string? Description { get; init; }
    public IReadOnlyList<string> Tags { get; init; } = [];
    public bool Deprecated { get; init; }

    public IReadOnlyList<MappedParameter> Parameters { get; init; } = [];

    /// <summary>A synthesized example body, or null when the operation takes none.</summary>
    public string? RequestBody { get; init; }
    public string? RequestContentType { get; init; }

    /// <summary>Keys into the document's <c>securitySchemes</c>, operation-level falling back to
    /// the document default.</summary>
    public IReadOnlyList<string> SecurityKeys { get; init; } = [];

    /// <summary>Hash of the normalized operation. Re-sync compares this to decide "did upstream
    /// change", without needing to keep the previous document around.</summary>
    public required string SourceHash { get; init; }

    public IReadOnlyList<string> Warnings { get; init; } = [];

    /// <summary>Path and query parameters that become request <c>vars</c>.</summary>
    public IEnumerable<MappedParameter> VariableParameters
        => Parameters.Where(p => p.In is ParameterIn.Path or ParameterIn.Query);
}

public enum ParameterIn { Path, Query, Header, Cookie }

/// <summary>
/// One operation parameter. The field set is chosen to line up 1:1 with Tap's <c>VarSpec</c>
/// (<c>default</c> / <c>description</c> / <c>required</c> / <c>example</c>), which is why path and
/// query parameters can become declared request variables rather than opaque text in a URL.
/// </summary>
public sealed record MappedParameter(
    string Name,
    ParameterIn In,
    bool Required,
    string? Description,
    string? Example,
    string? TypeHint);
