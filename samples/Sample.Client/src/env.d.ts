/// <reference types="vite/client" />

interface ImportMetaEnv {
  readonly VITE_TAPS?: string
}

interface ImportMeta {
  readonly env: ImportMetaEnv
}
