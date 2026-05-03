import { useState } from 'react'
import type { RequestRecord } from './types'
import { downloadHttpFile, generateHttpFile } from './httpFile'

interface Props {
  record: RequestRecord | null
  open: boolean
  onClose: () => void
}

export function HttpFileDialog({ record, open, onClose }: Props) {
  const [copied, setCopied] = useState(false)

  if (!open || !record) return null

  const content = generateHttpFile(record)

  const copy = async () => {
    try {
      await navigator.clipboard.writeText(content)
      setCopied(true)
      setTimeout(() => setCopied(false), 1500)
    } catch {
      /* ignore */
    }
  }

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
          padding: '20px',
          width: '720px',
          maxWidth: '90vw',
          maxHeight: '85vh',
          display: 'flex',
          flexDirection: 'column',
          boxShadow: '0 10px 30px rgba(0, 0, 0, 0.3)',
        }}
      >
        <div style={{ display: 'flex', alignItems: 'center', gap: '10px', marginBottom: '12px' }}>
          <h3 style={{ margin: 0, fontSize: '14px' }}>Export .http</h3>
          <span style={{ fontSize: '11px', color: 'var(--text-muted)' }}>
            VS Code REST Client / IntelliJ HTTP Client
          </span>
          <div style={{ marginLeft: 'auto', display: 'flex', gap: '6px' }}>
            <button onClick={copy} title="Copy to clipboard">
              {copied ? 'Copied ✓' : 'Copy'}
            </button>
            <button onClick={() => downloadHttpFile(record)} title="Download as .http file">
              Download
            </button>
            <button onClick={onClose}>Close</button>
          </div>
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
            whiteSpace: 'pre-wrap',
            wordBreak: 'break-word',
            minHeight: 0,
          }}
        >
          {content}
        </pre>
      </div>
    </div>
  )
}
