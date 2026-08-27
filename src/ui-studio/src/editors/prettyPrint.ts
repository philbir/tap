/**
 * Re-indenters for the structured response bodies the viewer knows how to read.
 *
 * Both are *display* transforms used by `CodeBlock`: a minified body is a wall of text with
 * no place to put a fold arrow, and an API that answers on one line is the common case. They
 * are deliberately total — anything that doesn't parse comes back untouched, because a body
 * that failed to parse is exactly the body you most need to read verbatim.
 */

const INDENT = '  '

/** The XML declaration is not a DOM node, so it survives only by being copied across. */
const XML_DECLARATION = /^\s*<\?xml[\s\S]*?\?>/

/** Pretty-print JSON. Invalid JSON (a truncated body, an error page) is returned as-is. */
export function tryPrettyJson(value: string): string {
  if (!value.trim()) return value
  try {
    return JSON.stringify(JSON.parse(value), null, 2)
  } catch {
    return value
  }
}

/**
 * Pretty-print XML by parsing it and writing it back out one node per line.
 *
 * `DOMParser` rather than a regex: SOAP faults, namespaced payloads, CDATA and comments all
 * have to survive the trip, and a tag-matching pass gets each of those wrong in its own way.
 * A parse failure hands back the original text — browsers signal it with a `<parsererror>`
 * element rather than by throwing, so both routes are checked.
 */
export function tryPrettyXml(value: string): string {
  if (!value.trim()) return value
  if (typeof DOMParser === 'undefined') return value

  let doc: Document
  try {
    doc = new DOMParser().parseFromString(value, 'application/xml')
  } catch {
    return value
  }
  if (!doc.documentElement) return value
  if (doc.getElementsByTagName('parsererror').length > 0) return value

  const out: string[] = []
  const declaration = XML_DECLARATION.exec(value)
  if (declaration) out.push(declaration[0].trim())
  for (const node of Array.from(doc.childNodes)) writeNode(node, 0, out)

  const text = out.join('\n')
  return text.trim() ? text : value
}

function writeNode(node: Node, depth: number, out: string[]): void {
  const pad = INDENT.repeat(depth)
  switch (node.nodeType) {
    case Node.ELEMENT_NODE:
      writeElement(node as Element, depth, out)
      return
    case Node.TEXT_NODE: {
      const text = (node.nodeValue ?? '').trim()
      if (text) out.push(pad + escapeText(text))
      return
    }
    case Node.CDATA_SECTION_NODE:
      out.push(`${pad}<![CDATA[${node.nodeValue ?? ''}]]>`)
      return
    case Node.PROCESSING_INSTRUCTION_NODE: {
      const pi = node as ProcessingInstruction
      out.push(`${pad}<?${pi.target}${pi.data ? ` ${pi.data}` : ''}?>`)
      return
    }
    case Node.COMMENT_NODE:
      out.push(`${pad}<!--${node.nodeValue ?? ''}-->`)
      return
    case Node.DOCUMENT_TYPE_NODE:
      out.push(pad + writeDoctype(node as DocumentType))
      return
  }
}

function writeElement(el: Element, depth: number, out: string[]): void {
  const pad = INDENT.repeat(depth)
  const open = `<${el.nodeName}${writeAttributes(el)}`
  const kids = Array.from(el.childNodes).filter(isSignificant)

  if (kids.length === 0) {
    out.push(`${pad}${open} />`)
    return
  }

  // A lone text or CDATA child stays inline — `<id>42</id>` reads better than three lines,
  // and a leaf per line is what makes the rest of the tree scannable.
  if (kids.length === 1 && isTextual(kids[0])) {
    out.push(`${pad}${open}>${inlineText(kids[0])}</${el.nodeName}>`)
    return
  }

  // Mixed content — text sitting between child elements — is the one shape where indenting
  // changes what the document says: `<p>a <b>b</b></p>` is not the same string once the
  // text is on its own line. Hand that subtree back exactly as it arrived.
  if (kids.some((k) => k.nodeType === Node.TEXT_NODE) && kids.some((k) => k.nodeType === Node.ELEMENT_NODE)) {
    for (const line of new XMLSerializer().serializeToString(el).split('\n')) out.push(pad + line)
    return
  }

  out.push(`${pad}${open}>`)
  for (const kid of kids) writeNode(kid, depth + 1, out)
  out.push(`${pad}</${el.nodeName}>`)
}

/** Whitespace between elements is layout, not content — it is what we are replacing. */
function isSignificant(node: Node): boolean {
  return node.nodeType !== Node.TEXT_NODE || (node.nodeValue ?? '').trim().length > 0
}

function isTextual(node: Node): boolean {
  return node.nodeType === Node.TEXT_NODE || node.nodeType === Node.CDATA_SECTION_NODE
}

function inlineText(node: Node): string {
  return node.nodeType === Node.CDATA_SECTION_NODE
    ? `<![CDATA[${node.nodeValue ?? ''}]]>`
    : escapeText((node.nodeValue ?? '').trim())
}

function writeAttributes(el: Element): string {
  let out = ''
  for (const attr of Array.from(el.attributes)) out += ` ${attr.name}="${escapeAttribute(attr.value)}"`
  return out
}

function writeDoctype(dt: DocumentType): string {
  const external = dt.publicId
    ? ` PUBLIC "${dt.publicId}"${dt.systemId ? ` "${dt.systemId}"` : ''}`
    : dt.systemId ? ` SYSTEM "${dt.systemId}"` : ''
  return `<!DOCTYPE ${dt.name}${external}>`
}

function escapeText(value: string): string {
  return value.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;')
}

function escapeAttribute(value: string): string {
  return escapeText(value).replace(/"/g, '&quot;')
}
