/// <reference types="vite/client" />

interface ImportMetaEnv {
  readonly VITE_TAPS?: string
  readonly VITE_JWT_SECRET?: string
  readonly VITE_JWT_ISSUER?: string
  readonly VITE_JWT_AUDIENCE?: string
}

interface ImportMeta {
  readonly env: ImportMetaEnv
}
