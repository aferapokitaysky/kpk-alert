import react from '@vitejs/plugin-react';
import { fileURLToPath } from 'node:url';
import { resolve } from 'node:path';
import { defineConfig } from 'vitest/config';

const clientRoot = resolve(
  fileURLToPath(new URL('.', import.meta.url)),
  'src/Watcher.Api/ClientApp',
);

export default defineConfig({
  root: clientRoot,
  plugins: [react()],
  server: {
    proxy: {
      '/api': {
        target: process.env.VITE_API_PROXY ?? 'https://localhost:5001',
        changeOrigin: true,
        secure: false,
      },
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
});
