using System.Diagnostics.CodeAnalysis;
using Puck.Abstractions.Machines;

namespace Puck.World;

public static partial class WorldDefinitionValidator {
    /// <summary>Validates a loaded document and its neighbour claims, retaining the exact compiled programs.</summary>
    /// <param name="definition">The unchanged candidate owned by this admission operation.</param>
    /// <param name="machines">The selected catalog, or null to defer provider checks.</param>
    /// <param name="neighbours">The resolver used to prove neighbour claims at this boundary.</param>
    /// <param name="admission">The validation result, or null on refusal.</param>
    /// <param name="reason">The collapsed refusal, or empty on success.</param>
    /// <returns>Whether document and adjacency validation succeeded.</returns>
    public static bool TryAdmit(WorldDefinition definition, IMachineValidationCatalog? machines,
        IWorldNeighbourResolver? neighbours, [NotNullWhen(true)] out WorldDefinitionAdmission? admission, out string reason) =>
        TryAdmitCore(admission: out admission, definition: definition, machines: machines, neighbours: neighbours, proveNeighbours: true, reason: out reason);

    internal static bool TryAdmitCore(WorldDefinition definition, IMachineValidationCatalog? machines,
        IWorldNeighbourResolver? neighbours, bool proveNeighbours,
        [NotNullWhen(true)] out WorldDefinitionAdmission? admission, out string reason) {
        ArgumentNullException.ThrowIfNull(definition);
        admission = null;
        try {
            var compilation = ValidateCore(definition, neighbours, proveNeighbours, retainCompilation: true,
                throwOnErrors: true, errorSink: null, deferredSink: null, machines: machines);
            admission = new WorldDefinitionAdmission(compilation: compilation!, machines: machines);
            reason = string.Empty;
            return true;
        } catch (InvalidOperationException exception) {
            reason = exception.Message.ReplaceLineEndings(replacementText: " ");
            return false;
        }
    }

    /// <summary>Validates document-local facts and machine capabilities once for an admission operation.
    /// Pass the result directly between preparation steps without changing the document or validation inputs.</summary>
    /// <param name="definition">The operation's candidate definition.</param>
    /// <param name="machines">The selected host's machine catalog.</param>
    /// <param name="admission">The validation and compilation result, or null on refusal.</param>
    /// <param name="reason">The collapsed refusal, or empty on success.</param>
    /// <returns>Whether local document and provider validation succeeded.</returns>
    public static bool TryAdmitLocally(WorldDefinition definition, IMachineValidationCatalog machines,
        [NotNullWhen(true)] out WorldDefinitionAdmission? admission, out string reason) {
        ArgumentNullException.ThrowIfNull(argument: definition);
        ArgumentNullException.ThrowIfNull(argument: machines);
        admission = null;
        if (!TryValidateLocally(compilation: out var compilation, definition: definition, machines: machines, reason: out reason)) {
            return false;
        }
        admission = new WorldDefinitionAdmission(compilation: compilation!, machines: machines);
        return true;
    }
}
