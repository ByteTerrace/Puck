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
    /// <param name="overrides">Rewrites the drawn document before its one admission, or null when the host
    /// overrides nothing the document carries.</param>
    /// <param name="compiled">The compiled world the drawn document is read from or written to, or null to draw the
    /// document without one.</param>
    /// <param name="documentDirectory">The directory the document's relative paths resolve beside
    /// (<see cref="WorldDefinition.DocumentDirectory"/>), or null for bytes that came from no file.</param>
    /// <returns>Whether the document loaded and validated.</returns>
    public static bool TryLoadForAdmission(ReadOnlyMemory<byte> utf8, string sourceName,
        out WorldDefinitionAdmission? admission, out string reason, string instanceIdentity = BootInstanceName,
        IWorldNeighbourResolver? neighbours = null, string catalogFingerprint = "", IMachineValidationCatalog? catalog = null,
        Func<WorldDefinition, WorldDefinition>? overrides = null, CompiledWorldRequest? compiled = null, string? documentDirectory = null) {
        admission = null;
        if (!TryDecode(json: out var json, reason: out reason, sourceName: sourceName, utf8: utf8) ||
            !WorldDefinitionFileSource.TryParseDocument(definition: out var parsed, json: json,
                reason: out reason, sourceName: sourceName)) { return false; }
        parsed = (parsed! with { DocumentDirectory = documentDirectory });
        return TryPrepareAndAdmit(catalog: catalog, compiled: compiled, definition: parsed, instanceIdentity: instanceIdentity, neighbours: neighbours,
            overrides: overrides, reason: out reason, resolved: out admission, sourceName: sourceName);
    }
    /// <summary>Loads a file and retains the final, draw-resolved document's validation and programs — the one door
    /// every document a world runs is read through: the boot, an instance start, a transfer's destination, and
    /// <c>world.load</c>/<c>world.reload</c> with the replay drive's re-read of what they pinned. It composes the file,
    /// fills its first-fill draw sites for <paramref name="instanceIdentity"/>, settles its boot values, and admits the
    /// result, so a document whose references read a drawn cell is never admitted, or carried anywhere, unfilled.</summary>
    /// <param name="path">The document file.</param>
    /// <param name="admission">The final validation result, or null on refusal.</param>
    /// <param name="contentHash">The content-address pin of the document read
    /// (<see cref="WorldDefinitionFileSource.ComputeContentHash"/>), taken before any draw so a draw's outcome never
    /// moves it; empty on refusal.</param>
    /// <param name="reason">The named refusal, or empty on success.</param>
    /// <param name="instanceIdentity">The instance identity used for first-fill draws.</param>
    /// <param name="neighbours">The resolver proving cross-document claims.</param>
    /// <param name="proveNeighbours">Whether cross-document claims are proved through <paramref name="neighbours"/>;
    /// <see langword="false"/> validates only the facts the document owns, for a read on the tick path, which never
    /// reaches a transport.</param>
    /// <param name="catalogFingerprint">The host's composition cache partition.</param>
    /// <param name="catalog">The selected catalog, or null to defer provider checks.</param>
    /// <param name="overrides">Rewrites the drawn document before its one admission, or null when the host
    /// overrides nothing the document carries.</param>
    /// <param name="compiled">The compiled world the drawn document is read from or written to, or null to draw the
    /// document without one.</param>
    /// <param name="documents">The source the root and every basis/import reference read through, or
    /// <see langword="null"/> to read files directly; see <see cref="WorldDefinitionFileSource.TryLoad"/>.</param>
    /// <returns>Whether the document loaded and validated.</returns>
    public static bool TryLoadFileForAdmission(string path, out WorldDefinitionAdmission? admission, out string contentHash, out string reason,
        string instanceIdentity = BootInstanceName, IWorldNeighbourResolver? neighbours = null, bool proveNeighbours = true,
        string catalogFingerprint = "", IMachineValidationCatalog? catalog = null,
        Func<WorldDefinition, WorldDefinition>? overrides = null, CompiledWorldRequest? compiled = null, IWorldDocumentSource? documents = null) {
        admission = null;
        contentHash = string.Empty;
        try {
            if (!WorldDefinitionFileSource.TryLoadParsed(catalog: catalog, catalogFingerprint: catalogFingerprint, contentHash: out var parsedHash,
                    definition: out var parsed, documents: documents, path: path, reason: out reason) ||
                !TryPrepareAndAdmit(catalog: catalog, compiled: compiled, definition: parsed!, instanceIdentity: instanceIdentity, neighbours: neighbours,
                    overrides: overrides, proveNeighbours: proveNeighbours, reason: out reason, resolved: out admission, sourceName: path)) {
                return false;
            }

            contentHash = parsedHash;
            return true;
        } catch (Exception exception) {
            admission = null;
            reason = $"{path} is not a valid {WorldDefinition.SchemaVersion} document: {exception.Message.ReplaceLineEndings(replacementText: " ")}";
            return false;
        }
    }
}
