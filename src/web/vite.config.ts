import { defineConfig } from "vite";
import react from "@vitejs/plugin-react";
import { fileURLToPath, URL } from "node:url";

// 后端开发地址（launchSettings http profile）
const BACKEND = "http://localhost:5211";

export default defineConfig({
  plugins: [react()],
  resolve: {
    alias: {
      "@": fileURLToPath(new URL("./src", import.meta.url)),
    },
  },
  server: {
    port: 5173,
    proxy: {
      "/api": { target: BACKEND, changeOrigin: true, ws: true },
      "/i": { target: BACKEND, changeOrigin: true },
      "/client-updates": { target: BACKEND, changeOrigin: true },
    },
  },
  build: {
    // 构建产物直接输出到 .NET 服务端 wwwroot，覆盖旧 bundle
    outDir: "../CfSpeedtest.Server/wwwroot",
    emptyOutDir: true,
    chunkSizeWarningLimit: 1200,
  },
});
