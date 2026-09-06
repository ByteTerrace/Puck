import { TokenCredential } from "@azure/identity";
import { BlobServiceClient } from "@azure/storage-blob";
import {
  isByteTerraceStorageUrl,
  resolveStorageEndpoint,
} from "./resolveStorageEndpoint";
import { AVAILABLE_PRESETS } from "../catalog/worldCatalog";

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

const LOCAL_STORAGE_VAULT_KEY = "byteterrace.puck.worldVault.v1";
const LOCAL_CHECKPOINTS_KEY = "byteterrace.puck.checkpoints.v1";

// Helper to compute sha256 hex pin
export async function computeContentHash(content: string): Promise<string> {
  const enc = new TextEncoder();
  const data = enc.encode(content);
  const hashBuffer = await crypto.subtle.digest("SHA-256", data);
  const hashArray = Array.from(new Uint8Array(hashBuffer));
  const hex = hashArray.map((b) => b.toString(16).padStart(2, "0")).join("");
  return `sha256-64/${hex.slice(0, 16)}`;
}

export class WorldStorageClient {
  private tokenCredential?: TokenCredential;
  private apiTokenScopes: string[];
  private storageTokenScopes: string[];

  constructor(
    tokenCredential?: TokenCredential,
    apiTokenScopes: string[] = ["https://api.byteterrace.com/user_impersonation"],
    storageTokenScopes: string[] = ["https://storage.azure.com/.default"]
  ) {
    this.tokenCredential = tokenCredential;
    this.apiTokenScopes = apiTokenScopes;
    this.storageTokenScopes = storageTokenScopes;
  }

  // --- Local Vault Persistence (IndexedDB/localStorage) ---
  private getLocalVault(): Record<string, WorldVaultItem> {
    try {
      const raw = localStorage.getItem(LOCAL_STORAGE_VAULT_KEY);
      if (!raw) return {};
      return JSON.parse(raw);
    } catch {
      return {};
    }
  }

  private saveLocalVault(vault: Record<string, WorldVaultItem>) {
    try {
      localStorage.setItem(LOCAL_STORAGE_VAULT_KEY, JSON.stringify(vault));
    } catch (e) {
      console.warn("Failed to persist to local storage vault:", e);
    }
  }

  // --- Public Gallery / Presets ---
  public listPublicWorlds(): WorldVaultItem[] {
    const presets: WorldVaultItem[] = AVAILABLE_PRESETS.map((p) => {
      const lattices = p.world?.state?.lattices ?? [];
      const rules = p.world?.rules ?? [];
      return {
        metadata: {
          id: p.id,
          name: p.name,
          description: p.subtitle,
          author: "ByteTerrace Core",
          category: p.category,
          visibility: "public",
          version: "1.0.0",
          revision: 1,
          lastModified: new Date().toISOString(),
          rulesCount: rules.length,
          topologyType: lattices[0]?.$type ?? "box",
          checkpointHash: "sha256-64/canonical00000",
        },
        world: p.world,
        isPreset: true,
      };
    });

    // Add any locally published public worlds
    const localVault = this.getLocalVault();
    for (const item of Object.values(localVault)) {
      if (item.metadata.visibility === "public" && !presets.find((pr) => pr.metadata.id === item.metadata.id)) {
        presets.push(item);
      }
    }

    return presets;
  }

  // --- Private Vault (My Worlds) ---
  public async listPrivateWorlds(): Promise<WorldVaultItem[]> {
    const local = this.getLocalVault();
    const items: WorldVaultItem[] = Object.values(local).filter(
      (item) => item.metadata.visibility === "private" || !item.isPreset
    );

    // If Azure credentials are provided, attempt to list blobs in user's partition
    if (this.tokenCredential) {
      try {
        const endpoint = await resolveStorageEndpoint(this.tokenCredential, this.apiTokenScopes);
        const token = await this.tokenCredential.getToken(this.storageTokenScopes);
        if (token) {
          const blobService = new BlobServiceClient(`${endpoint}?${token.token}`);
          const containerClient = blobService.getContainerClient("worlds");
          for await (const blob of containerClient.listBlobsFlat({ prefix: "private/" })) {
            if (blob.name.endsWith(".world.json")) {
              const id = blob.name.replace("private/", "").replace(".world.json", "");
              if (!items.find((i) => i.metadata.id === id)) {
                items.push({
                  metadata: {
                    id,
                    name: id,
                    author: "You",
                    visibility: "private",
                    version: "1.0.0",
                    revision: 1,
                    lastModified: blob.properties.lastModified?.toISOString() ?? new Date().toISOString(),
                    rulesCount: 0,
                    topologyType: "box",
                  },
                  world: null,
                });
              }
            }
          }
        }
      } catch (err) {
        console.warn("Could not query Azure storage partition (falling back to local vault):", err);
      }
    }

    return items;
  }

