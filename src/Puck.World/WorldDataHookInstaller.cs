using System.Runtime.CompilerServices;
using Puck.AdvancedGamingBrick;
using Puck.AdvancedGamingBrick.Forge;
using Puck.HumbleGamingBrick;
using Puck.HumbleGamingBrick.Forge;
using Puck.HumbleGamingBrick.Forge.Tune;
using Puck.World.Client;

namespace Puck.World;

/// <summary>
/// Wires every <c>Puck.World.Schema</c> injection seam the instant this assembly loads — before <c>Main</c>, before
/// the DI container, before any validator can run. Registers the shipped screen machine extensions and wires
/// <see cref="WorldSchemaVocabularyHooks.Install"/>.
/// </summary>
internal static class WorldDataHookInstaller {
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


