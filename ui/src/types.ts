export interface SseEvent {
  timestamp: string
  event: string
  data: string
  id: string | null
  retry: number | null
  comment: string | null
}

export interface WebSocketMessage {
  timestamp: string
  /** "client" = browser → upstream, "server" = upstream → browser */
  direction: 'client' | 'server'
  /** "text" | "binary" | "close" */
  type: string
  text: string | null
  base64: string | null
  size: number
  truncated: boolean
  closeStatus: number | null
  closeDescription: string | null
}

export interface RequestRecord {
  sequence: number
  id: string
  timestamp: string
  method: string
  host: string
  scheme: string
  path: string
  upstream: string | null
  remoteIp: string | null
  requestHeaders: Record<string, string>
  requestBody: string | null
  requestBodyTruncated: boolean
  requestBodyOriginalSize: number
  requestContentType: string | null
  statusCode: number
  responseHeaders: Record<string, string>
  responseBody: string | null
  responseBodyBase64: string | null
  responseBodyTruncated: boolean
  responseBodyOriginalSize: number
  responseContentType: string | null
  durationMs: number
  error: string | null
  isStream?: boolean
  streamCompleted?: boolean
  sseEvents?: SseEvent[]
  isWebSocket?: boolean
  webSocketMessages?: WebSocketMessage[]
}

export interface SseEventEnvelope {
  recordId: string
  sequence: number
  event: SseEvent
}

export interface WebSocketMessageEnvelope {
  recordId: string
  sequence: number
  message: WebSocketMessage
}

export interface IngressEntry {
  hostname: string
  upstream: string
  tunnelMode?: 'token' | 'api-managed' | 'dynamic' | 'quick' | 'local' | 'tailscale-system' | 'tailscale-ephemeral' | null
  tunnelName?: string | null
  publicUrl?: string | null
  /** True when the tunnel is exposed to the public internet (any Cloudflare mode, or Tailscale Funnel).
   *  False for Tailscale serve (tailnet-only). Drives the public-exposure warning banner. */
  publicExpose?: boolean
}

export interface InspectorConfig {
  proxyPort: number
  ingress: IngressEntry[]
  apiMode: 'token' | 'cloudflare-api'
  mode: 'standalone' | 'tunnel'
  provider?: 'cloudflare' | 'tailscale' | null
}

export interface HostnameResult {
  hostname: string
  service: string
}
