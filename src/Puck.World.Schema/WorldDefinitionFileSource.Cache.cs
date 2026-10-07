using System.Collections.Concurrent;
using System.Text;
using Puck.Abstractions;
using Puck.Assets;
using System.Text.Json.Nodes;

namespace Puck.World;

public static partial class WorldDefinitionFileSource {
    private static bool TryFindComposedImage(IWorldDocumentSource source, string key, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out WorldComposedDocument? image) {
        if (ComposedDocuments.TryGetValue(key: key, value: out image)) { return true; }
        image = source.Compositions?.ReadComposition(key: key);
        if (image is null) { return false; }
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
    private static readonly ConcurrentDictionary<string, WorldComposedDocument> ComposedDocuments = new(comparer: StringComparer.Ordinal);

    // An image is held per document path, catalog fingerprint and source: two sources may resolve one path's graph
    // differently (the directory source refuses what the composer compiles), so neither answers for the other.
    private static string CacheKey(string path, string fingerprint, IWorldDocumentSource source) =>
        $"{(PuckPaths.FoldsCase ? PuckPaths.Normalize(path: path).ToUpperInvariant() : PuckPaths.Normalize(path: path))}\0{fingerprint}\0{source.GetType().FullName}";
    // Mirrors File.ReadAllText's own encoding detection (BOM-sniffed, UTF-8 default), so a chain link read through
    // any IWorldDocumentSource decodes exactly like a load through File.ReadAllText would.
    private static string DecodeJson(byte[] bytes) {
        using var reader = new StreamReader(
            stream: new MemoryStream(buffer: bytes),
            encoding: Encoding.UTF8,
            detectEncodingFromByteOrderMarks: true
        );

        return reader.ReadToEnd();
    }
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
    // The one writer of s_composedDocuments: both of TryComposeLayers' success exits hold what they are about to
    // return, so the next reader of the same path meets an image recorded together with the chain it was composed
    // from. A composition over a source whose names are not files beside their referrers is never held: nothing
    // could prove its image still stands.
    private static void HoldComposedImage(IWorldDocumentSource source, string resolvedPath, string catalogFingerprint, JsonObject composed, List<byte[]> touched, List<string> touchedPaths, int reach, IReadOnlyList<CompileInput>? inputs) {
        if (!source.ResolvesFiles) {
            return;
        }

        var chain = new List<WorldComposedLink>(capacity: touched.Count);

        for (var index = 0; (index < touched.Count); index++) {
            chain.Add(item: new WorldComposedLink(
                Bytes: touched[index],
                Path: touchedPaths[index]
            ));
        }

        var key = CacheKey(
            fingerprint: catalogFingerprint,
            path: resolvedPath,
            source: source
        );
        var image = new WorldComposedDocument(
            Chain: chain,
            ComposedJson: Encoding.UTF8.GetBytes(s: composed.ToJsonString()),
            Reach: reach,
            Inputs: inputs
        );

        ComposedDocuments[key] = image;
        if (inputs is not null) { source.Compositions?.WriteComposition(image: image, key: key); }
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
            var stands = (((index == 0) && (ownBytes is not null))
                ? ownBytes.AsSpan().SequenceEqual(other: link.Bytes)
                : source.StillReads(
                    content: link.Bytes,
                    resolvedName: link.Path
                )
            );

            if (!stands) {
                return false;
            }
        }

        return true;
    }
}
