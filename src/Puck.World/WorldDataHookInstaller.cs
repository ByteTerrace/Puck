using System.Runtime.CompilerServices;
using Puck.Abstractions.Machines;

namespace Puck.World;

/// <summary>
/// Wires every Puck.World.Data injection seam the instant this assembly loads — before <c>Main</c>, before the DI
/// container, before any validator can run. Puck.World.Data cannot reference Puck.Input (the architecture gate's
/// structural denial), so <see cref="WorldDefinitionValidator"/> and identity-owned world validation reaches the
/// engine-default binding document and the live vocabulary check through <see cref="BindingVocabularyHook"/> rather
/// than naming <see cref="WorldDefaultBindings"/>/<see cref="WorldAffordances"/> directly.
/// <see cref="BindingVocabularyHook.DefaultDocument"/> is wired unconditionally (a pure, always-available function);
/// <see cref="BindingVocabularyHook.VocabularyCheck"/> forwards to <see cref="WorldAffordances.Validate"/>,
/// whose command half keeps its own separate absent-tolerant contract (a no-op until
/// <see cref="WorldAffordances.Install"/> runs) and whose channel half needs no install at all — the caller hands it
/// the table compiled from the very document under validation.
/// <see cref="WorldExtensionVocabularyHook.ScreenMachineEngineCheck"/> is wired against a fresh
/// <see cref="WorldExtensionRegistry{TExtension}"/> built from <see cref="WorldScreenMachineEngines.All"/> — the same
/// list <c>WorldBootComposition</c> registers into DI, so a document-declared engine key and the DI-resolvable set can
/// never disagree. Unlike the vocabulary check, that hook is required: leaving it unwired does not degrade the
/// registered-key refusal, it fails the document (see the hook's own remarks), which is why it is installed here with
/// the unconditional pair rather than anywhere later.
/// </summary>
internal static class WorldDataHookInstaller {
    [ModuleInitializer]
    internal static void Install() {
        BindingVocabularyHook.DefaultDocument = WorldDefaultBindings.BuildDocument;
        BindingVocabularyHook.VocabularyCheck = WorldAffordances.Validate;

        var screenMachineEngines = new WorldExtensionRegistry<IScreenMachineEngine>(
            extensions: WorldScreenMachineEngines.All,
            keyOf: static engine => engine.Id
        );

        WorldExtensionVocabularyHook.ScreenMachineEngineCheck = screenMachineEngines.IsRegistered;
    }
}
