import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'

export default defineConfig({
  plugins: [react()],
  server: {
    port: 5173,
    proxy: {
      '/api': { target: 'http://localhost:5080', changeOrigin: true },
      '/hub': { target: 'http://localhost:5080', ws: true, changeOrigin: true },
    },
  },
  build: {
    outDir: '../ScpSlPanel.Api/wwwroot',
    emptyOutDir: true,
  },
})
