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
    /// <summary>Reads a composed definition a release publishes (<c>puck world prepare</c>, <c>puck world release</c>,
    /// the official package scan, a release bootstrap) and returns it undrawn: draws are instance state, filled when an
    /// instance admits the published bytes under its own identity, so a published definition never carries one
    /// instance's cells. The read still proves the document admits: a copy is drawn for
    /// <see cref="BootInstanceName"/>, settled and admitted exactly as a boot admits it, validating the facts the
    /// document owns (its neighbours are published beside it and prove nothing yet); the drawn copy is discarded.
    /// What comes back is the parsed document, never an admission, so nothing reads it as a document to run.</summary>
    /// <param name="json">The composed JSON text.</param>
    /// <param name="sourceName">The origin echoed in refusals.</param>
    /// <param name="definition">The parsed, undrawn definition, or <see langword="null"/> on refusal.</param>
    /// <param name="reason">The named refusal, classed as <see cref="TryLoadFileForAdmission"/> classes its own, or
    /// empty on success.</param>
    /// <param name="catalog">The selected catalog, or null to defer provider checks.</param>
    /// <param name="documentDirectory">The directory the document's relative paths resolve beside
    /// (<see cref="WorldDefinition.DocumentDirectory"/>), or null for a document that came from no file.</param>
    /// <returns>Whether the document parsed and a drawn copy of it was admitted.</returns>
    public static bool TryReadPublishable(string json, string sourceName,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(returnValue: true)] out WorldDefinition? definition, out string reason,
        IMachineValidationCatalog? catalog = null, string? documentDirectory = null) {
        definition = null;
        if (!WorldDefinitionFileSource.TryParseDocument(definition: out var parsed, json: json, reason: out reason, sourceName: sourceName)) {
            return false;
        }
        parsed = (parsed! with { DocumentDirectory = documentDirectory });
        if (!TryProvePublishable(catalog: catalog, definition: parsed, reason: out reason, sourceName: sourceName)) {
            return false;
        }
        definition = parsed;
        return true;
    }
    /// <summary>Proves a published, undrawn definition admits, the proof <see cref="TryReadPublishable"/> runs after
    /// it parses: a copy is drawn for <see cref="BootInstanceName"/>, settled and admitted exactly as a boot admits
    /// it, validating the facts the document owns (its neighbours are published beside it and prove nothing yet);
    /// the drawn copy is discarded and <paramref name="definition"/> is never modified. A caller holding a parsed
    /// published definition, such as a release transition comparing two packages, proves it here rather than
    /// validating it undrawn.</summary>
    /// <param name="definition">The parsed, undrawn definition.</param>
    /// <param name="sourceName">The origin echoed in refusals.</param>
    /// <param name="reason">The named refusal, classed as <see cref="TryLoadFileForAdmission"/> classes its own, or
    /// empty on success.</param>
    /// <param name="catalog">The selected catalog, or null to defer provider checks.</param>
    /// <returns>Whether a drawn copy of the definition was admitted.</returns>
    public static bool TryProvePublishable(WorldDefinition definition, string sourceName, out string reason, IMachineValidationCatalog? catalog = null) {
        ArgumentNullException.ThrowIfNull(argument: definition);
        try {
            return TryPrepareAndAdmit(catalog: catalog, definition: definition, instanceIdentity: BootInstanceName, neighbours: null,
                proveNeighbours: false, reason: out reason, resolved: out _, sourceName: sourceName);
        } catch (Exception exception) {
            reason = $"{sourceName} is not a valid {WorldDefinition.SchemaVersion} document: {exception.Message.ReplaceLineEndings(replacementText: " ")}";
            return false;
        }
    }
    /// <summary>Loads a file and retains the final, draw-resolved document's validation and programs — the one door
    /// every document file is admitted through: the boot, an instance start, a transfer's destination,
    /// <c>world.load</c>/<c>world.reload</c> with the replay drive's re-read of what they pinned, the owned-world
    /// catalog and its cloud-sync gate, and the CLI's inspections. It composes the file, fills its first-fill draw
    /// sites for <paramref name="instanceIdentity"/>, settles its boot values, and admits the result, so a document
    /// whose references read a drawn cell is never admitted, or carried anywhere, unfilled.
    /// <para>A refusal never throws; <paramref name="reason"/>'s opening words name its class: <c>no file at</c>,
    /// <c>cannot read</c>, <c>cannot decode</c>, <c>&lt;path&gt; composition refused</c>, <c>&lt;path&gt; draw
    /// refused</c>, <c>&lt;path&gt; could not resolve a state reference</c>, <c>&lt;path&gt; document validation
    /// refused</c>, or <c>&lt;path&gt; is not a valid puck.world.definition.v1 document</c>. Only <c>cannot decode</c>
    /// and the last are verdicts on the bytes alone; the others rest on the moment (a lock, a vanished file), on files
    /// beside this one (a composition link or adjacency neighbour not in place yet), or on the drawing identity, so a
    /// caller acting destructively on a refusal classifies it first.</para></summary>
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
    /// <see langword="null"/> for <see cref="WorldDefinitionFileSource.LocalDocuments"/>. A source that lowers
    /// <c>.puck</c> makes <paramref name="contentHash"/> pin the lowered document.</param>
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
