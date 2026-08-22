using System.Runtime.CompilerServices;
using Puck.World.Client;

namespace Puck.World.Silo;

/// <summary>
/// Wires the <c>Puck.World.Schema</c> injection seams a document load's own validator needs, through the same
/// <see cref="WorldSchemaVocabularyHooks.Install"/> the desktop client and the test suite call — a server-only
/// process that never loads <c>Puck.World</c> must not accept a document the owner's windowed client will reject at
/// boot. The silo mounts no machine or addon host of its own regardless (the checkpoint arm gate already refuses a
/// row that pumps one), so a validated engine key never actually runs here; it only fails to load for want of
/// recognizing it, exactly as it would on the desktop.
/// </summary>
internal static class WorldSiloDataHookInstaller {
    [ModuleInitializer]
    internal static void Install() => WorldSchemaVocabularyHooks.Install(
        postRenderExtensionCheck: WorldPostRenderExtensions.IsShipped,
        screenMachineEngineCheck: WorldScreenMachineEngines.IsRegistered,
        probeKindCheck: WorldProbeKinds.IsShipped
    );
}
