using Puck.Abstractions.Machines;

namespace Puck.World;

public static partial class WorldDefinitionFileSource {
    /// <summary>Parses and validates a composed document, retaining the exact compilation used by validation.</summary>
    /// <param name="json">The composed JSON.</param>
    /// <param name="sourceName">The origin echoed in refusals.</param>
    /// <param name="neighbours">The resolver proving cross-document claims.</param>
    /// <param name="validateAdjacencyClaims">Whether this boundary proves neighbour claims.</param>
    /// <param name="admission">The validation result, or null on refusal.</param>
    /// <param name="reason">The named refusal, or empty on success.</param>
    /// <param name="catalog">The selected catalog, or null to defer provider checks.</param>
    /// <param name="documentDirectory">The directory the document's relative paths resolve beside
    /// (<see cref="WorldDefinition.DocumentDirectory"/>), or null for a document that came from no file.</param>
    /// <returns>Whether parsing and validation succeeded.</returns>
    public static bool TryParseComposedForAdmission(string json, string sourceName, IWorldNeighbourResolver? neighbours,
        bool validateAdjacencyClaims, out WorldDefinitionAdmission? admission, out string reason,
        IMachineValidationCatalog? catalog = null, string? documentDirectory = null) {
        admission = null;
        if (!TryParseDocument(definition: out var parsed, json: json, reason: out reason, sourceName: sourceName)) { return false; }
        parsed = (parsed! with { DocumentDirectory = documentDirectory });
        if (!WorldDefinitionValidator.TryAdmitCore(admission: out admission, definition: parsed, machines: catalog, neighbours: neighbours,
            proveNeighbours: validateAdjacencyClaims, reason: out var refusal)) {
            reason = $"{sourceName} document validation refused: {refusal.ReplaceLineEndings(replacementText: " ")}";
            return false;
        }
        reason = string.Empty;
        return true;
    }
    /// <summary>Parses and validates an already-decoded, already-composed document string — the shared
    /// middle of every load path once its own bytes/basis handling has produced flat JSON: a directory load
    /// (composed above) and a bytes-only load with no directory to resolve a chain against
    /// (<c>WorldDefinitionLoader.TryLoad</c>,
    /// which refuses a <c>basis</c> member outright rather than composing one). The validation class answers under
    /// its own wording, never the strict parse's: a validation refusal can rest on facts outside this call — an
    /// adjacency claim resolved through <paramref name="neighbours"/> against documents this caller may itself be
    /// about to move — so it is retryable in a way "these bytes are not a puck.world.definition.v1 document" never is, and
    /// a caller classifying on <paramref name="reason"/> must be able to tell them apart (see
    /// <see cref="WorldDefinitionLoader.TryLoadFileForAdmission"/> for the classified prefixes).</summary>
    /// <param name="json">The already-decoded, already-composed document JSON.</param>
    /// <param name="sourceName">The document's source name, echoed in every refusal.</param>
    /// <param name="neighbours">The injected neighbour resolver a cross-document adjacency proof reads.</param>
    /// <param name="validateAdjacencyClaims">Whether to prove cross-document adjacency claims
    /// (<see cref="WorldDefinitionValidator.TryValidate"/>) or validate only document-local facts
    /// (<see cref="WorldDefinitionValidator.TryValidateLocally(WorldDefinition, out string)"/>).</param>
    /// <param name="definition">The parsed, validated definition on success; <see langword="null"/> on failure.</param>
    /// <param name="reason">The one-line failure reason, or empty on success.</param>
    /// <param name="catalog">The selected host machine catalog used for provider validation, or null for structural parsing.</param>
    /// <param name="documentDirectory">The directory the document's relative paths resolve beside
    /// (<see cref="WorldDefinition.DocumentDirectory"/>), or null for a document that came from no file.</param>
    /// <returns><see langword="true"/> when the document parsed and validated.</returns>
    public static bool TryParseComposed(string json, string sourceName, IWorldNeighbourResolver? neighbours, bool validateAdjacencyClaims, out WorldDefinition? definition, out string reason, IMachineValidationCatalog? catalog = null, string? documentDirectory = null) {
        var accepted = TryParseComposedForAdmission(admission: out var admission, catalog: catalog, documentDirectory: documentDirectory, json: json, neighbours: neighbours,
            reason: out reason, sourceName: sourceName, validateAdjacencyClaims: validateAdjacencyClaims);

        definition = admission?.Definition;
        return accepted;
    }
}
