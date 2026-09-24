/**
 * The one share between the host and the portal, declared identically on both sides. Each entry is a page
 * singleton: the host provides it, the portal uses the host's instance, and the portal served on its own falls back
 * to its bundled copy.
 *
 * - `react` and `react-dom`: one React per page, or hooks called from the portal's tree find no dispatcher. A
 *   singleton React is also what makes the federation plugin route a dependency's CommonJS `require("react")`
 *   (use-sync-external-store, under @xstate/react) through the share rather than to a private copy.
 * - `xstate` and `rxjs`: the libraries the dashboard's lifecycles and streams are built on. The host hands the
 *   portal a live actor (`HostContextValue.onboarding`) whose snapshots the portal queries with `can`, `hasTag`,
 *   and `@xstate/react`; one XState per page means the runtime that answers is the one the portal's types
 *   describe. RxJS is shared on the same rule: a runtime dependency both applications declare is one instance.
 *
 * Everything else the host uses (MSAL) the portal never imports at runtime (its `@azure/identity` imports are
 * types), and the portal's own libraries (Mantine, three.js, `@xstate/react`) stay private to it.
 *
 * The host and portal ship in one release, so `strictVersion` turns any skew between them into a load error by
 * name rather than a silent second copy.
 */
export const federationShare = Object.fromEntries(
  ["react", "react-dom", "rxjs", "xstate"].map((name) => [name, { singleton: true, strictVersion: true }]),
);
