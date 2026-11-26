using Puck.World.Server;

namespace Puck.World;

/// <summary>
/// The client's own whole copies of the documents its package ships, by document id: the boot document, and each
/// document a <c>references</c> row of the boot document names, loaded beside the boot document through its file origin
/// (<see cref="WorldFileOrigin.TryResolveReference"/>, <see cref="WorldFileOrigin.TryLoad"/>) when first asked for. What a
/// remote seat's prediction builds its population from (<see cref="WorldSeatPrediction"/>), since a delivered document is
/// what its authority disclosed and may lack the kits and motion programs the body step reads.
/// </summary>
/// <remarks>Every member runs on the thread that pumps and presents. A document is loaded at most once, and an id no
/// reference resolves to is answered from a cached miss.</remarks>
/// <param name="boot">The boot document and the path it was loaded from.</param>
public sealed class WorldPackageDocuments(WorldDefinitionSource boot) {
    private readonly Dictionary<string, WorldDefinition?> m_byId = new(comparer: StringComparer.Ordinal);
    private int m_loaded;

    /// <summary>Returns the package's copy of the document a document id names.</summary>
    /// <param name="documentId">The document id, or <see langword="null"/>.</param>
    /// <returns>The document, or <see langword="null"/> when the package ships none of that id.</returns>
    public WorldDefinition? Find(string? documentId) {
        if (documentId is null) {
            return null;
        }
        if (string.Equals(a: documentId, b: boot.Definition.DocumentId, comparisonType: StringComparison.Ordinal)) {
            return boot.Definition;
        }
        if (m_byId.TryGetValue(key: documentId, value: out var known)) {
            return known;
        }

        // References load in order, each once, until one is the id asked for; the ones passed on the way are kept.
        var references = (boot.Definition.References ?? []);

        while (m_loaded < references.Count) {
            var reference = references[m_loaded++];

            if ((reference?.Document is not { } document) ||
                !new WorldFileOrigin(resolvedPath: boot.SourcePath).TryResolveReference(document: document, reason: out _, sibling: out var sibling) ||
                !sibling!.TryLoad(definition: out var loaded, instanceIdentity: document, reason: out _) ||
                (loaded!.DocumentId is not { } id)) {
                continue;
            }

            _ = m_byId.TryAdd(key: id, value: loaded);
            if (string.Equals(a: id, b: documentId, comparisonType: StringComparison.Ordinal)) {
                return loaded;
            }
        }

        m_byId[documentId] = null;
        return null;
    }
}
