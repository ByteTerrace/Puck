using Puck.World.Server;

namespace Puck.World;

/// <summary>
/// The client's own whole copies of the documents its package ships, by document id: the boot document, and each
/// document a <c>references</c> row of the boot document names, loaded beside the boot document through its file origin
/// (<see cref="WorldFileOrigin.TryResolveReference"/>, <see cref="WorldFileOrigin.TryLoad"/>). What a remote seat's
/// prediction builds its population from (<see cref="WorldSeatPrediction"/>), since a delivered document is what its
/// authority disclosed and may lack the kits and motion programs the body step reads.
/// </summary>
/// <remarks>A document other than the boot one is loaded off the asking thread, once per id: <see cref="Find"/> answers
/// <see langword="null"/> until the load completes, so composing a document never stalls the thread that pumps and
/// presents. The load tries first the references whose name or document is the id, then the rest. <see cref="Find"/>
/// runs on the thread that pumps and presents.</remarks>
/// <param name="boot">The boot document and the path it was loaded from.</param>
public sealed class WorldPackageDocuments(WorldDefinitionSource boot) {
    private readonly Dictionary<string, Task<WorldDefinition?>> m_byId = new(comparer: StringComparer.Ordinal);

    /// <summary>Returns the package's copy of the document a document id names.</summary>
    /// <param name="documentId">The document id, or <see langword="null"/>.</param>
    /// <returns>The document, or <see langword="null"/> while it loads or when the package ships none of that
    /// id.</returns>
    public WorldDefinition? Find(string? documentId) {
        if (documentId is null) {
            return null;
        }
        if (string.Equals(a: documentId, b: boot.Definition.DocumentId, comparisonType: StringComparison.Ordinal)) {
            return boot.Definition;
        }
        if (!m_byId.TryGetValue(key: documentId, value: out var load)) {
            load = Task.Run(function: () => Load(documentId: documentId));
            m_byId.Add(key: documentId, value: load);
        }

        return (load.IsCompletedSuccessfully ? load.Result : null);
    }

    private WorldDefinition? Load(string documentId) {
        var references = (boot.Definition.References ?? []);
        var origin = new WorldFileOrigin(resolvedPath: boot.SourcePath);

        foreach (var reference in references.Where(predicate: Names).Concat(second: references.Where(predicate: reference => !Names(reference: reference)))) {
            if ((reference?.Document is { } document) &&
                origin.TryResolveReference(document: document, reason: out _, sibling: out var sibling) &&
                sibling!.TryLoad(definition: out var loaded, instanceIdentity: document, reason: out _) &&
                string.Equals(a: loaded!.DocumentId, b: documentId, comparisonType: StringComparison.Ordinal)) {
                return loaded;
            }
        }

        return null;

        bool Names(WorldReference? reference) =>
            ((reference is not null) && (string.Equals(a: reference.Name.Value, b: documentId, comparisonType: StringComparison.Ordinal) ||
                string.Equals(a: Path.GetFileName(path: reference.Document), b: documentId, comparisonType: StringComparison.Ordinal)));
    }
}
