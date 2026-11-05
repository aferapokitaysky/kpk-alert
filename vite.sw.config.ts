import { fileURLToPath } from 'node:url';
import { resolve } from 'node:path';
import { defineConfig } from 'vite';

const clientRoot = resolve(
  fileURLToPath(new URL('.', import.meta.url)),
  'src/Watcher.Api/ClientApp',
);

export default defineConfig({
  root: clientRoot,
  build: {
    outDir: '../wwwroot',
    emptyOutDir: false,
    lib: {
      entry: resolve(clientRoot, 'src/service-worker.ts'),
      formats: ['es'],
      fileName: () => 'service-worker.js',
    },
  },
});
