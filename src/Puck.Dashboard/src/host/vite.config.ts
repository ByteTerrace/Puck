import { federation } from "@module-federation/vite";
import react from "@vitejs/plugin-react";
import { defineConfig } from "vite";
import lockfile from "./package-lock.json" with { type: "json" };

function getLockedVersions(packageNames: string[]): Record<string, string> {
  const packages: Record<string, { version?: string }> = lockfile.packages;
  const versions: Record<string, string> = {};

  for (const name of packageNames) {
    const entry = packages[`node_modules/${name}`];

    if (entry?.version) {
      versions[name] = entry.version;
    } else {
      throw new Error(`Could not resolve locked version for "${name}".`);
    }
  }

  return versions;
}

const sharedPackages = [
  "@azure/identity",
  "@azure/msal-browser",
  "@azure/msal-react",
  "@reduxjs/toolkit",
  "@uidotdev/usehooks",
  "react",
  "react-dom",
  "react-redux",
  "rxjs",
];

export default defineConfig(() => ({
  build: {
    emptyOutDir: true,
    outDir: "../../dist/host",
    target: "esnext",
  },
  define: {
    __LOCKED_VERSIONS__: JSON.stringify(getLockedVersions(sharedPackages)),
  },
  plugins: [
    react(),
    federation({
      exposes: {},
      filename: "host-entry.js",
      name: "host",
      shared: sharedPackages,
    }),
  ],
  preview: {
    origin: "http://localhost:61100",
    port: 61100,
    strictPort: true,
  },
  server: {
    origin: "http://localhost:61100",
    port: 61100,
    proxy: {
      "/api": {
        changeOrigin: true,
        target: "https://api.byteterrace.com",
      },
    },
    strictPort: true,
  },
}));
