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
/// Machine vocabulary is supplied per validation invocation through
/// <see cref="Puck.Abstractions.Machines.IMachineValidationCatalog"/>. Post-render and probe checks arrive as
/// parameters; the remaining hooks resolve against catalogs this project already reaches:
/// <see cref="WorldAffordances.Validate"/>, <see cref="InputSourceVocabulary"/>,
/// <see cref="GamepadFamilyCatalog"/>, <c>Puck.World.Protocol.WorldMutationKindCatalog</c>, and
/// <see cref="WorldContextFamilies.Families"/>.
/// </remarks>
public static class WorldSchemaVocabularyHooks {
    /// <summary>Installs every Schema vocabulary hook.</summary>
    /// <param name="postRenderExtensionCheck">Answers whether a document-declared post-render extension key is
    /// shipped (<c>Puck.World.WorldPostRenderExtensions.IsShipped</c> in a real root).</param>
    /// <param name="probeKindCheck">Answers whether a document-declared <c>probes[].kind</c> key is
    /// shipped (<c>Puck.World.WorldProbeKinds.IsShipped</c> in a real root).</param>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    public static void Install(Func<string, bool> postRenderExtensionCheck, Func<string, bool> probeKindCheck) {
        ArgumentNullException.ThrowIfNull(argument: postRenderExtensionCheck);
        ArgumentNullException.ThrowIfNull(argument: probeKindCheck);

        BindingVocabularyHook.VocabularyCheck = WorldAffordances.Validate;
        ContextFamilyVocabularyHook.ReservedFamilyNames = WorldContextFamilies.Families;
        GamepadFamilyVocabularyHook.IsKnownFamilyName = GamepadFamilyCatalog.IsKnownName;
        InputSourceVocabularyHook.IsKnownSourceId = InputSourceVocabulary.IsKnownSourceId;
        Protocol.MutationKindVocabularyHook.Describe = Protocol.WorldMutationKindCatalog.DescribeMask;
        Protocol.MutationKindVocabularyHook.TryParse = Protocol.WorldMutationKindCatalog.TryParseMask;
        // These roots always carry a real catalog (never "no catalog at all"), so each wraps its plain bool
        // predicate as the three-way bool? the hook now declares — never answering null itself. Only
        // Puck.World.Browser's own installer answers null.
        WorldExtensionVocabularyHook.PostRenderExtensionCheck = id => postRenderExtensionCheck(id);
        WorldProbeVocabularyHook.ProbeKindCheck = id => probeKindCheck(id);
    }
}
