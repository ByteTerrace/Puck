import { federation } from "@module-federation/vite";
import react from "@vitejs/plugin-react";
import { defineConfig } from "vite";

export default defineConfig(() => ({
  build: {
    outDir: "../../dist/portal",
    target: "esnext",
  },
  base: "./",
  plugins: [
    react(),
    federation({
      exposes: { "./portal-app": "./src/App.tsx" },
      filename: "portal-entry.js",
      manifest: true,
      name: "portal",
      shared: [
        "@azure/identity",
        "@azure/msal-browser",
        "@azure/msal-react",
        "@reduxjs/toolkit",
        "@uidotdev/usehooks",
        "react",
        "react-dom",
        "react-redux",
        "rxjs",
      ],
    }),
  ],
  preview: {
    origin: "http://localhost:61101",
    port: 61101,
    strictPort: true,
  },
  server: {
    origin: "http://localhost:61101",
    port: 61101,
    strictPort: true,
  },
}));
