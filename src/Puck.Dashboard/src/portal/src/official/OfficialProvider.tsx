import { createContext, useContext, useEffect, useState, type ReactNode } from "react";
import { loadOfficial, type OfficialLoad } from "./officialClient";
import { resolveOfficial } from "./officialBase";

export type OfficialState =
  | { status: "loading"; official?: undefined; refusal?: undefined }
  | { status: "ready"; official: OfficialLoad; refusal?: undefined }
  | { status: "refused"; official?: undefined; refusal: Error };

const LOADING_STATE: OfficialState = { status: "loading" };

// import.meta.env is real only inside Vite-bundled browser code — this is the one call site
// that reads it (see officialBase.ts's header for why it stays out of every other module).
const OfficialContext = createContext<OfficialState>(LOADING_STATE);

export function OfficialProvider({ children }: { children: ReactNode }) {
  const [state, setState] = useState<OfficialState>(LOADING_STATE);

  useEffect(() => {
    let cancelled = false;

    (async () => {
      try {
        const official = resolveOfficial(import.meta.env);
        const loaded = await loadOfficial(official);
        if (!cancelled) {
          setState({ status: "ready", official: loaded });
        }
      } catch (error) {
        if (!cancelled) {
          setState({ status: "refused", refusal: error instanceof Error ? error : new Error(String(error)) });
        }
      }
    })();

    return () => {
      cancelled = true;
    };
  }, []);

  return <OfficialContext.Provider value={state}>{children}</OfficialContext.Provider>;
}

export function useOfficial(): OfficialState {
  return useContext(OfficialContext);
}
