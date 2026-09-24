/**
 * Local draft persistence for the studio machine: one browser-storage-backed library keyed by draft id. A draft is
 * an overlay on the official build: the text of every workspace file the author changed, keyed by worlds-relative
 * path, plus the document it opens on. Each draft keeps up to ten revisions, and the whole library is one atomic
 * storage write.
 *
 * `localStorage` is read through an injectable `DraftStorage` so the store works identically under Node
 * (`node --test`, no `localStorage` global) and in a browser tab; the default falls back to an in-memory `Map`,
 * matching `official/byteStore.ts`'s own caches-vs-Map fallback.
 */
import { checkDocumentSize } from "./intake";

/** Changed workspace files, keyed by worlds-relative path. */
export type DraftFiles = Readonly<Record<string, string>>;

export interface DraftRevision {
  readonly files: DraftFiles;
  readonly label: string;
  readonly savedAt: string;
}

export interface StudioDraft {
  readonly id: string;
  readonly title: string;
  /** The official document the draft opens on (its manifest name). */
  readonly documentName: string;
  /** Newest first, capped at {@link MAX_REVISIONS_PER_DRAFT}. */
  readonly revisions: readonly DraftRevision[];
}

export interface DraftStorage {
  getItem(key: string): string | null;
  setItem(key: string, value: string): void;
}

export const MAX_REVISIONS_PER_DRAFT = 10;

const DRAFTS_KEY = "byteterrace.puck.studioSourceDrafts";
const VALID_ID = /^[a-z0-9][a-z0-9-]{0,63}$/;

export class DraftRefusal extends Error {
  constructor(message: string) {
    super(message);
    this.name = "DraftRefusal";
  }
}

function validId(id: string): void {
  if (!VALID_ID.test(id) || id === "constructor" || id === "prototype") {
    throw new DraftRefusal(`invalid local draft id '${id}'.`);
  }
}

function validFiles(files: unknown): boolean {
  return !!files && typeof files === "object" && !Array.isArray(files)
    && Object.values(files).every((text) => typeof text === "string");
}

class MemoryDraftStorage implements DraftStorage {
  private readonly entries = new Map<string, string>();

  getItem(key: string): string | null {
    return this.entries.get(key) ?? null;
  }

  setItem(key: string, value: string): void {
    this.entries.set(key, value);
  }
}

function defaultStorage(): DraftStorage {
  try { return typeof localStorage !== "undefined" ? localStorage : new MemoryDraftStorage(); }
  catch { return new MemoryDraftStorage(); }
}

/** The library as the studio shows it: every draft, or the reason the library could not be read. */
export interface DraftListing {
  readonly drafts: readonly StudioDraft[];
  readonly error: string | null;
}

/** A listing failure stays visible without crashing the editor or overwriting the library. */
export function readDraftListing(store: Pick<LocalDraftStore, "list">): DraftListing {
  try { return { drafts: store.list(), error: null }; }
  catch (error) { return { drafts: [], error: error instanceof Error ? error.message : String(error) }; }
}

/** Offline draft library. A single atomic storage write carries every draft and every one of its revisions, so a
 * write either lands whole or (on a quota/availability failure) not at all; storage never holds a half-written
 * entry. */
export class LocalDraftStore {
  private readonly storage: DraftStorage;

  constructor(storage: DraftStorage = defaultStorage()) {
    this.storage = storage;
  }

  private read(): Record<string, StudioDraft> {
    const raw = this.storage.getItem(DRAFTS_KEY);
    if (!raw) {
      return {};
    }
    let parsed: unknown;
    try {
      parsed = JSON.parse(raw);
    } catch (error) {
      throw new DraftRefusal(`local draft library is unreadable: ${(error as Error).message}`);
    }
    if (!parsed || typeof parsed !== "object" || Array.isArray(parsed)) {
      throw new DraftRefusal("local draft library is unreadable: not a JSON object.");
    }
    for (const [id, value] of Object.entries(parsed)) {
      validId(id);
      const draft = value as Partial<StudioDraft> | null;
      if (!draft || draft.id !== id || typeof draft.title !== "string" || typeof draft.documentName !== "string" ||
          !Array.isArray(draft.revisions) || draft.revisions.length === 0 ||
          draft.revisions.some(revision => !revision || !validFiles(revision.files) || typeof revision.label !== "string" || typeof revision.savedAt !== "string")) {
        throw new DraftRefusal(`local draft '${id}' is unreadable: invalid stored draft.`);
      }
    }
    return parsed as Record<string, StudioDraft>;
  }

  private write(drafts: Record<string, StudioDraft>): void {
    try {
      this.storage.setItem(DRAFTS_KEY, JSON.stringify(drafts));
    } catch (error) {
      throw new DraftRefusal(`could not save the draft in this browser: ${(error as Error).message}`);
    }
  }

  list(): StudioDraft[] {
    return Object.values(this.read());
  }

  load(id: string): StudioDraft | undefined {
    return this.read()[id];
  }

  save(id: string, title: string, documentName: string, files: DraftFiles, label: string): StudioDraft {
    validId(id);
    checkDocumentSize(JSON.stringify(files));
    const drafts = this.read();
    const existing = drafts[id];
    const revision: DraftRevision = { files, label, savedAt: new Date().toISOString() };
    const revisions = [revision, ...(existing?.revisions ?? [])].slice(0, MAX_REVISIONS_PER_DRAFT);
    const draft: StudioDraft = { id, title: title || existing?.title || id, documentName, revisions };
    this.write({ ...drafts, [id]: draft });
    return draft;
  }

  delete(id: string): boolean {
    validId(id);
    const drafts = this.read();
    if (!Object.hasOwn(drafts, id)) {
      return false;
    }
    const next = { ...drafts };
    delete next[id];
    this.write(next);
    return true;
  }
}

export const defaultLocalDraftStore = new LocalDraftStore();
