/**
 * The "puck.official.v1" manifest shape — see the official manifest contract recorded beside
 * this feature's landing change. Parsing refuses an unknown `schema` or a `build.worldSchema`
 * other than "puck.world.definition.v1" by name, a manifest with no `sources[]`, and any object hash not spelled
 * `sha256/<64 lowercase hex>` (the engine's own pin grammar); every other shape mismatch surfaces as a thrown
 * `ManifestRefusal` naming the offending field.
 */

export const OFFICIAL_MANIFEST_SCHEMA = "puck.official.manifest.v1" as const;
export const REQUIRED_WORLD_SCHEMA = "puck.world.definition.v1" as const;

/** The `build.commit` of a manifest whose worlds tree no commit holds: it sat outside every git checkout, or in one
 * with no commit yet. */
export const NO_COMMIT = "none" as const;

/** What a manifest was built from. `commit` and `dirty` name the worlds tree it was read from, never the build of
 * the generator or the engine: that is the world schema bundle's own `x-puck.commit`. */
export interface ManifestBuild {
  /** The HEAD commit of the checkout holding the worlds tree, or {@link NO_COMMIT}. */
  readonly commit: string;
  /** Whether the worlds tree differed from `commit`; always true with {@link NO_COMMIT}. */
  readonly dirty: boolean;
  readonly generator: string;
  readonly worldSchema: string;
}

/** The build line's account of the worlds tree: its abbreviated commit, marked when the tree differed from it, or
 * that no commit holds it. */
export function describeTree(build: Pick<ManifestBuild, "commit" | "dirty">): string {
  if (build.commit === NO_COMMIT) return "worlds tree outside git";
  return `commit ${build.commit.slice(0, 12)}${build.dirty ? " + local edits" : ""}`;
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
  /** The extensionless document name (`games/klondike`, `puck`), treated as opaque. */
  readonly name: string;
  /** The `sources[]` file this document is authored in: its `.puck` source, or its own `.world.json` when it has none. */
  readonly source: string;
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

/** One authoring file under the worlds directory: a `.puck` source, a sidecar lock, or a document with no source.
 * `name` is its worlds-relative path (`games/klondike.puck`). */
export interface ManifestSourceEntry extends ManifestFileEntry {
  readonly name: string;
}

export type AssetFamily = "music" | "table" | "patch" | "tune";

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
  readonly sources: readonly ManifestSourceEntry[];
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

/** An object's content hash as the engine spells it: SHA-256, 64 lowercase hex digits. The engine refuses any other
 * spelling, so a manifest carrying one is refused here, by the field that carries it, before any byte is fetched. */
const HASH_NAME = /^sha256\/[0-9a-f]{64}$/;

function refuseMalformedHashes(manifest: Record<string, unknown>): void {
  const check = (field: string, entry: unknown) => {
    if (isRecord(entry) && !(typeof entry.hash === "string" && HASH_NAME.test(entry.hash))) {
      throw new ManifestRefusal(
        `official manifest ${field}.hash '${String(entry.hash)}' is not 'sha256/' followed by 64 lowercase hex digits.`,
      );
    }
  };
  check("worldSchemaBundle", manifest.worldSchemaBundle);
  const engine = manifest.engine;
  const lists: readonly [string, unknown][] = [
    ["engine.files", isRecord(engine) ? engine.files : undefined],
    ["sources", manifest.sources],
    ["documents", manifest.documents],
    ["composed", manifest.composed],
    ["assets", manifest.assets],
  ];
  for (const [field, list] of lists) {
    if (!Array.isArray(list)) continue;
    list.forEach((entry, index) => check(`${field}[${index}]`, entry));
  }
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

  if (!Array.isArray(value.sources)) {
    throw new ManifestRefusal("official manifest carries no sources[]; the studio edits a document through its source files.");
  }

  refuseMalformedHashes(value);

  return value as unknown as OfficialManifest;
}
