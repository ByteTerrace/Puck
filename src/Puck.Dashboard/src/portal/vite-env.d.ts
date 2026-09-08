/// <reference types="vite/client" />

interface ImportMetaEnv {
  /** The official content root — see officialBase.ts's resolveOfficial. */
  readonly VITE_PUCK_OFFICIAL_BASE: string;
  /** "stable" | "next" in production, "dev" for a local dry run. */
  readonly VITE_PUCK_OFFICIAL_CHANNEL: string;
  /** Dev-only: overrides the /official proxy target (default http://localhost:61102). */
  readonly VITE_PUCK_OFFICIAL_PROXY_TARGET?: string;
}

interface ImportMeta {
  readonly env: ImportMetaEnv;
}
