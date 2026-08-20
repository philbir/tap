import {
  Callout,
  CodeBlock,
  ConfigTable,
  DocList,
  FeatureGrid,
  FlowDiagram,
  MiniPanel,
  ModeGrid,
  ProviderDetail,
  Screenshot,
  SectionHeading,
  type DocSection,
} from "../components/ui";
import { commands } from "../data/commands";
import { studioFeatures } from "../data/features";
import { href } from "../router";
import { repoUrl } from "../site";

const docs: DocSection[] = [
  {
    id: "studio-compose",
    eyebrow: "Compose",
    title: "The request composer",
    body: "One URL bar and eight tabs: params, headers, body, auth, variables, meta, docs, and the generated Markdown source.",
    content: () => (
      <>
        <ConfigTable
          label="Request editor tabs"
          rows={[
            ["Params", "Query string as key/value rows, kept in sync with the URL bar."],
            ["Headers", "Request headers, with completion for the common ones."],
            ["Body", "None, Form, Multipart, Raw, Binary, or GraphQL. Raw bodies get JSON/Text/XML modes and a Format button; multipart handles multiple files."],
            ["Auth", "Which profile applies: inherited from the collection, overridden here, or none."],
            ["Variables", "Request-scoped variables with descriptions, defaults, and required flags."],
            ["Meta", "Name, tags, and protocol — http or websocket."],
            ["Docs", "Markdown documentation, rendered under the request. This is the file's body."],
            ["Source", "The generated *.req.tap, read-only. Studio is the only thing that writes YAML."],
          ]}
        />
        <ModeGrid
          gap
          items={[
            ["Response panel", "Status, duration, size, and content type, with tabs for the body, headers, the exact request sent, the auth and variable flow, and the secrets resolved."],
            ["Streaming", "text/event-stream responses stream into a live timeline; protocol: websocket opens a real socket and appends frames as they arrive."],
            ["GraphQL", "The GraphQL body mode fetches the endpoint schema, so queries, mutations, and variables get completion and validation against the live server."],
            ["Base URL and stages", "A request stores a path; the collection contributes the base URL, and a named stage can override it for dev, uat, or prod."],
          ]}
        />
      </>
    ),
  },
  {
    id: "studio-auth",
    eyebrow: "Identity",
    title: "Authentication flows",
    body: "Auth profiles are reusable files. Start from a template, fill only the fields that flow needs, and prove it works before wiring it to a request.",
    content: () => (
      <>
        <Screenshot
          src="./screenshots/studio-auth-wizard.png"
          alt="Tap Studio's auth template catalogue"
          caption="Creating an auth profile starts from a template catalogue; the wizard then asks only for the fields that flow actually needs."
        />
        <ConfigTable
          label="Authentication templates"
          rows={[
            ["GitHub PAT", "Classic or fine-grained personal access token."],
            ["GitHub CLI", "Reuses whichever account gh auth login is signed in as."],
            ["GitHub App", "Mints an RS256 JWT from the App key, exchanges for an installation token."],
            ["GitHub OAuth App", "Interactive sign-in for tools acting as a user."],
            ["OAuth 2.0 / OIDC", "Authorization code with PKCE, client credentials, ROPC, or device code — endpoints by hand or via .well-known discovery."],
            ["Microsoft Entra", "Pre-filled AAD v2 authority; supply tenant and client id."],
            ["Azure CLI", "az account get-access-token, direct or on-behalf-of."],
            ["AWS Signature V4", "Signs requests with AWS access keys."],
            ["Signed JWT", "Mints a self-signed JWT per call for service-to-service patterns."],
            ["Bearer / Basic / API key", "Static credentials, referenced from a variable provider."],
            ["Custom headers", "Free-form header and cookie injection."],
          ]}
        />
        <Screenshot
          src="./screenshots/studio-auth-oauth2.png"
          alt="An OAuth 2.0 authorization code with PKCE profile in Tap Studio"
          caption="Turn on discovery and Studio fetches /.well-known/openid-configuration to fill in the endpoints. Try it runs the flow on its own, so you can prove a profile works before wiring it to a request."
        />
        <ModeGrid
          items={[
            ["Tokens stay out of the repo", "Access tokens, refresh tokens, and expiry live in your OS state folder, keyed by workspace and profile, tightened to user-only permissions. Refresh is automatic."],
            ["The redirect URI is runtime-owned", "Studio derives it from its own base URL and shows it read-only. The desktop app registers the stable tap-studio:// deep link instead of a random loopback port."],
            ["Pick your browser", "Interactive flows let you choose which installed browser and profile handles the sign-in, so a work tenant doesn't land in your personal session."],
            ["Profiles are inheritable", "Set a default auth on the collection and override it per request — or opt out entirely with auth: none."],
          ]}
        />
        <CodeBlock title="auth/corp-entra.auth.tap" code={commands.studioAuth} />
        <p className="doc-note">
          Every template above is in the box. Corporate identity is not an upgrade here — Entra,
          Azure CLI on-behalf-of, GitHub App installation tokens, and AWS SigV4 ship in the same
          free build as bearer tokens.
        </p>
      </>
    ),
  },
  {
    id: "studio-testing",
    eyebrow: "Testing",
    title: "Flows and test sets",
    body: "Assertions answer whether one response looked right. Flows and test sets answer the two questions above that: does this multi-step exchange still work end to end, and do these requests still pass?",
    content: () => (
      <>
        <Screenshot
          src="./screenshots/studio-testing.png"
          alt="The Tap Studio Testing tab running a test set, with results streaming below"
          caption="A test set's entries above, the run below. Results stream in as they land — and the last entry here runs a whole flow, expanded to its steps, the request each one sent, and every assertion verdict."
        />
        <ConfigTable
          label="Testing file kinds"
          rows={[
            ["*.flow.tap", "A flow: an ordered list of steps. Each step names an existing request, may override its variables, and may extract values out of its response for the steps below."],
            ["*.test.tap", "A test set: variables that apply to the whole run, plus a list of tests that each run one request or one whole flow, with extra assertions layered on the target's own."],
          ]}
        />
        <div className="code-grid section-gap">
          <CodeBlock title="tests/checkout.flow.tap" code={commands.studioFlow} />
          <CodeBlock title="tests/order-api.test.tap" code={commands.studioTestSet} />
        </div>
        <Callout title="Step one binds it, step two reads it">
          That is the whole mechanism. Extract from a JSONPath, an XPath, a header, the status, the
          duration, the whole body, or a regex capture group — and in the editor the row reads left
          to right as the sentence it is: variable, arrow, source. A value that doesn't turn up fails
          the step rather than quietly binding nothing, because the next request is about to send an
          unexpanded token and saying so here beats a strange URL two steps later. Mark it optional
          with a default when it genuinely is.
        </Callout>
        <ModeGrid
          gap
          items={[
            ["Neither request knows it is in a flow", "Steps point at the same files the Requests tab sends, carrying the same assertions. The flow only supplies variables and carries values across. A failed step stops the flow, because everything after it would run against a state that never happened."],
            ["Set variables are the last word", "A test set's variables sit above the environment and above the request's own, which is what lets one set pin an identity for every check inside it. Values bound by extract beat even those — a flow whose id could be overridden by its caller is no longer a flow."],
            ["Keep going, or stop the set", "By default a failing test doesn't stop the others; one broken endpoint shouldn't hide the state of the rest. Switch onFailure to stop for entries that build on each other."],
            ["A flow is a test target", "A test entry runs either a request or a whole flow. Assertions on a flow entry check the last step's response — the one a caller of the flow actually sees."],
            ["Results stream as they land", "A ten-entry set against a slow API reports progress instead of appearing all at once. Each row expands to the request that ran, every assertion verdict, the values a step bound, and the response body. Failures open themselves, and one test can be re-run from its own row."],
            ["Runs read what is on disk", "Run is disabled while there are unsaved edits — the alternative is a green result for a file that doesn't exist yet. Nothing is persisted either: a run annotates the screen, and an extracted value lives only for the length of it."],
          ]}
        />
        <p className="doc-note">
          <a
            className="text-link"
            href="https://github.com/philbir/tap/blob/main/docs/studio.md#tests-and-flows"
          >
            Read the testing guide
          </a>
        </p>
      </>
    ),
  },
  {
    id: "studio-cli",
    eyebrow: "Pipelines",
    title: "The tap-studio CLI",
    body: "The same runs, headless, through a .NET tool. It carries the execution engine and nothing that serves a UI, so it stays small enough to install on every runner.",
    content: () => (
      <>
        <CodeBlock title="Install and run" code={commands.studioCliInstall} />
        <ConfigTable
          gap
          label="tap-studio commands"
          rows={[
            ["tap-studio test", "Run a test set or a flow."],
            ["tap-studio send", "Send one request and evaluate its assertions."],
            ["tap-studio lint", "Parse the workspace and report what doesn't load."],
            ["tap-studio vars", "Print the resolved variable cascade, secrets masked."],
            ["tap-studio migrate", "Convert a pre-0.7 .md workspace to .tap, renaming files and rewriting refs."],
          ]}
        />
        <Callout title="It is the same engine">
          The Studio's API and the CLI both call Tap.Execution, so a verdict from a pipeline and a
          verdict from the Testing tab are the same computation over the same files — not two
          implementations that drift apart. tap-studio is a separate package from the tap tunnel CLI:
          different product, different command, both installable side by side.
        </Callout>
        <ModeGrid
          gap
          items={[
            ["Name it the way you read it", "A target is a workspace-relative path, the name from the file's frontmatter, or its filename stem — so the thing on the Testing tab is the thing you can type. An ambiguous name lists the candidates instead of guessing."],
            ["The workspace finds itself", "Discovery walks up from the working directory to the nearest workspace.tap, the way git finds a repo. Pass --workspace to override it."],
            ["A selection that matches nothing is an error", "A misspelled --tag that quietly ran zero tests would leave a pipeline passing forever with nothing in the output to notice it by. So an unmatched tag lists the tags that do exist, and exits 2."],
            ["Input variables win", "--var and --var-file land in the same tier the UI's per-run overrides use, above every file scope. Later files beat earlier ones, and --var beats all of them."],
          ]}
        />
        <div className="code-grid section-gap">
          <CodeBlock title="Selecting what runs" code={commands.studioCliSelect} />
          <CodeBlock title="Environment, stage, and input variables" code={commands.studioCliVars} />
        </div>
        <CodeBlock title=".github/workflows/api-tests.yml" code={commands.studioCliCi} />
        <ConfigTable
          gap
          label="Report formats"
          rows={[
            ["--output junit", "Ingested by GitHub Actions, GitLab, Azure DevOps, and Jenkins without a plugin, so a failed assertion is a failed test in the UI rather than a line in a log. A tagged run writes one testsuite per target."],
            ["--output trx", "Azure DevOps' native reporting. Several targets merge into one TestRun."],
            ["--output json", "Scripting. Always an envelope — ok, passed, failed, skipped, durationMs, runs — whatever the target count, so nothing has to branch on how the run was selected."],
            ["--output markdown", "The places a human reads. Verdict first, then failures in full, then a table per target; passing targets collapse into a details block and failing ones stay open."],
          ]}
        />
        <ConfigTable
          gap
          label="Exit codes"
          rows={[
            ["0", "Everything that ran, passed."],
            ["1", "A test or assertion failed."],
            ["2", "Usage error — unknown or ambiguous name, bad option, a selection matching nothing."],
            ["3", "Workspace error — no workspace.tap, or a file that doesn't parse."],
            ["4", "Auth couldn't be acquired without a human."],
            ["130", "Cancelled."],
          ]}
        />
        <p className="doc-note">
          One versus everything above it is the distinction worth having: a red build because the API
          misbehaved is a different situation from a red build because the runner couldn't do its
          job, and a single exit code for both makes them indistinguishable from a dashboard.
        </p>
        <Callout title="Auth that refuses rather than hangs">
          Bearer, basic, API key, custom, AWS SigV4, GitHub PAT, OAuth 2.0 client credentials and
          ROPC, and Azure CLI all mint without a human — federated credentials included. An
          interactive grant fails immediately with exit 4, naming the profile and the alternatives,
          instead of blocking the job on a sign-in prompt nobody can see. Secrets reach the run
          through the same variable providers the UI uses; the env provider is the natural CI path,
          deny-by-default behind TAP_SECRETS_ALLOWED. The developer token cache is ignored unless you
          pass --use-cached-tokens, because a run that passes on a warm token from someone's laptop
          is a test that didn't really run.
        </Callout>
        <p className="doc-note">
          <a
            className="text-link"
            href="https://github.com/philbir/tap/blob/main/docs/studio.md#running-tests-from-ci"
          >
            Read the CI guide
          </a>
        </p>
      </>
    ),
  },
  {
    id: "studio-agents",
    eyebrow: "Agents",
    title: "Agents and MCP",
    body: "An AI coding agent can run fully-authenticated requests against your APIs without ever needing, seeing, or holding a credential. The workspace owns the auth; the agent gets discovery, execution, and verdicts.",
    content: () => (
      <>
        <CodeBlock title="Wire up an agent environment" code={commands.studioAgentInit} />
        <ConfigTable
          gap
          label="Agent-facing commands"
          rows={[
            ["tap-studio list [kind]", "What's in the workspace — requests, collections, envs, tests, auths."],
            ["tap-studio describe <request>", "One request's template surface: method, URL template, headers, referenced variables, auth as name and type only."],
            ["tap-studio call <METHOD> <url> -c <collection>", "An ad-hoc request through an existing collection, inheriting its baseUrl, headers, variables, and auth."],
            ["--json", "On every execution command: one parseable, secret-redacted document on stdout, progress and errors on stderr."],
            ["tap-studio mcp", "The same surface as MCP tools over stdio, for clients that launch a process."],
          ]}
        />
        <ModeGrid
          gap
          items={[
            ["Secrets are redacted from every echo", "During render the engine collects each resolved secret — secret-flagged variables, provider values, minted tokens — into a redactor that scrubs the serialized output, JSON-escaped variants included. Sensitive headers are masked by name as belt-and-braces."],
            ["Dynamic requests can't exfiltrate", "An ad-hoc call inherits a collection's auth, so its URL must stay inside that collection: relative, joined onto the baseUrl. Absolute URLs are refused before anything is sent — including ones smuggled in through a variable that expands after the join."],
            ["Collections can opt out", "Set agent: false in _collection.tap, or flip the Agent access switch in the collection editor. The collection still appears in discovery with a truthful request count, but its requests are omitted and calls refuse with E_AGENT_ACCESS_DISABLED."],
            ["Two MCP hosts, one contract", "tap-studio mcp serves the tools over stdio with headless auth. The running Studio maps the same five tools at /mcp over its live workspace — and over the token cache you filled by signing in, which is what makes interactive OAuth work for an agent without a credential leaving the process."],
          ]}
        />
        <div className="code-grid section-gap">
          <CodeBlock title=".mcp.json" code={commands.studioMcpConfig} />
          <CodeBlock title="The discover → describe → call loop" code={commands.studioAgentLoop} />
        </div>
        <Callout title="Skills ship with the tool">
          Two skills are embedded in `tap-studio` itself and written to be copied into consumer
          repos: one for operating a workspace, one for authoring it — the full per-kind frontmatter
          spec, the assertion grammar, and the write → lint → describe → send → test loop.
          Re-running `agent init` after a tool update refreshes them.
        </Callout>
        <p className="doc-note">
          <a className="text-link" href="https://github.com/philbir/tap/blob/main/docs/agent-surface.md">
            Read the agent surface guide
          </a>
        </p>
      </>
    ),
  },
  {
    id: "studio-ai",
    eyebrow: "Assist",
    title: "AI assistance",
    body: "Studio spawns an AI coding CLI you already have installed, hands it the real shape of your workspace, and applies what it proposes as an unsaved edit.",
    content: () => (
      <>
        <Screenshot
          src="./screenshots/studio-assistant.png"
          alt="The Tap Studio AI assistant proposing an edit to a request"
          caption="The assistant proposes; you apply. Its edit lands in the editor as an unsaved change you review and save — or discard."
        />
        <ModeGrid
          items={[
            ["Your CLI, your login", "Studio spawns GitHub Copilot CLI or Claude Code from your machine per request instead of bundling an SDK, so there is no extra native dependency, no second set of credentials, and no AI add-on to buy from us."],
            ["It sees the workspace", "The prompt carries the current request, the collection's base URL, default auth and shared headers, the available auth profiles, the environment names, and the variable catalogue with scopes and secret flags."],
            ["It proposes, you apply", "The assistant never writes files. It returns a structured request that the UI applies as an unsaved edit; re-apply is one click."],
            ["It documents as it goes", "New or meaningfully changed requests come back with Markdown documentation, and it refines existing notes rather than overwriting them."],
            ["No inlined secrets", "Anything sensitive has to be a {{variable}} reference — the assistant is told never to hardcode a token or key."],
            ["Model choice", "Pick the provider and model in Settings; the status endpoint reports what was detected and what still needs setup."],
          ]}
        />
      </>
    ),
  },
  {
    id: "studio-workspace",
    eyebrow: "Format",
    title: "The workspace is your repo",
    body: "Seven file kinds, all Markdown with YAML frontmatter. A runnable request is the composition of workspace, collection, stage, auth, environment, and request.",
    content: () => (
      <>
        <Screenshot
          src="./screenshots/studio-git.png"
          alt="A request edit shown as an ordinary git diff inside Tap Studio"
          caption="Because a request is a couple of lines of Markdown, review, blame, cherry-pick, and revert all work the way they do for code. Branch, stage, diff, and commit without leaving the app."
        />
        <ConfigTable
          label="Workspace file kinds"
          rows={[
            ["workspace.tap", "The workspace: name, default environment, variable providers, workspace-wide variables."],
            ["_collection.tap", "A collection: base URL, named stages, default auth, default headers, collection variables."],
            ["*.req.tap", "One request, as a fenced http block plus Markdown documentation."],
            ["*.auth.tap", "A reusable authentication profile."],
            ["*.env.tap", "A named environment: a set of variables and secret references."],
            ["*.flow.tap", "A flow: requests in order, with values carried from one response into the next."],
            ["*.test.tap", "A test set: run-wide variables plus a list of tests, each running a request or a flow."],
          ]}
        />
        <Callout title="A runnable request is a composition">
          Workspace + collection (+ stage) + auth + environment + request. No single file is the
          whole story, and sub-folders inside a collection are pure grouping — they carry no metadata
          and no inheritance.
        </Callout>
        <div className="code-grid section-gap">
          <CodeBlock title="collections/stripe/create-customer.req.tap" code={commands.studioRequest} />
          <CodeBlock title="collections/stripe/_collection.tap" code={commands.studioCollection} />
          <CodeBlock title="workspace.tap" code={commands.studioWorkspace} />
          <CodeBlock title="environments/prod.env.tap" code={commands.studioEnv} />
        </div>
        <p className="doc-note">
          <a
            className="text-link"
            href="https://github.com/philbir/tap/blob/main/docs/workspace-format.md"
          >
            Read the workspace format spec
          </a>
        </p>
      </>
    ),
  },
  {
    id: "studio-variables",
    eyebrow: "Values",
    title: "Variables and secrets",
    body: "A cascade of scopes over pluggable providers, so a workspace file only ever contains a reference — never a secret value.",
    content: () => (
      <>
        <Callout title="Two token shapes">
          `{"{{name}}"}` resolves from the scope cascade first, then the default provider, then the
          remaining providers in registration order — first hit wins. `{"{{provider:name}}"}`
          resolves only against that provider, with no fall-through. Write a backslash before the
          braces for a literal.
        </Callout>
        <div className="section-gap">
          <FlowDiagram
            label="Variable cascade"
            steps={["workspace", "collection", "stage", "env", "request", "run"]}
          />
        </div>
        <ModeGrid
          gap
          items={[
            ["Scopes carry the boring values", "Base URLs, tenant names, page sizes — the things that differ per environment and are fine to read in a diff. They live in the vars block of the workspace, collection, stage, env, or request file."],
            ["Providers carry everything else", "A provider is a named, typed connection to somewhere values actually live. The workspace file records the name and the type; the value is fetched when the request runs."],
            ["Declared in two places", "Workspace providers live in workspace.tap and travel with the repo. System providers live in ~/.tap/system.json and follow the machine. A workspace provider shadows a system one with the same name."],
            ["Every read is recorded", "Each execution logs which provider answered which name and whether it was secret, so the response's Secrets tab can show what a request depends on without surfacing a value."],
          ]}
        />
        <div className="code-grid">
          <CodeBlock title="workspace.tap — declaring providers" code={commands.studioProviders} />
          <CodeBlock title="environments/prod.env.tap" code={commands.studioEnv} />
        </div>
        <p className="doc-note">
          Providers are read on demand and cached per render, so one variables panel doesn't spawn a
          vault round trip per row. Each type is covered below in{" "}
          <a href={href("studio", "studio-providers")}>Variable providers</a>.
        </p>
      </>
    ),
  },
  {
    id: "studio-providers",
    eyebrow: "Providers",
    title: "Variable providers",
    body: "Five backends behind the same {{token}} syntax: the allow-listed host environment, an encrypted file in the repo, Azure Key Vault, 1Password, and Studio's machine-local store.",
    content: () => (
      <>
        <div className="config-table" role="table" aria-label="Provider types">
          {[
            ["env", "Read", "Allow-listed variables from the process Studio runs in."],
            ["file", "Read / write", "An encrypted YAML file inside the workspace."],
            ["azkv", "Read / write", "Azure Key Vault via DefaultAzureCredential."],
            ["1password", "Read / write", "A 1Password Environment, vault, or item via the op CLI."],
            ["system", "Read / write", "Studio's own machine-local variable list."],
          ].map(([key, mode, value]) => (
            <div className="config-row wide" role="row" key={key}>
              <code role="cell">{key}</code>
              <span className="provider-chip mode" role="cell">
                {mode}
              </span>
              <span role="cell">{value}</span>
            </div>
          ))}
        </div>
        <div className="step-list section-gap">
          <MiniPanel title="Pick a scope">
            Add a provider from Settings → Variable providers when it belongs to the machine, or from
            the workspace editor when it belongs to the repo. Same shape either way: a name, a type,
            and a settings bag.
          </MiniPanel>
          <MiniPanel title="The form comes from the provider">
            Each type publishes its own field list, so the dialog shows exactly the settings that
            type understands — with vault pickers, CLI auto-detection, and fields that appear only
            when the mode they belong to is selected.
          </MiniPanel>
          <MiniPanel title="Test before you save">
            Test runs a real listing against the draft settings — bounded to 20 seconds so a stalled
            credential probe fails loudly — and reports how many variables came back. A file provider
            whose passphrase is wrong is reported as a failure, not an empty vault.
          </MiniPanel>
          <MiniPanel title="Then browse it">
            Saved providers list their variable names in the Variables panel with a secret badge and
            a count. Values stay masked until you ask for one explicitly, and Refresh drops the
            provider's cached listing.
          </MiniPanel>
        </div>

        <ProviderDetail
          id="provider-env"
          name="Host environment"
          type="env"
          mode="Read-only"
          blurb={
            <>
              Exposes variables from the process Studio runs in — the natural fit for values your
              shell, CI job, or `direnv` setup already exports. Membership is decided by two host
              environment variables rather than by the workspace, so no file in the repo can widen
              what a provider can reach. Both take comma-separated globs, and a name on both lists is
              treated as secret. Set neither and the provider exposes nothing.
            </>
          }
          settings={[
            ["TAP_VARS_ALLOWED", "Names exposed as plain variables — values may be displayed."],
            ["TAP_SECRETS_ALLOWED", "Names that resolve at execute time but stay masked in every UI surface."],
          ]}
        >
          <CodeBlock title="Allowlisting host environment variables" code={commands.studioAllowlist} />
        </ProviderDetail>

        <ProviderDetail
          id="provider-file"
          name="Encrypted file"
          type="file"
          mode="Read / write"
          blurb={
            <>
              A YAML store at `.vars/&lt;provider&gt;.yml` inside the workspace, written by Studio
              rather than by hand. Values marked secret are encrypted at rest with AES-256-GCM under
              a key derived from this machine's encryption key (PBKDF2-HMAC-SHA256, 200k iterations),
              so the file itself is safe to commit. Plain values work without a key, and storing the
              first secret generates <code>~/.tap/encryption.key</code> for you — set{' '}
              <code>TAP_ENCRYPTION_KEY</code> to supply your own instead, or run{' '}
              <code>tap-studio key init</code> to create it up front and back it up. The key is
              never a provider setting: a passphrase stored beside the ciphertext it unlocks
              travels with it into Git.
            </>
          }
          settings={[]}
        >
          <CodeBlock title=".vars/local.yml" code={commands.studioFileStore} />
        </ProviderDetail>

        <ProviderDetail
          id="provider-azkv"
          name="Azure Key Vault"
          type="azkv"
          mode="Read / write"
          blurb={
            <>
              Reads and writes secrets in an Azure Key Vault through `DefaultAzureCredential`, so it
              picks up `az login`, environment credentials, workload and managed identity, or your
              IDE's Azure sign-in — no client secret in the workspace file. Listing returns names and
              metadata only; a value is fetched when a token actually references it. Names Key Vault
              cannot hold (anything outside `A-Z a-z 0-9 -`) count as a miss rather than an error, so
              a bare token keeps falling through to the next provider.
            </>
          }
          settings={[
            ["vaultName", "Required. Short vault name; expands to https://<name>.vault.azure.net/. The settings form can pick from the vaults you can see."],
            ["tenantId", "Pins authentication to one tenant when your account can reach several."],
            ["prefix", "Prepended to every Key Vault lookup. Tokens keep using the unprefixed name."],
          ]}
        >
          <div className="code-grid">
            <CodeBlock title="workspace.tap entry" code={commands.studioAzkv} />
            <CodeBlock title="Signing in" code={commands.studioAzkvLogin} />
          </div>
        </ProviderDetail>

        <ProviderDetail
          id="provider-1password"
          name="1Password"
          type="1password"
          mode="Read / write"
          blurb={
            <>
              Backed by the local `op` CLI, which is already authenticated on your machine — through
              the 1Password desktop app integration and biometric unlock, or an existing `op signin`
              session. Like Key Vault, the provider carries no credentials of its own. Three shapes,
              chosen by the mode: an Environment, a whole vault, or a single item.
            </>
          }
          settings={[
            ["mode", "environment, vault, or item. Decides which of the fields below apply."],
            ["environment", "Environment mode. The Environment ID copied from the 1Password app."],
            ["vault", "Vault mode and item mode. Vault name or ID; every lookup is scoped to it."],
            ["item", "Item mode. The item whose fields become this provider's variables."],
            ["field", "Vault mode. Which field holds the value. Empty falls back to password, then credential, then the first concealed field."],
            ["account", "Account shorthand or sign-in address — only needed when op is signed in to several."],
            ["serviceAccountToken", "For headless hosts. Empty uses whatever session op already has."],
            ["cliPath", "Override when op isn't on PATH. Otherwise TAP_OP_CLI, then the usual install locations, then PATH."],
          ]}
        >
          <ModeGrid
            gap
            items={[
              ["Environment mode", "A 1Password Environment is already a flat name-to-value namespace, which is exactly what a variable provider is — so its variables become the provider's, in one call. Read-only: the CLI can't write an Environment back. Environments are still beta and need op 2.38.2-beta.01 or later."],
              ["Vault mode", "Every item in the vault becomes one variable, named after the item's title and valued by its field. This mirrors the Key Vault model: vault, name, value. Writing a name that isn't there yet creates the item."],
              ["Item mode", "One item's fields become the variables — username, password, and any custom field you added. Section-scoped fields are addressed as section.field, the same spelling op's own assignment syntax uses."],
              ["Set-up help built in", "The settings form detects the op binary for you and can browse the vaults your session can see, so the vault field is a picker rather than a string you have to spell correctly."],
            ]}
          />
          <div className="code-grid">
            <CodeBlock title="Environment mode" code={commands.studio1pEnvironment} />
            <CodeBlock title="Vault mode" code={commands.studio1pVault} />
            <CodeBlock title="Item mode" code={commands.studio1pItem} />
            <CodeBlock title="The op CLI" code={commands.studio1pCli} />
          </div>
          <p className="doc-note">
            1Password docs:{" "}
            <a href="https://developer.1password.com/docs/cli/get-started/">
              get started with the CLI
            </a>{" "}
            and <a href="https://developer.1password.com/docs/environments/">Environments</a>.
          </p>
        </ProviderDetail>

        <ProviderDetail
          id="provider-system"
          name="System variables"
          type="system"
          mode="Read / write"
          blurb={
            <>
              Always registered, and backed by the same `~/.tap/system.json` the Settings screen
              edits — so a value you type into Settings is a variable every workspace on this machine
              can resolve. This is where a personal token belongs when it should never travel with
              the repo. Point `TAP_SYSTEM_DIR` somewhere else to relocate the file.
            </>
          }
          settings={[]}
        />

        <SectionHeading
          className="section-gap"
          kicker="Per environment"
          title="One vault per environment, one token in the request."
        >
          A request shouldn't have to know whether it is running against dev or prod. An environment
          can bind provider names, so the same token reads from a different vault depending on which
          environment is active.
        </SectionHeading>
        <ConfigTable
          label="Environment provider binding"
          rows={[
            ["defaultVariableProvider", "The provider bare {{name}} tokens hit first, and the one that receives writes made without naming a target. Precedence: environment, then workspace, then system."],
            ["providerAliases", "Alias-to-provider bindings. Requests use a stable prefix like {{kv:stripe-key}}; each environment points kv at its own provider."],
            ["strictVariables", "With a default provider set, a bare token that misses it fails instead of falling through — so a prod run can never silently pick up a dev value."],
          ]}
        />
        <div className="code-grid section-gap">
          <CodeBlock title="environments/prod.env.tap" code={commands.studioEnvBinding} />
          <CodeBlock title="workspace.tap — both vaults registered" code={commands.studioProviders} />
        </div>
        <Callout title="What never reaches a file">
          Workspace files hold provider names and references, not values. Settings the provider marks
          sensitive leave the API as `***` and are only ever sent back as that same mask, so the
          browser never receives one. Secret values are masked in every listing until you ask for a
          specific one, and the execution trace records provider, name, and secret flag — never the
          value. Keep secret-bearing settings on system-scope providers; workspace providers are
          written into `workspace.tap` verbatim.
        </Callout>
      </>
    ),
  },
  {
    id: "studio-aspire",
    eyebrow: "Aspire",
    title: "Run Studio from your AppHost",
    body: "One call adds Studio to an Aspire solution as a companion resource — pinned to a workspace folder in the repo, pointed at the APIs under development, and health-checked from the dashboard.",
    content: () => (
      <>
        <CodeBlock title="AppHost" code={commands.studioAspire} />
        <ModeGrid
          gap
          items={[
            ["From source, or from the image", "AddTapStudio compiles Studio from a ProjectReference — the route to take when you want the assistant to spawn your coding CLI, an interactive sign-in to open your browser, or op and az to be on PATH. AddTapStudioContainer runs ghcr.io/philbir/tap-studio instead: same handle, same calls, nothing to build, and Aspire pulls the image for you."],
            ["Pinned workspace", "Studio opens the folder the AppHost names, every time. Without the pin it would open whichever workspace that developer last used on that machine."],
            ["Service discovery", "WithApi injects the standard services__* variables, so a collection's baseUrl can be {{aspire:orders-api}} and follow whatever port was allocated."],
            ["Health and wiring", "Waits for each API, health-checks Studio at /health, and shows up in the dashboard as Tap Studio."],
            ["First-run scaffold", "An empty folder gets a manifest and one collection per API, each with a starter .http request. Additive only — nothing existing is touched."],
          ]}
        />
        <div className="code-grid section-gap">
          <CodeBlock title="No build: run the image" code={commands.studioAspireContainer} />
          <CodeBlock
            title="A collection that follows the allocated port"
            code={commands.studioAspireCollection}
          />
          <CodeBlock title="The same workspace in CI" code={commands.studioAspireCi} />
        </div>
        <Callout title="Two references in the AppHost csproj">
          A plain ProjectReference to Tap.Studio — that is what makes Aspire's source generator emit
          Projects.Tap_Studio — plus a reference to Tap.Hosting with IsAspireProjectResource="false",
          because it is a library rather than a launchable project. Building Tap.Studio builds its
          React UI, so this route needs yarn on PATH.
        </Callout>
        <p className="doc-note">
          The Studio is a development tool: it is excluded from the manifest, so nothing you publish
          carries it. While an AppHost is hosting it the workspace switcher is locked and the header
          says Aspire — the folder is part of the solution rather than a per-user preference. The
          header also links to the desktop app, which opens the same workspace folder without the
          AppHost having to be up: <a href={href("studio", "studio-install")}>get Tap Studio</a>.
        </p>
      </>
    ),
  },
  {
    id: "studio-install",
    eyebrow: "Install",
    title: "Get Tap Studio",
    body: "Studio ships as a native desktop app wrapping the self-contained backend, and runs from source through the Aspire dev loop.",
    content: () => (
      <>
        <ModeGrid
          items={[
            ["macOS", "A signed .dmg / .app for Apple Silicon and Intel."],
            ["Windows", "An .msi and an NSIS setup .exe."],
            ["Linux", "A .deb package."],
            ["Auto-update", "Each release publishes a signed manifest the updater client polls, so the app keeps itself current."],
            ["Container", "ghcr.io/philbir/tap-studio carries the same UI, API, and engine for a headless host or an Aspire AppHost. Mount the workspace at /workspace; port 8080."],
          ]}
        />
        <div className="code-grid">
          <CodeBlock title="Run from source" code={commands.studioRun} />
          <CodeBlock title="Build the desktop bundle" code={commands.studioBuild} />
        </div>
        <p className="doc-note">
          The dev loop starts a demo API that exercises every verb, content type, SSE, WebSockets,
          GraphQL, and a real OAuth2/OIDC server — so every Studio feature has something to talk to
          out of the box.
        </p>
        <p>
          <a className="text-link" href={`${repoUrl}/releases/latest`}>
            Download the latest release from GitHub
          </a>
        </p>
        <p className="doc-note">
          Every platform bundle is attached to the release on{" "}
          <a className="text-link" href={`${repoUrl}/releases`}>
            the GitHub releases page
          </a>
          , alongside the signed update manifest and the release notes.
        </p>
      </>
    ),
  },
];

