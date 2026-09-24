import { federation } from "@module-federation/vite";
import babel from "@rolldown/plugin-babel";
import react, { reactCompilerPreset } from "@vitejs/plugin-react";
import { fileURLToPath } from "node:url";
import { defineConfig, loadEnv, type Plugin, searchForWorkspaceRoot } from "vite";
import { duckdbExtensions } from "../build/duckdbExtensions.ts";
import { federationShare } from "../build/federationShare.ts";
import { officialProxy } from "../build/officialProxy.ts";
import { strictBuild } from "../build/strictBuild.ts";

// The 3D viewport's vendor code loads only when the Spatial tab opens a volumetric topology. three.js is one
// module graph React Three Fiber registers whole (its `extend(THREE)` catalogue defeats tree shaking), so it
// is split by size into chunks under the default 500 kB budget rather than raising the budget. A group also takes
// its modules' dependencies, so the React runtime the rest of the portal shares with React Three Fiber
// (use-sync-external-store, used by @xstate/react and zustand; scheduler, used by react-dom and the reconciler) is
// claimed first by a group of its own: otherwise it lands in the r3f chunk, and every page would load three.js to
// get it. `spatialVendorsStayLazy` checks the result.
const spatialVendors = [
  { name: "react-runtime", test: /node_modules[\\/](use-sync-external-store|scheduler)[\\/]/, priority: 1 },
  { name: "three", test: /node_modules[\\/]three[\\/]/, maxSize: 480_000 },
  { name: "r3f", test: /node_modules[\\/](@react-three|three-stdlib|troika-|camera-controls|maath|meshline|stats-gl|@monogrid|three-mesh-bvh|@use-gesture|its-fine|suspend-react|zustand|detect-gpu|tunnel-rat)/ },
];

/** Fails the build when anything but the lazy 3D viewport imports the 3D vendor chunks statically. */
const spatialVendorsStayLazy: Plugin = {
  name: "puck-spatial-vendors-stay-lazy",
  apply: "build",
  generateBundle(_options, bundle) {
    const vendor = (fileName: string) => /(^|\/)(three|r3f)-[^/]*\.js$/.test(fileName);

    for (const output of Object.values(bundle)) {
      if ("chunk" !== output.type || vendor(output.fileName) || /(^|\/)SpatialTopology3D-[^/]*\.js$/.test(output.fileName)) {
        continue;
      }

      const eager = output.imports.filter(vendor);

      if (0 < eager.length) {
        this.error(`${output.fileName} statically imports ${eager.join(", ")}; 3D vendor code must load only with the 3D viewport.`);
      }
    }
  },
};

export default defineConfig(({ command, mode }) => {
  const env = loadEnv(mode, process.cwd(), "VITE_");
  const official = officialProxy(env.VITE_PUCK_OFFICIAL_PROXY_TARGET || undefined);
  const strict = strictBuild([
    {
      code: "IMPORT_IS_UNDEFINED",
      reason: "React Three Fiber probes React.act by computed name for its test utilities; production React has no act, and the probe expects undefined.",
      matches: (log) => /@react-three[\\/]fiber/.test(log.id ?? "") && log.message.includes("`act`"),
    },
  ]);

  return {
    base: "./",
    build: {
      emptyOutDir: true,
      outDir: "../../dist/portal",
      rolldownOptions: {
        checks: strict.checks,
        onwarn: strict.onwarn,
        output: { codeSplitting: { groups: spatialVendors } },
      },
      target: "esnext",
    },
    customLogger: command === "build" ? strict.customLogger : undefined,
    plugins: [
      react(),
      babel({ presets: [reactCompilerPreset()] }),
      strict.plugin,
      spatialVendorsStayLazy,
      duckdbExtensions(fileURLToPath(new URL("./duckdb-extensions.json", import.meta.url))),
      federation({
        // No federated types are generated or consumed: the module's contract is shared/interfaces.tsx's PortalModule.
        dev: false,
        dts: false,
        exposes: { "./portal-app": "./src/App.tsx" },
        filename: "portal-entry.js",
        manifest: true,
        name: "portal",
        shared: federationShare,
      }),
    ],
    preview: {
      origin: "http://localhost:61101",
      port: 61101,
      proxy: official,
      strictPort: true,
    },
    server: {
      // The brand tokens and self-hosted fonts live outside the workspace, in branding/ and the docs site theme.
      fs: {
        allow: [searchForWorkspaceRoot(process.cwd()), "../../../../branding", "../../../../docs/site/_theme/fonts"],
      },
      origin: "http://localhost:61101",
      port: 61101,
      proxy: official,
      strictPort: true,
    },
    // engine.worker.ts is a module Worker (`new Worker(url, { type: "module" })` in engineHost.ts/engineBoot.ts);
    // 'es' keeps its own bundle an ES module too, so its `import()` of dotnet.js-family modules and
    // workerBoot.ts's own imports work the same built as they do in dev.
    worker: {
      format: "es",
    },
  };
});
