using Puck.Abstractions.Machines;

namespace Puck.World;

public static partial class WorldDefinitionValidator {
    /// <summary>Completes an unchanged document's admission after host vocabularies and neighbour transports compose.
    /// Rechecks the environment-dependent sections without recompiling rules, tables, or work analysis.</summary>
    /// <param name="admission">The operation's successful document-local validation.</param>
    /// <param name="machines">The same selected machine catalog used by that validation.</param>
    /// <param name="neighbours">The current resolver used to prove cross-document claims.</param>
    /// <param name="reason">The collapsed refusal, or empty on success.</param>
    /// <returns>Whether the admitted document remains valid in the completed environment.</returns>
    public static bool TryCompleteAdmission(WorldDefinitionAdmission admission, IMachineValidationCatalog? machines,
        IWorldNeighbourResolver? neighbours, out string reason) {
        ArgumentNullException.ThrowIfNull(admission);
        var definition = admission.Definition;
        if (!admission.AppliesTo(definition: definition, machines: machines)) {
            reason = "the admission result belongs to a different machine catalog";
            return false;
        }
        try {
            var errors = new List<string>();
            // These are the existing section validators, including their original diagnostics. Their vocabulary
            // hooks can change answers without changing delegate identity (notably the command registry at boot).
            // Keep every ambient vocabulary consumer in this phase; rule/state validation belongs to the receipt.
            ValidateRenderExtensions(definition.Render.Extensions, errors, deferred: null);
            ValidateSeatModes(definition: definition, errors: errors);
            var (iconNames, iconsAuthored) = ValidateIconography(definition: definition, errors: errors);
            if (definition.BindingOverlays.Count != 0) {
                var stateRows = definition.State.ToDictionary(row => row.Name.Value, StringComparer.Ordinal);
                ValidateBindingOverlays(definition, definition.BindingOverlays,
                    WorldChannelTable.Compile(channels: definition.Channels), stateRows,
                    definition.SeatModes, iconNames, iconsAuthored, errors);
            }
            if (definition.ProbesRaw is { Count: > 0 }) {
                var cameras = new HashSet<string>(collection: definition.Cameras.Select(selector: camera => camera.Name), comparer: StringComparer.Ordinal);
                ValidateProbes(definition, cameras, errors, deferred: null);
            }
            if (definition.Adjacencies is { Count: > 0 }) {
                var destinations = new HashSet<string>(collection: (definition.Destinations ?? []).Select(selector: destination => destination.Name.Value), comparer: StringComparer.Ordinal);
                ValidateAdjacencies(definition, destinations, neighbours, proveNeighbours: true, errors);
            }
            RefuseCollected(errors: errors);
            reason = string.Empty;
            return true;
        } catch (InvalidOperationException exception) {
            reason = exception.Message.ReplaceLineEndings(replacementText: " ");
            return false;
        }
    }
}
