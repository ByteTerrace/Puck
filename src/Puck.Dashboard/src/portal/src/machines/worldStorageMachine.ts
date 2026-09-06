import { setup, assign, fromPromise } from "xstate";
import {
  WorldStorageClient,
  WorldVaultItem,
  WorldMetadata,
  defaultWorldStorageClient,
} from "../clients/worldStorageClient";

export interface WorldStorageContext {
  storageClient: WorldStorageClient;
  activeWorldId: string;
  metadata: Partial<WorldMetadata>;
  privateWorlds: WorldVaultItem[];
  publicWorlds: WorldVaultItem[];
  checkpoints: Array<{ revision: number; hash: string; timestamp: string }>;
  shareUrl: string | null;
  error: string | null;
  statusMessage: string | null;
}

export type WorldStorageEvent =
  | { type: "REFRESH_VAULT" }
  | {
      type: "SAVE_WORLD";
      id: string;
      data: any;
      metadata: Partial<WorldMetadata>;
    }
  | { type: "GENERATE_SHARE"; durationHours?: number }
  | { type: "DELETE_WORLD"; id: string }
  | { type: "SET_ACTIVE_WORLD"; id: string; metadata?: Partial<WorldMetadata> }
  | { type: "CLEAR_SHARE_URL" }
  | { type: "CLEAR_ERROR" };

