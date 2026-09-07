import { federation } from "@module-federation/vite";
import react from "@vitejs/plugin-react";
import { defineConfig, loadEnv } from "vite";

export default defineConfig(({ mode }) => {
  const env = loadEnv(mode, process.cwd(), "VITE_");
  // Dev only: the studio's own dev server proxies /official/* to a local `puck official serve`
  // so VITE_PUCK_OFFICIAL_BASE can stay the same relative "/official" path it is in production.
  const officialProxyTarget = env.VITE_PUCK_OFFICIAL_PROXY_TARGET || "http://localhost:61102";

  return {
    build: {
      outDir: "../../dist/portal",
      target: "esnext",
    },
    base: "./",
    // engine.worker.ts is a module Worker (`new Worker(url, { type: "module" })` in
    // engineHost.ts/engineBoot.ts) — 'es' keeps its own bundle an ES module too, so its
    // `import()` of dotnet.js-family modules and workerBoot.ts's own imports work the same built
    // as they do in dev.
    worker: {
      format: "es",
    },
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
      proxy: {
        // The dev proxy plays the platform edge: production's Front Door rewrites /official/* onto the
        // official container, so the local `puck official serve` (rooted at /) needs the same prefix stripped.
        "/official": {
          target: officialProxyTarget,
          changeOrigin: true,
          rewrite: (requestPath: string) => requestPath.replace(/^\/official/, ""),
        },
      },
    },
  };
});
