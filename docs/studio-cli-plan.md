# Tap Studio CLI — design proposal

Status: **implemented**. The user-facing docs are in
[studio.md § Running tests from CI](studio.md#running-tests-from-ci) and the package's own
[README](../src/backend/Tap.Studio.Cli/README.md) — read those first. This file is the design
record.

Six things landed differently from the plan below, all noted inline: `--tag` selects flows as
well as test sets and replaces the name argument rather than combining with it (and a selection
matching nothing is an error, not an empty green run); the engine kept the
variable providers (§2), `send` is a one-entry run rather than its own path (§3), the token
cache moved into the engine so both front ends read one file (§4), `--fail-fast` became a
field on the run request rather than a CLI-side rewrite of `onFailure`, and `lint` / `vars`
arrived alongside `test` rather than after it.

Goal: run a workspace's requests and test sets (§10 / §11 of
[workspace-format.md](workspace-format.md)) from a terminal and from CI, shipped as a
`dotnet tool` NuGet package — **separate from the existing `Tap` tunnel/inspector CLI**, which
is a different product and stays as it is.

A run from CI must produce the same verdict as the same run from the Studio UI. That single
requirement drives the whole design: there can only be one execution engine, and it currently
lives in the wrong place.

---

## 1. Where the code is today

| Layer | Project | Reusable headless? |
|---|---|---|
| Parse / render / cascade / assert / extract | `Tap.Workspace` | **Yes** — pure, already packable, no I/O beyond the loader. |
| HTTP send, redirect policy, body capture, TLS | `Tap.Studio` (`internal`) | No — `internal`, and the project is `Sdk.Web`. |
| Auth flows + token cache | `Tap.Studio` | Partly — logic is fine, wiring assumes a browser and an HTTP context. |
| Variable provider registry composition | `Tap.Studio` | Yes, mechanically — needs `ILogger` only. |
| Test / flow runner | `Tap.Studio.Testing.TestRunner` (`internal`) | No — takes `WorkspaceService`. |
| Result contracts | `Tap.Studio.Contracts` | They're wire DTOs, but the shapes are right. |

The blocker is `WorkspaceService`: `TestRunner` needs it, and it drags in a
`FileSystemWatcher`, the known-workspace list, the system settings file, and the OAuth token
store. Referencing `Tap.Studio` from a tool package would also pull in Kestrel, LibGit2Sharp,
the Azure SDK, and a **5.6 MB** React bundle — and force a `yarn` build at pack time. Not
something to install on every CI runner.

**~1,600 lines** actually need to move (`HttpExecutionHelpers`, `HttpTransport`, `AssertRunner`,
`TestRunner`, plus a decoupled render path). The auth code (~1,700 lines) moves too, but only
its non-interactive half matters for CI.

---

## 2. Proposed shape

Three projects instead of two, and one new package:

```
Tap.Workspace      (exists, packable)   parse · render · assert · extract      ← pure
      ▲
Tap.Execution      (NEW, packable)      send · auth · run flows & test sets    ← no web, no UI
      ▲                     ▲
Tap.Studio         (exists)      Tap.Studio.Cli (NEW, dotnet tool)
  HTTP API + React UI              headless commands for terminals and CI
```

- **`Tap.Execution`** — plain `Microsoft.NET.Sdk` class library. Everything needed to turn a
  workspace file into an executed, asserted result. No ASP.NET Core, no Git, no UI.
- **`Tap.Studio.Cli`** — `PackAsTool`, the deliverable. Depends only on `Tap.Execution` +
  `Spectre.Console.Cli` (the same CLI framework the existing `tap` tool uses, so the two feel
  like siblings).
- **`Tap.Studio`** keeps the API, the UI, git, the AI assistant, the interactive auth flows,
  and the workspace watcher — and becomes a *consumer* of the engine rather than its owner.

The engine ends up with no dependency on the app, which is the direction that was wrong before.
It also becomes unit-testable without standing up a server — today `TestRunner` can only be
exercised through HTTP.

### 2.1 The decoupling seam

`WorkspaceService.RenderAsync` does five things; only three of them are Studio's business.
Split it:

```csharp
// Tap.Execution
public interface IWorkspaceHost
{
    LoadedWorkspace Workspace { get; }
    string RootDirectory { get; }
    VariableProviderRegistry CreateRegistry(EnvFile? env);
    IAuthTokenSource Tokens { get; }
}

/// Where a runtime-acquired bearer token comes from. The one thing a CI run and a
/// developer's Studio genuinely disagree about.
public interface IAuthTokenSource
{
    ValueTask<AuthToken?> GetAsync(AuthFile profile, AuthScope scope, CancellationToken ct);
}
```

