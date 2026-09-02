using System.Runtime.CompilerServices;
using Puck.World.Client;

namespace Puck.World;

/// <summary>
/// Wires every <c>Puck.World.Schema</c> injection seam the instant this assembly loads — before <c>Main</c>, before
/// the DI container, before any validator can run. The wiring itself is
/// <see cref="WorldSchemaVocabularyHooks.Install"/>, shared with <c>Puck.World.Silo</c> and the test suite; this
/// installer supplies only the two predicates that live in <c>Puck.World.Server</c>, which the shared installer's
/// project does not reference.
/// </summary>
internal static class WorldDataHookInstaller {
    [ModuleInitializer]
    internal static void Install() => WorldSchemaVocabularyHooks.Install(
        postRenderExtensionCheck: WorldPostRenderExtensions.IsShipped,
        probeKindCheck: WorldProbeKinds.IsShipped,
        screenMachineEngineCheck: WorldScreenMachineEngines.IsRegistered
    );
}
