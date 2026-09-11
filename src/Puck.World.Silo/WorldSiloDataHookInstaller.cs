using System.Runtime.CompilerServices;
using Puck.AdvancedGamingBrick;
using Puck.AdvancedGamingBrick.Forge;
using Puck.HumbleGamingBrick;
using Puck.HumbleGamingBrick.Forge;
using Puck.HumbleGamingBrick.Forge.Tune;
using Puck.World.Client;

namespace Puck.World.Silo;

/// <summary>
/// Wires the <c>Puck.World.Schema</c> injection seams a document load's own validator needs, through the same
/// <see cref="WorldSchemaVocabularyHooks.Install"/> the desktop client and the test suite call.
/// </summary>
internal static class WorldSiloDataHookInstaller {
    [ModuleInitializer]
    internal static void Install() {
        WorldScreenMachineEngines.Register(engine: new GamingBrickEngine(), compiler: new HgbCartridgeCompiler());
        WorldScreenMachineEngines.Register(engine: new TuneInstrumentEngine());
        WorldScreenMachineEngines.Register(engine: new AdvancedGamingBrickEngine(), compiler: new AgbCartridgeCompiler());
        WorldSchemaVocabularyHooks.Install(
            postRenderExtensionCheck: WorldPostRenderExtensions.IsShipped,
            probeKindCheck: WorldProbeKinds.IsShipped,
            screenMachineCartridgeCheck: WorldScreenMachineEngines.CompilesCartridges,
            screenMachineEngineCheck: WorldScreenMachineEngines.IsRegistered
        );
    }
}


