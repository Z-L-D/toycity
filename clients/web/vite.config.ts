import { defineConfig } from 'vite';
import { fileURLToPath } from 'node:url';

const repoRoot = fileURLToPath(new URL('../..', import.meta.url));

// The dev server proxies the WebSocket to a ToyCity server on port 5180.
export default defineConfig({
  server: {
    port: 5173,
    fs: { allow: [repoRoot] },
    proxy: { '/ws': { target: 'ws://localhost:5180', ws: true } },
  },
  build: { outDir: 'dist', emptyOutDir: true },
});
