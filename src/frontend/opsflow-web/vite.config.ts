/// <reference types="vitest/config" />
import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'

// https://vite.dev/config/
export default defineConfig({
  plugins: [react()],
  server: {
    // In local development the browser talks to Vite (same origin) and Vite
    // forwards /api/** to the ASP.NET Core API. This keeps the SPA and the
    // API on one origin so the refresh cookie (HttpOnly, Secure, SameSite=Strict,
    // Path=/api/v1/auth) is set and sent by the browser without any CORS setup.
    proxy: {
      '/api': {
        target: 'http://localhost:5203',
        changeOrigin: false,
      },
    },
  },
  test: {
    environment: 'jsdom',
    setupFiles: ['./src/test/setup.ts'],
  },
})