export const StudioPage = () => (
  <>
    <section className="product-hero">
      <div className="product-hero-copy">
        <span className="kicker product-eyebrow">
          <span className="product-index large">01</span> Product
        </span>
        <h1>Tap Studio</h1>
        <p className="lead">
          The HTTP request and auth credential crafter. Shape requests, authenticate against real
          identity providers, execute, document, and prove they keep working—with the whole
          workspace living in your repository as reviewable Markdown.
        </p>
        <div className="hero-actions">
          <a className="button primary" href={`${repoUrl}/releases/latest`}>
            Download Studio
          </a>
          <a className="button ghost" href={href("studio", "studio-testing")}>
            Flows and tests
          </a>
        </div>
        <div className="proof-row">
          <span>Free</span>
          <span>Desktop</span>
          <span>OAuth 2.0</span>
          <span>Entra</span>
          <span>AWS SigV4</span>
          <span>GraphQL</span>
          <span>Key Vault</span>
          <span>1Password</span>
          <span>Git</span>
          <span>CI</span>
          <span>MCP</span>
        </div>
      </div>
      <picture className="product-hero-visual">
        <img
          src="./tap-studio-workbench-hero.png"
          alt="A precision craft workbench shaping request and credential flows into one HTTP request"
        />
      </picture>
    </section>

    <section className="section" id="studio-features">
      <SectionHeading
        kicker="What it does"
        title="A request client that behaves like part of your codebase."
      >
        Studio is the other direction of travel: you are the client. It composes the call, runs the
        authentication flow behind it, executes it, and stores the result as text your team can
        review — then keeps proving it works, from the Testing tab and from your pipeline. Every
        feature below is in the free build; there is no other build.
      </SectionHeading>
      <FeatureGrid features={studioFeatures} />
    </section>

    <section className="docs-wrap" id="studio-docs">
      <DocList docs={docs} />
    </section>
  </>
);
