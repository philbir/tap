import { PrismLight as SyntaxHighlighter } from 'react-syntax-highlighter'
import json from 'react-syntax-highlighter/dist/esm/languages/prism/json'
import graphql from 'react-syntax-highlighter/dist/esm/languages/prism/graphql'
import markup from 'react-syntax-highlighter/dist/esm/languages/prism/markup'
import javascript from 'react-syntax-highlighter/dist/esm/languages/prism/javascript'
import typescript from 'react-syntax-highlighter/dist/esm/languages/prism/typescript'
import css from 'react-syntax-highlighter/dist/esm/languages/prism/css'
import { oneDark, oneLight } from 'react-syntax-highlighter/dist/esm/styles/prism'
import { useState, type ReactNode } from 'react'

SyntaxHighlighter.registerLanguage('json', json)
SyntaxHighlighter.registerLanguage('graphql', graphql)
SyntaxHighlighter.registerLanguage('markup', markup)
SyntaxHighlighter.registerLanguage('javascript', javascript)
SyntaxHighlighter.registerLanguage('typescript', typescript)
SyntaxHighlighter.registerLanguage('css', css)

type Language = 'json' | 'graphql' | 'markup' | 'javascript' | 'typescript' | 'css' | 'text'

interface Props {
  body: string | null
  base64?: string | null
  contentType: string | null
  originalSize: number
  truncated: boolean
  theme: 'light' | 'dark'
}

function detectLanguage(contentType: string | null, body: string | null): Language {
  const ct = contentType?.toLowerCase() ?? ''
  if (ct.includes('json')) return 'json'
  if (ct.includes('graphql')) return 'graphql'
  if (ct.includes('typescript')) return 'typescript'
  if (ct.includes('javascript') || ct.includes('ecmascript')) return 'javascript'
  if (ct.includes('css')) return 'css'
  if (ct.includes('xml') || ct.includes('html') || ct.includes('svg')) return 'markup'
  if (!ct && body?.trim().startsWith('<')) return 'markup'
  if (!ct && body?.trim().startsWith('{')) return 'json'
  return 'text'
}

function tryFormat(body: string, lang: Language): string {
  if (lang === 'json') {
    try {
      return JSON.stringify(JSON.parse(body), null, 2)
    } catch {
      return body
    }
  }
  return body
}

function extractGraphql(body: string): { query: string; variables: unknown; operationName: string | null } | null {
  try {
    const parsed = JSON.parse(body) as { query?: unknown; variables?: unknown; operationName?: unknown }
    if (typeof parsed?.query === 'string' && parsed.query.trim().length > 0) {
      return {
        query: parsed.query,
        variables: parsed.variables ?? null,
        operationName: typeof parsed.operationName === 'string' ? parsed.operationName : null,
      }
    }
  } catch {
    /* not json */
  }
  return null
}

function SvgPreview({ svg }: { svg: string }) {
  const src = `data:image/svg+xml;utf8,${encodeURIComponent(svg)}`
  return (
    <img
      src={src}
      alt="SVG response"
      style={{
        maxWidth: '100%',
        background: 'var(--bg-input)',
        border: '1px solid var(--border)',
        borderRadius: '4px',
        padding: '8px',
      }}
    />
  )
}

function Highlighted({ code, language, theme }: { code: string; language: Language; theme: 'light' | 'dark' }) {
  if (language === 'text') {
    return (
      <pre style={{ background: 'var(--bg-input)', padding: '10px', borderRadius: '4px' }}>
        {code}
      </pre>
    )
  }
  return (
    <SyntaxHighlighter
      language={language}
      style={theme === 'dark' ? oneDark : oneLight}
      customStyle={{
        margin: 0,
        borderRadius: '4px',
        padding: '10px',
        fontSize: '12px',
        background: 'var(--bg-input)',
      }}
      codeTagProps={{ style: { fontFamily: 'SF Mono, Menlo, Consolas, monospace' } }}
      wrapLongLines
    >
      {code}
    </SyntaxHighlighter>
  )
}

function SectionLabel({ text, first = false }: { text: string; first?: boolean }) {
  return (
    <div
      style={{
        fontSize: '10px',
        color: 'var(--text-muted)',
        textTransform: 'uppercase',
        letterSpacing: '0.08em',
        fontWeight: 600,
        margin: first ? '0 0 4px' : '10px 0 4px',
      }}
    >
      {text}
    </div>
  )
}

