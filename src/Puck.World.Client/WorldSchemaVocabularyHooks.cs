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
/// <see cref="Puck.Abstractions.Machines.IMachineValidationCatalog"/>. The probe kind check arrives as a parameter;
/// the remaining hooks resolve against catalogs this project already reaches:
/// <see cref="Puck.Shaders.RenderGraphPackageCatalog.Engine"/>'s post-process packages,
/// <see cref="WorldAffordances.Validate"/>, <see cref="InputSourceVocabulary"/>,
/// <see cref="GamepadFamilyCatalog"/>, <c>Puck.World.Protocol.WorldMutationKindCatalog</c>, and
/// <see cref="WorldContextFamilies.Families"/>.
/// </remarks>
public static class WorldSchemaVocabularyHooks {
    /// <summary>Installs every Schema vocabulary hook.</summary>
    /// <param name="probeKindCheck">Answers whether a document-declared <c>probes[].kind</c> key is
    /// shipped (<c>Puck.World.WorldProbeKinds.IsShipped</c> in a real root).</param>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    public static void Install(Func<string, bool> probeKindCheck) {
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
        WorldPostProcessVocabularyHook.PostProcessPackageCheck = static package => (Puck.Shaders.RenderGraphPackageCatalog.Engine.TryGet(
            id: package,
            package: out var offered
        ) && offered.IsPostProcess);
        // A post pass's config binds as the graph compiler binds it when the root is composed; a package the check above
        // refuses is reported there, not here.
        WorldPostProcessVocabularyHook.PostProcessConfigCheck = static (package, pass, config) => {
            if (!Puck.Shaders.RenderGraphPackageCatalog.Engine.TryGet(
                id: package,
                package: out var offered
            )) {
                return null;
            }
            if (offered.Config is not { } schema) {
                return ((config is null)
                    ? null
                    : "the package takes no config");
            }

            return (Puck.Shaders.ShaderConfigBinding.TryBind(
                config: config,
                ownerName: pass,
                reason: out var reason,
                schema: schema,
                values: out _
            )
                ? null
                : reason);
        };
        WorldProbeVocabularyHook.ProbeKindCheck = id => probeKindCheck(id);
    }
}
