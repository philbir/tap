# The agent surface

Tap Studio treats AI coding agents as first-class users of a workspace — with a harder
constraint than any human user gets: **an agent can run fully-authenticated requests
without ever needing, seeing, or holding a credential.** The workspace owns base URLs,
environments, variables, and auth; the agent gets discovery, execution, and verdicts.

Everything here rides the same engine as the Studio UI and CI (`Tap.Execution`), so a
result an agent reads and a result a human sees in the Testing tab are one computation.

## Contents

- [The trust model](#the-trust-model)
- [Getting set up: `agent init`](#getting-set-up-agent-init)
- [Agent-friendly CLI](#agent-friendly-cli)
- [Dynamic requests: `call`](#dynamic-requests-call)
- [MCP servers](#mcp-servers)
- [Per-collection agent control](#per-collection-agent-control)
- [Skills](#skills)
- [Auth without a human](#auth-without-a-human)

## The trust model

Four rules hold on every agent-facing surface:

1. **Secrets are redacted from every echo.** During render, the engine collects each
   resolved secret value (secret-flagged variables, provider-resolved secrets, minted
   tokens) into a redactor that scrubs the serialized output — including JSON-escaped
   variants, so a token containing a quote can't slip through the encoder. Sensitive
   headers (`Authorization`, `Cookie`, api-key style) are masked by name as
   belt-and-braces.
2. **Discovery never exposes auth internals.** Inventory and describe report an auth
   profile as its name and type only — never its fields, never a token.
3. **Dynamic requests can't exfiltrate credentials.** An ad-hoc request inherits a
   collection's auth, so its URL must stay inside that collection — relative, joined
   onto the baseUrl. Absolute URLs are refused *before anything is sent*, including ones
   smuggled in through a `{{variable}}` that expands after the join. `--allow-any-url`
   exists as an explicit, deliberate opt-out.
4. **Collections can opt out entirely** — see
   [per-collection agent control](#per-collection-agent-control).

This is policy, not a sandbox: it governs what the sanctioned surfaces will do. An agent
with shell access could always run `curl` — the point is that the sanctioned path is
also the easiest one, and it never requires a credential in the agent's context.

## Getting set up: `agent init`

One command wires a project (or a machine) up for an agent environment — it installs the
skills and registers the MCP server in that environment's own config format:

```bash
tap-studio agent init --env claude                       # this project, for Claude Code
tap-studio agent init --env claude --env copilot         # several environments at once
tap-studio agent init --env codex --scope user           # this machine, all projects
tap-studio agent init --env opencode --mcp               # just the MCP registration
tap-studio agent init --env claude --skills              # just the skills
```

| `--env` | Skills go to | MCP registered in |
|---|---|---|
| `claude` | `.claude/skills/` (project) or `~/.claude/skills/` (user) | `.mcp.json` (project); user scope prints the `claude mcp add` command |
| `codex` | `.tap/agent/` + a managed block in `AGENTS.md` (`~/.codex/AGENTS.md` for user) | `.codex/config.toml` (project) or `~/.codex/config.toml` (user) |
| `copilot` | `.tap/agent/` + a managed block in `.github/copilot-instructions.md` | `.vscode/mcp.json` (`servers` key); user scope prints the VS Code step |
| `opencode` | `.tap/agent/` + a managed block in `AGENTS.md` (`~/.config/opencode/AGENTS.md` for user) | `opencode.json` (project) or `~/.config/opencode/opencode.json` (user) |

The skills are embedded in the `tap-studio` tool itself, so re-running `agent init`
after a tool update refreshes them — that is the intended upgrade path. Everything is
idempotent: registrations are added once (an existing `tap-studio` entry is left alone
unless `--force`), and the instructions block is replaced between its markers, never
duplicated. Config files owned by another app (Claude's `~/.claude.json`, Copilot's user
profile) are never edited — the command prints the exact manual step instead.

Project-scope MCP registrations pin `--workspace <relative path>` to the workspace found
from the project root; user-scope ones omit it, so the server resolves whatever
workspace the agent happens to be working in.

## Agent-friendly CLI

The `tap-studio` tool grew a discovery pair and a machine mode:

| Command | Purpose |
|---|---|
| `tap-studio list [requests\|collections\|envs\|tests\|auths]` | What's in the workspace. Keeps working on a partially-broken workspace — parse errors become stderr warnings. |
| `tap-studio describe <request>` | One request's template surface: method, URL template, headers, referenced `{{variables}}`, auth (name + type), stages, assertions. Nothing is rendered, so nothing secret can appear. |
| `tap-studio call <METHOD> <url> -c <collection>` | Ad-hoc request through a collection — see below. |

…and every execution command takes **`--json`**: one parseable, secret-redacted document
on stdout, progress and errors on stderr.

```bash
tap-studio list requests --json
tap-studio describe "GET /demo/methods" --json
tap-studio send "GET /demo/methods" --json
tap-studio test "Demo API smoke" --json
```

The JSON dialect is the same camelCase the Studio API emits, and the exit-code contract
is unchanged: `0` pass · `1` test/assertion failed · `2` usage error · `3` workspace
error · `4` auth needed a human · `130` cancelled.

## Dynamic requests: `call`

`call` sends a request that has no file — but *through* an existing collection, so it
inherits the collection's baseUrl, default headers, variables, and auth:

```bash
tap-studio call GET /users/42 -c demo --json
tap-studio call POST /things -c demo -H 'Content-Type: application/json' -d @body.json --json
tap-studio call GET /demo/auth/whoami -c demo --auth ./whoami-collection-auth.auth.md --json
```

Under the hood the request is synthesized through the same parser as a saved `.req.md`
and executed by the same runner as a test step — a dynamic call and a saved request
cannot behave differently. The URL guard from the trust model applies: relative URLs
only, checked again after variable expansion, unless `--allow-any-url` is passed
explicitly.

A productive agent pattern: `call` an endpoint to learn its real shape, then write the
`.req.md` with assertions pinned to what was observed, then `send` the saved file.

## MCP servers

The same surface is served as Model Context Protocol tools — one shared implementation
(`Tap.Studio.Mcp`), two hosts, identical tool contracts:

**`tap-studio mcp` (stdio).** For MCP clients that launch a process — register it
against a workspace:

```jsonc
// .mcp.json
{
  "mcpServers": {
    "tap-studio": {
      "command": "tap-studio",
      "args": ["mcp", "--workspace", "."]
    }
  }
}
```

The workspace is re-read on every tool call, so files the agent edits between calls are
what the next call runs. Auth is headless (see below); `--use-cached-tokens` opts into
the local token cache.

**The running Studio at `/mcp` (streamable HTTP).** The Studio maps the same tools over
its live workspace — and over the token cache the user fills by signing in through the
browser. This is the host that makes **interactive OAuth (PKCE) work for agents**: the
user clicks through sign-in once in the Studio, and tool calls ride that token without
it ever leaving the Studio process. Loopback-bound alongside the rest of the Studio API.

Five tools on both hosts: `workspace_inventory`, `describe_request`, `send_request`,
`call_request`, `run_test` — each returning the same redacted JSON document its CLI
counterpart prints.

## Per-collection agent control

A collection can fence itself off from agents in its `_collection.md`:

```yaml
agent: false            # shorthand
agent:
  enabled: false        # structured form — the extension point for finer control later
```

…or with the **Agent access** switch on the collection editor's General tab. When
disabled: the collection still appears in discovery (flagged `agentEnabled: false`,
with a truthful request count), but its requests are omitted, and `describe` / `send` /
`call` refuse with `E_AGENT_ACCESS_DISABLED`. The Studio UI, `send`/`test` run by
humans, and curated test-set runs are deliberately unaffected — the option governs
agents, not authors.

## Skills

Two Claude Code skills ship in `.claude/skills/` and are written to be copied into
consumer repos:

- **`tap-studio`** — operating a workspace: the discover → describe → call loop, the
  JSON contract, exit codes, and the auth rules an agent must respect (never read token
  caches or auth-file fields; ask the user on exit 4).
- **`tap-author`** — authoring one: the full per-kind frontmatter spec, the assertion
  and extraction grammar, templates for every file kind, and the write → `lint` →
  `describe` → `send` → `test` loop. Self-contained, so it works in repos that don't
  carry Tap's own docs.

## Auth without a human

Headless surfaces (CLI, stdio MCP) mint what can be minted without a browser:
client-credentials and ROPC OAuth, `az account get-access-token`, `gh auth token`,
PATs, API keys, JWT signing. Interactive flows (PKCE, device code) fail fast with exit
`4` naming the profile — the answers are:

- `--use-cached-tokens` — reuse a token the user minted interactively on this machine
  (off by default: a CI run shouldn't pass on a token somebody minted by hand);
- the Studio's `/mcp` endpoint, where the user's interactive session supplies the token;
- environment-gated variables for CI: `TAP_VARS_ALLOWED` / `TAP_SECRETS_ALLOWED`
  allowlist which process env vars the workspace may read (deny-by-default).
