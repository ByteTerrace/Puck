import { AVAILABLE_PRESETS } from "../catalog/worldCatalog";
import { inspectWorldDocument } from "../engine/documentValidation";
export interface WorldMetadata {
  id: string;
  name: string;
  description?: string;
  author?: string;
  category?: "Strategy" | "Lattice CAD" | "Board" | "Ring" | "Custom";
  visibility: "private" | "public";
  version: string;
  revision: number;
  lastModified: string;
  rulesCount: number;
  topologyType: string;
  checkpointHash?: string;
}
export interface WorldVaultItem {
  metadata: WorldMetadata;
  world: any;
  isPreset?: boolean;
  isForked?: boolean;
}
export interface LocalCheckpoint {
  revision: number;
  hash: string;
  timestamp: string;
  world?: any;
}
type StoredWorld = WorldVaultItem & {
  checkpoints?: LocalCheckpoint[];
};
const VAULT_KEY = "byteterrace.puck.worldVault.v1";
const validId = (id: string) => {
  if(!/^[a-z0-9][a-z0-9-]{0,63}$/.test(id) || ["constructor", "prototype"].includes(id))
    throw new Error("Invalid local document ID.");
};
export async function computeContentHash(content: string): Promise<string> {
  const bytes = await crypto.subtle.digest("SHA-256", new TextEncoder().encode(content));
  return "sha256/" + Array.from(new Uint8Array(bytes), b => b.toString(16).padStart(2, "0")).join("");
}
/** Offline document storage. A single atomic localStorage write contains document and revisions. */
export class WorldStorageClient {
  private read(): Record<string, StoredWorld> {
    const raw = localStorage.getItem(VAULT_KEY);
    if(!raw)
      return {};
    const vault = JSON.parse(raw);
    if(!vault || Array.isArray(vault) || typeof vault !== "object" || Object.values(vault).some((item: any) => !item?.metadata || !item?.world))
      throw new Error("Local library is unreadable. Export or recover browser storage before saving.");
    return vault;
  }
  private write(vault: Record<string, StoredWorld>) {
    try {
      localStorage.setItem(VAULT_KEY, JSON.stringify(vault));
    }
    catch {
      throw new Error("Could not save in this browser. Storage may be full or unavailable. Export JSON to keep your document.");
    }
  }
  public listPublicWorlds(): WorldVaultItem[] {
    return AVAILABLE_PRESETS.map(p => ({
      world: p.world, isPreset: true,
      metadata: {
        id: p.id, name: p.name, description: p.subtitle, author: "ByteTerrace", category: p.category,
        visibility: "public", version: "1.0.0", revision: 0, lastModified: "", rulesCount: p.world.rules?.length ?? 0,
        topologyType: p.world.state?.lattices?.[0]?.$type ?? "none"
      }
    }));
  }
  public async listPrivateWorlds(): Promise<WorldVaultItem[]> { return Object.values(this.read()); }
  public async saveWorld(id: string, world: any, overrides: Partial<WorldMetadata> = {}): Promise<WorldVaultItem> {
    validId(id);
    const text = JSON.stringify(world, null, 2);
    inspectWorldDocument(text);
    // Hash before reading, so concurrent saves in this tab cannot overwrite an older vault read.
    const hash = await computeContentHash(text);
    const vault = this.read();
    const existing = Object.hasOwn(vault, id) ? vault[id] : undefined;
    const revision = (existing?.metadata.revision ?? 0) + 1;
    const timestamp = new Date().toISOString();
    const metadata: WorldMetadata = {
      ...existing?.metadata, ...overrides, id, name: overrides.name || existing?.metadata.name || id,
      visibility: "private", revision, version: "1." + revision + ".0", lastModified: timestamp,
      rulesCount: world.rules?.length ?? 0, topologyType: world.state?.lattices?.[0]?.$type ?? "none", checkpointHash: hash
    };
    const savedWorld = JSON.parse(text);
    const item: StoredWorld = {
      metadata, world: savedWorld, isPreset: false,
      checkpoints: [{ revision, hash, timestamp, world: savedWorld }, ...(existing?.checkpoints ?? [])].slice(0, 10)
    };
    this.write({ ...vault, [id]: item });
    return item;
  }
  public async loadWorld(id: string): Promise<WorldVaultItem | null> {
    const vault = this.read();
    return (Object.hasOwn(vault, id) ? vault[id] : undefined) ?? this.listPublicWorlds().find(item => item.metadata.id === id) ?? null;
  }
  public async deleteWorld(id: string): Promise<boolean> {
    validId(id);
    const vault = this.read();
    if(!Object.hasOwn(vault, id))
      return false;
    delete vault[id];
    this.write(vault);
    return true;
  }
  public async forkWorld(sourceId: string, newId: string, newName?: string): Promise<WorldVaultItem> {
    const source = await this.loadWorld(sourceId);
    if(!source)
      throw new Error("Source document not found.");
    return this.saveWorld(newId, source.world, { ...source.metadata, name: newName ?? source.metadata.name + " (copy)" });
  }
  public listCheckpoints(id: string): LocalCheckpoint[] {
    const vault = this.read();
    return Object.hasOwn(vault, id) ? vault[id].checkpoints ?? [] : [];
  }
  public async generateShareLink(_id: string, _hours = 24): Promise<string> { throw new Error("Offline documents have no share URL. Export JSON to share a file."); }
}
export const defaultWorldStorageClient = new WorldStorageClient();
