import react from '@vitejs/plugin-react'
import { defineConfig } from 'vite'

export default defineConfig({
  plugins: [react()],
  build: { outDir: '../src/DvQna.Host/wwwroot', emptyOutDir: true },
  server: { proxy: { '/api': 'http://127.0.0.1:5178' } },
})
