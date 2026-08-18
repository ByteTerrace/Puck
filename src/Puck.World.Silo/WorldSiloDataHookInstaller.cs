using System.Runtime.CompilerServices;
using Puck.Abstractions.Machines;

namespace Puck.World.Silo;

/// <summary>
/// Wires the <c>Puck.World.Schema</c> injection seams a document load's own validator needs — the silo's own copy
/// of <c>Puck.World.WorldDataHookInstaller</c>, since a definition load (<c>silo.publish</c>, activation) runs the
/// same <see cref="WorldDefinitionValidator"/> the desktop does. <see cref="Protocol.MutationKindVocabularyHook"/>
/// forwards to the same catalog the desktop uses. <see cref="WorldExtensionVocabularyHook.ScreenMachineEngineCheck"/>/
/// <see cref="WorldExtensionVocabularyHook.PostRenderExtensionCheck"/> are wired against the same
/// <see cref="WorldScreenMachineEngines"/> list and <see cref="WorldPostRenderExtensions"/> catalog <c>Puck.World</c>'s
/// own installer reads (public in <c>Puck.World.Server</c>, this project's own reference), so a document with a
/// <c>screens[]</c> row validates identically here and on the desktop — the silo mounts no machine/addon host of its
/// own regardless (the checkpoint arm gate already refuses a row that pumps one), so a validated key never actually
/// runs here; it only fails to load for want of recognizing it, exactly as it would fail to load on the desktop for
/// naming an engine that build never shipped.
/// </summary>
internal static class WorldSiloDataHookInstaller {
    [ModuleInitializer]
    internal static void Install() {
        BindingVocabularyHook.VocabularyCheck = WorldAffordances.Validate;
        Protocol.MutationKindVocabularyHook.Describe = Protocol.WorldMutationKindCatalog.DescribeMask;
        Protocol.MutationKindVocabularyHook.TryParse = Protocol.WorldMutationKindCatalog.TryParseMask;

        var screenMachineEngines = new WorldExtensionRegistry<IScreenMachineEngine>(
            extensions: WorldScreenMachineEngines.All,
            keyOf: static engine => engine.Id
        );

        WorldExtensionVocabularyHook.ScreenMachineEngineCheck = screenMachineEngines.IsRegistered;
        WorldExtensionVocabularyHook.PostRenderExtensionCheck = WorldPostRenderExtensions.IsShipped;
    }
}
