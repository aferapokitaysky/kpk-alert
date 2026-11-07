import react from '@vitejs/plugin-react';
import { fileURLToPath } from 'node:url';
import { resolve } from 'node:path';
import { defineConfig } from 'vitest/config';

const clientRoot = resolve(
  fileURLToPath(new URL('.', import.meta.url)),
  'src/Watcher.Api/ClientApp',
);
const apiProxyTarget = process.env.VITE_API_PROXY ?? 'https://localhost:5001';

const createApiProxy = () => ({
  target: apiProxyTarget,
  changeOrigin: true,
  secure: false,
});

export default defineConfig(({ command }) => ({
  root: clientRoot,
  base: command === 'build' ? '/dist/' : '/',
  plugins: [react()],
  server: {
    proxy: {
      '/api': createApiProxy(),
      '/images': createApiProxy(),
      '/manifest.webmanifest': createApiProxy(),
    },
  },
  build: {
    outDir: '../wwwroot/dist',
    emptyOutDir: true,
    cssCodeSplit: false,
    rollupOptions: {
      input: resolve(clientRoot, 'src/main.tsx'),
      output: {
        entryFileNames: 'operator.js',
        chunkFileNames: 'chunks/[name].js',
        assetFileNames: (asset) =>
          asset.name?.endsWith('.css') ? 'operator.css' : 'assets/[name][extname]',
      },
    },
  },
  test: {
    environment: 'jsdom',
    setupFiles: [resolve(clientRoot, 'src/test-setup.ts')],
    clearMocks: true,
  },
}));
