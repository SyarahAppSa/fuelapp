import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'

// In development the API runs on :5004; the dev server forwards /api calls to it.
export default defineConfig({
  plugins: [react()],
  server: { proxy: { '/api': 'http://localhost:5004' } },
})
