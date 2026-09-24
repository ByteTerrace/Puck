import type { TokenCredential } from "@azure/identity";
import type { ContainerClient } from "@azure/storage-blob";
import { useEffect, useState } from "react";
import { Subject } from "rxjs";
import { resolveStorageEndpoint } from "../../clients/resolveStorageEndpoint";
import { API_TOKEN_SCOPES, containerClientFor, listPrivateFiles, listPublicFiles } from "./storage";
import {
  initialStorageAccountState,
  storageAccountState,
  type StorageReload,
} from "./storageStreams";

/**
 * The signed-in user's storage account: where it lives, their private files, and their published files, from
 * `storageAccountState`. `reload` refreshes the listings and resolves once the fresh listing has landed.
 */
export function useStorageAccount(tokenCredential: TokenCredential, userObjectId: string) {
  const [reloads] = useState(() => new Subject<StorageReload>());
  const [state, setState] = useState(initialStorageAccountState);

  useEffect(() => {
    const account = storageAccountState(
      {
        listPrivate: (endpoint) => listPrivateFiles(containerClientFor(endpoint, tokenCredential, userObjectId)),
        listPublic: () => listPublicFiles(tokenCredential),
        resolveEndpoint: () => resolveStorageEndpoint(tokenCredential, API_TOKEN_SCOPES),
      },
      reloads,
    ).subscribe(setState);

    return () => account.unsubscribe();
  }, [reloads, tokenCredential, userObjectId]);

  const containerClient = (): ContainerClient => {
    if (!state.storageEndpoint) {
      throw new Error("Your storage account is still resolving — try again in a moment.");
    }

    return containerClientFor(state.storageEndpoint, tokenCredential, userObjectId);
  };

  return {
    ...state,
    containerClient: containerClient,
    /**
     * Refreshes the private listing, and the published one too when `includePublic` is set. An account whose
     * endpoint failed to resolve tries again first.
     */
    reload: (includePublic = false): Promise<void> => new Promise((settled) => reloads.next({ includePublic, settled })),
  };
}
