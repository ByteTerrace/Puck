import { getInstance, loadRemote, registerRemotes } from "@module-federation/runtime";
import type { PortalModule } from "../../shared/interfaces";

/**
 * Registers the portal with the federation instance the Vite plugin created for the host, whose shared
 * singletons are declared once, in `src/build/federationShare.ts`. The portal's own
 * `mf-manifest.json` names its entry, so nothing here restates it.
 */
export function registerPortal(baseUrl: string): void {
  if (!getInstance()) {
    throw new Error("The host's module federation instance is missing; the federation plugin did not initialize.");
  }

  registerRemotes([{ entry: `${baseUrl}/mf-manifest.json`, name: "portal" }]);
}

/** Loads the portal's module. Start it early; React suspends on the same promise. */
export async function loadPortal(): Promise<PortalModule> {
  const portal = await loadRemote<PortalModule>("portal/portal-app");

  if (!portal) {
    throw new Error("The portal remote resolved to nothing.");
  }

  return portal;
}
