import { useMemo } from "react";
import { StudioContext } from "../../context/StudioContext";
import { bootEngineFromOfficial } from "../../native/engineBoot";
import { resolveOfficial } from "../../official/officialBase";
import { StudioShellWithConfirmation } from "./StudioShell";

/**
 * The studio's page entry: resolves the official content root from Vite's own env
 * (`VITE_PUCK_OFFICIAL_BASE`/`VITE_PUCK_OFFICIAL_CHANNEL`) and mounts `StudioContext.Provider`
 * around the composed shell (`StudioShell.tsx`). Deliberately the ONLY file in this package that
 * reads `import.meta.env` — a literal `import.meta` token anywhere in a module breaks this
 * repository's Node/CommonJS test harness the instant anything requires it (see
 * `official/officialBase.ts`'s own header), so this file stays a thin wrapper nothing else
 * imports and no test ever requires; every actual UI piece lives in `StudioShell.tsx` and is
 * exercised there, against a hand-built `StudioMachineInput` exactly the way
 * `tests/studioMachine.test.cjs`/`tests/workbench.test.cjs` already do.
 */
export function WorldStudio() {
  const input = useMemo(() => ({
    official: resolveOfficial(import.meta.env),
    // Inline, not worker: with the AOT AppBundle, dotnet.js booted inside a module Worker loads every
    // assembly, logs onRuntimeInitialized, and then never resolves create() (verified in Chromium
    // with diagnostic tracing; the same boot on the main thread resolves in ~250 ms warm).
    // Engine calls therefore block the UI thread for their duration (about 2 s to compose or compile
    // the island). Flip this back to "worker" once the worker boot is proven in a real browser.
    engineMode: "inline" as const,
    bootEngine: bootEngineFromOfficial,
  }), []);

  return (
    <StudioContext.Provider options={{ input }}>
      <StudioShellWithConfirmation />
    </StudioContext.Provider>
  );
}

export default WorldStudio;