  // --- Save World to Vault ---
  public async saveWorld(
    id: string,
    world: any,
    metadataOverrides?: Partial<WorldMetadata>
  ): Promise<WorldVaultItem> {
    const jsonStr = JSON.stringify(world, null, 2);
    const hash = await computeContentHash(jsonStr);

    const topologies = world?.state?.lattices ?? [];
    const rules = world?.rules ?? [];

    const existing = this.getLocalVault()[id];
    const revision = (existing?.metadata.revision ?? 0) + 1;

    const metadata: WorldMetadata = {
      id,
      name: metadataOverrides?.name || existing?.metadata.name || id,
      description: metadataOverrides?.description || existing?.metadata.description || "Authored in Puck World Studio",
      author: metadataOverrides?.author || existing?.metadata.author || "Player",
      category: metadataOverrides?.category || existing?.metadata.category || "Custom",
      visibility: metadataOverrides?.visibility || existing?.metadata.visibility || "private",
      version: metadataOverrides?.version || `1.${revision}.0`,
      revision,
      lastModified: new Date().toISOString(),
      rulesCount: rules.length,
      topologyType: topologies[0]?.$type ?? "box",
      checkpointHash: hash,
    };

    const item: WorldVaultItem = {
      metadata,
      world,
      isPreset: false,
    };

    // Save to local vault
    const localVault = this.getLocalVault();
    localVault[id] = item;
    this.saveLocalVault(localVault);

    // Record checkpoint entry in history
    this.recordCheckpoint(id, revision, hash, world);

    // If Azure token is present, commit to Azure Blob storage substrate
    if (this.tokenCredential) {
      try {
        const endpoint = await resolveStorageEndpoint(this.tokenCredential, this.apiTokenScopes);
        const token = await this.tokenCredential.getToken(this.storageTokenScopes);
        if (token) {
          const blobService = new BlobServiceClient(`${endpoint}?${token.token}`);
          const containerClient = blobService.getContainerClient("worlds");
          await containerClient.createIfNotExists();

          // Write world definition
          const blobPath = `${metadata.visibility}/${id}.world.json`;
          const blockBlob = containerClient.getBlockBlobClient(blobPath);
          await blockBlob.upload(jsonStr, jsonStr.length, {
            blobHTTPHeaders: { blobContentType: "application/json" },
          });

          // Write checkpoint snapshot (CAS/content-addressed)
          const checkPath = `checkpoints/${id}/${revision}-${hash.replace("sha256-64/", "")}.checkpoint.json`;
          const checkBlob = containerClient.getBlockBlobClient(checkPath);
          await checkBlob.upload(jsonStr, jsonStr.length, {
            blobHTTPHeaders: { blobContentType: "application/json" },
          });
        }
      } catch (err) {
        console.warn("Failed to push blob to Azure partition (saved in local vault):", err);
      }
    }

    return item;
  }

  // --- Load World by ID ---
  public async loadWorld(id: string): Promise<WorldVaultItem | null> {
    // 1. Check presets
    const preset = AVAILABLE_PRESETS.find((p) => p.id === id);
    if (preset) {
      return {
        metadata: {
          id: preset.id,
          name: preset.name,
          description: preset.subtitle,
          category: preset.category,
          visibility: "public",
          version: "1.0.0",
          revision: 1,
          lastModified: new Date().toISOString(),
          rulesCount: preset.world?.rules?.length ?? 0,
          topologyType: preset.world?.state?.lattices?.[0]?.$type ?? "box",
        },
        world: preset.world,
        isPreset: true,
      };
    }

    // 2. Check local vault
    const local = this.getLocalVault()[id];
    if (local && local.world) {
      return local;
    }

    // 3. Try Azure Blob storage
    if (this.tokenCredential) {
      try {
        const endpoint = await resolveStorageEndpoint(this.tokenCredential, this.apiTokenScopes);
        const token = await this.tokenCredential.getToken(this.storageTokenScopes);
        if (token) {
          const blobService = new BlobServiceClient(`${endpoint}?${token.token}`);
          const containerClient = blobService.getContainerClient("worlds");

          for (const prefix of ["private/", "public/"]) {
            const blockBlob = containerClient.getBlockBlobClient(`${prefix}${id}.world.json`);
            if (await blockBlob.exists()) {
              const download = await blockBlob.downloadToBuffer();
              const text = download.toString("utf-8");
              const parsed = JSON.parse(text);
              return {
                metadata: {
                  id,
                  name: id,
                  visibility: prefix === "public/" ? "public" : "private",
                  version: "1.0.0",
                  revision: 1,
                  lastModified: new Date().toISOString(),
                  rulesCount: parsed?.rules?.length ?? 0,
                  topologyType: parsed?.state?.lattices?.[0]?.$type ?? "box",
                },
                world: parsed,
              };
            }
          }
        }
      } catch (err) {
        console.warn("Could not download blob from Azure:", err);
      }
    }

    return null;
  }

