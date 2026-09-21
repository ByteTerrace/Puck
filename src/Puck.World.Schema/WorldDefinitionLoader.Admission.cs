using Puck.Abstractions.Machines;

namespace Puck.World;

public static partial class WorldDefinitionLoader {
    /// <summary>Loads composed bytes and retains the final, draw-resolved document's validation and programs.</summary>
    /// <param name="utf8">The composed JSON bytes.</param>
    /// <param name="sourceName">The origin echoed in refusals.</param>
    /// <param name="admission">The final validation result, or null on refusal.</param>
    /// <param name="reason">The named refusal, or empty on success.</param>
    /// <param name="instanceIdentity">The instance identity used for first-fill draws.</param>
    /// <param name="neighbours">The resolver proving cross-document claims.</param>
    /// <param name="catalogFingerprint">The host's composition cache partition.</param>
    /// <param name="catalog">The selected catalog, or null to defer provider checks.</param>
    /// <returns>Whether the document loaded and validated.</returns>
    public static bool TryLoadForAdmission(ReadOnlyMemory<byte> utf8, string sourceName,
        out WorldDefinitionAdmission? admission, out string reason, string instanceIdentity = BootInstanceName,
        IWorldNeighbourResolver? neighbours = null, string catalogFingerprint = "", IMachineValidationCatalog? catalog = null) {
        admission = null;
        if (!TryDecode(json: out var json, reason: out reason, sourceName: sourceName, utf8: utf8) ||
            !WorldDefinitionFileSource.TryParseDocument(definition: out var parsed, json: json,
                reason: out reason, sourceName: sourceName)) { return false; }
        return TryPrepareAndAdmit(catalog: catalog, definition: parsed!, instanceIdentity: instanceIdentity, neighbours: neighbours,
            reason: out reason, resolved: out admission, sourceName: sourceName);
    }

    /// <summary>Loads a file and retains the final, draw-resolved document's validation and programs.</summary>
    /// <param name="path">The document file.</param>
    /// <param name="admission">The final validation result, or null on refusal.</param>
    /// <param name="reason">The named refusal, or empty on success.</param>
    /// <param name="instanceIdentity">The instance identity used for first-fill draws.</param>
    /// <param name="neighbours">The resolver proving cross-document claims.</param>
    /// <param name="catalogFingerprint">The host's composition cache partition.</param>
    /// <param name="catalog">The selected catalog, or null to defer provider checks.</param>
    /// <returns>Whether the document loaded and validated.</returns>
    public static bool TryLoadFileForAdmission(string path, out WorldDefinitionAdmission? admission, out string reason,
        string instanceIdentity = BootInstanceName, IWorldNeighbourResolver? neighbours = null,
        string catalogFingerprint = "", IMachineValidationCatalog? catalog = null) {
        admission = null;
        try {
            return (WorldDefinitionFileSource.TryLoadParsed(path, out var parsed, out _, out reason,
                catalogFingerprint, catalog) &&
                TryPrepareAndAdmit(catalog: catalog, definition: parsed!, instanceIdentity: instanceIdentity, neighbours: neighbours, reason: out reason, resolved: out admission, sourceName: path));
        } catch (Exception exception) {
            admission = null;
            reason = $"{path} is not a valid {WorldDefinition.SchemaVersion} document: {exception.Message.ReplaceLineEndings(replacementText: " ")}";
            return false;
        }
    }
}
