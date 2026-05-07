export interface EndpointParameter {
  name: string
  default: string
  description?: string
}

export interface EndpointDescriptor {
  method: string
  path: string
  description: string
  parameters?: EndpointParameter[]
  sampleBody?: string
  isStream?: boolean
  requiresAuth?: boolean
}

export interface SseTick {
  id: string | null
  event: string
  data: string
  receivedAt: string
}

export interface TapDescriptor {
  name: string
  mode: string
  url: string
  requiresJwt?: boolean
}
