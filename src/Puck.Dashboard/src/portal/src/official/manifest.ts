/**
 * The "puck.official.v1" manifest shape — see the official manifest contract recorded beside
 * this feature's landing change. Parsing refuses an unknown `schema` or a `build.worldSchema`
 * other than "puck.world.def.v1" by name; every other shape mismatch surfaces as a thrown
 * `ManifestRefusal` naming the offending field.
 */

export const OFFICIAL_MANIFEST_SCHEMA = "puck.official.v1" as const;
export const REQUIRED_WORLD_SCHEMA = "puck.world.def.v1" as const;

export interface ManifestBuild {
  readonly commit: string;
  readonly dirty: boolean;
  readonly generator: string;
  readonly worldSchema: string;
}

export interface ManifestFileEntry {
  readonly path: string;
  readonly hash: string;
  readonly size: number;
  readonly contentType: string;
}

export interface ManifestEngineFile extends ManifestFileEntry {
  readonly name: string;
}

export interface ManifestEngine {
  readonly entry: string;
  readonly files: readonly ManifestEngineFile[];
}

export type DocumentRole = "world" | "basis" | "fragment" | "shard";

export interface ManifestDocumentImport {
  readonly document: string;
  readonly as: string | null;
}

export interface ManifestDocumentEntry extends ManifestFileEntry {
  readonly name: string;
  readonly role: DocumentRole;
  readonly documentId: string | null;
  readonly imports: readonly ManifestDocumentImport[];
  readonly exports: readonly string[];
  readonly pin: string | null;
}

export interface ManifestComposedEntry extends ManifestFileEntry {
  readonly documentId: string;
  readonly name: string;
  readonly pin: string;
  readonly identity: unknown | null;
}

export type AssetFamily = "music" | "table" | "audio" | "synth" | "font" | "patch" | "tune";

export interface ManifestAssetEntry extends ManifestFileEntry {
  readonly family: AssetFamily;
  readonly name: string;
  readonly source: string;
  readonly pin: string | null;
}

export interface OfficialManifest {
  readonly schema: typeof OFFICIAL_MANIFEST_SCHEMA;
  readonly channel: string;
  readonly build: ManifestBuild;
  readonly worldSchemaBundle: ManifestFileEntry;
  readonly engine: ManifestEngine;
  readonly documents: readonly ManifestDocumentEntry[];
  readonly composed: readonly ManifestComposedEntry[];
  readonly assets: readonly ManifestAssetEntry[];
  readonly signature: string | null;
}

export class ManifestRefusal extends Error {
  constructor(message: string) {
    super(message);
    this.name = "ManifestRefusal";
  }
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === "object" && value !== null && !Array.isArray(value);
}

/** Parses and structurally refuses an official manifest. Never verifies object bytes — that is verify.ts's job. */
export function parseManifest(text: string): OfficialManifest {
  let value: unknown;
  try {
    value = JSON.parse(text);
  } catch (error) {
    throw new ManifestRefusal(`official manifest is not valid JSON: ${(error as Error).message}`);
  }

  if (!isRecord(value)) {
    throw new ManifestRefusal("official manifest is not a JSON object.");
  }

  if (value.schema !== OFFICIAL_MANIFEST_SCHEMA) {
    throw new ManifestRefusal(
      `official manifest names unknown schema '${String(value.schema)}'; expected '${OFFICIAL_MANIFEST_SCHEMA}'.`,
    );
  }

  const build = value.build;
  if (!isRecord(build) || build.worldSchema !== REQUIRED_WORLD_SCHEMA) {
    throw new ManifestRefusal(
      `official manifest build.worldSchema '${String(isRecord(build) ? build.worldSchema : undefined)}' ` +
        `does not match '${REQUIRED_WORLD_SCHEMA}'.`,
    );
  }

  return value as unknown as OfficialManifest;
}
