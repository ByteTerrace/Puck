using System.Runtime.CompilerServices;
using Puck.World.Client;

namespace Puck.World.Silo;

/// <summary>
/// Wires the <c>Puck.World.Schema</c> injection seams a document load's own validator needs, through the same
/// <see cref="WorldSchemaVocabularyHooks.Install"/> the desktop client and the test suite call.
/// </summary>
internal static class WorldSiloDataHookInstaller {
    [ModuleInitializer]
    internal static void Install() {
        WorldSchemaVocabularyHooks.Install(
            postRenderExtensionCheck: WorldPostRenderExtensions.IsShipped,
            probeKindCheck: WorldProbeKinds.IsShipped,
            screenMachineCartridgeCheck: WorldScreenMachineEngines.CompilesCartridges,
            screenMachineEngineCheck: WorldScreenMachineEngines.IsRegistered
        );
    }
}


