import { href } from "../router";

/** Which product answers the job. `both` is the round trip: send in one, watch it arrive in the other. */
export type UseCaseProduct = "studio" | "tunnels" | "both";

export type UseCase = {
  title: string;
  text: string;
  product: UseCaseProduct;
  /** Where the docs for it live — the badge links here. */
  section: string;
  /** The concrete pieces that do the work. */
  chips: string[];
};

/**
 * The job-to-product map. Ordered inbound first (traffic arriving at your
 * machine, which is Tunnels' half) then outbound (you as the client, which is
 * Studio's), then the two things that need both.
 */
export const useCases: UseCase[] = [
  {
    title: "Mobile app development",
    text: "The app runs on a phone or an emulator; the API runs on your laptop. A tunnel gives it a real HTTPS URL — scan the QR code onto the device — and every call the app makes is captured on the way through.",
    product: "tunnels",
    section: href("tunnels", "quickstart"),
    chips: ["tap run --quick", "QR code", "Inspector"],
  },
  {
    title: "Webhooks in development",
    text: "Point Stripe, GitHub, or any provider at your machine and keep the delivery whole: headers, body, timing, status. Replay it as many times as it takes to get the handler right, without asking the provider to send another.",
    product: "tunnels",
    section: href("tunnels", "use-cases"),
    chips: ["Stable hostname", "Full capture", "Replay"],
  },
  {
    title: "Auth callbacks and redirect URIs",
    text: "Identity providers redirect to a hostname you registered, not to a port on your laptop. Bring a domain you own, put the callback on it, and run the whole sign-in against your local build before anything is deployed.",
    product: "tunnels",
    section: href("tunnels", "cloudflare"),
    chips: ["Own domain", "Cloudflare", "Tailscale"],
  },
  {
    title: "Inspect HTTP traffic in development",
    text: "See exactly what arrived rather than what you assume was sent — every header and body, SSE events as a live timeline, WebSocket frames in both directions. No SDK, no code change, and it works with no tunnel at all.",
    product: "tunnels",
    section: href("tunnels", "inspector-features"),
    chips: ["Standalone mode", "SSE", "WebSockets"],
  },
  {
    title: "Share a local build",
    text: "Send a colleague, a designer, or a customer a URL to what is running on your machine right now — gated by a header, a CIDR range, a country, or OIDC if it faces the public internet — then close the tunnel when the moment is over.",
    product: "tunnels",
    section: href("tunnels", "auth"),
    chips: ["Quick tunnel", "Proxy auth", "No account"],
  },
  {
    title: "Build and test HTTP requests",
    text: "Compose the call, send it, assert on the response, and document it next to the request. The workspace is plain text in your repository, so a new endpoint arrives as a reviewable diff instead of a link to someone's cloud collection.",
    product: "studio",
    section: href("studio", "studio-compose"),
    chips: ["Composer", "GraphQL", "Git"],
  },
  {
    title: "Testing auth flows",
    text: "Prove a PKCE, client-credentials, device-code, Entra, GitHub App, or SigV4 profile works before a line of it reaches your code. Try it runs the flow on its own and reports what came back; tokens are cached outside the repo and refreshed for you.",
    product: "studio",
    section: href("studio", "studio-auth"),
    chips: ["OAuth 2.0 / OIDC", "Entra", "AWS SigV4"],
  },
  {
    title: "Multi-step scenarios",
    text: "Sign in, create the order, read it back, cancel it — one flow, with values carried from each response into the next step. A failed step stops the flow, because everything after it would run against a state that never happened.",
    product: "studio",
    section: href("studio", "studio-testing"),
    chips: ["Flows", "Extract", "Assertions"],
  },
  {
    title: "HTTP testing in your CI pipeline",
    text: "The same test sets run headless from a .NET tool on any runner, reporting JUnit, TRX, JSON, or Markdown and exiting non-zero when an assertion fails — so a broken endpoint is a red build, not a line in a log nobody reads.",
    product: "studio",
    section: href("studio", "studio-cli"),
    chips: ["tap-studio test", "JUnit / TRX", "Exit codes"],
  },
  {
    title: "Give an AI agent real API access",
    text: "An agent discovers the workspace, describes a request, and calls it fully authenticated — over MCP or the CLI — while the workspace keeps the credentials. Every echo back to the agent is redacted, and a collection can opt out entirely.",
    product: "studio",
    section: href("studio", "studio-agents"),
    chips: ["MCP", "Redaction", "Per-collection opt-out"],
  },
  {
    title: "One AppHost, the whole loop",
    text: "Model both products in your .NET Aspire AppHost so every developer on the team gets the same wiring: AddTap attaches an inspector and a tunnel to a resource, AddTapStudio pins the workbench to a workspace folder in the repo.",
    product: "both",
    section: href("studio", "studio-aspire"),
    chips: ["AddTap", "AddTapStudio", "Service discovery"],
  },
  {
    title: "Debug an integration end to end",
    text: "Watch the provider's delivery land in the Inspector, then rebuild it as a Studio request with assertions — so the next time that integration changes shape, it shows up as a failing test rather than a surprise in production.",
    product: "both",
    section: href("home", "together"),
    chips: ["Capture", "Rebuild", "Assert"],
  },
];