export const worldStorageMachine = setup({
  types: {
    context: {} as WorldStorageContext,
    events: {} as WorldStorageEvent,
  },
  actors: {
    fetchVaultActor: fromPromise(
      async ({ input }: { input: { storageClient: WorldStorageClient; activeWorldId: string } }) => {
        const privateWorlds = await input.storageClient.listPrivateWorlds();
        const publicWorlds = input.storageClient.listPublicWorlds();
        const checkpoints = await input.storageClient.listCheckpoints(input.activeWorldId);
        return { privateWorlds, publicWorlds, checkpoints };
      }
    ),

    saveWorldActor: fromPromise(
      async ({
        input,
      }: {
        input: {
          storageClient: WorldStorageClient;
          id: string;
          data: any;
          metadata: Partial<WorldMetadata>;
        };
      }) => {
        const hash = await input.storageClient.saveWorld(
          input.id,
          input.data,
          input.metadata
        );
        const privateWorlds = await input.storageClient.listPrivateWorlds();
        const checkpoints = await input.storageClient.listCheckpoints(input.id);
        return { hash, privateWorlds, checkpoints };
      }
    ),

    generateShareActor: fromPromise(
      async ({
        input,
      }: {
        input: {
          storageClient: WorldStorageClient;
          id: string;
          durationHours?: number;
        };
      }) => {
        const url = await input.storageClient.generateShareLink(
          input.id,
          input.durationHours ?? 24
        );
        return { url };
      }
    ),

    deleteWorldActor: fromPromise(
      async ({
        input,
      }: {
        input: {
          storageClient: WorldStorageClient;
          id: string;
        };
      }) => {
        await input.storageClient.deleteWorld(input.id);
        const privateWorlds = await input.storageClient.listPrivateWorlds();
        return { privateWorlds };
      }
    ),
  },
  actions: {
    setActiveWorld: assign(({ event }) => {
      if (event.type !== "SET_ACTIVE_WORLD") return {};
      return {
        activeWorldId: event.id,
        metadata: event.metadata ?? {},
        shareUrl: null,
      };
    }),
    clearShareUrl: assign({
      shareUrl: () => null,
    }),
    clearError: assign({
      error: () => null,
    }),
  },
}).createMachine({
  id: "worldStorage",
  initial: "idle",
  context: {
    storageClient: defaultWorldStorageClient,
    activeWorldId: "tictactoe-3d",
    metadata: {
      name: "Qubic 3D 4x4x4 Tic-Tac-Toe",
      author: "ByteTerrace",
      description: "Standard 3D Qubic on a 4x4x4 lattice substrate.",
      category: "Strategy",
      visibility: "public",
    },
    privateWorlds: [],
    publicWorlds: [],
    checkpoints: [],
    shareUrl: null,
    error: null,
    statusMessage: null,
  },
  states: {
    idle: {
      on: {
        REFRESH_VAULT: "refreshing",
        SAVE_WORLD: "saving",
        GENERATE_SHARE: "generatingShare",
        DELETE_WORLD: "deleting",
        SET_ACTIVE_WORLD: {
          actions: "setActiveWorld",
        },
        CLEAR_SHARE_URL: {
          actions: "clearShareUrl",
        },
        CLEAR_ERROR: {
          actions: "clearError",
        },
      },
    },

    refreshing: {
      invoke: {
        src: "fetchVaultActor",
        input: ({ context }) => ({
          storageClient: context.storageClient,
          activeWorldId: context.activeWorldId,
        }),
        onDone: {
          target: "idle",
          actions: assign(({ event }) => ({
            privateWorlds: event.output.privateWorlds,
            publicWorlds: event.output.publicWorlds,
            checkpoints: event.output.checkpoints,
            error: null,
            statusMessage: "Local library loaded.",
          })),
        },
        onError: {
          target: "idle",
          actions: assign(({ event }: any) => ({
            error: event.error?.message ?? "Failed to refresh vault",
          })),
        },
      },
    },

    saving: {
      invoke: {
        src: "saveWorldActor",
        input: ({ context, event }: any) => ({
          storageClient: context.storageClient,
          id: event.id,
          data: event.data,
          metadata: event.metadata,
        }),
        onDone: {
          target: "idle",
          actions: assign(({ event }: any) => ({
            privateWorlds: event.output.privateWorlds,
            checkpoints: event.output.checkpoints,
            statusMessage: `Saved locally: ${event.output.hash.metadata.checkpointHash}...`,
            error: null,
          })),
        },
        onError: {
          target: "idle",
          actions: assign(({ event }: any) => ({
            error: event.error?.message ?? "Failed to save world",
          })),
        },
      },
    },

    generatingShare: {
      invoke: {
        src: "generateShareActor",
        input: ({ context, event }: any) => ({
          storageClient: context.storageClient,
          id: context.activeWorldId,
          durationHours: event.durationHours,
        }),
        onDone: {
          target: "idle",
          actions: assign(({ event }) => ({
            shareUrl: event.output.url,
            error: null,
          })),
        },
        onError: {
          target: "idle",
          actions: assign(({ event }: any) => ({
            error: event.error?.message ?? "Failed to generate share link",
          })),
        },
      },
    },

    deleting: {
      invoke: {
        src: "deleteWorldActor",
        input: ({ context, event }: any) => ({
          storageClient: context.storageClient,
          id: event.id,
        }),
        onDone: {
          target: "idle",
          actions: assign(({ event }) => ({
            privateWorlds: event.output.privateWorlds,
            error: null,
          })),
        },
        onError: {
          target: "idle",
          actions: assign(({ event }: any) => ({
            error: event.error?.message ?? "Failed to delete world",
          })),
        },
      },
    },
  },
});

// Fine-grained Selectors
export const selectPrivateWorlds = (s: { context: WorldStorageContext }): WorldVaultItem[] => s.context.privateWorlds;
export const selectPublicWorlds = (s: { context: WorldStorageContext }): WorldVaultItem[] => s.context.publicWorlds;
export const selectCheckpoints = (s: { context: WorldStorageContext }): Array<{ revision: number; hash: string; timestamp: string }> => s.context.checkpoints;
export const selectShareUrl = (s: { context: WorldStorageContext }): string | null => s.context.shareUrl;
export const selectStorageError = (s: { context: WorldStorageContext }): string | null => s.context.error;
export const selectIsSaving = (s: any): boolean => s.matches("saving");
export const selectIsRefreshing = (s: any): boolean => s.matches("refreshing");
export const selectIsSharing = (s: any): boolean => s.matches("generatingShare");