| Implementation | Used by | Behaviour |
|---|---|---|
| `StudioWorkspaceHost` | Tap.Studio | Today's `WorkspaceService` — watcher, reload, `~/.tap/auth-tokens.json`, interactive flows. |
| `CliWorkspaceHost` | Tap.Studio.Cli | Loads the workspace once, no watcher. Mints tokens for the non-interactive grants only; **refuses interactive ones with an actionable message** rather than hanging a pipeline. |

`TestRunner` then takes an `IWorkspaceHost` and is identical for both callers.

### 2.2 Result contracts

The engine owns the result records (`TestRunResult`, `TestStepResult`, `AssertResult`, …) and
Studio serializes them directly — its `[JsonSerializable]` entries just re-point at the engine
types. The alternative (engine keeps its own types, Studio maps) buys decoupling nobody needs
and costs ~150 lines of mechanical mapping.

The tradeoff to accept knowingly: **the Studio wire format becomes the engine's public API.**
For a pre-1.0 tool that's honest — the UI and a CI report should describe a run the same way —
but it means a breaking wire change is a breaking package change.

---

## 3. Command surface

```
tap-studio test <name|path> [options]     Run a test set or a flow
tap-studio send <name|path> [options]     Send one request, evaluate its assertions
tap-studio lint [options]                 Parse the workspace, report errors, exit non-zero
tap-studio vars [options]                 Print the resolved variable cascade (secrets masked)
```

`<name|path>` resolves in this order — workspace-relative path, then `name:` frontmatter, then
filename stem — so both `tap-studio test tests/demo-smoke.test.md` and
`tap-studio test "Demo API smoke"` work. An ambiguous name lists the candidates and exits 2.

Shared options:

| Option | Notes |
|---|---|
| `-w, --workspace <dir>` | Default: nearest ancestor containing `tap.md`. |
| `-e, --env <name\|path>` | Environment; falls back to the manifest's `defaultEnv`. |
| `-s, --stage <name>` | Collection stage. |
| `--var name=value` | Repeatable. Lands in the run's override tier — the same slot the UI's per-run overrides use, so it beats every file scope. |
| `--var-file <file>` | `.env`-style or JSON. Repeatable; later files win. `--var` beats both. |
| `--tag <tag>` | Run every test set **and flow** carrying the tag. Repeatable; repeats union. Replaces the name argument. |
| `--filter <substring>` | Narrow to matching entries within a set. Refused for a flow, whose steps are a chain. |
| `--fail-fast` | Override `onFailure:` to `stop` for this run. |
| `--timeout <ms>` | Per-request ceiling; overrides `transport.timeoutMs`. |
| `--output <fmt>` | `console` (default) · `junit` · `trx` · `json`. |
| `--output-file <path>` | Where the machine-readable report goes. |
| `--no-color` / `--quiet` / `-v` | Honours `NO_COLOR` and `CI` automatically. |

`--var` is exactly the "name and input variables" case: `tap-studio test "Order API" --var customer=cus_ci --var sku=ABC-1`.

### 3.1 Exit codes

Distinguishing "tests failed" from "couldn't run" is what lets a pipeline tell a red build from
a broken one:

| Code | Meaning |
|---|---|
| 0 | Everything that ran, passed. |
| 1 | At least one test or assertion failed. |
| 2 | Usage error — unknown name, ambiguous name, bad option. |
| 3 | Workspace error — parse failure, dangling ref, no `tap.md`. |
| 4 | Auth could not be acquired headlessly. |
| 130 | Cancelled (Ctrl-C / pipeline timeout). |

### 3.2 Output

Console output is a live tree mirroring the UI's run panel (Spectre), degrading to plain
prefixed lines when `CI` is set or the output isn't a TTY.

`--output junit` is the priority: GitHub Actions, GitLab, Azure DevOps, and Jenkins all ingest
JUnit XML without a plugin. Mapping: one `<testsuite>` per test set, one `<testcase>` per test,
`<failure>` carrying expected/actual and the request line. `trx` follows for Azure DevOps
native reporting; `json` is the raw engine result for anyone scripting.

---

## 4. Auth in CI — the part that actually decides adoption

Today's profile types split three ways:

| Kind | Types | Headless? |
|---|---|---|
| Rendered inline | `bearer`, `basic`, `apiKey`, `custom`, `aws-sigv4`, `github` (PAT) | **Yes, already** — no token store involved. |
| Runtime, non-interactive | `oauth2` (client_credentials, ROPC), `azure-cli`, `github` (gh-cli / App) | **Yes, given a credential** — client secret from a variable provider, or a federated/workload identity on the runner. |
| Interactive | `oauth2` (authorization_code, PKCE, device_code), `github` (oauth) | **No.** |

Design decisions:

1. **No cached-token dependency by default.** A CI run must not silently pass because a
   developer's `~/.tap/auth-tokens.json` happened to be warm. `--use-cached-tokens` opts in for
   local convenience.
