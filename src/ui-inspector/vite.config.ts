import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'

export default defineConfig(() => {
  const inspectorApiUrl = process.env['VITE_INSPECTOR_API_URL'] ?? 'http://localhost:5198'
  const port = Number(process.env['PORT'] ?? 5197)

  return {
    plugins: [react()],
    server: {
      port,
      strictPort: true,
      proxy: {
        '/api': {
          target: inspectorApiUrl,
          changeOrigin: true,
          ws: false,
        },
      },
    },
  }
})
