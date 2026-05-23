import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'

export default defineConfig({
  plugins: [react()],
  build: {
    target: 'esnext',
    minify: 'esbuild', // Changed from 'terser' to 'esbuild' (built-in, faster, no extra dependency)
    sourcemap: false,
    rollupOptions: {
      output: {
        manualChunks: {
          'react-vendor': ['react', 'react-dom', 'react-router-dom'],
          'form-vendor': ['react-hook-form', '@hookform/resolvers', 'zod'],
          'ui-vendor': ['react-toastify'],
          'data-vendor': ['xlsx'],
        },
      },
    },
  },
  server: {
    host: '0.0.0.0', 
    port: 5173,
    strictPort: false, 
    open: true, 
    proxy: {
      '/api': {
        target: 'https://localhost:7079',
        changeOrigin: true,
        secure: false,
      },
    },
  },
})
