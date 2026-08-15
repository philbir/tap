# Tap Studio Optimization Plan

## Review Scope

Reviewed the new Tap Studio feature across:

- Backend: `src/backend/Tap.Studio`, `src/backend/Tap.Workspace`, new AppHost/sample wiring, and changed Tap hosting/server code.
- UI: `src/ui-studio/src`, Studio API client/types, editor shell patterns, execution/streaming panels, variable tooling.
- Security-sensitive areas: filesystem writes, workspace switching, token storage, OAuth callback, request execution, server binding, external CLI calls, SSRF surfaces.

Verification attempted:

- `dotnet build Tap.slnx -p:SkipTapUiBuild=true` currently fails because Aspire SDK/package versions are inconsistent.
- `yarn --cwd src/ui-studio build` currently fails on a CodeMirror `StreamParser` typing mismatch in `src/ui-studio/src/editors/CodeBlock.tsx`.

## Highest Priority Findings

### P0: Restore Build Consistency

The solution is not buildable as-is.

- `samples/Sample.AppHost/Sample.AppHost.csproj` uses `<Sdk Name="Aspire.AppHost.Sdk" Version="13.3.0" />`.
- `samples/Studio.AppHost/Studio.AppHost.csproj` uses `<Project Sdk="Aspire.AppHost.Sdk/13.4.2">`.
- `Directory.Packages.props` defines `AspireVersion` as `13.3.4`, pins `Aspire.Hosting.JavaScript` to `13.4.2`, and no longer defines `Aspire.Hosting.AppHost`.

Plan:

- Pick one Aspire version for the repo and apply it consistently to SDK declarations and central package versions.
- Restore `Aspire.Hosting.AppHost` in central package management if AppHost projects reference it.
- Prefer one AppHost csproj pattern across `Sample.AppHost` and `Studio.AppHost`.

Acceptance:

- `dotnet restore Tap.slnx` passes.
- `dotnet build Tap.slnx -p:SkipTapUiBuild=true` passes with zero warnings.

### P0: Fix Studio UI Typecheck

`yarn --cwd src/ui-studio build` fails in `src/ui-studio/src/editors/CodeBlock.tsx` because the custom HTTP `StreamLanguage` parser narrows `StringStream.next()` to `string | undefined`, while CodeMirror's type returns `string | void`.

Plan:

- Type the parser against CodeMirror's actual `StringStream` contract or remove the handwritten stream type.
- Add a tiny compile-only guard for the HTTP mode helper if practical.

Acceptance:

- `yarn --cwd src/ui-studio build` passes.

### P0: Bind Studio Locally by Default

`StudioHost.Build` calls `k.ListenAnyIP(options.Port)` when `ASPNETCORE_URLS` is unset, while `StudioOptions.Host` defaults to `localhost` but is not used for binding. This exposes a local developer tool that can execute arbitrary workspace requests, read workspace source, return tokens to the UI, and mutate files.

Plan:

- Bind to loopback by default, using `options.Host` explicitly.
- Require an explicit opt-in for non-loopback binding.
- Add a startup warning when listening on anything other than localhost/loopback.
- Consider a local bearer token or origin guard for browser-facing API calls.

Acceptance:

- Default Studio startup only listens on loopback.
- Non-loopback mode is explicit and documented.

### P0: Centralize Workspace Path Validation

Most folder/move endpoints use `TryResolveWorkspacePath`, but raw source saves call `WorkspaceService.Save(path, content)`, which combines `RootDirectory/.tap` with the caller-provided path without canonical containment checks. `/api/workspace/source` accepts this path from the client.

Plan:

- Move the workspace path resolver into `WorkspaceService` or a reusable `WorkspacePathResolver`.
- Use it for every read/write/delete/move path, including `ReadSource`, `Save`, collection directory writes, and folder deletes.
- Reject absolute paths, empty segments, `.`, `..`, and paths that canonicalize outside `.tap`.
- Keep collection slug validation as a narrower guard, but still route writes through the shared resolver.

Acceptance:

- All filesystem mutations and raw source reads go through one resolver.
- Unit tests cover traversal attempts such as `../x.req.md`, `a/../../x.req.md`, absolute paths, Windows separators, and encoded/normalized variants.

