using Puck.Assets;
using Puck.Scripting;

namespace Puck.World.Addons;

public sealed partial class WorldAddonRuntime {
    // Process-lifetime compiled code; every runtime still owns independent stores, channels and mutable state.
    // Lazy type initialization avoids paying Wasmtime for an addon-free process.
    private static class SharedModules {
        internal static readonly ScriptingEngine Engine = new(options: ScriptingEngineOptions.Deterministic);
        internal static readonly WasmModuleLoader Loader = new(Engine, new FileSystemAssetSource());
    }
}
