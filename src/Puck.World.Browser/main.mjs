// The one entry point the studio (Puck.Dashboard, its own engineHost.ts) and this repository's Node harness both
// import: `createEngine(options)` boots the Mono runtime from this AppBundle's own `_framework/dotnet.js` and
// resolves the `[JSExport]` surface, without ever calling `runMain` — nothing here has a `Main` worth running.
import { dotnet } from "./_framework/dotnet.js";

// options.resourceLoader: dotnet.js' own resource-loader hook, dotnet.d.ts' LoadBootResourceCallback
// (withResourceLoader): given (type, name, defaultUri, integrity, behavior), return a string URL or a
// Promise<Response> to satisfy the fetch from caller-verified cached bytes instead of the network, or
// null/undefined to fall back to the default fetch.
// options.runtimeConfig: forwarded to dotnet.withConfig when present, for a caller that boots against a relocated
// _framework (the official-content CDN rather than this AppBundle's own path).
export async function createEngine(options = {}) {
    let builder = dotnet;

    if (options.resourceLoader) {
        builder = builder.withResourceLoader(options.resourceLoader);
    }
    if (options.runtimeConfig) {
        builder = builder.withConfig(options.runtimeConfig);
    }

    const { getAssemblyExports } = await builder.create();
    const exports = await getAssemblyExports("Puck.World.Browser.dll");

    return exports.Puck.World.Browser.Exports.BrowserExports;
}
