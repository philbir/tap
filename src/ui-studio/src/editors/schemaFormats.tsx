import { IconApi, IconPlugConnected } from '@tabler/icons-react'
import type { OpenApiLink, WsdlLink } from '../api/types'

/**
 * The description formats a collection can be generated from.
 *
 * <p>One registry rather than a branch per format in the Schema tab. Each format brings its own
 * wizard, its own lock sidecar, and its own vocabulary for what an "operation" is — but the tab
 * only ever needs to know what to call it, what to show about the link, and whether it can
 * re-sync. GraphQL slots in as another entry here.</p>
 */
export type SchemaFormat = 'openapi' | 'wsdl'

export interface SchemaFormatDescriptor {
  id: SchemaFormat
  /** Short name, used in badges and button labels. */
  label: string
  icon: React.ReactNode
  /** Shown on the empty state, under the buttons. */
  blurb: string
}

export const SCHEMA_FORMATS: SchemaFormatDescriptor[] = [
  {
    id: 'openapi',
    label: 'OpenAPI',
    icon: <IconApi size={14} />,
    blurb: 'REST — pick the operations you want, how they’re laid out, and which security scheme '
      + 'becomes the collection’s default auth.',
  },
  {
    id: 'wsdl',
    label: 'WSDL',
    icon: <IconPlugConnected size={14} />,
    blurb: 'SOAP — each operation becomes a POST with an envelope built from the service’s schema, '
      + 'with the right SOAPAction and content type for its binding.',
  },
]

/**
 * A link, normalized across formats so the Schema tab renders every one of them the same way.
 *
 * <p>`extras` is where a format says whatever only it has to say — an API version, a target
 * namespace, whether a WS-Security header was generated. They render as plain badges, so adding
 * one costs nothing here.</p>
 */
export interface SchemaLink {
  format: SchemaFormat
  label: string
  /** Where it came from: a URL, an uploaded file name, or the bare source kind. */
  source: string
  /** True when there is an address to re-fetch from rather than a file that was uploaded. */
  fromUrl: boolean
  extras: string[]
  layout: string
  trackedOperations: number
  fetchedAt: string
  /** Only formats with a re-sync flow set this. */
  canResync: boolean
}

export function openApiSchemaLink(link: OpenApiLink): SchemaLink {
  return {
    format: 'openapi',
    label: 'OpenAPI',
    source: link.url ?? link.fileName ?? link.sourceKind,
    // `aspire` links carry a URL too — what matters for the icon is whether there is an address
    // to re-fetch from or just an uploaded file.
    fromUrl: !!link.url,
    extras: [
      `OpenAPI ${link.specVersion}`,
      ...(link.apiVersion ? [`api ${link.apiVersion}`] : []),
      ...(link.sourceKind === 'aspire' ? ['aspire'] : []),
    ],
    layout: link.layout,
    trackedOperations: link.trackedOperations,
    fetchedAt: link.fetchedAt,
    canResync: true,
  }
}

export function wsdlSchemaLink(link: WsdlLink): SchemaLink {
  return {
    format: 'wsdl',
    label: 'WSDL',
    source: link.url ?? link.fileName ?? link.sourceKind,
    fromUrl: !!link.url,
    extras: [
      'WSDL 1.1',
      ...(link.serviceName ? [link.serviceName] : []),
      ...(link.usernameTokenHeader ? ['UsernameToken'] : []),
    ],
    layout: link.layout,
    trackedOperations: link.trackedOperations,
    fetchedAt: link.fetchedAt,
    canResync: false,
  }
}
