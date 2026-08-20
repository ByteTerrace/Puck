using Puck.Input;
using Puck.Input.Devices;

namespace Puck.World.Client;

/// <summary>
/// The ONE wiring of every <c>Puck.World.Schema</c> injection seam, called from each composition root's
/// <see cref="System.Runtime.CompilerServices.ModuleInitializerAttribute"/> body so the desktop client, the silo, and
/// the test suite cannot drift: a hook one root wires and another does not is a mutation door that admits what the
/// other refuses.
/// </summary>
/// <remarks>
/// The two <see cref="WorldExtensionVocabularyHook"/> predicates arrive as parameters rather than being resolved here
/// because they live in <c>Puck.World.Server</c>, which this project does not reference (the client seam never sees
/// the authoritative simulation). Everything else resolves against catalogs this project already reaches:
/// <see cref="WorldAffordances.Validate"/>, <see cref="InputSourceVocabulary"/>,
/// <see cref="GamepadFamilyCatalog"/>, <c>Puck.World.Protocol.WorldMutationKindCatalog</c>, and
/// <see cref="WorldContextFamilies.Families"/>.
/// </remarks>
public static class WorldSchemaVocabularyHooks {
    /// <summary>Installs every Schema vocabulary hook.</summary>
    /// <param name="screenMachineEngineCheck">Answers whether a document-declared <c>screens[]</c> engine key names an
    /// engine the caller's build ships (<c>Puck.World.WorldScreenMachineEngines.IsRegistered</c> in a real root).</param>
    /// <param name="postRenderExtensionCheck">Answers whether a document-declared post-render extension key is
    /// shipped (<c>Puck.World.WorldPostRenderExtensions.IsShipped</c> in a real root).</param>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    public static void Install(Func<string, bool> screenMachineEngineCheck, Func<string, bool> postRenderExtensionCheck) {
        ArgumentNullException.ThrowIfNull(argument: postRenderExtensionCheck);
        ArgumentNullException.ThrowIfNull(argument: screenMachineEngineCheck);

        BindingVocabularyHook.VocabularyCheck = WorldAffordances.Validate;
        ContextFamilyVocabularyHook.ReservedFamilyNames = WorldContextFamilies.Families;
        GamepadFamilyVocabularyHook.IsKnownFamilyName = GamepadFamilyCatalog.IsKnownName;
        InputSourceVocabularyHook.IsKnownSourceId = InputSourceVocabulary.IsKnownSourceId;
        Protocol.MutationKindVocabularyHook.Describe = Protocol.WorldMutationKindCatalog.DescribeMask;
        Protocol.MutationKindVocabularyHook.TryParse = Protocol.WorldMutationKindCatalog.TryParseMask;
        WorldExtensionVocabularyHook.PostRenderExtensionCheck = postRenderExtensionCheck;
        WorldExtensionVocabularyHook.ScreenMachineEngineCheck = screenMachineEngineCheck;
    }
}
