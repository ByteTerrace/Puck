import { useEffect, useMemo } from "react";
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
    // Validation, composition and preview stay off the page's input/rendering thread.
    engineMode: "worker" as const,
    bootEngine: bootEngineFromOfficial,
  }), []);

  return (
    <StudioContext.Provider options={{ input }}>
      {import.meta.env.DEV && <DevelopmentHook />}
      <StudioShellWithConfirmation />
    </StudioContext.Provider>
  );
}

/** Development builds only: the studio actor as `window.__puckStudio`, so a browser session can ask the engines for
 * their counts (`context.languageEngine.wasmCounts()`) the way the Node tests do. */
function DevelopmentHook() {
  const actor = StudioContext.useActorRef();
  useEffect(() => {
    const page = window as { __puckStudio?: unknown };
    page.__puckStudio = actor;
    return () => { delete page.__puckStudio; };
  }, [actor]);
  return null;
}

export default WorldStudio;