/**
 * Splits body rendering into a sticky toolbar (tab toggle, operation name)
 * and the scrollable content (syntax-highlighted code). RequestDetail can
 * render the toolbar on the fixed "Body" title row and the content inside
 * its scroll container.
 */
export function useCodeView({ body, base64, contentType, originalSize, truncated, theme }: Props): {
  toolbar: ReactNode
  content: ReactNode
} {
  const [tab, setTab] = useState<'view' | 'raw'>('view')

  if (originalSize === 0) {
    return { toolbar: null, content: <div style={{ color: 'var(--text-muted)' }}>(empty body)</div> }
  }

  const ctLower = contentType?.toLowerCase() ?? ''
  const isSvg = ctLower.includes('svg')
  const isImage = ctLower.startsWith('image/')

  if (isImage && !isSvg && base64) {
    const src = `data:${contentType};base64,${base64}`
    return {
      toolbar: null,
      content: (
        <div>
          <img
            src={src}
            alt="Response"
            style={{
              maxWidth: '100%',
              background: 'var(--bg-input)',
              border: '1px solid var(--border)',
              borderRadius: '4px',
              padding: '8px',
            }}
          />
          <div style={{ fontSize: '11px', color: 'var(--text-muted)', marginTop: '6px' }}>
            {contentType} · {originalSize.toLocaleString()} bytes
          </div>
        </div>
      ),
    }
  }

  if (isSvg && body) {
    return {
      toolbar: (
        <div style={{ display: 'flex', gap: '2px', alignItems: 'center' }}>
          {(['view', 'raw'] as const).map((t) => (
            <button
              key={t}
              onClick={() => setTab(t)}
              style={{
                padding: '3px 10px',
                fontSize: '11px',
                background: tab === t ? 'var(--accent)' : 'var(--bg-input)',
                color: tab === t ? '#fff' : 'var(--text-muted)',
                borderColor: tab === t ? 'var(--accent)' : 'var(--border)',
              }}
            >
              {t === 'view' ? 'Preview' : 'Source'}
            </button>
          ))}
          <span style={{ marginLeft: '10px', fontSize: '11px', color: 'var(--text-muted)' }}>
            {contentType} · {originalSize.toLocaleString()} bytes
          </span>
        </div>
      ),
      content: tab === 'view' ? (
        <SvgPreview svg={body} />
      ) : (
        <Highlighted code={body} language="markup" theme={theme} />
      ),
    }
  }

  if (truncated || !body) {
    return {
      toolbar: null,
      content: (
        <div style={{ color: 'var(--text-muted)' }}>
          (body not captured · {contentType ?? 'unknown'} · {originalSize.toLocaleString()} bytes)
        </div>
      ),
    }
  }

  const lang = detectLanguage(contentType, body)
  const gql = lang === 'json' ? extractGraphql(body) : null

  if (!gql) {
    return {
      toolbar: null,
      content: <Highlighted code={tryFormat(body, lang)} language={lang} theme={theme} />,
    }
  }

  const toolbar = (
    <div style={{ display: 'flex', gap: '2px', alignItems: 'center' }}>
      {(['view', 'raw'] as const).map((t) => (
        <button
          key={t}
          onClick={() => setTab(t)}
          style={{
            padding: '3px 10px',
            fontSize: '11px',
            background: tab === t ? 'var(--accent)' : 'var(--bg-input)',
            color: tab === t ? '#fff' : 'var(--text-muted)',
            borderColor: tab === t ? 'var(--accent)' : 'var(--border)',
          }}
        >
          {t === 'view' ? 'GraphQL' : 'Raw JSON'}
        </button>
      ))}
      {gql.operationName && (
        <span style={{ marginLeft: '10px', fontSize: '11px', color: 'var(--text-muted)' }}>
          <code>{gql.operationName}</code>
        </span>
      )}
    </div>
  )

  const content = tab === 'view' ? (
    <>
      <SectionLabel text="Query" first />
      <Highlighted code={gql.query} language="graphql" theme={theme} />
      {gql.variables !== null && gql.variables !== undefined && (
        <>
          <SectionLabel text="Variables" />
          <Highlighted code={JSON.stringify(gql.variables, null, 2)} language="json" theme={theme} />
        </>
      )}
    </>
  ) : (
    <Highlighted code={tryFormat(body, 'json')} language="json" theme={theme} />
  )

  return { toolbar, content }
}
