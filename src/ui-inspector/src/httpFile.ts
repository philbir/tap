import type { RequestRecord } from './types'

/** Generate a VS Code REST Client / IntelliJ HTTP Client compatible file body. */
export function generateHttpFile(record: RequestRecord): string {
  const url = `${record.scheme}://${record.host}${record.path}`
  const lines: string[] = []

  lines.push(`### ${record.method} ${record.host}${record.path}`)
  lines.push(`# Captured: ${new Date(record.timestamp).toISOString()}`)
  lines.push(`# Status: ${record.statusCode} · ${record.durationMs}ms`)
  lines.push('')
  lines.push(`${record.method} ${url}`)

  for (const [k, v] of Object.entries(record.requestHeaders)) {
    const lk = k.toLowerCase()
    if (lk === 'content-length') continue // auto-computed by client
    lines.push(`${k}: ${v}`)
  }

  if (record.requestBody) {
    lines.push('')
    lines.push(record.requestBody)
  }

  lines.push('')
  return lines.join('\n')
}

export function downloadHttpFile(record: RequestRecord): void {
  const content = generateHttpFile(record)
  const slug = (record.path.replace(/[^a-z0-9]+/gi, '_').replace(/^_|_$/g, '') || 'request').slice(0, 40)
  const name = `${record.method.toLowerCase()}-${slug}.http`

  const blob = new Blob([content], { type: 'application/x-http;charset=utf-8' })
  const url = URL.createObjectURL(blob)
  const anchor = document.createElement('a')
  anchor.href = url
  anchor.download = name
  document.body.appendChild(anchor)
  anchor.click()
  document.body.removeChild(anchor)
  URL.revokeObjectURL(url)
}
