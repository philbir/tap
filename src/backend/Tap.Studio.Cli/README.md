# tap-studio

Run [Tap](https://github.com/philbir/tap) workspace requests, flows, and test sets from a
terminal or from CI.

Same engine as Tap Studio's UI, so a verdict here and a verdict there are the same
computation — not two implementations that drift.

```bash
dotnet tool install --global Tap.Studio.Cli
tap-studio test "Demo API smoke"
```

## Commands

| | |
|---|---|
| `tap-studio test <name>` | Run a test set or a flow. |
| `tap-studio send <name>` | Send one request and evaluate its assertions. |
| `tap-studio lint` | Parse the workspace and report what doesn't load. |
| `tap-studio vars` | Print the resolved variable cascade, secrets masked. |

`<name>` is a workspace-relative path, the `name:` from the file's frontmatter, or its
filename stem — whichever you have to hand. `tap-studio test --list` shows what's available.

Select more than one at a time, or less than all of one:

```bash
tap-studio test --tag smoke                  # every test set and flow tagged smoke
tap-studio test --tag smoke --tag graphql    # either tag — repeated tags union
tap-studio test "Order API" --filter refund  # only the tests whose name contains "refund"
```

A selection matching nothing exits 2 rather than reporting a green run over zero tests.

The workspace is found by walking up from the working directory to the nearest `workspace.tap`, the
way `git` finds a repo. `--workspace <dir>` overrides it.

## Input variables

```bash
tap-studio test "Order API" --var customer=cus_ci --var sku=ABC-1
tap-studio test "Order API" --var-file ci.env --var-file overrides.json
```

These land above every file scope, so they beat whatever the environment or the request
declares. Later `--var-file`s win over earlier ones; `--var` beats all of them.

## In CI

```yaml
- run: dotnet tool install --global Tap.Studio.Cli
- run: tap-studio test "Demo API smoke" --env ci --output junit --output-file results.xml
  env:
    TAP_SECRETS_ALLOWED: "DEMO_*_TOKEN"      # the env provider's allowlist
    DEMO_API_TOKEN: ${{ secrets.DEMO_API_TOKEN }}
```

`--output` takes `junit`, `trx`, `json`, or `markdown`. JUnit is read by GitHub Actions,
GitLab, Azure DevOps, and Jenkins without a plugin; `markdown` is for the places a human reads.

```yaml
- run: |
    tap-studio test --tag smoke --output markdown --output-file summary.md
    cat summary.md >> "$GITHUB_STEP_SUMMARY"
```

The Markdown report leads with failures in full, then a table per target. Passing targets
collapse into a `<details>`; failing ones stay open.

### Exit codes

| Code | Meaning |
|---|---|
| 0 | Everything that ran, passed. |
| 1 | A test or assertion failed. |
| 2 | Usage error — unknown or ambiguous name, bad option. |
| 3 | Workspace error — no `workspace.tap`, a file that doesn't parse. |
| 4 | Auth couldn't be acquired without a human. |
| 130 | Cancelled. |

`1` versus everything above it is the distinction that matters: a red build because an API
misbehaved is not the same situation as a red build because the runner couldn't do its job.

### Authentication

Profiles that need a runtime token are handled without a browser:

| Works headlessly | Doesn't |
|---|---|
| `bearer`, `basic`, `apiKey`, `custom`, `aws-sigv4`, `github` (PAT) — built inline by the renderer | `oauth2` authorization_code / PKCE / device_code |
| `oauth2` client_credentials and ROPC | `github` oauth |
| `azure-cli` (`az` signed in — federated credentials work) | |

An interactive grant fails immediately with exit 4 and a message naming the profile and the
alternatives, rather than blocking the job on a sign-in prompt nobody can see.

The developer token cache at `~/.tap/auth-tokens.json` is **not** consulted unless you pass
`--use-cached-tokens`. A CI run that passes because someone's laptop had a warm token is a
test that didn't really run.

## Related packages

- **`Tap.Execution`** — the engine, if you want to embed it.
- **`Tap`** — the separate Tap Tunnels CLI, with inspection built in. Different product and command; both
  can be installed side by side.

Full documentation: [docs/studio.md](https://github.com/philbir/tap/blob/main/docs/studio.md).