  // --- Delete / Quarantine World ---
  public async deleteWorld(id: string): Promise<boolean> {
    const local = this.getLocalVault();
    if (local[id]) {
      delete local[id];
      this.saveLocalVault(local);
    }

    if (this.tokenCredential) {
      try {
        const endpoint = await resolveStorageEndpoint(this.tokenCredential, this.apiTokenScopes);
        const token = await this.tokenCredential.getToken(this.storageTokenScopes);
        if (token) {
          const blobService = new BlobServiceClient(`${endpoint}?${token.token}`);
          const containerClient = blobService.getContainerClient("worlds");
          await containerClient.getBlockBlobClient(`private/${id}.world.json`).deleteIfExists();
        }
      } catch (err) {
        console.warn("Failed to delete remote blob:", err);
      }
    }

    return true;
  }

  // --- Fork a World to My Vault ---
  public async forkWorld(sourceId: string, newId: string, newName?: string): Promise<WorldVaultItem> {
    const source = await this.loadWorld(sourceId);
    if (!source || !source.world) {
      throw new Error(`Source world '${sourceId}' not found.`);
    }

    const clonedWorld = JSON.parse(JSON.stringify(source.world));
    return this.saveWorld(newId, clonedWorld, {
      name: newName || `${source.metadata.name} (Fork)`,
      description: `Forked from ${source.metadata.name}`,
      author: "You",
      visibility: "private",
    });
  }

  // --- Checkpoints Timeline ---
  public listCheckpoints(worldId: string): Array<{ revision: number; hash: string; timestamp: string }> {
    try {
      const raw = localStorage.getItem(`${LOCAL_CHECKPOINTS_KEY}.${worldId}`);
      if (!raw) return [];
      return JSON.parse(raw);
    } catch {
      return [];
    }
  }

  private recordCheckpoint(worldId: string, revision: number, hash: string, _world: any) {
    try {
      const list = this.listCheckpoints(worldId);
      list.unshift({
        revision,
        hash,
        timestamp: new Date().toISOString(),
      });
      localStorage.setItem(`${LOCAL_CHECKPOINTS_KEY}.${worldId}`, JSON.stringify(list.slice(0, 30)));
    } catch (e) {
      console.warn("Failed to record checkpoint:", e);
    }
  }

  // --- Generate Time-Bounded SAS Share Link ---
  public async generateShareLink(worldId: string, durationHours: number = 24): Promise<string> {
    if (this.tokenCredential) {
      try {
        const apiToken = await this.tokenCredential.getToken(this.apiTokenScopes);
        const response = await fetch("/api/shares", {
          method: "POST",
          headers: {
            Authorization: `Bearer ${apiToken!.token}`,
            "Content-Type": "application/json",
          },
          body: JSON.stringify({
            BlobName: `worlds/private/${worldId}.world.json`,
            DurationHours: durationHours,
          }),
        });

        if (response.ok) {
          const body = await response.json();
          const uri = body.uri ?? body.Uri;
          if (uri && isByteTerraceStorageUrl(uri)) {
            return `${window.location.origin}/#world-share=${encodeURIComponent(uri)}`;
          }
        }
      } catch (err) {
        console.warn("Could not obtain SAS token from /api/shares:", err);
      }
    }

    // Fallback: Local share link
    return `${window.location.origin}/#world=${encodeURIComponent(worldId)}`;
  }
}

export const defaultWorldStorageClient = new WorldStorageClient();
