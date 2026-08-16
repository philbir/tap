export type Feature = {
  title: string;
  text: string;
  glyph: string;
};

export const inspectorFeatures: Feature[] = [
  {
    title: "Tunnel without ceremony",
    text: "Use free TryCloudflare URLs, dashboard connector tokens, API-managed Cloudflare DNS, or Tailscale Serve/Funnel when your tailnet is the right boundary.",
    glyph: "T",
  },
  {
    title: "Built for real dev loops",
    text: "Mobile hooks, webhook deliveries, auth redirects, partner callbacks, and temporary demos all become one inspectable tunnel.",
    glyph: "U",
  },
  {
    title: "Run as CLI or Aspire",
    text: "Use `tap run` for one-off local tunnels, or model inspectors and tunnels directly in your .NET Aspire AppHost.",
    glyph: "C",
  },
  {
    title: "Inspect every hop",
    text: "Tap captures method, host, path, headers, status, timing, request bodies, response bodies, and image payload previews before forwarding to your upstream.",
    glyph: "I",
  },
  {
    title: "Live streaming protocols",
    text: "WebSockets and Server-Sent Events stream through the same proxy and the inspector UI renders a live, direction-tagged frame/event timeline in dedicated WS and SSE tabs — watch messages append while the connection is open.",
    glyph: "L",
  },
  {
    title: "Aspire-native wiring",
    text: "Attach an inspector or public tunnel to an Aspire resource with extension methods, while allocated ports and hostnames are resolved at startup.",
    glyph: "A",
  },
  {
    title: "Built for local trust",
    text: "Keep the UI local, default Tailscale to tailnet-only Serve, put auth on public proxy traffic, and combine header, CIDR, country, and OIDC checks.",
    glyph: "S",
  },
];

export const studioFeatures: Feature[] = [
  {
    title: "Full request composition",
    text: "Method, URL, query params, headers, and bodies as None / Form / Multipart / Raw / Binary / GraphQL — with JSON and XML formatting, multi-file uploads, and a GraphQL editor backed by the live schema.",
    glyph: "R",
  },
  {
    title: "Many authentication flows",
    text: "OAuth 2.0 / OIDC with PKCE, client credentials, ROPC and device code; Microsoft Entra; Azure CLI direct and on-behalf-of; GitHub PAT, gh CLI, App and OAuth App; AWS SigV4; signed JWT; bearer, basic, API key, custom headers.",
    glyph: "A",
  },
  {
    title: "AI assistance",
    text: "Hand the request to GitHub Copilot CLI or Claude Code — running locally under your existing CLI login — and get a proposed edit, with documentation, that you review before saving.",
    glyph: "AI",
  },
  {
    title: "Responses you can read",
    text: "Status, duration, size, a syntax-highlighted body with image and binary previews, the exact request that went on the wire, the auth and variable flow behind it, and which secrets were resolved.",
    glyph: "V",
  },
  {
    title: "Streaming built in",
    text: "Server-Sent Events stream into a live timeline, and requests marked protocol: websocket open a real socket that appends frames as they arrive.",
    glyph: "S",
  },
  {
    title: "Flows and test sets",
    text: "A flow runs requests in order and carries values out of one response into the next. A test set groups checks that each run one request or one whole flow. Both are Markdown in tests/, and the Testing tab streams every result as it lands.",
    glyph: "T",
  },
  {
    title: "The same verdict in CI",
    text: "The tap-studio .NET tool runs those flows and test sets headlessly and reports JUnit, TRX, JSON, or Markdown, with exit codes a pipeline can branch on. It calls the same engine the UI does, so a pull-request check and the Testing tab are one computation.",
    glyph: "CI",
  },
  {
    title: "Git-native workspace",
    text: "Every request, collection, auth profile, flow, and test set is a Markdown file in your repo. Branch, diff, stage, and commit without leaving the app.",
    glyph: "G",
  },
  {
    title: "Variables and secrets",
    text: "A six-level cascade over pluggable providers: allow-listed host environment, an encrypted file in the repo, Azure Key Vault, 1Password, and machine-local system variables. Files hold references; values arrive at execute time.",
    glyph: "X",
  },
  {
    title: "Desktop app",
    text: "A native shell for macOS, Windows, and Linux that self-updates and registers a stable tap-studio:// redirect URI for OAuth sign-in.",
    glyph: "D",
  },
];
