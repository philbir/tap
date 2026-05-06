export interface SseEvent {
  timestamp: string
  event: string
  data: string
  id: string | null
  retry: number | null
  comment: string | null
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
}

export interface SseEventEnvelope {
  recordId: string
  sequence: number
  event: SseEvent
}

export interface IngressEntry {
  hostname: string
  upstream: string
  tunnelMode?: 'token' | 'api-managed' | 'dynamic' | 'quick' | 'local' | null
  tunnelName?: string | null
  publicUrl?: string | null
}

export interface InspectorConfig {
  proxyPort: number
  ingress: IngressEntry[]
  apiMode: 'token' | 'cloudflare-api'
  mode: 'standalone' | 'tunnel'
}

export interface HostnameResult {
  hostname: string
  service: string
}
