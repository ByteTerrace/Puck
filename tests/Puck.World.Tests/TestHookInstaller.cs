using System.Runtime.CompilerServices;
using Puck.AdvancedGamingBrick;
using Puck.AdvancedGamingBrick.Forge;
using Puck.HumbleGamingBrick;
using Puck.HumbleGamingBrick.Forge;
using Puck.HumbleGamingBrick.Forge.Tune;
using Puck.World.Client;

namespace Puck.World.Tests;

/// <summary>
/// This project's wiring of <c>Puck.World.Schema</c>'s composition-root injection seams — the same
/// <see cref="WorldSchemaVocabularyHooks.Install"/> both real roots call, so a law here exercises the real refusals
/// rather than a stand-in that could drift from them.
/// </summary>
internal static class TestHookInstaller {
    [ModuleInitializer]
    internal static void Install() {
        WorldScreenMachineEngines.Register(engine: new GamingBrickEngine(), compiler: new HgbCartridgeCompiler());
        WorldScreenMachineEngines.Register(engine: new TuneInstrumentEngine());
        WorldScreenMachineEngines.Register(engine: new AdvancedGamingBrickEngine(), compiler: new AgbCartridgeCompiler());
        WorldSchemaVocabularyHooks.Install(
            postRenderExtensionCheck: static _ => true,
            probeKindCheck: static _ => true,
            screenMachineCartridgeCheck: WorldScreenMachineEngines.CompilesCartridges,
            screenMachineEngineCheck: static _ => true
        );
    }
}


