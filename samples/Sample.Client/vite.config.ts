import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'

export default defineConfig(() => {
  const apiTarget = process.env['VITE_API_URL'] ?? 'http://localhost:5210'
  const port = Number(process.env['PORT'] ?? 5210)

  return {
    plugins: [react()],
    server: {
      port,
      strictPort: false,
      proxy: {
        '/api': { target: apiTarget, changeOrigin: true, ws: false },
      },
    },
    build: {
      outDir: 'dist',
      emptyOutDir: true,
    },
  }
})
