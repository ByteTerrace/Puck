using System.Runtime.CompilerServices;
using Puck.World.Client;

namespace Puck.World;

/// <summary>
/// Wires every <c>Puck.World.Schema</c> injection seam the instant this assembly loads — before <c>Main</c>, before
/// the DI container, before any validator can run. Machine catalogs are supplied explicitly by the host; this wires
/// <see cref="WorldSchemaVocabularyHooks.Install"/>.
/// </summary>
internal static class WorldDataHookInstaller {
    [ModuleInitializer]
    internal static void Install() {
        WorldSchemaVocabularyHooks.Install(
            postRenderExtensionCheck: WorldPostRenderExtensions.IsShipped,
            probeKindCheck: WorldProbeKinds.IsShipped
        );
    }
}
