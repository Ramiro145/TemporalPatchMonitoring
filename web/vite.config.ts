import path from 'node:path'
import tailwindcss from '@tailwindcss/vite'
import react from '@vitejs/plugin-react'
import { defineConfig } from 'vite'

// https://vite.dev/config/
export default defineConfig({
  plugins: [react(), tailwindcss()],
  resolve: {
    alias: {
      '@': path.resolve(import.meta.dirname, './src'),
    },
  },
  server: {
    proxy: {
      // El front llama siempre a rutas relativas /api/...; nunca hardcodea localhost:5100,
      // para que el mismo build sirva en dev (este proxy) y detrás de nginx (spec 11, paso 10).
      '/api': {
        target: 'http://localhost:5100',
        changeOrigin: true,
        rewrite: (urlPath) => urlPath.replace(/^\/api/, ''),
      },
    },
  },
})
