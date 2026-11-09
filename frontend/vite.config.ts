/// <reference types="vitest/config" />
import { fileURLToPath, URL } from 'node:url'
import { defineConfig } from 'vite'
import vue from '@vitejs/plugin-vue'

// The SPA is served same-origin with the API (reverse proxy in production, Vite proxy in development)
// so session cookies stay host-only per tenant domain (ADR-004, ADR-006).
export default defineConfig({
  plugins: [vue()],
  resolve: {
    alias: {
      '@': fileURLToPath(new URL('./src', import.meta.url)),
      // Shared editor contract (manifests + fixtures) at the repository root, also read by the backend (ADR-010).
      '@schema': fileURLToPath(new URL('../schema', import.meta.url)),
    },
  },
  server: {
    port: 5173,
    fs: { allow: ['..'] },
    // *.raavi.localhost resolves to loopback in modern browsers, which lets developers test subdomain tenants.
    allowedHosts: ['.raavi.localhost', 'localhost'],
    proxy: {
      '/api': { target: 'http://localhost:5180', changeOrigin: false },
      // Path-addressed workspaces (/t/<slug>/api, /t/<slug>/hubs) and the system area's API (/system/api), ADR-045.
      '^/t/[^/]+/(api|hubs|health)': { target: 'http://localhost:5180', changeOrigin: false, ws: true },
      '^/system/api': { target: 'http://localhost:5180', changeOrigin: false },
      '/health': { target: 'http://localhost:5180', changeOrigin: false },
      // In-app event channel (SignalR over WebSockets).
      '/hubs': { target: 'http://localhost:5180', changeOrigin: false, ws: true },
    },
  },
  test: {
    environment: 'jsdom',
    include: ['tests/**/*.test.ts'],
    setupFiles: ['tests/setup.ts'],
    // Several tests mount the whole app with a real editor; under parallel jsdom workers that can exceed the 5 s default.
    testTimeout: 20000,
  },
})
