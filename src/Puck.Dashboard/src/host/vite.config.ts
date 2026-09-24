import { readFileSync } from "node:fs";
import { federation } from "@module-federation/vite";
import babel from "@rolldown/plugin-babel";
import react, { reactCompilerPreset } from "@vitejs/plugin-react";
import { defineConfig, searchForWorkspaceRoot } from "vite";
import { federationShare } from "../build/federationShare.ts";
import { officialProxy } from "../build/officialProxy.ts";
import { strictBuild } from "../build/strictBuild.ts";

const strict = strictBuild();
// The dashboard's one version is the workspace manifest's; the host and portal ship together under it.
const { version } = JSON.parse(readFileSync(new URL("../package.json", import.meta.url), "utf8")) as { version: string };

export default defineConfig(({ command }) => ({
  build: {
    emptyOutDir: true,
    outDir: "../../dist/host",
    rolldownOptions: {
      checks: strict.checks,
      onwarn: strict.onwarn,
    },
    target: "esnext",
  },
  customLogger: command === "build" ? strict.customLogger : undefined,
  define: {
    __PUCK_DASHBOARD_VERSION__: JSON.stringify(version),
  },
  plugins: [
    react(),
    babel({ presets: [reactCompilerPreset()] }),
    strict.plugin,
    federation({
      // No federated types are generated or consumed: the portal module's contract is shared/interfaces.tsx's
      // PortalModule, and the portal registers at runtime (src/remote.tsx), so its base URL can follow the page.
      dev: false,
      dts: false,
      exposes: {},
      filename: "host-entry.js",
      name: "host",
      shared: federationShare,
    }),
  ],
  preview: {
    origin: "http://localhost:61100",
    port: 61100,
    proxy: officialProxy(),
    strictPort: true,
  },
  server: {
    // The loading screen's palette comes from the brand tokens, which live outside the workspace in branding/.
    fs: {
      allow: [searchForWorkspaceRoot(process.cwd()), "../../../../branding"],
    },
    origin: "http://localhost:61100",
    port: 61100,
    proxy: {
      "/api": {
        changeOrigin: true,
        target: "https://api.byteterrace.com",
      },
      ...officialProxy(),
    },
    strictPort: true,
  },
}));
