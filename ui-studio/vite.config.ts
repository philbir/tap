import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'

export default defineConfig(() => {
  const studioApiUrl = process.env['VITE_STUDIO_API_URL'] ?? 'http://localhost:5298'
  const port = Number(process.env['PORT'] ?? 5297)

  return {
    plugins: [react()],
    server: {
      port,
      strictPort: true,
      proxy: {
        '/api': {
          target: studioApiUrl,
          changeOrigin: true,
          ws: false,
        },
      },
    },
  }
})
