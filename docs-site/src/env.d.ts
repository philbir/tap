/// <reference types="vite/client" />

interface ImportMetaEnv {
  readonly VITE_TAP_VERSION: string;
  readonly VITE_TAP_REPO_URL: string;
}

interface ImportMeta {
  readonly env: ImportMetaEnv;
}