2. **Interactive grants fail fast, with the fix in the message** — naming the profile, the
   grant, and the alternatives (switch to `client_credentials` for CI, or inject a token via
   `--var` / an env-backed variable). A pipeline that hangs on an invisible browser prompt is
   the worst possible outcome.
3. **`az` / `gh` are used if present.** Both are on most hosted runners and both support
   federated credentials, which makes them the best secretless path.

Recommended CI pattern, documented with the tool:

```yaml
env:
  TAP_SECRETS_ALLOWED: "DEMO_*_TOKEN,AZURE_*"     # the env provider's allowlist
  DEMO_API_TOKEN: ${{ secrets.DEMO_API_TOKEN }}
run: |
  dotnet tool install --global Tap.Studio.Cli
  tap-studio test "Demo API smoke" --env ci --output junit --output-file results.xml
```

Secrets reach the run through the existing variable providers — `env` (allowlisted) is the
natural CI path, `azkv` works with workload identity. The `file` provider needs its key
material present; 1Password needs `op` and a service account. A short matrix of which providers
are CI-viable belongs in the tool's README.

---

## 5. Packaging

| | Value |
|---|---|
| PackageId | `Tap.Studio.Cli` |
| Tool command | `tap-studio` |
| Also published | `Tap.Execution` (library, for anyone embedding the engine) |
| TFM | `net10.0` |
| Deps | `Tap.Execution`, `Spectre.Console.Cli` |

Deliberately **not** in this package: the React UI, Kestrel, LibGit2Sharp. Launching the Studio
UI stays the desktop app's job. That keeps the install small enough to sit in a `dotnet tool
restore` manifest without anyone minding, and keeps the pack from needing Node.

No collision with the existing `Tap` tool (`tap`): different package, different command; both
can be installed side by side.

Versioning follows the repo's existing `Directory.Build.props` metadata block. `Tap.Execution`
and `Tap.Studio.Cli` version together, since the wire contract is shared (§2.2).

---

## 6. Phases

Each ends somewhere shippable.

**Phase 1 — extract `Tap.Execution`.** ✅ Moved `HttpExecutionHelpers`, `HttpTransport`,
`AssertRunner`, `TestRunner`, `AuthScope`, `AuthFieldResolver`, `AuthTokenStore`,
`WorkspacePaths`, `AtomicStateFile`, the provider registry builder, and every variable provider
— plus the shared result contracts. The render path became `RequestPipeline`, behind
`IWorkspaceHost` + `IAuthTokenSource`; `WorkspaceService` implements the former and delegates,
so the 24 endpoint files that use it were untouched.

The providers moved too, which the plan left open. A CLI that silently supported fewer
providers than the UI would resolve a different value for the same `{{token}}` — the exact
divergence the split exists to prevent. The cost is `Azure.Identity` in the tool package;
still 2.7 MB against the `Tap` tool's 5.6 MB of bundled UI.

**Phase 2 — `tap-studio test`, console output.** ✅ Workspace discovery (walks up to `tap.md`),
three-way name resolution with an ambiguity error, `--var` / `--var-file`, `--only`,
`--fail-fast`, `--list`, and the exit-code contract.

**Phase 3 — CI shape.** ✅ JUnit, TRX, and JSON writers; colour suppressed on `NO_COLOR` or a
redirected stdout; `HeadlessAuthTokenSource` minting client_credentials / ROPC / `az` and
refusing interactive grants with guidance and exit 4.

**Phase 4 — `send`, `lint`, `vars`.** ✅ `send` runs through `TestRunner.SendAsync` — a
one-entry run rather than a second execution path, so a request sent on its own and the same
request sent inside a test set cannot diverge.

**Phase 5 — publish.** ✅ Package README, `.config/dotnet-tools.json`, and
`.github/workflows/workspace-tests.yml` — which runs the sample set on every push and doubles
as the worked example the docs point at. The existing `nuget-publish.yml` packs the solution,
so both new packages ship with no change to it.

---

## 7. Risks and deferred

- **The extraction is the whole risk.** 24 of Studio's files touch `WorkspaceService`. Mitigated
  by keeping `WorkspaceService`'s public surface intact and having it delegate — the endpoints
  shouldn't notice.
- **Wire contract becomes public API** (§2.2) — accepted knowingly.
- **Parallel execution** is deferred. Ordered runs make failures far easier to read, and the
  engine's variable bag is per-run mutable state that would need care.
- **`tap-studio watch`** (re-run on file change) — pleasant locally, pointless in CI.
- **Recorded/replay runs** — running a test set against a captured exchange instead of the
  network. The `ResponseSnapshot` type already makes this possible; it needs a capture format.
- **JUnit fidelity** — flow steps have no natural JUnit nesting. Proposal: one `<testcase>` per
  test, with step detail in the failure text. Revisit if it reads badly in practice.
