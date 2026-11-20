using System.Collections.Concurrent;
using Puck.Abstractions;
using Puck.Assets;
using System.Text.Json.Nodes;

namespace Puck.World;

public static partial class WorldDefinitionFileSource {
    private static bool TryFindComposedImage(IWorldDocumentSource source, ImageCacheKey key, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out WorldComposedDocument? image) {
        if (ComposedDocuments.TryGetValue(key: key, value: out image)) { return true; }
        image = source.Compositions?.ReadComposition(key: key.DiskKey);
        // An image this process composed holds one JSON object by construction (Compact); one read back from the
        // store is checked to hold one once, when it is admitted, rather than on every serve.
        if ((image is null) || (MembersOf(json: image.ComposedJson) is null)) {
            image = null;
            return false;
        }
        ComposedDocuments.TryAdd(key: key, value: image);
        return true;
    }

    // A composed image, held per resolved document path and catalog fingerprint, so a second reach for the same document merges nothing.
    // One quilt shard names the island as its own basis and again as an adjacency neighbour, and each derived
    // corner reaches it once more, so a single boot used to ask for the same twenty-one-document merge scores of
    // times and pay for it every time. An image records every file its composition read and the exact bytes it read
    // from each, and it is offered again only when all of them still hold those bytes, so an edit to the document
    // itself, to a basis several hops above it, or to any import recomposes rather than serving a stale merge, and
    // a file that has since vanished or turned unreadable does the same. Identity is the resolved path plus the catalog
    // fingerprint, freshness is content: no clock takes part in either.
    private static readonly ConcurrentDictionary<ImageCacheKey, WorldComposedDocument> ComposedDocuments = new();

    // An image is held per document path, catalog fingerprint and source: two sources may resolve one path's graph
    // differently (the directory source refuses what the composer compiles), so neither answers for the other.
    private readonly record struct ImageCacheKey(string Path, string Fingerprint, Type Source) {
        public bool Equals(ImageCacheKey other) =>
            PuckPaths.Comparer.Equals(x: Path, y: other.Path) && (Fingerprint == other.Fingerprint) && (Source == other.Source);

        public override int GetHashCode() => HashCode.Combine(PuckPaths.Comparer.GetHashCode(obj: Path), Fingerprint, Source);

        public string DiskKey => $"{PuckPaths.Key(path: Path)}\0{Fingerprint}\0{Source.FullName}";
    }

    private static ImageCacheKey CacheKey(string path, string fingerprint, IWorldDocumentSource source) =>
        new(Path: PuckPaths.Normalize(path: path), Fingerprint: fingerprint, Source: source.GetType());
    // A chain link read through any IWorldDocumentSource decodes exactly like a load through File.ReadAllText would.
    private static string DecodeJson(byte[] bytes) => Puck.Confinement.FileContent.Text(content: bytes);
    // Every document a load or a composition reads comes through here, so the boot ledger counts each read once.
    private static bool TryReadDocument(IWorldDocumentSource source, string name, string referrerName, out string resolvedName, out byte[]? content, out string reason) {
        if (!source.TryRead(
            content: out content,
            name: name,
            reason: out reason,
            referrerName: referrerName,
            resolvedName: out resolvedName
        )) {
            return false;
        }

        WorldBootWork.Count(kind: WorldBootWork.DocumentsRead);

        return true;
    }
    // The documents one composition read, each once, in the order first read: the composed document itself first, then
    // what its basis read, then what each import read. A library several imports reach is read once.
    internal sealed class ComposedChain {
        private readonly HashSet<string> m_paths = new(comparer: PuckPaths.Comparer);

        public List<WorldComposedLink> Links { get; } = [];

        public void Add(WorldComposedLink link) {
            if (m_paths.Add(item: link.Path)) { Links.Add(item: link); }
        }
        public void AddRange(IEnumerable<WorldComposedLink> links) {
            foreach (var link in links) { Add(link: link); }
        }
    }
    // The one writer of ComposedDocuments: both of TryComposeBody's success exits hold what they are about to
    // return, so the next reader of the same path meets an image recorded together with the chain it was composed
    // from. A composition over a source whose names are not files beside their referrers is never held: nothing
    // could prove its image still stands. `composedJson` is the composed tree's compact JSON; `composed` is the tree,
    // when the caller holds one, for its origins. Returns the held image, or null when nothing was held.
    private static WorldComposedDocument? HoldComposedImage(IWorldDocumentSource source, string resolvedPath, string catalogFingerprint, byte[] composedJson, JsonObject? composed, ComposedChain touched, int reach, IReadOnlyList<CompileInput>? inputs, WorldDocumentOrigins? origins, ComposedLayers? layers) {
        if (!source.ResolvesFiles) {
            return null;
        }

        // An image its input facts prove keeps the bytes of its own document only, which a reader compares with its
        // own; every other document it read is kept by digest, for the pin.
        var chain = new WorldComposedLink[touched.Links.Count];

        for (var index = 0; (index < chain.Length); index++) {
            chain[index] = (((inputs is not null) && (index > 0)) ? touched.Links[index].WithoutBytes() : touched.Links[index]);
        }

        var key = CacheKey(
            fingerprint: catalogFingerprint,
            path: resolvedPath,
            source: source
        );
        var image = new WorldComposedDocument(
            Chain: chain,
            ComposedJson: composedJson,
            Reach: reach,
            Inputs: inputs
        ) { Layers = layers, Origins = ((composed is null) ? null : origins?.Snapshot(node: composed)) };

        ComposedDocuments[key] = image;
        if (inputs is not null) { source.Compositions?.WriteComposition(image: image, key: key.DiskKey); }

        return image;
    }
    // Whether a held image still answers for a reader whose own bytes are `ownBytes`: every document the image read
    // must still read the same through its source (a .puck link recompiles). The image's own document is the chain's
    // first link; a reader that has already read it passes its bytes, and one that has not reads it through the
    // source like every other link.
    // This one question also settles the cycle rule, which the walk above no longer gets to ask on a reuse: a
    // document already on the reader's resolution path can only appear inside an image if that document reaches
    // back into the image's own root, and an image exists only for a document whose own walk COMPLETED — a walk
    // that would have met exactly that cycle and refused. The only way the two could disagree is a file that has
    // changed since, which is what this check is.
    private static bool ImageStillStands(WorldComposedDocument image, IWorldDocumentSource source, byte[]? ownBytes) {
        if (image.Inputs is { } inputs) {
            if (((ownBytes is not null) && !ownBytes.AsSpan().SequenceEqual(other: image.Chain[0].Bytes)) || inputs.Any(predicate: static input => !input.StillHolds())) { return false; }
            CompileInputs.Restate(inputs: inputs);
            return true;
        }
        for (var index = 0; (index < image.Chain.Count); index++) {
            var link = image.Chain[index];
            // A link held by digest alone cannot be compared by its bytes, so it proves nothing here.
            var stands = ((link.Bytes is { } read) && (((index == 0) && (ownBytes is not null))
                ? ownBytes.AsSpan().SequenceEqual(other: read)
                : source.StillReads(
                    content: read,
                    resolvedName: link.Path
                )
            ));

            if (!stands) {
                return false;
            }
        }

        return true;
    }
}
