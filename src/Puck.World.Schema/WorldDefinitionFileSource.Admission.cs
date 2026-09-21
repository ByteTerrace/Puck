using Puck.Abstractions.Machines;

namespace Puck.World;

public static partial class WorldDefinitionFileSource {
    /// <summary>Loads a file through the shared composition and content-pin path, retaining validation's programs.</summary>
    /// <param name="path">The document file.</param>
    /// <param name="admission">The admitted document and compilation, or null on refusal.</param>
    /// <param name="contentHash">The pin over the authored composition bytes.</param>
    /// <param name="reason">The named refusal, or empty on success.</param>
    /// <param name="neighbours">The resolver proving cross-document claims.</param>
    /// <param name="catalogFingerprint">The composition cache's catalog partition.</param>
    /// <param name="catalog">The selected catalog, or null to defer provider checks.</param>
    /// <param name="documents">The source used for the root and its composition dependencies.</param>
    /// <returns>Whether composition and validation succeeded.</returns>
    public static bool TryLoadForAdmission(string path, out WorldDefinitionAdmission? admission,
        out string contentHash, out string reason, IWorldNeighbourResolver? neighbours = null,
        string catalogFingerprint = "", IMachineValidationCatalog? catalog = null, IWorldDocumentSource? documents = null) =>
        TryLoadCore(admission: out admission, catalog: catalog, catalogFingerprint: catalogFingerprint, contentHash: out contentHash, definition: out _, documents: documents, neighbours: neighbours, path: path, reason: out reason, validateAdjacencyClaims: true);

    /// <summary>Parses and validates a composed document, retaining the exact compilation used by validation.</summary>
    /// <param name="json">The composed JSON.</param>
    /// <param name="sourceName">The origin echoed in refusals.</param>
    /// <param name="neighbours">The resolver proving cross-document claims.</param>
    /// <param name="validateAdjacencyClaims">Whether this boundary proves neighbour claims.</param>
    /// <param name="admission">The validation result, or null on refusal.</param>
    /// <param name="reason">The named refusal, or empty on success.</param>
    /// <param name="catalog">The selected catalog, or null to defer provider checks.</param>
    /// <returns>Whether parsing and validation succeeded.</returns>
    public static bool TryParseComposedForAdmission(string json, string sourceName, IWorldNeighbourResolver? neighbours,
        bool validateAdjacencyClaims, out WorldDefinitionAdmission? admission, out string reason,
        IMachineValidationCatalog? catalog = null) {
        admission = null;
        if (!TryParseDocument(definition: out var parsed, json: json, reason: out reason, sourceName: sourceName)) { return false; }
        if (!WorldDefinitionValidator.TryAdmitCore(admission: out admission, definition: parsed!, machines: catalog, neighbours: neighbours,
            proveNeighbours: validateAdjacencyClaims, reason: out var refusal)) {
            reason = $"{sourceName} document validation refused: {refusal.ReplaceLineEndings(replacementText: " ")}";
            return false;
        }
        reason = string.Empty;
        return true;
    }
}