### P1: Harden OAuth Callback Output

`AuthFlowEndpoints` HTML-encodes the visible message, but interpolates `state` directly into JavaScript and uses `postMessage(..., "*")`.

Plan:

- JavaScript-encode serialized callback payloads with `JsonSerializer`, not string interpolation.
- Restrict `postMessage` target origin to the resolved Studio origin where possible.
- Add `Referrer-Policy: no-referrer` and a narrow `Content-Security-Policy` for the callback page.

Acceptance:

- Callback page has no raw query string values inside script.
- Browser callback flow still completes.

### P1: Treat Tokens and Secrets as First-Class Security Data

`AuthTokenStore` persists OAuth/access/refresh tokens in JSON under the system directory. System and workspace variables have masking rules, but token persistence, file permissions, and redaction are spread across several places.

Plan:

- Introduce a `SecretStore` abstraction for auth tokens and system secrets.
- Set restrictive file permissions on Unix and avoid inherited broad ACLs where possible.
- Evaluate OS credential store integration as the preferred backend, with JSON as a fallback.
- Create one redaction helper used by API DTO mapping, logs, errors, and variable traces.

Acceptance:

- Token files are user-private.
- No API response exposes secret values unless the endpoint is explicitly an auth execution result.
- Logs and error paths do not include bearer tokens, refresh tokens, private keys, or client secrets.

## Architecture Improvements

### 1. Separate Studio Host, Domain Services, and Endpoint Adapters

Current endpoint files are doing orchestration, DTO mapping, validation, execution, and response shaping. Keep minimal APIs, but push repeated behavior down into services.

Create:

- `WorkspaceFileService`: load/read/write/move/delete source files safely.
- `RequestExecutionService`: render and execute HTTP/WebSocket requests.
- `GraphQLIntrospectionService`: use the shared execution plumbing with GraphQL-specific request shaping.
- `AuthExecutionService`: keep auth profile execution separate from endpoint DTOs.
- `StudioDtoMapper`: shared conversion for variable traces, workspace errors, summaries, and secret masking.

Acceptance:

- Endpoint classes mostly contain route registration and thin calls to services.
- Shared execution behavior no longer differs between `/api/execute`, `/api/execute/stream`, and `/api/graphql/schema`.

### 2. Consolidate HTTP Execution Plumbing

`ExecuteEndpoint` and `ExecuteStreamEndpoint` duplicate body caps, content header classification, response decoding, request construction, variable trace mapping, and response header collection.

Plan:

- Create a shared `RenderedHttpRequestFactory`.
- Create a shared `ResponseBodyCapture` utility with one body cap policy.
- Create a shared `ContentTypeClassifier`.
- Keep streaming-specific SSE pumping separate, but reuse request construction and final metadata mapping.

Acceptance:

- `IsContentHeader`, `TryDecodeBody`, `BodyCap`, and request header transfer rules exist in one place.
- Unit tests cover binary, image, text, large/truncated, SSE, redirect, and content-header cases.

### 3. Generate or Validate Client Contracts

`src/backend/Tap.Studio/Contracts/Dtos.cs` and `src/ui-studio/src/api/types.ts` are manually kept in lockstep.

Plan:

- Add an OpenAPI or source-generated TypeScript contract step for Studio DTOs.
- If full generation is too much initially, add a contract snapshot test that compares C# JSON shape against TypeScript-facing fixtures.
- Keep source-generated `JsonSerializerContext`; generation should consume those DTOs rather than introduce reflection serialization.

Acceptance:

- Changing a DTO breaks CI unless the UI contract is updated.
- Manual duplicate type drift is reduced or eliminated.

## UI Reuse Improvements

### 1. Extract the Spec Editor Lifecycle

`RequestEditor`, `AuthEditor`, `EnvEditor`, `WorkspaceEditor`, and `CollectionEditor` repeat:

- fetch detail on `generation`
- derive `spec`
- store `savedSpec`
- compute dirty with `JSON.stringify`
- save, discard, error, saving state

Plan:

