/*
    Entra bearer bridge for in-browser DuckDB: stamps a storage-scoped access
    token onto requests bound for the public storage account, because
    DuckDB-WASM cannot attach auth headers itself
    (https://github.com/duckdb/duckdb-wasm/issues/1967). Azure Blob OAuth
    requests additionally require an x-ms-version header.

    The token is PULLED from an open page (which answers via MSAL) whenever it
    is needed, because the browser freely terminates idle service workers and
    in-memory state does not survive that. Requests that already carry an
    Authorization header (e.g. the Azure SDK's own calls) pass through
    untouched.
*/

const STORAGE_HOST = "bytrcstp001.blob.core.windows.net";

let accessToken = null;

async function requestTokenFromClients() {
    const clients = await self.clients.matchAll({ type: "window" });

    for (const client of clients) {
        const token = await new Promise((resolve) => {
            const channel = new MessageChannel();
            const timeout = setTimeout(() => resolve(null), 3000);

            channel.port1.onmessage = (event) => {
                clearTimeout(timeout);
                resolve((event.data && event.data.token) || null);
            };
            client.postMessage({ type: "storage-token-request" }, [channel.port2]);
        });

        if (token) {
            return token;
        }
    }

    return null;
}

self.addEventListener("install", () => self.skipWaiting());
self.addEventListener("activate", (event) => event.waitUntil(self.clients.claim()));
self.addEventListener("fetch", (event) => {
    const url = new URL(event.request.url);

    if ((STORAGE_HOST !== url.host) || event.request.headers.has("Authorization")) {
        return;
    }

    event.respondWith((async () => {
        const stampedRequest = () => {
            const headers = new Headers(event.request.headers);

            headers.set("Authorization", `Bearer ${accessToken}`);
            headers.set("x-ms-version", "2025-01-05");

            return new Request(event.request, {
                headers: headers,
                mode: "cors",
            });
        };

        if (!accessToken) {
            accessToken = await requestTokenFromClients();
        }

        if (!accessToken) {
            return fetch(event.request);
        }

        let response = await fetch(stampedRequest());

        if ((401 === response.status) || (403 === response.status)) {
            accessToken = await requestTokenFromClients();

            if (accessToken) {
                response = await fetch(stampedRequest());
            }
        }

        return response;
    })());
});
