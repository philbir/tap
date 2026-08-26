namespace Tap.Execution.Contracts;

// Result contracts produced by the execution engine and consumed by every front end: the
// Studio serializes them straight onto its SSE stream, the CLI renders them to a console or a
// JUnit report. They live here rather than in either app because a verdict has to mean the
// same thing wherever it is read — which also makes this the engine's public API, so a change
// here is a breaking change to both.

/// <summary>Outcome of one assertion, produced only by the server so a pass/fail shown in
/// the Studio and one computed by a headless runner can never disagree.</summary>
public sealed record AssertResultDto(
    int Index,
    string Name,
    bool Ok,
    bool Skipped,
    string? Actual,
    string? Expected,
    string? Message);

public sealed record AssertSummaryDto(bool Ok, int Passed, int Failed, int Skipped);

/// <summary>One value a step bound into the run bag. <see cref="Error"/> is set when the
/// source matched nothing and the extraction wasn't optional — which fails the step.</summary>
public sealed record ExtractedValueDto(string Var, string? Value, string? Error);

/// <summary>Outcome of one request inside a run. A request-backed test has exactly one of
/// these; a flow-backed one has a step per flow step.</summary>
public sealed record TestStepResultDto(
    int Index,
    string Name,
    string? RequestPath,
    string Method,
    string Url,
    int Status,
    string? StatusText,
    string? ContentType,
    /// <summary>Response body, truncated to <c>TestRunner.BodyPreviewCap</c> — enough to
    /// diagnose a failure without shipping a 2 MiB payload per step down the event stream.</summary>
    string? ResponseBody,
    long ResponseBodyBytes,
    double DurationMs,
    IReadOnlyList<AssertResultDto> Assertions,
    AssertSummaryDto? AssertSummary,
    IReadOnlyList<ExtractedValueDto> Extracted,
    bool Ok,
    bool Skipped,
    /// <summary>Why the step failed, when the failure wasn't an assertion — a render error, a
    /// connection refused. On a <see cref="Skipped"/> step this carries the reason it didn't
    /// run instead ("Skipped.", or which earlier step failed).</summary>
    string? Error);

public sealed record TestEntryResultDto(
    int Index,
    string Name,
    /// <summary><c>request</c> or <c>flow</c>.</summary>
    string TargetKind,
    string? TargetPath,
    IReadOnlyList<TestStepResultDto> Steps,
    bool Ok,
    bool Skipped,
    double DurationMs,
    string? Error);

/// <summary>The skeleton of a run, sent before anything executes so the UI can render every
/// row as pending instead of growing the list one entry at a time.</summary>
public sealed record TestRunPlanEntryDto(int Index, string Name, string TargetKind, string? TargetPath, bool Skip);

public sealed record TestRunStartDto(
    string Path,
    /// <summary><c>test</c> or <c>flow</c> — which kind was run.</summary>
    string Kind,
    string Name,
    string? Env,
    IReadOnlyList<TestRunPlanEntryDto> Entries);

/// <summary>Emitted as each step completes, so a long flow reports progress rather than
/// sitting silent until the end.</summary>
public sealed record TestRunStepEventDto(int EntryIndex, TestStepResultDto Step);

public sealed record TestRunResultDto(
    string Path,
    string Kind,
    string Name,
    IReadOnlyList<TestEntryResultDto> Entries,
    bool Ok,
    int Passed,
    int Failed,
    int Skipped,
    double DurationMs,
    string? Error);

/// <summary><c>POST /api/tests/run</c> — run a test set or a flow. <see cref="Path"/> points at
/// either kind; the server decides from the file. <see cref="Only"/> narrows the run to one
/// entry (by index) so the editor can re-run a single failing test without the rest.</summary>
public sealed record RunTestRequestDto(
    string Path,
    string? Env,
    int? Only,
    IReadOnlyDictionary<string, string>? Overrides,
    /// <summary>Stop at the first failure regardless of the set's own <c>onFailure</c>. A
    /// caller-level override: the file says what the set normally wants, this says what this
    /// particular invocation wants.</summary>
    bool FailFast = false,
    /// <summary>Narrow the run to the tests whose name contains this, case-insensitively.
    /// Applies to a test set's entries; a flow's steps are a chain and can't be filtered
    /// without breaking what a later step depends on.</summary>
    string? Filter = null);

/// <summary>One verdict the chain builder (or the SSL policy) reached, kept split so a UI can
/// name the fault — <c>NotTimeValid</c> — separately from the sentence that explains it.</summary>
public sealed record TlsStatusDto(string Code, string Description);

/// <summary>A single named check with a traffic-light verdict. Computed server-side so both the
/// Studio and any other front end agree on what "trusted" means; <c>State</c> is one of
/// <c>ok</c> / <c>fail</c> / <c>warn</c> / <c>unknown</c>.</summary>
public sealed record TlsCheckDto(string Id, string Label, string State, string? Detail);

/// <summary>One certificate in the chain the server presented. <see cref="Errors"/> is that
/// element's own chain status rather than the whole chain's — which is what lets a report point
/// at the certificate that is actually wrong instead of colouring every card red.</summary>
public sealed record TlsCertificateDto(
    string Subject,
    string Issuer,
    string Thumbprint,
    DateTime NotBefore,
    DateTime NotAfter,
    string SerialNumber,
    /// <summary>Leaf-first index within the presented chain (0 = the server's own certificate).</summary>
    int Index = 0,
    string? CommonName = null,
    IReadOnlyList<string>? DnsNames = null,
    string? SignatureAlgorithm = null,
    string? KeyAlgorithm = null,
    int? KeySizeBits = null,
    bool SelfSigned = false,
    IReadOnlyList<TlsStatusDto>? Errors = null);

public sealed record TlsDiagnosisDto(
    string Url,
    bool Valid,
    string? Error,
    IReadOnlyList<TlsCertificateDto> Certificates,
    IReadOnlyList<TlsStatusDto> Errors,
    string? Host = null,
    int? Port = null,
    /// <summary>Negotiated protocol, already spelled the way people say it ("TLS 1.3").</summary>
    string? Protocol = null,
    string? CipherSuite = null,
    /// <summary>The named checks — hostname, expiry, trust, revocation — in display order.</summary>
    IReadOnlyList<TlsCheckDto>? Checks = null,
    long? HandshakeMs = null);