- Introduce `useSpecEditor<TDetail, TSpec>()`.
- Include stable deep equality, reload integration, parse error mapping, and discard handling.
- Keep editor-specific render code in each component.

Acceptance:

- Each editor owns only kind-specific fields and layout.
- Save/discard/error behavior is consistent across all editors.

### 2. Extract Variable Row Conversion

Variable tables repeatedly convert between `Record<string, string>` plus `secrets: string[]` and `KvRow[]`.

Plan:

- Add helpers like `varSpecsToRows`, `rowsToVarSpecPatch`, and `splitVarSpecs`.
- Use them in request, collection, environment, workspace, and stage editors.

Acceptance:

- Secret flag behavior is identical across all variable tables.
- One test suite covers empty rows, duplicate keys, secret flags, and stable ordering.

### 3. Consolidate Execution State Handling

`RequestEditor` assembles streaming execution snapshots locally from `meta`, `body`, `sse`, `ws`, `done`, and `error` events.

Plan:

- Extract a reducer such as `executionStreamReducer`.
- Keep UI-specific state like active tabs in the component.
- Reuse the reducer for future CLI/history/replay views.

Acceptance:

- Stream event behavior is testable without React.
- SSE and WebSocket frame accumulation has one implementation.

## Security Hardening Plan

### 1. Add Local API Protection

Studio is powerful enough to need browser and network hardening even as a local tool.

Plan:

- Loopback bind by default.
- Add allowed-origin checks for mutating endpoints.
- Consider a generated per-run local token for UI-to-API calls.
- Ensure Vite dev proxy forwards the token during local development if token protection is enabled.

### 2. Add Request Execution Guardrails

Request execution is an intentional API-client feature, but the attack surface should be explicit.

Plan:

- Allow only `http`, `https`, `ws`, and `wss`.
- Consider optional host allow/deny policy for metadata IPs, loopback, private ranges, and link-local addresses.
- Cap redirects and consider stripping sensitive headers on cross-host redirects.
- Make timeout/body/stream caps configurable but bounded by defaults.

### 3. Add Filesystem Safety Tests

Plan:

- Add tests for workspace loading, path resolution, source save/read, move/delete, and collection slug writes.
- Include platform-specific path cases for Windows separators and case sensitivity.

## Consistency Plan

### Package and Project Consistency

- Keep package versions in `Directory.Packages.props`.
- Use one Aspire SDK declaration pattern.
- Keep AppHost project references consistent with Aspire source-generator requirements.
- Decide whether `samples/aspire.config.json` should target `Sample.AppHost` or `Studio.AppHost`; if both are useful, document the root-level `aspire.config.json` versus `samples/aspire.config.json` split.

### Backend Style Consistency

- Keep all JSON DTOs in `Contracts/Dtos.cs` or split by bounded context once generation is in place.
- Every new serialized DTO must be added to `StudioJson`.
- Use one endpoint error shape for workspace parse errors, validation errors, and execution transport errors.
- Prefer dependency-injected `HttpClient` instances over static clients so policies are centrally configured.

### UI Style Consistency

- Keep Mantine form patterns in shared editor primitives where possible.
- Move common labels, method options, body modes, auth type metadata, and header suggestions into shared modules.
- Avoid per-editor copies of dirty/save/source behavior.

## Proposed Sequence

1. Stabilize build and typecheck.
2. Add workspace path resolver and tests.
3. Change Studio default binding to loopback and add startup warnings.
4. Consolidate HTTP execution helpers.
5. Extract `useSpecEditor` and variable row helpers.
6. Add DTO contract generation or snapshot validation.
7. Harden token storage and callback page output.
8. Add CI coverage for `dotnet build`, `src/ui-studio` build, and focused backend/UI tests.

## Definition of Done

- `dotnet build Tap.slnx -p:SkipTapUiBuild=true` passes with warnings as errors.
- `yarn --cwd src/ui-studio build` passes.
- Filesystem traversal tests pass.
- Studio listens on loopback by default.
- Execute/render/GraphQL share request construction and body decoding.
- Editor save/dirty behavior is implemented once and reused.
- DTO drift has automated detection.
- Security-sensitive storage and callback behavior have tests or documented manual verification.
