// Code samples shown across the site. Moved verbatim from the previous single-file main.tsx.
export const commands = {
  install: `dotnet tool install -g Tap`,
  installCurl: `curl -fsSL https://raw.githubusercontent.com/philbir/tap/main/install.sh | sh`,
  cli: `tap run http://localhost:3000`,
  cliQuick: `tap run http://localhost:3000 --quick`,
  cliToken: `tap run http://localhost:3000 \\
  --token "$CLOUDFLARE_TUNNEL_TOKEN" \\
  --hostname api-local.example.com`,
  cliManaged: `tap run http://localhost:3000 \\
  --api-token "$CLOUDFLARE_API_TOKEN" \\
  --account "$CLOUDFLARE_ACCOUNT_ID" \\
  --api-managed tap-cli \\
  --dynamic example.com`,
  cliTailscaleServe: `tap run http://localhost:3000 --tailscale`,
  cliTailscalePublic: `tap run http://localhost:3000 --tailscale --tailscale-public \\
  --auth-header "X-Tap-Key=$TAP_KEY"`,
  cliTailscaleEphemeral: `export TAILSCALE_AUTHKEY=tskey-...
tap run http://localhost:3000 --tailscale --tailscale-port 8443`,
  cliTailscaleDocker: `export TAILSCALE_AUTHKEY=tskey-...
tap run http://localhost:3000 --tailscale --docker`,
  cliConfig: `{
  "upstream": "http://localhost:3000"
}`,
  cliAuth: `tap run http://localhost:3000 --quick \\
  --auth-header "X-Tap-Key=$TAP_KEY" \\
  --auth-cidr "203.0.113.0/24" \\
  --auth-country "CH"`,
  cliOidc: `tap run http://localhost:3000 --quick \\
  --auth-oidc-authority "https://issuer.example.com" \\
  --auth-oidc-client-id "$OIDC_CLIENT_ID" \\
  --auth-oidc-client-secret "$OIDC_CLIENT_SECRET"`,
  standalone: `using Aspire.Hosting;

var builder = DistributedApplication.CreateBuilder(args);

var api = builder.AddProject<Projects.Sample_Api>("api");

var tap = builder.AddTap<Projects.Tap_Server>();
api.WithTap(tap);

builder.Build().Run();`,
  quick: `var tap = builder.AddTap<Projects.Tap_Server>(
        name: "tap-quick",
        proxyPort: 5307,
        uiPort: 5306)
    .WithQuickTunnel();

api.WithTap(tap);`,
  token: `var tap = builder.AddTap<Projects.Tap_Server>()
    .WithTunnel("tap-tunnel", t =>
        t.WithExistingTunnel(builder.Configuration["Cloudflare:TunnelToken"]));

api.WithTap(tap, "api-local.example.com");`,
  managed: `var tap = builder.AddTap<Projects.Tap_Server>()
    .WithTunnel("tap-tunnel", t => t
        .WithApiManagedTunnel(
            builder.Configuration["Cloudflare:ApiToken"]!,
            builder.Configuration["Cloudflare:AccountId"]!,
            tunnelName: "tap-dev")
        .WithDynamicHostname("example.com", prefix: "api-", suffix: "-tap"));

api.WithTap(tap);`,
  tailscaleServe: `var tap = builder.AddTap<Projects.Tap_Server>(mode: "tunnel")
    .WithTailscaleServe("tap-serve", t => t.WithSystemDaemon());

api.WithTap(tap);`,
  tailscalePublic: `var tap = builder.AddTap<Projects.Tap_Server>(mode: "tunnel")
    .WithTailscaleFunnel("tap-funnel", t => t.WithSystemDaemon())
    .WithHeaderAuth("X-Tap-Key", builder.Configuration["Tap:Key"]!);

api.WithTap(tap);`,
  tailscaleEphemeral: `var tap = builder.AddTap<Projects.Tap_Server>(mode: "tunnel")
    .WithTailscaleServe("tap-ts-ephemeral", t => t
        .WithEphemeralDaemon(builder.Configuration["Tailscale:AuthKey"]!)
        .WithFunnelPort(8443));

api.WithTap(tap);`,
  tailscaleDocker: `var tap = builder.AddTap<Projects.Tap_Server>(mode: "tunnel")
    .WithTailscaleServe("tap-ts-docker", t => t
        .WithEphemeralDaemon(builder.Configuration["Tailscale:AuthKey"]!)
        .WithFunnelPort(10000),
        hostMode: TailscaleHostMode.Docker);

api.WithTap(tap);`,
  aspireAuth: `var tap = builder.AddTap<Projects.Tap_Server>()
    .WithHeaderAuth("X-Tap-Key", builder.Configuration["Tap:Key"]!)
    .WithIpAllowList("203.0.113.0/24")
    .WithCountryAllowList("CH")
    .WithOidcAuth(
        authority: builder.Configuration["Auth:Authority"]!,
        clientId: builder.Configuration["Auth:ClientId"]!,
        clientSecret: builder.Configuration["Auth:ClientSecret"]);

api.WithTap(tap);`,
  secrets: `dotnet user-secrets set Cloudflare:TunnelToken "<connector-token>" \\
  --project samples/Sample.AppHost

dotnet user-secrets set Cloudflare:ApiToken "<api-token>" \\
  --project samples/Sample.AppHost

dotnet user-secrets set Cloudflare:AccountId "<account-id>" \\
  --project samples/Sample.AppHost`,
  tailscaleSecrets: `dotnet user-secrets set Tailscale:UseSystem "true" \\
  --project samples/Sample.AppHost

dotnet user-secrets set Tailscale:AuthKey "<tskey-...>" \\
  --project samples/Sample.AppHost

dotnet user-secrets set Tailscale:UseDocker "true" \\
  --project samples/Sample.AppHost`,
  sampleScenarios: `dotnet run --project samples/Sample.AppHost -- --scenarios tailscale
dotnet run --project samples/Sample.AppHost -- --scenarios cloudflare
dotnet run --project samples/Sample.AppHost -- --scenarios all`,
  studioRequest: `---
kind: request
name: Create customer
auth: ../../auth/stripe-bearer.auth.tap
tags: [customer, write]
---

# Create customer

Called during signup. Idempotent on \`email\`.

\`\`\`http
POST /v1/customers
Content-Type: application/json

{ "email": "{{customer.email}}", "name": "{{customer.name}}" }
\`\`\``,
  studioCollection: `---
kind: collection
name: Stripe
baseUrl: https://api.stripe.com
defaultAuth: ../../auth/stripe-bearer.auth.tap
defaultHeaders:
  Accept: application/json
stages:
- name: live
- name: test
  defaultAuth: ../../auth/stripe-test.auth.tap
---`,
  studioAuth: `---
kind: auth
name: Corp Entra
type: oauth2
flow: authorization_code
authority: https://login.microsoftonline.com/{{tenant}}/v2.0
clientId: '{{env:ENTRA_CLIENT_ID}}'
scopes:
  - https://graph.microsoft.com/.default
---`,
  studioWorkspace: `---
kind: workspace
name: acme-billing
defaultEnv: environments/local.env.tap
variableProviders:
- name: env
  type: env
- name: vault
  type: azkv
  settings:
    vaultUrl: https://acme-prod.vault.azure.net
---`,
  studioEnv: `---
kind: env
name: Production
vars:
  api.baseUrl: https://api.stripe.com
  STRIPE_KEY: '{{vault:stripe-live-key}}'
---`,
  studioFlow: `---
kind: flow
name: Checkout
steps:
- name: Create the order
  request: ../collections/demo/create-order.req.tap
  extract:
  - var: orderId
    jsonpath: $.order.id
- name: Read it back
  request: ../collections/demo/get-order.req.tap
  vars:
    id: '{{orderId}}'
  assertions:
  - jsonpath: $.order.status
    equals: open
tags: [demo, smoke]
---`,
  studioTestSet: `---
kind: test
name: Order API
vars:
  customer: cus_demo
tests:
- name: Rejects an unknown SKU
  request: ../collections/demo/create-order.req.tap
  vars: { item: nope }
  assertions:
  - status: 404
- name: Full checkout
  flow: ./checkout.flow.tap
onFailure: continue
tags: [demo, smoke]
---`,
  studioCliInstall: `dotnet tool install --global Tap.Studio.Cli

# run a test set, a flow, or a single request
tap-studio test "Demo API smoke"
tap-studio test tests/checkout.flow.tap
tap-studio send "Create customer"`,
  studioCliSelect: `# every test set and flow carrying the tag — repeated tags union
tap-studio test --tag smoke --tag graphql

# just the tests inside a set whose name contains "refund"
tap-studio test "Order API" --filter refund

# one entry by index, or list what is available
tap-studio test "Order API" --only 2
tap-studio test --list`,
  studioCliVars: `tap-studio test "Order API" \\
  --env ci --stage uat \\
  --var customer=cus_ci \\
  --var-file ci.env

# what would the requests actually see?
tap-studio vars --env ci
tap-studio lint`,
  studioCliCi: `- run: dotnet tool install --global Tap.Studio.Cli

- run: tap-studio test "Demo API smoke" --env ci \\
    --output junit --output-file results.xml
  env:
    TAP_SECRETS_ALLOWED: "DEMO_*_TOKEN"
    DEMO_API_TOKEN: \${{ secrets.DEMO_API_TOKEN }}

- run: |
    tap-studio test --tag smoke \\
      --output markdown --output-file summary.md
    cat summary.md >> "$GITHUB_STEP_SUMMARY"`,
  studioAllowlist: `# Names whose values the UI may show in clear text
export TAP_VARS_ALLOWED="DEMO_*,ASPNETCORE_ENVIRONMENT"

# Names that resolve at execute time but stay masked everywhere
export TAP_SECRETS_ALLOWED="ACME_*_TOKEN,AZURE_*"

# Neither set? The env provider exposes nothing at all.`,
  studioProviders: `---
kind: workspace
name: acme-billing
defaultEnv: environments/local.env.tap
defaultVariableProvider: kv-dev
variableProviders:
- name: env
  type: env
- name: local
  type: file
- name: kv-dev
  type: azkv
  settings:
    vaultName: acme-dev
- name: kv-prod
  type: azkv
  settings:
    vaultName: acme-prod
- name: 1p
  type: 1password
  settings:
    mode: vault
    vault: Acme Dev
---`,
  studioFileStore: `# .vars/local.yml — written by Studio; commit it
variables:
  api.baseUrl: https://localhost:5001
  STRIPE_KEY:
    value: 'enc:v1:<iv>:<ciphertext>:<tag>'
    secret: true`,
  studioAzkv: `- name: kv-prod
  type: azkv
  settings:
    # -> https://acme-prod.vault.azure.net/
    vaultName: acme-prod
    # optional: pin one tenant
    tenantId: <tenant-guid>
    # optional: tokens keep the unprefixed name
    prefix: billing-`,
  studioAzkvLogin: `az login
az account set --subscription "<subscription>"

# DefaultAzureCredential also accepts environment
# credentials, workload and managed identity, and
# your IDE's Azure sign-in — nothing about the
# credential lives in the workspace file.`,
  studio1pEnvironment: `- name: 1p
  type: 1password
  settings:
    mode: environment
    # 1Password app: Developer
    #   -> View Environments
    #   -> Manage environment
    #   -> Copy environment ID
    environment: <environment-id>`,
  studio1pVault: `- name: 1p
  type: 1password
  settings:
    mode: vault
    # every item title becomes a variable name
    vault: Acme Dev
    # default: password, then credential, then
    # the first populated concealed field
    field: credential`,
  studio1pItem: `- name: acme-api
  type: 1password
  settings:
    mode: item
    vault: Acme Dev
    # this item's fields become the variables:
    # {{acme-api:username}}, {{acme-api:api-key}}
    item: Acme API`,
  studio1pCli: `# Nothing to configure on a normal desktop: op
# reuses the 1Password app integration or your
# existing "op signin" session.

# Environments need op 2.38.2-beta.01 or later:
op --version

# Only when op is not on PATH:
export TAP_OP_CLI=/opt/homebrew/bin/op`,
  studioEnvBinding: `---
kind: env
name: Production
defaultVariableProvider: kv-prod
strictVariables: true
providerAliases:
  kv: kv-prod
vars:
  api.baseUrl: https://api.acme.com
---`,
  studioRun: `cd samples
aspire run

# your own repo instead of the sample workspace
STUDIO_WORKSPACE=/path/to/your/repo aspire run

# add the native desktop window
RunDesktop=true aspire run`,
  studioBuild: `scripts/build-desktop.sh          # publish sidecar + tauri build
scripts/build-desktop.sh --dev    # publish sidecar + tauri dev`,
  studioAspire: `var orders  = builder.AddProject<Projects.Orders_Api>("orders-api");
var billing = builder.AddProject<Projects.Billing_Api>("billing-api");

var studio = builder.AddTapStudio<Projects.Tap_Studio>()
    .WithWorkspaceFolder("tap")   // default; relative to the AppHost directory
    .WithApi(orders)
    .WithApi(billing);

// Seeding an OAuth client? Studio's redirect URI is only known once its port
// is allocated, so it is a ReferenceExpression rather than a string.
identity.WithEnvironment("STUDIO_CALLBACK_URL", studio.CallbackUrl);`,
  studioAspireCollection: `---
kind: collection
name: Orders
baseUrl: '{{aspire:orders-api}}'
---`,
  studioAgentInit: `# detects the agent environments on this machine, then wires them up
tap-studio agent init

# or name them: claude, codex, copilot, opencode
tap-studio agent init --env claude --env copilot
tap-studio agent init --env codex --scope user`,
  studioMcpConfig: `{
  "mcpServers": {
    "tap-studio": {
      "command": "tap-studio",
      "args": ["mcp", "--workspace", "."]
    }
  }
}`,
  studioAgentLoop: `# what is in here?
tap-studio list requests --json

# what does this one send?
tap-studio describe "Create customer" --json

# try the real endpoint through the collection's auth
tap-studio call GET /users/42 -c demo --json

# then save it, and run it as a test
tap-studio test "Order API" --json`,
  studioAspireCi: `# Nothing in the workspace is Aspire-specific. {{aspire:orders-api}} reads the
# standard service-discovery variables, so CI resolves it by exporting them.
export services__orders-api__https__0=https://staging.example.com
tap-studio test "Orders smoke"`,
};
