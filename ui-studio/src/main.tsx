import { ColorSchemeScript, MantineProvider } from '@mantine/core'
import { ModalsProvider } from '@mantine/modals'
import { Notifications } from '@mantine/notifications'
import { StrictMode } from 'react'
import { createRoot } from 'react-dom/client'
import { App } from './App'
import { theme } from './theme'

// Mantine v9 ships its core CSS as a single stylesheet that consumers import directly.
// Order matters: core first, then per-package extras.
import '@mantine/core/styles.css'
import '@mantine/notifications/styles.css'
import '@mantine/dropzone/styles.css'
import '@mantine/tiptap/styles.css'

import './styles/index.css'

createRoot(document.getElementById('root')!).render(
  <StrictMode>
    {/*
      ColorSchemeScript writes a <script> that sets data-mantine-color-scheme on <html>
      BEFORE React mounts, preventing the light/dark flash on first paint. The default
      "auto" scheme also follows `prefers-color-scheme` until the user picks one.
     */}
    <ColorSchemeScript defaultColorScheme="auto" />
    <MantineProvider theme={theme} defaultColorScheme="auto">
      <ModalsProvider>
        <Notifications position="bottom-right" zIndex={2000} />
        <App />
      </ModalsProvider>
    </MantineProvider>
  </StrictMode>,
)
