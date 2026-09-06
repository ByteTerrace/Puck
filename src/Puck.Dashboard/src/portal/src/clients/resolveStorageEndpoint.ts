import { TokenCredential } from "@azure/identity";

// The user's container lives in the storage-account partition their oid maps to. Rather than run
// the partitioner in the browser, we ask the edge — which asks the user's grain, the authority on
// where the container actually IS (a pending migration moves it mid-session) — so the browser and
// backend never disagree on a user's account, and raising the partition count takes effect with no
// portal rebuild. Cached briefly rather than per-session: after a migration flips the home, a
// session-long cache would strand writes against the frozen old account until the next full reload.
const CACHE_TTL_MILLISECONDS = 300000;
let cached: { expiresAt: number; value: Promise<string> } | undefined;

export function resolveStorageEndpoint(
  tokenCredential: TokenCredential,
  apiTokenScopes: string[],
): Promise<string> {
  if (!cached || Date.now() >= cached.expiresAt) {
    cached = {
      expiresAt: Date.now() + CACHE_TTL_MILLISECONDS,
      value: (async () => {
        const apiToken = await tokenCredential.getToken(apiTokenScopes);
        const response = await fetch("/api/storage-endpoint", {
          headers: { Authorization: `Bearer ${apiToken!.token}` },
        });

        if (!response.ok) {
          cached = undefined; // let a later attempt retry rather than caching the failure
          throw new Error(`Could not resolve your storage account (${response.status}).`);
        }

        const body = await response.json();

        return String(body.endpoint ?? body.Endpoint);
      })(),
    };
  }

  return cached.value;
}

// A pasted share link may live in any partition account — the sharer's, not the viewer's — so
// validate the host is a ByteTerrace storage account rather than one specific endpoint.
export function isByteTerraceStorageUrl(url: string): boolean {
  try {
    return /^bytrcstp\d{3}\.blob\.core\.windows\.net$/i.test(new URL(url).host);
  } catch {
    return false;
  }
}
