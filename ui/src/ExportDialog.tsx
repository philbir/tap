import { useEffect, useMemo, useState } from 'react'
import type { RequestRecord } from './types'
import { EXPORT_FORMATS, downloadExport, generateExport, type ExportFormat } from './exportFormats'

interface Props {
  record: RequestRecord | null
  open: boolean
  onClose: () => void
}

const FORMAT_STORAGE_KEY = 'tap.exportFormat'

function loadInitialFormat(): ExportFormat {
  try {
    const v = localStorage.getItem(FORMAT_STORAGE_KEY)
    if (v && EXPORT_FORMATS.some((f) => f.id === v)) return v as ExportFormat
  } catch { /* ignore */ }
  return 'http'
}

export function ExportDialog({ record, open, onClose }: Props) {
  const [format, setFormat] = useState<ExportFormat>(loadInitialFormat)
  const [copied, setCopied] = useState(false)

  useEffect(() => {
    try { localStorage.setItem(FORMAT_STORAGE_KEY, format) } catch { /* ignore */ }
    setCopied(false)
  }, [format])

  // Esc closes; Cmd/Ctrl+C copies (when no text is selected).
  useEffect(() => {
    if (!open) return
    const onKey = (e: KeyboardEvent) => {
      if (e.key === 'Escape') { e.preventDefault(); onClose() }
    }
    window.addEventListener('keydown', onKey)
    return () => window.removeEventListener('keydown', onKey)
  }, [open, onClose])

  const content = useMemo(() => {
    if (!record) return ''
    try { return generateExport(record, format) }
    catch (ex) { return `// Export failed: ${String(ex)}` }
  }, [record, format])

  if (!open || !record) return null

  const copy = async () => {
    try {
      await navigator.clipboard.writeText(content)
      setCopied(true)
      setTimeout(() => setCopied(false), 1500)
    } catch {
      /* ignore */
    }
  }

  const activeMeta = EXPORT_FORMATS.find((f) => f.id === format)!
  const byteSize = new Blob([content]).size
  const lineCount = content === '' ? 0 : content.split('\n').length

  return (
    <div
      onClick={onClose}
      style={{
        position: 'fixed',
        inset: 0,
        background: 'rgba(0, 0, 0, 0.4)',
        display: 'flex',
        alignItems: 'flex-start',
        justifyContent: 'center',
        paddingTop: '60px',
        zIndex: 100,
      }}
    >
      <div
        onClick={(e) => e.stopPropagation()}
        style={{
          background: 'var(--bg-raised)',
          border: '1px solid var(--border)',
          borderRadius: '8px',
          padding: '16px',
          width: '760px',
          maxWidth: '92vw',
          maxHeight: '85vh',
          display: 'flex',
          flexDirection: 'column',
          boxShadow: '0 10px 30px rgba(0, 0, 0, 0.3)',
        }}
      >
        <div style={{ display: 'flex', alignItems: 'center', gap: '10px', marginBottom: '10px' }}>
          <h3 style={{ margin: 0, fontSize: '14px' }}>Export request</h3>
          <span style={{ fontSize: '11px', color: 'var(--text-muted)' }}>
            preview · copy · download
          </span>
          <button
            onClick={onClose}
            aria-label="Close"
            style={{ marginLeft: 'auto', background: 'transparent', border: 'none', fontSize: 18, color: 'var(--text-muted)', cursor: 'pointer' }}
          >
            ×
          </button>
        </div>

        {/* Format switcher (segmented control) */}
        <div
          role="tablist"
          aria-label="Export format"
          style={{
            display: 'inline-flex',
            border: '1px solid var(--border)',
            borderRadius: 6,
            overflow: 'hidden',
            alignSelf: 'flex-start',
            marginBottom: 8,
          }}
        >
          {EXPORT_FORMATS.map((f) => {
            const active = format === f.id
            return (
              <button
                key={f.id}
                role="tab"
                aria-selected={active}
                onClick={() => setFormat(f.id)}
                title={f.detail}
                style={{
                  background: active ? 'color-mix(in srgb, var(--accent) 18%, transparent)' : 'transparent',
                  color: active ? 'var(--accent)' : 'var(--text-muted)',
                  border: 'none',
                  borderRadius: 0,
                  padding: '5px 14px',
                  fontSize: 12,
                  fontWeight: 600,
                  borderRight: '1px solid var(--border)',
                  cursor: 'pointer',
                }}
              >
                {f.label}
              </button>
            )
          })}
        </div>

        <div style={{ display: 'flex', alignItems: 'center', gap: 10, marginBottom: 8, fontSize: 11, color: 'var(--text-muted)' }}>
          <span>{activeMeta.detail}</span>
          <span style={{ marginLeft: 'auto', fontFamily: 'SF Mono, Menlo, monospace' }}>
            {lineCount} line{lineCount === 1 ? '' : 's'} · {formatBytes(byteSize)}
          </span>
        </div>

        <pre
          style={{
            flex: 1,
            overflow: 'auto',
            margin: 0,
            padding: '12px',
            background: 'var(--bg-input)',
            border: '1px solid var(--border)',
            borderRadius: '4px',
            fontFamily: 'SF Mono, Menlo, Consolas, monospace',
            fontSize: '12px',
            whiteSpace: 'pre',
            minHeight: 0,
            color: 'var(--text)',
          }}
        >
          {content}
        </pre>

        <div style={{ display: 'flex', gap: 6, marginTop: 12, alignItems: 'center' }}>
          <span style={{ fontSize: 10.5, color: 'var(--text-muted)' }}>
            choose a format above · output updates live
          </span>
          <div style={{ marginLeft: 'auto', display: 'flex', gap: 6 }}>
            <button onClick={copy} title="Copy to clipboard" style={{ fontWeight: 600 }}>
              {copied ? 'Copied ✓' : 'Copy'}
            </button>
            <button onClick={() => downloadExport(record, format)} title={`Download as .${activeMeta.ext}`}>
              Download .{activeMeta.ext}
            </button>
            <button onClick={onClose} style={{ background: 'transparent' }}>Close</button>
          </div>
        </div>
      </div>
    </div>
  )
}

function formatBytes(n: number): string {
  if (n < 1024) return `${n} B`
  if (n < 1024 * 1024) return `${(n / 1024).toFixed(n < 10 * 1024 ? 2 : 1)} KB`
  return `${(n / (1024 * 1024)).toFixed(2)} MB`
}
