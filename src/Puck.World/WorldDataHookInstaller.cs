using System.Runtime.CompilerServices;
using Puck.Abstractions.Machines;
using Puck.Input.Devices;

namespace Puck.World;

/// <summary>
/// Wires every Puck.World.Schema injection seam the instant this assembly loads — before <c>Main</c>, before the DI
/// container, before any validator can run. Puck.World.Schema cannot reference Puck.Input (the architecture gate's
/// structural denial), so <see cref="WorldDefinitionValidator"/> and identity-owned world validation reaches the
/// live vocabulary check through <see cref="BindingVocabularyHook"/> rather than naming
/// <see cref="WorldAffordances"/> directly.
/// <see cref="BindingVocabularyHook.VocabularyCheck"/> forwards to <see cref="WorldAffordances.Validate"/>,
/// whose command half keeps its own separate absent-tolerant contract (a no-op until
/// <see cref="WorldAffordances.Install"/> runs) and whose channel half needs no install at all — the caller hands it
/// the table compiled from the very document under validation.
/// <see cref="GamepadButtonVocabularyHook.IsKnownButtonName"/> crosses the identical seam for a binding-bar slot
/// name, forwarding to <see cref="GamepadButtonCatalog.IsKnownName"/>.
/// <see cref="GamepadFamilyVocabularyHook.IsKnownFamilyName"/> crosses the same seam for an icon badge override's
/// family name, against <see cref="GamepadType"/>.
/// <see cref="WorldExtensionVocabularyHook.ScreenMachineEngineCheck"/> is wired against a fresh
/// <see cref="WorldExtensionRegistry{TExtension}"/> built from <see cref="WorldScreenMachineEngines.All"/> — the same
/// list <c>WorldBootComposition</c> registers into DI, so a document-declared engine key and the DI-resolvable set can
/// never disagree. Unlike the vocabulary check, that hook is required: leaving it unwired does not degrade the
/// registered-key refusal, it fails the document (see the hook's own remarks), which is why it is installed here with
/// the unconditional pair rather than anywhere later.
/// <see cref="Protocol.MutationKindVocabularyHook.Describe"/>/<see cref="Protocol.MutationKindVocabularyHook.TryParse"/>
/// forward to <see cref="Protocol.WorldMutationKindCatalog"/> — Puck.World.Schema cannot reference
/// Puck.World.Protocol (the mutation-kind vocabulary lives downstream of the document model a mask is a field on),
/// so a mask's name round-trip crosses the same seam shape as the binding/extension hooks above.
/// <see cref="WorldExtensionVocabularyHook.PostRenderExtensionCheck"/> is wired the identical way, to
/// <see cref="WorldPostRenderExtensions.IsShipped"/> — required, not absent-tolerant, for the same reason as the
/// screen-machine engine check.
/// </summary>
internal static class WorldDataHookInstaller {
    [ModuleInitializer]
    internal static void Install() {
        BindingVocabularyHook.VocabularyCheck = WorldAffordances.Validate;
        GamepadButtonVocabularyHook.IsKnownButtonName = GamepadButtonCatalog.IsKnownName;
        GamepadFamilyVocabularyHook.IsKnownFamilyName = static name => (
            Enum.TryParse<GamepadType>(value: name, ignoreCase: false, result: out var family) &&
            (family != GamepadType.Unknown) &&
            Enum.IsDefined(value: family)
        );
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
