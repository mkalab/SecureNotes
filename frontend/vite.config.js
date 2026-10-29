import react from '@vitejs/plugin-react'
import { defineConfig } from 'vite'

const securityHeaders = {
  'Content-Security-Policy': "default-src 'self'; base-uri 'self'; object-src 'none'; frame-ancestors 'none'; form-action 'self'; img-src 'self' data:; script-src 'self'; style-src 'self' 'unsafe-inline'; connect-src 'self' ws: wss:",
  'X-Content-Type-Options': 'nosniff',
  'Referrer-Policy': 'no-referrer',
  'Permissions-Policy': 'camera=(), microphone=(), geolocation=()',
  'X-Frame-Options': 'DENY',
  'Cache-Control': 'no-store',
}

const devSecurityHeaders = {
  ...securityHeaders,
  'Content-Security-Policy': securityHeaders['Content-Security-Policy'].replace(
    "script-src 'self'",
    "script-src 'self' 'unsafe-inline'",
  ),
}

export default defineConfig({
  plugins: [react()],
  server: {
    headers: devSecurityHeaders,
    proxy: {
      '/api': {
        target: process.env.VITE_API_PROXY_TARGET ?? 'https://localhost:7139',
        changeOrigin: true,
        secure: false,
      },
      '/signin-oidc': {
        target: process.env.VITE_API_PROXY_TARGET ?? 'https://localhost:7139',
        changeOrigin: true,
        secure: false,
      },
    },
  },
  preview: {
    headers: securityHeaders,
  },
})
