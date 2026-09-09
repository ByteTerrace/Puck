using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Puck.World;

/// <summary>Loads and validates a world document from a file, always alongside the canonical content-address pin of
/// the exact bytes read — the one implementation both the console's <c>world.load</c>/<c>world.reload</c> handlers
/// (<c>Puck.World.WorldMutationCommandModule</c>, via <c>WorldDefinitionLoader.TryLoadFile</c>) and the replay
/// tape's offline re-drive (<c>Puck.World.WorldReplaySnapshot.Drive</c>, through <c>WorldServer.ApplyRebuild</c>)
/// share, so a live read and a re-drive's later re-read of the same path compute the hash the same way.
/// Puck.World.Server depends on Puck.World.Schema already, so this is the lowest layer both can reach without a new
/// project reference.</summary>
public static class WorldDefinitionFileSource {
    // A composed image, held per resolved document path, so a second reach for the same document merges nothing.
    // One quilt shard names the island as its own basis and again as an adjacency neighbour, and each derived
    // corner reaches it once more, so a single boot used to ask for the same twenty-one-document merge scores of
    // times and pay for it every time. An image records every file its composition read and the exact bytes it read
    // from each, and it is offered again only when all of them still hold those bytes, so an edit to the document
    // itself, to a basis several hops above it, or to any import recomposes rather than serving a stale merge, and
    // a file that has since vanished or turned unreadable does the same. Identity is the resolved path, freshness
    // is content: no clock takes part in either.
    private static readonly ConcurrentDictionary<string, ComposedDocument> s_composedDocuments = new(comparer: StringComparer.OrdinalIgnoreCase);
    private static long s_documentsComposed;
    private static long s_documentCompositionsShared;
    // One file a composition read, with the bytes it read from it.
    private readonly record struct ComposedDocumentLink(string Path, byte[] Bytes);
    // A held image: the chain it composed from, its final tree as UTF-8 JSON (parsed on every reuse, so no reader
    // is ever handed a tree an earlier reader may have edited), and `Reach` — how many further ancestor slots the
    // subtree beneath this document occupies. Reach is what stops reuse from quietly widening the composition-depth
    // rule: an image first composed at the top of a chain is offered to a reader deeper in one only while that
    // reader's own depth plus the reach still fits inside WorldDocumentBasis.MaxChainDepth, exactly as a fresh walk
    // of the same subtree would have had to.
    private sealed record ComposedDocument(IReadOnlyList<ComposedDocumentLink> Chain, byte[] ComposedJson, int Reach);

    /// <summary>Gets how many document compositions this process has performed — one per document whose basis and
    /// imports were merged, counting every document a composition walked into, not only the ones a caller named.</summary>
    public static long DocumentsComposed => Interlocked.Read(location: ref s_documentsComposed);
    /// <summary>Gets how many document compositions this process answered from an image it had already composed,
    /// rather than merging the same tree again.</summary>
    public static long DocumentCompositionsShared => Interlocked.Read(location: ref s_documentCompositionsShared);
    /// <summary>Gets how many distinct document paths this process currently holds a composed image of. The store
    /// is not capped: it holds one image per distinct document path a directory composition has completed in this
    /// process, so it grows with the documents the process actually composes and never with how long it runs. What
    /// that costs is <see cref="ComposedDocumentBytes"/>, which the boot narration and <c>world.status</c> both
    /// read, and <see cref="ForgetComposedDocuments"/> drops the lot.</summary>
    public static int ComposedDocumentsHeld => s_composedDocuments.Count;
    /// <summary>Gets the total bytes the held composed images occupy — each image's own composed tree as UTF-8
    /// JSON, plus the bytes of every file its composition read. A file's bytes are shared with every image whose
    /// chain also read it, so this counts the shared array once per image that names it, an upper bound on what
    /// the store actually retains.</summary>
    public static long ComposedDocumentBytes {
        get {
            var total = 0L;

            foreach (var image in s_composedDocuments.Values) {
                total += image.ComposedJson.LongLength;

                for (var index = 0; (index < image.Chain.Count); index++) {
                    total += image.Chain[index].Bytes.LongLength;
                }
            }

            return total;
        }
    }

    /// <summary>Whether this process already holds a composed image of <paramref name="resolvedPath"/> whose whole
    /// chain still carries the bytes it composed from — so the next composition of that path is answered from the
    /// image instead of merging again. The fact a read-back names per neighbour: asked before a load, it says
    /// whether that load's document will be shared or composed fresh.</summary>
    /// <param name="resolvedPath">The absolute, normalized path a composition would resolve against.</param>
    /// <returns><see langword="true"/> when a held image stands for the path.</returns>
    public static bool HoldsComposedDocument(string resolvedPath) {
        if (
            string.IsNullOrEmpty(value: resolvedPath) ||
            !s_composedDocuments.TryGetValue(
            key: resolvedPath,
            value: out var image
        )
        ) {
            return false;
        }

        byte[] bytes;

        try {
            bytes = File.ReadAllBytes(path: resolvedPath);
        } catch (Exception exception) when ((exception is IOException or UnauthorizedAccessException)) {
            return false;
        }

        return ImageStillStands(
            image: image,
            ownBytes: bytes
        );
    }
    /// <summary>Drops every held composed image and zeroes the composition accounting — the door a host teardown
    /// and a law that needs a genuinely first composition both reach for. Correctness never rests on it: an image
    /// is re-proved against its chain's bytes before every reuse regardless.</summary>
    public static void ForgetComposedDocuments() {
        s_composedDocuments.Clear();
        _ = Interlocked.Exchange(
            location1: ref s_documentsComposed,
            value: 0L
        );
        _ = Interlocked.Exchange(
            location1: ref s_documentCompositionsShared,
            value: 0L
        );
    }
    // Whether a held image still answers for a reader whose own bytes are `ownBytes`: every file the image read
    // must still hold the bytes it read. The image's own document is the chain's first link and the reader has
    // already read it, so that link is compared against what the reader holds rather than read a second time.
    // This one question also settles the cycle rule, which the walk above no longer gets to ask on a reuse: a
    // document already on the reader's resolution path can only appear inside an image if that document reaches
    // back into the image's own root, and an image exists only for a document whose own walk COMPLETED — a walk
    // that would have met exactly that cycle and refused. The only way the two could disagree is a file that has
    // changed since, which is what this check is.
    private static bool ImageStillStands(ComposedDocument image, byte[] ownBytes) {
        for (var index = 0; (index < image.Chain.Count); index++) {
            var link = image.Chain[index];

            byte[] current;

            if (index == 0) {
                current = ownBytes;
            } else {
                try {
                    current = File.ReadAllBytes(path: link.Path);
                } catch (Exception exception) when ((exception is IOException or UnauthorizedAccessException)) {
                    return false;
                }
            }

            if (!current.AsSpan().SequenceEqual(other: link.Bytes)) {
                return false;
            }
        }

        return true;
    }
    // The one reader of s_composedDocuments on the composition path. A held image answers only when it still stands
    // for this reader (ImageStillStands) and its subtree still fits under the depth rule from where this reader
    // stands. The tree is parsed out of the image's own UTF-8 JSON, never handed out by reference: whoever receives
    // it edits it (a merge one level up, the neighbour resolver's reference re-expression), and the image has to
    // stay usable for the next reader. Only a directory composition is answered, matching what HoldComposedImage
    // records: every other source names its documents in a space of its own (an in-memory pair calls them "host"
    // and "fragment"), and nothing else may resolve one of those names onto a file image.
    private static bool TryServeComposedImage(IWorldDocumentSource source, string resolvedPath, byte[] bytes, IReadOnlyList<string> ancestors, out JsonObject? composed, out List<byte[]> touched, out List<string> touchedPaths, out int reach) {
        composed = null;
        touched = [];
        touchedPaths = [];
        reach = 0;

        if (
            (source is not DirectoryDocumentSource) ||
            !s_composedDocuments.TryGetValue(
            key: resolvedPath,
            value: out var image
        )
        ) {
            return false;
        }

        if (
            ((ancestors.Count + image.Reach) >= WorldDocumentBasis.MaxChainDepth) ||
            !ImageStillStands(
            image: image,
            ownBytes: bytes
        )
        ) {
            return false;
        }

        JsonObject? tree;

        try {
            tree = (JsonNode.Parse(utf8Json: new MemoryStream(buffer: image.ComposedJson)) as JsonObject);
        } catch (JsonException) {
            tree = null;
        }

        if (tree is null) {
            _ = s_composedDocuments.TryRemove(
                key: resolvedPath,
                value: out _
            );

            return false;
        }

        foreach (var link in image.Chain) {
            touched.Add(item: link.Bytes);
            touchedPaths.Add(item: link.Path);
        }

        composed = tree;
        reach = image.Reach;
        _ = Interlocked.Increment(location: ref s_documentCompositionsShared);

        return true;
    }
    // The one writer of s_composedDocuments: both of TryComposeLayers' success exits hold what they are about to
    // return, so the next reader of the same path meets an image recorded together with the chain it was composed
    // from. A composition over any byte source other than the directory one is never held — its names are not paths
    // this class could re-read to prove the image still stands.
    private static void HoldComposedImage(IWorldDocumentSource source, string resolvedPath, JsonObject composed, List<byte[]> touched, List<string> touchedPaths, int reach) {
        if (source is not DirectoryDocumentSource) {
            return;
        }

        var chain = new List<ComposedDocumentLink>(capacity: touched.Count);

        for (var index = 0; (index < touched.Count); index++) {
            chain.Add(item: new ComposedDocumentLink(
                Bytes: touched[index],
                Path: touchedPaths[index]
            ));
        }

        s_composedDocuments[resolvedPath] = new ComposedDocument(
            Chain: chain,
            ComposedJson: Encoding.UTF8.GetBytes(s: composed.ToJsonString()),
            Reach: reach
        );
    }
    // The directory-backed IWorldDocumentSource every local load walks over — the one place Path.Combine/
    // Path.GetFullPath/File.Exists/File.ReadAllBytes for a basis reference live, so TryLoad's directory behavior and
    // TryResolveChainFiles' push-side walk can never drift apart.
    private sealed class DirectoryDocumentSource : IWorldDocumentSource {
        public bool TryRead(string name, string referrerName, out string resolvedName, out byte[]? content, out string reason) {
            content = null;

            try {
                var directory = (Path.GetDirectoryName(path: Path.GetFullPath(path: referrerName)) ?? ".");

                resolvedName = Path.GetFullPath(path: Path.Combine(
                    path1: directory,
                    path2: name
                ));
            } catch (Exception exception) when ((exception is ArgumentException or NotSupportedException or PathTooLongException)) {
                resolvedName = name;
                reason = $"cannot resolve basis path '{name}' from {referrerName}: {exception.Message.ReplaceLineEndings(replacementText: " ")}";

                return false;
            }

            if (!File.Exists(path: resolvedName)) {
                reason = $"basis document {resolvedName} (named by {referrerName}) does not exist.";

                return false;
            }

            try {
                content = File.ReadAllBytes(path: resolvedName);
            } catch (Exception exception) when ((exception is IOException or UnauthorizedAccessException)) {
                reason = $"cannot read basis document {resolvedName}: {exception.Message.ReplaceLineEndings(replacementText: " ")}";

                return false;
            }

            reason = string.Empty;

            return true;
        }
    }

    /// <summary>Resolves one document name — already combined against its referrer's directory and normalized (see
    /// <see cref="ResolverDocumentSource"/>) — to its raw bytes, for the resolver-taking
    /// <see cref="TryComposeDocumentTree(string,ReadOnlyMemory{byte},WorldDocumentResolver,out JsonObject?,out string)"/>
    /// overload. Used by a caller with no filesystem of its own (a browser runtime holding every document of an
    /// import tree in memory, keyed by its worlds-relative name).</summary>
    /// <param name="resolvedName">The already-resolved document name.</param>
    /// <param name="content">The document's raw bytes on success.</param>
    /// <returns><see langword="true"/> when <paramref name="resolvedName"/> names a document the caller holds.</returns>
    public delegate bool WorldDocumentResolver(string resolvedName, out ReadOnlyMemory<byte> content);

    /// <summary>Combines <paramref name="name"/> against <paramref name="referrerName"/>'s own directory and
    /// normalizes "."/".." segments — the same relative resolution <see cref="DirectoryDocumentSource"/> performs
    /// through <see cref="Path.Combine(string,string)"/>/<see cref="Path.GetFullPath(string)"/>, replayed over
    /// forward-slash-separated keys with no filesystem underneath, so a caller resolving a reference by name alone
    /// (a browser runtime discovering which alias a document composed under) resolves it exactly as a directory load
    /// would (a <c>games/*.world.json</c> fragment importing a sibling by bare name, a <c>shards/*.world.json</c>
    /// shard naming its basis as <c>"../puck.world.json"</c>).</summary>
    /// <param name="referrerName">The referring document's own resolved name.</param>
    /// <param name="name">The authored reference, exactly as the document spells it.</param>
    /// <returns>The resolved, normalized document name.</returns>
    public static string CombineRelativeDocumentName(string referrerName, string name) {
        var directory = referrerName.Replace(oldChar: '\\', newChar: '/');
        var slash = directory.LastIndexOf(value: '/');
        var combined = ((slash >= 0)
            ? $"{directory[..slash]}/{name.Replace(oldChar: '\\', newChar: '/')}"
            : name.Replace(oldChar: '\\', newChar: '/')
        );

        return NormalizeRelativeDocumentName(path: combined);
    }
    /// <summary>Collapses "."/".." segments in a forward-slash-separated relative document name.</summary>
    /// <param name="path">The combined, not-yet-normalized relative path.</param>
    /// <returns>The normalized path.</returns>
    public static string NormalizeRelativeDocumentName(string path) {
        var segments = new List<string>();

        foreach (var segment in path.Split(separator: '/')) {
            if ((segment.Length == 0) || (segment == ".")) {
                continue;
            }

            if (segment == "..") {
                if (segments.Count > 0) {
                    segments.RemoveAt(index: (segments.Count - 1));
                }

                continue;
            }

            segments.Add(item: segment);
        }

        return string.Join(separator: "/", values: segments);
    }
    // The IWorldDocumentSource backing the resolver-taking TryComposeDocumentTree overload: resolves a reference
    // exactly like DirectoryDocumentSource (relative combination against the referrer, normalized), then hands the
    // final resolved name to the caller's own resolver instead of touching a real filesystem.
    private sealed class ResolverDocumentSource(WorldDocumentResolver resolver) : IWorldDocumentSource {
        public bool TryRead(string name, string referrerName, out string resolvedName, out byte[]? content, out string reason) {
            resolvedName = CombineRelativeDocumentName(referrerName: referrerName, name: name);

            if (!resolver(resolvedName, out var bytes)) {
                content = null;
                reason = $"document {resolvedName} (named by {referrerName}) resolves to nothing this caller can supply.";

                return false;
            }

            content = bytes.ToArray();
            reason = string.Empty;

            return true;
        }
    }

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
    private static bool TryLoadCore(string path, out WorldDefinition? definition, out string contentHash, out string reason, IWorldNeighbourResolver? neighbours, bool validateAdjacencyClaims) {
        definition = null;
        contentHash = string.Empty;

        if (!File.Exists(path: path)) {
            reason = $"no file at {path}";

            return false;
        }

        byte[] bytes;

        // The environmental read class, filtered exactly like every sibling read here (TryResolveChainFiles,
        // DirectoryDocumentSource.TryRead): a locked, half-written, or permission-refused file, whose verdict is a
        // property of the moment rather than of the bytes. Callers classify on this wording — WorldOwnedWorlds
        // quarantines a file only for a document-shape refusal — so nothing but a real I/O refusal may reach it.
        try {
            bytes = File.ReadAllBytes(path: path);
        } catch (Exception exception) when ((exception is IOException or UnauthorizedAccessException)) {
            reason = $"cannot read {path}: {exception.Message.ReplaceLineEndings(replacementText: " ")}";

            return false;
        }

        string json;

        try {
            // Mirrors File.ReadAllText's own encoding detection (BOM-sniffed, UTF-8 default) so a load through this
            // path and a load through File.ReadAllText decode identically — the hash below is over the RAW bytes
            // regardless, but the parsed document must still match what the console has always loaded.
            using var reader = new StreamReader(
                stream: new MemoryStream(buffer: bytes),
                encoding: Encoding.UTF8,
                detectEncodingFromByteOrderMarks: true
            );

            json = reader.ReadToEnd();
        } catch (Exception exception) {
            reason = $"cannot decode {path}: {exception.Message.ReplaceLineEndings(replacementText: " ")}";

            return false;
        }

        // The basis/imports composition composes on the raw JSON trees, before the strict parse: a delta, a partial
        // template, or an import fragment can never parse as a WorldDefinition on its own (required members), so the
        // model only ever sees the finished composition — with the consumed `basis`/`imports` members stripped,
        // which is why a live document's Basis/Imports are always null. The substring gate keeps flat documents on
        // the untouched single-parse path.
        var chain = ((IReadOnlyList<byte[]>)[bytes]);

        if (
            json.Contains(
            comparisonType: StringComparison.Ordinal,
            value: $"\"{WorldDocumentBasis.BasisMemberName}\""
        ) ||
            json.Contains(
            comparisonType: StringComparison.Ordinal,
            value: $"\"{WorldDocumentBasis.ImportsMemberName}\""
        )
        ) {
            if (!TryComposeChainWithImports(
                source: new DirectoryDocumentSource(),
                rootResolvedName: Path.GetFullPath(path: path),
                rootBytes: bytes,
                composed: out var composed,
                chainBytes: out var chainBytes,
                reason: out var composeReason
            )) {
                reason = $"{path} composition refused: {composeReason}";

                return false;
            }

            // composed is null when the root names neither basis nor imports, or its bytes are not a JSON object at
            // all — the already-decoded `json`/`chain` above are left untouched and the strict parse below owns the
            // wording.
            if (composed is not null) {
                json = composed.ToJsonString();
                chain = chainBytes;
            }
        }

        try {
            if (!TryParseComposed(
                definition: out var parsed,
                json: json,
                neighbours: neighbours,
                reason: out reason,
                sourceName: path,
                validateAdjacencyClaims: validateAdjacencyClaims
            )) {
                return false;
            }

            definition = parsed;
            contentHash = ((chain.Count == 1)
                ? ComputeContentHash(content: bytes)
                : ComputeChainContentHash(chain: chain)
            );

            return true;
        } catch (Exception exception) {
            reason = $"{path} is not a valid {WorldDefinition.SchemaVersion} document: {exception.Message.ReplaceLineEndings(replacementText: " ")}";

            return false;
        }
    }

    /// <summary>Parses, migrates, and validates an already-decoded, already-composed document string — the shared
    /// middle of every load path once its own bytes/basis handling has produced flat JSON: a directory load
    /// (composed above) and a bytes-only load with no directory to resolve a chain against
    /// (<see cref="WorldDefinitionLoader.TryLoad(ReadOnlyMemory{byte},string,out WorldDefinition?,out string,string,IWorldNeighbourResolver?)"/>,
    /// which refuses a <c>basis</c> member outright rather than composing one). The validation class answers under
    /// its own wording, never the strict parse's: a validation refusal can rest on facts outside this call — an
    /// adjacency claim resolved through <paramref name="neighbours"/> against documents this caller may itself be
    /// about to move — so it is retryable in a way "these bytes are not a puck.world.def.v1 document" never is, and
    /// a caller classifying on <paramref name="reason"/> must be able to tell them apart (see <see cref="TryLoad"/>'s
    /// own remarks for the exact classified prefixes).</summary>
    /// <param name="json">The already-decoded, already-composed document JSON.</param>
    /// <param name="sourceName">The document's source name, echoed in every refusal.</param>
    /// <param name="neighbours">The injected neighbour resolver a cross-document adjacency proof reads.</param>
    /// <param name="validateAdjacencyClaims">Whether to prove cross-document adjacency claims
    /// (<see cref="WorldDefinitionValidator.TryValidate"/>) or validate only document-local facts
    /// (<see cref="WorldDefinitionValidator.TryValidateLocally(WorldDefinition, out string)"/>).</param>
    /// <param name="definition">The parsed, migrated, validated definition on success; <see langword="null"/> on failure.</param>
    /// <param name="reason">The one-line failure reason, or empty on success.</param>
    /// <returns><see langword="true"/> when the document parsed, migrated, and validated.</returns>
    public static bool TryParseComposed(string json, string sourceName, IWorldNeighbourResolver? neighbours, bool validateAdjacencyClaims, out WorldDefinition? definition, out string reason) {
        definition = null;

        // This is the loader's first parse: a reference into a draw site that has not filled yet stays attached and
        // resolves on the post-draw pass (WorldDefinitionLoader), the one door that runs the draw resolver.
        if (!WorldJsonPayload.TryParse(
            json: json,
            info: WorldJsonContext.Default.WorldDefinition,
            value: out var parsed,
            error: out var parseError,
            deferDrawSites: true
        )) {
            reason = $"{sourceName} is not a valid {WorldDefinition.SchemaVersion} document: {parseError}";

            return false;
        }

        if (!string.Equals(
            a: parsed.Schema,
            b: WorldDefinition.SchemaVersion,
            comparisonType: StringComparison.Ordinal
        )) {
            reason = $"{sourceName} is not a valid {WorldDefinition.SchemaVersion} document: schema '{(parsed.Schema ?? "(absent)")}' is not {WorldDefinition.SchemaVersion}";

            return false;
        }

        parsed = WorldDefinitionMigrations.Apply(definition: parsed);

        var validated = (validateAdjacencyClaims
            ? WorldDefinitionValidator.TryValidate(
            definition: parsed,
            neighbours: neighbours,
            reason: out var refusal
        )
            : WorldDefinitionValidator.TryValidateLocally(
            definition: parsed,
            reason: out refusal
        )
        );

        if (!validated) {
            reason = $"{sourceName} document validation refused: {refusal.ReplaceLineEndings(replacementText: " ")}";

            return false;
        }

        definition = parsed;
        reason = "";

        return true;
    }

    // The one reader of an `imports` entry: an object naming its `document`, optionally an `as` alias, and nothing
    // else — shared by the composer, the describer, and the save-side peek so the three refuse one shape identically.
    private static bool TryReadImportEntry(JsonNode? entry, string referrerPath, out string document, out string? alias, out string reason) {
        document = string.Empty;
        alias = null;

        if (entry is not JsonObject entryObject) {
            reason = $"'{WorldDocumentBasis.ImportsMemberName}' in {referrerPath} must hold only entries of the form {{\"{WorldImport.DocumentMemberName}\": \"<path>\"}} with an optional \"{WorldImport.AsMemberName}\".";

            return false;
        }

        foreach (var (name, _) in entryObject) {
            if (
                !string.Equals(a: name, b: WorldImport.DocumentMemberName, comparisonType: StringComparison.Ordinal) &&
                !string.Equals(a: name, b: WorldImport.AsMemberName, comparisonType: StringComparison.Ordinal)
            ) {
                reason = $"'{WorldDocumentBasis.ImportsMemberName}' entry in {referrerPath} carries '{name}'; an entry carries only '{WorldImport.DocumentMemberName}' and '{WorldImport.AsMemberName}'.";

                return false;
            }
        }

        if (
            !entryObject.TryGetPropertyValue(propertyName: WorldImport.DocumentMemberName, jsonNode: out var documentNode) ||
            (documentNode is not JsonValue documentValue) ||
            !documentValue.TryGetValue<string>(value: out var documentText) ||
            (documentText.Length == 0)
        ) {
            reason = $"'{WorldDocumentBasis.ImportsMemberName}' entry in {referrerPath} must name a non-empty '{WorldImport.DocumentMemberName}' file path.";

            return false;
        }

        document = documentText;

        if (entryObject.TryGetPropertyValue(propertyName: WorldImport.AsMemberName, jsonNode: out var aliasNode) && (aliasNode is not null)) {
            if ((aliasNode is not JsonValue aliasValue) || !aliasValue.TryGetValue<string>(value: out var aliasText)) {
                reason = $"'{WorldDocumentBasis.ImportsMemberName}' entry {document} in {referrerPath}: '{WorldImport.AsMemberName}' must be a string.";

                return false;
            }

            if (!WorldImport.TryValidateAlias(alias: aliasText, reason: out var aliasReason)) {
                reason = $"'{WorldDocumentBasis.ImportsMemberName}' entry {document} in {referrerPath}: {aliasReason}.";

                return false;
            }

            alias = aliasText;
        }

        reason = string.Empty;

        return true;
    }
    private static string DescribeImport(string resolvedName, string? alias) =>
        ((alias is null) ? resolvedName : $"{resolvedName} as {alias}");
    private static bool TryResolveBasisPath(JsonNode? basisNode, string referrerPath, out string? basisPath, out string reason) {
        basisPath = null;

        if (
            (basisNode is not JsonValue value) ||
            !value.TryGetValue<string>(value: out var relative) ||
            (relative.Length == 0)
        ) {
            reason = $"'{WorldDocumentBasis.BasisMemberName}' in {referrerPath} must be a non-empty file path string.";

            return false;
        }

        try {
            var directory = (Path.GetDirectoryName(path: Path.GetFullPath(path: referrerPath)) ?? ".");

            basisPath = Path.GetFullPath(path: Path.Combine(
                path1: directory,
                path2: relative
            ));
            reason = string.Empty;

            return true;
        } catch (Exception exception) when ((exception is ArgumentException or NotSupportedException or PathTooLongException)) {
            reason = $"cannot resolve basis path '{relative}' from {referrerPath}: {exception.Message.ReplaceLineEndings(replacementText: " ")}";

            return false;
        }
    }
    // The one cycle/depth/read walk both TryComposeChain (which then merges) and TryResolveChainFiles (which only
    // collects) run — so the two can never refuse a cycle, a depth overrun, or a malformed ancestor differently.
    // `chain[0]` is always the root itself; a root with no `basis` member (or one that does not parse as a JSON
    // object at all) returns a one-element chain rather than refusing — a non-composing root is not this walk's
    // concern, it is the caller's strict-parse concern. Each link's own already-parsed `JsonObject` rides along in
    // `Parsed` (null only for the one-element/no-basis root case, where nothing downstream reads it) so a caller
    // composing the chain never re-parses bytes this walk already decoded.
    private static bool TryWalkChain(IWorldDocumentSource source, string rootResolvedName, byte[] rootBytes, out List<(string ResolvedName, byte[] Bytes, JsonObject? Parsed)> chain, out string reason) {
        chain = [(rootResolvedName, rootBytes, null)];
        reason = string.Empty;

        JsonObject? currentObject;

        try {
            currentObject = (JsonNode.Parse(json: DecodeJson(bytes: rootBytes)) as JsonObject);
        } catch (JsonException) {
            currentObject = null;
        }

        if (currentObject is null) {
            return true;
        }

        chain[0] = (chain[0].ResolvedName, chain[0].Bytes, currentObject);

        var visited = new List<string> { rootResolvedName };
        var currentResolvedName = rootResolvedName;

        while (true) {
            if (!currentObject.TryGetPropertyValue(
                jsonNode: out var basisNode,
                propertyName: WorldDocumentBasis.BasisMemberName
            )) {
                return true;
            }

            if (
                (basisNode is not JsonValue basisValue) ||
                !basisValue.TryGetValue<string>(value: out var name) ||
                (name.Length == 0)
            ) {
                reason = $"'{WorldDocumentBasis.BasisMemberName}' in {currentResolvedName} must be a non-empty file path string.";

                return false;
            }

            if (!source.TryRead(
                content: out var content,
                name: name,
                reason: out var readReason,
                referrerName: currentResolvedName,
                resolvedName: out var resolvedName
            )) {
                reason = readReason;

                return false;
            }

            if (visited.Contains(
                value: resolvedName,
                comparer: StringComparer.OrdinalIgnoreCase
            )) {
                reason = $"basis chain cycles back to {resolvedName} (chain: {string.Join(
                    separator: " -> ",
                    values: visited
                )}).";

                return false;
            }

            if (visited.Count >= WorldDocumentBasis.MaxChainDepth) {
                reason = $"basis chain exceeds {WorldDocumentBasis.MaxChainDepth} documents at {resolvedName}.";

                return false;
            }

            JsonObject? nextObject;

            try {
                nextObject = (JsonNode.Parse(json: DecodeJson(bytes: content!)) as JsonObject);
            } catch (JsonException) {
                nextObject = null;
            }

            if (nextObject is null) {
                reason = $"basis document {resolvedName} does not hold a JSON object.";

                return false;
            }

            chain.Add(item: (resolvedName, content!, nextObject));
            visited.Add(item: resolvedName);
            currentObject = nextObject;
            currentResolvedName = resolvedName;
        }
    }

    /// <summary>Computes the content-address pin of a basis-and-imports graph — each file's byte length (unsigned
    /// 64-bit little-endian) followed by its raw bytes, derived file first, then the basis chain, then each import's
    /// own touched bytes in authored order (see <see cref="TryComposeChainWithImports"/>), folded through one
    /// SHA-256 in the same <c>sha256-64/{hex}</c> form as <see cref="ComputeContentHash"/>. The length delimiter
    /// keeps two graphs with different file boundaries from folding to one pin, and folding every file means an edit
    /// anywhere in the graph — a template or an imported fragment included — moves a derived document's pin. A
    /// single-file chain is deliberately not equivalent to <see cref="ComputeContentHash"/>; flat documents stay on
    /// that method's undelimited form.</summary>
    /// <param name="chain">The graph's raw file bytes, derived document first.</param>
    /// <returns>The canonical content-address string.</returns>
    public static string ComputeChainContentHash(IReadOnlyList<byte[]> chain) {
        ArgumentNullException.ThrowIfNull(argument: chain);

        using var sha = IncrementalHash.CreateHash(hashAlgorithm: HashAlgorithmName.SHA256);

        Span<byte> length = stackalloc byte[8];

        foreach (var content in chain) {
            BinaryPrimitives.WriteUInt64LittleEndian(
                destination: length,
                value: ((ulong)content.LongLength)
            );
            sha.AppendData(data: length);
            sha.AppendData(data: content);
        }

        Span<byte> hash = stackalloc byte[32];

        sha.GetHashAndReset(destination: hash);

        var value = BitConverter.ToUInt64(value: hash[..8]);

        return $"sha256-64/{value:x16}";
    }
    /// <summary>Computes the canonical <c>sha256-64/{16 lowercase hex}</c> content-address pin of
    /// <paramref name="content"/> — the leading 64 bits of its SHA-256, matching
    /// <c>Puck.Assets.AssetContentHash</c>'s algorithm and <c>WorldDefinitionValidator.IsValidAddonHash</c>'s wire
    /// form exactly, so every "sha256-64/" pin in the tree reads the same bytes the same way.</summary>
    /// <param name="content">The bytes to hash.</param>
    /// <returns>The canonical content-address string.</returns>
    public static string ComputeContentHash(ReadOnlySpan<byte> content) {
        Span<byte> hash = stackalloc byte[32];

        SHA256.HashData(
            destination: hash,
            source: content
        );

        var value = BitConverter.ToUInt64(value: hash[..8]);

        return $"sha256-64/{value:x16}";
    }
    /// <summary>Composes <paramref name="rootBytes"/>' basis chain over <paramref name="source"/> — the same merge,
    /// cycle-refusal, and depth-cap (<see cref="WorldDocumentBasis.MaxChainDepth"/>) logic a directory load runs,
    /// generalized onto any <see cref="IWorldDocumentSource"/>. The caller has already read the root document's own
    /// bytes (a directory caller via <c>File.ReadAllBytes</c>, a storage caller via its own blob read) — this
    /// composes everything ABOVE the root in the chain.</summary>
    /// <param name="source">The document byte source basis references resolve against.</param>
    /// <param name="rootResolvedName">The root's own canonical resolved name (see
    /// <see cref="IWorldDocumentSource.TryRead"/>'s <c>resolvedName</c> contract) — seeds cycle detection.</param>
    /// <param name="rootBytes">The root document's own already-read raw bytes.</param>
    /// <param name="composed">The composed tree (basis member stripped) when the root named a basis and every
    /// ancestor composed; <see langword="null"/> when the root names no basis, or its bytes are not a parseable JSON
    /// object at all — in either case the caller's own strict parse of its own already-decoded bytes owns the
    /// refusal wording, and must never dereference this as non-null.</param>
    /// <param name="chainBytes">The ordered raw bytes, root first then each basis ancestor in resolution order; a
    /// single-element list (just the root) whenever <paramref name="composed"/> is <see langword="null"/>.</param>
    /// <param name="reason">The one-line refusal reason, or empty on success.</param>
    /// <returns><see langword="true"/> when the chain composed (or the root carries no basis).</returns>
    public static bool TryComposeChain(IWorldDocumentSource source, string rootResolvedName, byte[] rootBytes, out JsonObject? composed, out IReadOnlyList<byte[]> chainBytes, out string reason) {
        ArgumentNullException.ThrowIfNull(argument: source);

        composed = null;

        if (!TryWalkChain(
            chain: out var chain,
            reason: out reason,
            rootBytes: rootBytes,
            rootResolvedName: rootResolvedName,
            source: source
        )) {
            chainBytes = [rootBytes];

            return false;
        }

        chainBytes = [.. chain.Select(selector: static link => link.Bytes)];

        if (chain.Count == 1) {
            return true;
        }

        var objects = new JsonObject[chain.Count];

        for (var index = 0; (index < chain.Count); index++) {
            objects[index] = chain[index].Parsed!;
        }

        var mergedFromTop = objects[^1];

        for (var index = (chain.Count - 2); (index >= 0); index--) {
            var overlay = ((JsonObject)objects[index].DeepClone());

            overlay.Remove(propertyName: WorldDocumentBasis.BasisMemberName);

            if (!WorldDocumentBasis.TryMerge(
                basis: mergedFromTop,
                composed: out var merged,
                overlay: overlay,
                reason: out var mergeReason
            )) {
                reason = $"{chain[index].ResolvedName} over {chain[(index + 1)].ResolvedName}: {mergeReason}";
                composed = null;

                return false;
            }

            mergedFromTop = merged!;
        }

        composed = mergedFromTop;

        return true;
    }
    // The one recursive whole-file composer: resolves `resolvedPath`'s basis (recursively, through this same core)
    // and its own `imports` (each recursively resolved through this same core, then folded left to right by
    // WorldDocumentBasis.TryMergeImports), then layers basis -> imports -> this file's own body via ordinary
    // TryMerge, per WorldDocumentBasis's remarks. `stack` is the pre-own-body layer (basis merged under the folded
    // import layer) — what a derivation-preserving save diffs a target against; `composed` is the final tree.
    // `ancestors` is the resolution path from the top down to (not including) `resolvedPath`: a stack, not a global
    // visited set, so two imports independently reaching the same shared ancestor (a diamond) is never a cycle.
    private static bool TryComposeLayers(IWorldDocumentSource source, string resolvedPath, byte[] bytes, IReadOnlyList<string> ancestors, bool serveHeldImage, out JsonObject? stack, out JsonObject? composed, out List<byte[]> touched, out List<string> touchedPaths, out int reach, out string reason) {
        stack = null;
        composed = null;
        touched = [bytes];
        touchedPaths = [resolvedPath];
        reach = 0;

        if (ancestors.Contains(
            value: resolvedPath,
            comparer: StringComparer.OrdinalIgnoreCase
        )) {
            reason = $"composition cycles back to {resolvedPath} (chain: {string.Join(
                separator: " -> ",
                values: ancestors.Append(resolvedPath)
            )}).";

            return false;
        }

        if (ancestors.Count >= WorldDocumentBasis.MaxChainDepth) {
            reason = $"composition chain exceeds {WorldDocumentBasis.MaxChainDepth} documents at {resolvedPath}.";

            return false;
        }

        // The held image answers here, after the two walk rules above have had their say and before any parsing —
        // a reuse skips the merge, never a refusal the walk itself owes. `serveHeldImage` is false only for the
        // caller that wants this document's own pre-own-body layer, which an image does not carry; its children
        // are still served, so that caller pays for one merge rather than a whole graph.
        if (serveHeldImage && TryServeComposedImage(
            ancestors: ancestors,
            bytes: bytes,
            composed: out var held,
            reach: out var heldReach,
            resolvedPath: resolvedPath,
            source: source,
            touched: out var heldTouched,
            touchedPaths: out var heldTouchedPaths
        )) {
            composed = held;
            reach = heldReach;
            touched = heldTouched;
            touchedPaths = heldTouchedPaths;
            reason = string.Empty;

            return true;
        }

        JsonObject? root;

        try {
            root = (JsonNode.Parse(json: DecodeJson(bytes: bytes)) as JsonObject);
        } catch (JsonException) {
            root = null;
        }

        if (root is null) {
            reason = $"{resolvedPath} does not hold a JSON object.";

            return false;
        }

        // A flat file (neither member authored) returns its own tree completely unmerged: an ordinary TryMerge pass
        // over an empty basis would silently drop any of the file's OWN top-level members whose value happens to be
        // a literal JSON null (an unset nullable field with no per-property WhenWritingNull condition), since a
        // null-valued overlay member means "remove the inherited member" — there is nothing to remove here, but
        // nothing to ADD either, so the member would vanish. A flat file has nothing to compose, so it must never
        // cross that merge at all.
        if (
            !root.ContainsKey(propertyName: WorldDocumentBasis.BasisMemberName) &&
            !root.ContainsKey(propertyName: WorldDocumentBasis.ImportsMemberName)
        ) {
            stack = new JsonObject();
            composed = ((JsonObject)root.DeepClone());
            reason = string.Empty;
            _ = Interlocked.Increment(location: ref s_documentsComposed);
            HoldComposedImage(
                composed: composed,
                reach: reach,
                resolvedPath: resolvedPath,
                source: source,
                touched: touched,
                touchedPaths: touchedPaths
            );

            return true;
        }

        var nextAncestors = ((IReadOnlyList<string>)[.. ancestors, resolvedPath]);
        var basisComposed = new JsonObject();

        if (root.TryGetPropertyValue(
            jsonNode: out var basisNode,
            propertyName: WorldDocumentBasis.BasisMemberName
        )) {
            if (
                (basisNode is not JsonValue basisValue) ||
                !basisValue.TryGetValue<string>(value: out var basisName) ||
                (basisName.Length == 0)
            ) {
                reason = $"'{WorldDocumentBasis.BasisMemberName}' in {resolvedPath} must be a non-empty file path string.";

                return false;
            }

            if (!source.TryRead(
                content: out var basisContent,
                name: basisName,
                reason: out reason,
                referrerName: resolvedPath,
                resolvedName: out var basisResolvedName
            )) {
                return false;
            }

            if (!TryComposeLayers(
                ancestors: nextAncestors,
                bytes: basisContent!,
                composed: out var basisResult,
                reach: out var basisReach,
                reason: out reason,
                resolvedPath: basisResolvedName,
                serveHeldImage: true,
                source: source,
                stack: out _,
                touched: out var basisTouched,
                touchedPaths: out var basisTouchedPaths
            )) {
                return false;
            }

            basisComposed = basisResult!;
            reach = Math.Max(
                val1: reach,
                val2: (basisReach + 1)
            );
            touched.AddRange(collection: basisTouched);
            touchedPaths.AddRange(collection: basisTouchedPaths);
        }

        var ownBody = ((JsonObject)root.DeepClone());

        ownBody.Remove(propertyName: WorldDocumentBasis.BasisMemberName);
        ownBody.Remove(propertyName: WorldDocumentBasis.ImportsMemberName);

        var importsLayer = new JsonObject();

        if (root.TryGetPropertyValue(
            jsonNode: out var importsNode,
            propertyName: WorldDocumentBasis.ImportsMemberName
        )) {
            if (importsNode is not JsonArray importsArray) {
                reason = $"'{WorldDocumentBasis.ImportsMemberName}' in {resolvedPath} must be an array of import entries.";

                return false;
            }

            var importTrees = new List<(string Name, JsonObject Tree)>();
            var modules = new List<(string Name, JsonObject Tree, WorldExports Exports)>();

            foreach (var entry in importsArray) {
                if (!TryReadImportEntry(
                    alias: out var importAlias,
                    document: out var importName,
                    entry: entry,
                    reason: out reason,
                    referrerPath: resolvedPath
                )) {
                    return false;
                }

                if (!source.TryRead(
                    content: out var importContent,
                    name: importName,
                    reason: out reason,
                    referrerName: resolvedPath,
                    resolvedName: out var importResolvedName
                )) {
                    return false;
                }

                if (!TryComposeLayers(
                    ancestors: nextAncestors,
                    bytes: importContent!,
                    composed: out var importResult,
                    reach: out var importReach,
                    reason: out reason,
                    resolvedPath: importResolvedName,
                    serveHeldImage: true,
                    source: source,
                    stack: out _,
                    touched: out var importTouched,
                    touchedPaths: out var importTouchedPaths
                )) {
                    return false;
                }

                reach = Math.Max(
                    val1: reach,
                    val2: (importReach + 1)
                );

                if ((importAlias is not null) && !WorldModuleNamespace.TryApply(
                    alias: importAlias,
                    module: importResult!,
                    reason: out var aliasReason
                )) {
                    reason = $"{resolvedPath} imports {importResolvedName} as '{importAlias}': {aliasReason}";

                    return false;
                }

                var importDescription = DescribeImport(alias: importAlias, resolvedName: importResolvedName);

                if (!WorldModuleExports.TryTake(
                    exports: out var exports,
                    module: importResult!,
                    moduleName: importDescription,
                    reason: out var exportsReason
                )) {
                    reason = $"{resolvedPath} imports {exportsReason}";

                    return false;
                }

                modules.Add(item: (importDescription, importResult!, exports));
                importTrees.Add(item: (importDescription, importResult!));
                touched.AddRange(collection: importTouched);
                touchedPaths.AddRange(collection: importTouchedPaths);
            }

            if (!WorldModuleExports.TryCheckLayers(
                basis: basisComposed,
                hostPath: resolvedPath,
                imports: modules,
                ownBody: ownBody,
                reason: out var surfaceReason,
                surfaces: out _
            )) {
                reason = surfaceReason;

                return false;
            }

            if (!WorldDocumentBasis.TryMergeImports(
                composed: out var mergedImports,
                imports: importTrees,
                reason: out var mergeReason,
                restated: ownBody
            )) {
                reason = $"{resolvedPath}: {mergeReason}";

                return false;
            }

            importsLayer = mergedImports!;
        }

        if (!WorldDocumentBasis.TryMerge(
            basis: basisComposed,
            composed: out var afterImports,
            overlay: importsLayer,
            reason: out var stackReason
        )) {
            reason = $"{resolvedPath}: {stackReason}";

            return false;
        }

        stack = afterImports;

        if (!WorldDocumentBasis.TryMerge(
            basis: afterImports!,
            composed: out var final,
            overlay: ownBody,
            reason: out var finalReason
        )) {
            reason = $"{resolvedPath}: {finalReason}";

            return false;
        }

        composed = final;
        reason = string.Empty;
        _ = Interlocked.Increment(location: ref s_documentsComposed);
        HoldComposedImage(
            composed: composed!,
            reach: reach,
            resolvedPath: resolvedPath,
            source: source,
            touched: touched,
            touchedPaths: touchedPaths
        );

        return true;
    }
    /// <summary>Composes <paramref name="rootBytes"/>' whole basis-and-imports graph over <paramref name="source"/>
    /// — the generalization of <see cref="TryComposeChain"/> that additionally resolves the root's (and every
    /// ancestor's) own <c>imports</c> list (see <see cref="WorldDocumentBasis"/>'s remarks). The caller has already
    /// read the root document's own bytes.</summary>
    /// <param name="source">The document byte source basis/import references resolve against.</param>
    /// <param name="rootResolvedName">The root's own canonical resolved name — seeds cycle detection.</param>
    /// <param name="rootBytes">The root document's own already-read raw bytes.</param>
    /// <param name="composed">The composed tree (basis/imports members stripped) when the root named either and
    /// every reference composed; <see langword="null"/> when the root names neither, or its bytes are not a
    /// parseable JSON object at all — in either case the caller's own strict parse of its own already-decoded bytes
    /// owns the refusal wording, and must never dereference this as non-null.</param>
    /// <param name="chainBytes">The bytes of every file touched composing the root — the root first, then its basis
    /// chain, then each import's own touched bytes in authored order; a single-element list (just the root) whenever
    /// <paramref name="composed"/> is <see langword="null"/>.</param>
    /// <param name="reason">The one-line refusal reason, or empty on success.</param>
    /// <returns><see langword="true"/> when the graph composed (or the root names neither basis nor imports).</returns>
    public static bool TryComposeChainWithImports(IWorldDocumentSource source, string rootResolvedName, byte[] rootBytes, out JsonObject? composed, out IReadOnlyList<byte[]> chainBytes, out string reason) {
        ArgumentNullException.ThrowIfNull(argument: source);

        composed = null;
        chainBytes = [rootBytes];
        reason = string.Empty;

        JsonObject? root;

        try {
            root = (JsonNode.Parse(json: DecodeJson(bytes: rootBytes)) as JsonObject);
        } catch (JsonException) {
            root = null;
        }

        if (root is null) {
            return true;
        }

        if (
            !root.ContainsKey(propertyName: WorldDocumentBasis.BasisMemberName) &&
            !root.ContainsKey(propertyName: WorldDocumentBasis.ImportsMemberName)
        ) {
            return true;
        }

        if (!TryComposeLayers(
            ancestors: [],
            bytes: rootBytes,
            composed: out var result,
            reach: out _,
            reason: out reason,
            resolvedPath: rootResolvedName,
            serveHeldImage: true,
            source: source,
            stack: out _,
            touched: out var touched,
            touchedPaths: out _
        )) {
            return false;
        }

        composed = result;
        chainBytes = touched;

        return true;
    }
    /// <summary>Composes <paramref name="path"/>'s basis-and-imports graph, stopping BEFORE its own body — the
    /// layer a derivation-preserving save diffs a target definition against (see
    /// <see cref="WorldDefinitionSerialization.SavePreservingBasis"/>). An absent basis and absent imports both
    /// compose to an empty object, matching a flat file's empty inheritance.</summary>
    /// <param name="path">The document file to compose.</param>
    /// <param name="stack">The composed basis-plus-imports layer on success; <see langword="null"/> on failure.</param>
    /// <param name="reason">The one-line failure reason, or empty on success.</param>
    /// <returns><see langword="true"/> when the file was readable and its basis/imports graph composed.</returns>
    public static bool TryComposeStackTree(string path, out JsonObject? stack, out string reason) {
        stack = null;

        try {
            var bytes = File.ReadAllBytes(path: path);

            if (!TryComposeLayers(
                ancestors: [],
                bytes: bytes,
                composed: out _,
                reach: out _,
                reason: out reason,
                resolvedPath: Path.GetFullPath(path: path),
                serveHeldImage: false,
                source: new DirectoryDocumentSource(),
                stack: out var result,
                touched: out _,
                touchedPaths: out _
            )) {
                return false;
            }

            stack = result;

            return true;
        } catch (Exception exception) when ((exception is IOException or UnauthorizedAccessException or JsonException or ArgumentException or NotSupportedException)) {
            stack = null;
            reason = $"cannot compose {path}: {exception.Message.ReplaceLineEndings(replacementText: " ")}";

            return false;
        }
    }
    /// <summary>Loads the document at <paramref name="path"/> as its composed raw JSON tree — its basis chain and
    /// its own imports resolved and merged, both consumed members stripped — without parsing, migrating, or
    /// validating it. A flat document returns its own tree. The seam the derivation-preserving save uses to obtain
    /// the basis/imports side of its diff, where a referenced document may be a partial fragment no model parse
    /// could admit on its own.</summary>
    /// <param name="path">The document file to compose.</param>
    /// <param name="tree">The composed tree on success; <see langword="null"/> on failure.</param>
    /// <param name="reason">The one-line failure reason, or empty on success.</param>
    /// <returns><see langword="true"/> when the file was readable and its graph composed.</returns>
    public static bool TryComposeDocumentTree(string path, out JsonObject? tree, out string reason) {
        tree = null;

        try {
            var bytes = File.ReadAllBytes(path: path);

            return TryComposeDocumentTreeCore(
                bytes: bytes,
                reason: out reason,
                resolvedPath: Path.GetFullPath(path: path),
                source: new DirectoryDocumentSource(),
                tree: out tree
            );
        } catch (Exception exception) when ((exception is IOException or UnauthorizedAccessException or JsonException or ArgumentException or NotSupportedException)) {
            tree = null;
            reason = $"cannot compose {path}: {exception.Message.ReplaceLineEndings(replacementText: " ")}";

            return false;
        }
    }
    /// <summary>Composes <paramref name="rootBytes"/>' whole basis-and-imports graph purely from memory —
    /// <paramref name="resolver"/> answers every reference instead of a real filesystem, resolved by the SAME
    /// relative-combination rule <see cref="TryComposeDocumentTree(string,out JsonObject?,out string)"/> applies on
    /// disk (a reference is combined against its referrer's own directory and "."/".." segments are collapsed), so a
    /// caller holding an import tree's documents keyed by their worlds-relative names (<c>"puck.world.json"</c>,
    /// <c>"games/tictactoe.world.json"</c>) composes identically to a directory load of the same tree.</summary>
    /// <param name="rootName">The root document's own worlds-relative name — seeds relative resolution for its own
    /// basis/imports references and cycle detection.</param>
    /// <param name="rootBytes">The root document's own raw bytes.</param>
    /// <param name="resolver">Resolves every basis/imports reference the root's graph names, by its resolved name.</param>
    /// <param name="tree">The composed tree (basis/imports members stripped) on success; <see langword="null"/> on failure.</param>
    /// <param name="reason">The one-line refusal reason, or empty on success.</param>
    /// <returns><see langword="true"/> when the graph composed (or the root names neither basis nor imports).</returns>
    public static bool TryComposeDocumentTree(string rootName, ReadOnlyMemory<byte> rootBytes, WorldDocumentResolver resolver, out JsonObject? tree, out string reason) {
        ArgumentNullException.ThrowIfNull(argument: resolver);

        return TryComposeDocumentTreeCore(
            bytes: rootBytes.ToArray(),
            reason: out reason,
            resolvedPath: NormalizeRelativeDocumentName(path: rootName),
            source: new ResolverDocumentSource(resolver: resolver),
            tree: out tree
        );
    }
    // The shared core both TryComposeDocumentTree overloads call: compose the graph over whichever
    // IWorldDocumentSource the caller supplied (disk-backed or resolver-backed), never duplicating
    // TryComposeLayers' walk itself.
    private static bool TryComposeDocumentTreeCore(byte[] bytes, string resolvedPath, IWorldDocumentSource source, out JsonObject? tree, out string reason) {
        tree = null;

        if (!TryComposeLayers(
            ancestors: [],
            bytes: bytes,
            composed: out var composed,
            reach: out _,
            reason: out reason,
            resolvedPath: resolvedPath,
            serveHeldImage: true,
            source: source,
            stack: out _,
            touched: out _,
            touchedPaths: out _
        )) {
            return false;
        }

        tree = composed;
        reason = string.Empty;

        return true;
    }
    // Shares TryComposeLayers' cycle/depth walk and basis-then-imports-then-own-body recursion order, but collects
    // a (path, own top-level keys) entry per file instead of merging JSON — the read-back `world.imports` prints.
    // Each entry's keys are exactly what that file's own JSON declares at the root (basis/imports/exports members
    // excluded; exports ride beside the keys as authored), in MERGE order: a later entry's same key overrides an
    // earlier one's.
    private static bool TryDescribeLayers(IWorldDocumentSource source, string resolvedPath, string? alias, byte[] bytes, IReadOnlyList<string> ancestors, List<(string Path, string? Alias, IReadOnlyList<string> Keys, WorldExports? Exports)> layers, out string reason) {
        if (ancestors.Contains(
            value: resolvedPath,
            comparer: StringComparer.OrdinalIgnoreCase
        )) {
            reason = $"composition cycles back to {resolvedPath} (chain: {string.Join(
                separator: " -> ",
                values: ancestors.Append(resolvedPath)
            )}).";

            return false;
        }

        if (ancestors.Count >= WorldDocumentBasis.MaxChainDepth) {
            reason = $"composition chain exceeds {WorldDocumentBasis.MaxChainDepth} documents at {resolvedPath}.";

            return false;
        }

        if (JsonNode.Parse(json: DecodeJson(bytes: bytes)) is not JsonObject root) {
            reason = $"{resolvedPath} does not hold a JSON object.";

            return false;
        }

        var nextAncestors = ((IReadOnlyList<string>)[.. ancestors, resolvedPath]);

        if (root.TryGetPropertyValue(
            jsonNode: out var basisNode,
            propertyName: WorldDocumentBasis.BasisMemberName
        )) {
            if (
                (basisNode is not JsonValue basisValue) ||
                !basisValue.TryGetValue<string>(value: out var basisName) ||
                (basisName.Length == 0)
            ) {
                reason = $"'{WorldDocumentBasis.BasisMemberName}' in {resolvedPath} must be a non-empty file path string.";

                return false;
            }

            if (!source.TryRead(
                content: out var basisContent,
                name: basisName,
                reason: out reason,
                referrerName: resolvedPath,
                resolvedName: out var basisResolvedName
            )) {
                return false;
            }

            if (!TryDescribeLayers(
                alias: null,
                ancestors: nextAncestors,
                bytes: basisContent!,
                layers: layers,
                reason: out reason,
                resolvedPath: basisResolvedName,
                source: source
            )) {
                return false;
            }
        }

        if (root.TryGetPropertyValue(
            jsonNode: out var importsNode,
            propertyName: WorldDocumentBasis.ImportsMemberName
        )) {
            if (importsNode is not JsonArray importsArray) {
                reason = $"'{WorldDocumentBasis.ImportsMemberName}' in {resolvedPath} must be an array of import entries.";

                return false;
            }

            foreach (var entry in importsArray) {
                if (!TryReadImportEntry(
                    alias: out var importAlias,
                    document: out var importName,
                    entry: entry,
                    reason: out reason,
                    referrerPath: resolvedPath
                )) {
                    return false;
                }

                if (!source.TryRead(
                    content: out var importContent,
                    name: importName,
                    reason: out reason,
                    referrerName: resolvedPath,
                    resolvedName: out var importResolvedName
                )) {
                    return false;
                }

                if (!TryDescribeLayers(
                    alias: importAlias,
                    ancestors: nextAncestors,
                    bytes: importContent!,
                    layers: layers,
                    reason: out reason,
                    resolvedPath: importResolvedName,
                    source: source
                )) {
                    return false;
                }
            }
        }

        var ownKeys = root.Select(selector: static member => member.Key)
            .Where(predicate: static name => (
                !string.Equals(a: name, b: WorldDocumentBasis.BasisMemberName, comparisonType: StringComparison.Ordinal) &&
                !string.Equals(a: name, b: WorldDocumentBasis.ImportsMemberName, comparisonType: StringComparison.Ordinal) &&
                !string.Equals(a: name, b: WorldExports.MemberName, comparisonType: StringComparison.Ordinal)
            ))
            .ToArray();

        WorldExports? exports = null;

        if (root.TryGetPropertyValue(propertyName: WorldExports.MemberName, jsonNode: out var exportsNode) && (exportsNode is not null)) {
            try {
                exports = JsonSerializer.Deserialize<WorldExports>(node: exportsNode, options: WorldJsonContext.Default.Options);
            } catch (JsonException exception) {
                reason = $"'{WorldExports.MemberName}' in {resolvedPath} is malformed: {exception.Message}";

                return false;
            }
        }

        layers.Add(item: (resolvedPath, alias, ownKeys, exports));
        reason = string.Empty;

        return true;
    }
    /// <summary>Describes <paramref name="path"/>'s whole basis-and-imports graph in merge order — the read-back
    /// <c>world.imports</c> prints: each resolved file path paired with the top-level keys ITS OWN JSON declares
    /// (basis/imports members excluded), from the deepest basis ancestor through every import to the file's own
    /// body last. A later entry's same key overrides an earlier one's (see <see cref="WorldDocumentBasis"/>'s
    /// remarks).</summary>
    /// <param name="path">The document file to describe.</param>
    /// <param name="layers">Each layer in merge order on success — its resolved path, the alias it composed under
    /// (<see langword="null"/> for a basis link or an unaliased import), its own top-level keys, and the
    /// <c>exports</c> it authors as written (<see langword="null"/> when it authors none); empty on failure.</param>
    /// <param name="reason">The one-line failure reason, or empty on success.</param>
    /// <returns><see langword="true"/> when the file was readable and its graph resolved.</returns>
    public static bool TryDescribeComposition(string path, out IReadOnlyList<(string Path, string? Alias, IReadOnlyList<string> Keys, WorldExports? Exports)> layers, out string reason) {
        var collected = new List<(string Path, string? Alias, IReadOnlyList<string> Keys, WorldExports? Exports)>();

        try {
            var bytes = File.ReadAllBytes(path: path);

            if (!TryDescribeLayers(
                alias: null,
                ancestors: [],
                bytes: bytes,
                layers: collected,
                reason: out reason,
                resolvedPath: Path.GetFullPath(path: path),
                source: new DirectoryDocumentSource()
            )) {
                layers = collected;

                return false;
            }

            layers = collected;

            return true;
        } catch (Exception exception) when ((exception is IOException or UnauthorizedAccessException or JsonException or ArgumentException or NotSupportedException)) {
            layers = collected;
            reason = $"cannot describe {path}: {exception.Message.ReplaceLineEndings(replacementText: " ")}";

            return false;
        }
    }
    /// <summary>Loads, migrates, and validates a world document from <paramref name="path"/>, returning the
    /// canonical <c>sha256-64/{hex}</c> content-address pin of the exact bytes consumed — never a re-serialization
    /// of the parsed document, so a byte the parse ignores (whitespace, member order) still moves the pin, and
    /// never a re-serialization of a migrated document either: <see cref="WorldDefinitionMigrations.Apply"/> runs
    /// on the in-memory parse only, between parsing and validating, so a pre-field save on disk still hashes to
    /// what its bytes actually are. A document naming a <c>basis</c> and/or <c>imports</c> composes its whole graph
    /// first (see <see cref="WorldDocumentBasis"/>) and pins every touched file's raw bytes
    /// (<see cref="ComputeChainContentHash"/>), so an edit to a template or an imported fragment moves every
    /// dependent document's pin.
    /// A load boundary never throws out of this method: every failure comes back as
    /// <paramref name="reason"/>, whose opening words name the class — <c>no file at</c>, <c>cannot read</c>,
    /// <c>cannot decode</c>, <c>&lt;path&gt; composition refused</c>, <c>&lt;path&gt; document validation
    /// refused</c>, or <c>&lt;path&gt; is not a valid puck.world.def.v1 document</c>. Only that last pair is a
    /// verdict on the bytes themselves; the rest can each answer differently on a later call, so a caller acting
    /// destructively on a refusal (<c>WorldOwnedWorlds</c> quarantines a file it cannot admit) must classify before
    /// it acts.</summary>
    /// <param name="path">The file to load.</param>
    /// <param name="definition">The loaded definition on success; <see langword="null"/> on failure.</param>
    /// <param name="contentHash">The canonical content-address pin of the bytes read, on success; empty on failure.</param>
    /// <param name="reason">The one-line failure reason, or empty on success.</param>
    /// <param name="neighbours">The injected neighbour resolver <see cref="WorldDefinitionValidator.Validate"/>
    /// reads for a cross-document adjacency proof — see its own remarks. Optional here (unlike
    /// <see cref="WorldDefinitionValidator.Validate"/>'s own required parameter): this method loads arbitrary files
    /// for purposes that mostly have nothing to do with adjacency (catalog scans, replay re-reads, tests), so
    /// <see langword="null"/> (the default) is the ordinary case. A caller that does have a reachable resolver at
    /// hand should pass it.</param>
    /// <returns><see langword="true"/> when the file loaded and validated.</returns>
    public static bool TryLoad(string path, out WorldDefinition? definition, out string contentHash, out string reason, IWorldNeighbourResolver? neighbours = null) =>
        TryLoadCore(
            contentHash: out contentHash,
            definition: out definition,
            neighbours: neighbours,
            path: path,
            reason: out reason,
            validateAdjacencyClaims: true
        );
    /// <summary>Loads a file while validating only the facts owned by that document. Used before the composition root
    /// can supply a neighbour resolver, and by replay to obtain the bytes whose recorded content hash is compared by
    /// the caller; a live first-load boundary must use <see cref="TryLoad"/> and prove cross-document claims.</summary>
    /// <param name="path">The file to load.</param>
    /// <param name="definition">The loaded definition on success; <see langword="null"/> on failure.</param>
    /// <param name="contentHash">The canonical content-address pin of the bytes read, on success; empty on failure.</param>
    /// <param name="reason">The one-line failure reason, or empty on success.</param>
    /// <returns><see langword="true"/> when the file loaded and its document-local facts validated.</returns>
    public static bool TryLoadLocally(string path, out WorldDefinition? definition, out string contentHash, out string reason) =>
        TryLoadCore(
            contentHash: out contentHash,
            definition: out definition,
            neighbours: null,
            path: path,
            reason: out reason,
            validateAdjacencyClaims: false
        );
    // A fully in-memory IWorldDocumentSource for TryComposeFragmentBytes: the two names the synthetic root below
    // ever names ("host", "fragment") resolve to caller-supplied bytes, never a file. Any other name is a fragment
    // or host that itself declares basis/imports of its own — refused by name, since neither this entry point nor
    // its caller (a browser runtime with no filesystem) can resolve a further file reference.
    private sealed class InMemoryDocumentSource(byte[] host, byte[] fragment) : IWorldDocumentSource {
        public bool TryRead(string name, string referrerName, out string resolvedName, out byte[]? content, out string reason) {
            resolvedName = name;

            switch (name) {
                case "host":
                    content = host;
                    reason = string.Empty;

                    return true;
                case "fragment":
                    content = fragment;
                    reason = string.Empty;

                    return true;
                default:
                    content = null;
                    reason = $"'{name}' (named by {referrerName}) resolves to nothing — this composition is in-memory only and carries no further basis or imports beyond the fragment itself.";

                    return false;
            }
        }
    }
    /// <summary>Composes <paramref name="fragmentBytes"/> under <paramref name="hostBytes"/> entirely in memory —
    /// no file system read — the way a directory load composes an aliased <c>imports</c> entry
    /// (<see cref="WorldModuleNamespace.TryApply"/> renames the fragment's own rows under
    /// <c>&lt;alias&gt;_&lt;name&gt;</c>, <see cref="WorldModuleExports.TryTake"/> and
    /// <see cref="WorldModuleExports.TryCheckLayers"/> enforce its declared <c>exports</c>), so a fragment fetched as
    /// bytes over the network composes identically to one loaded from a <c>games/*.world.json</c> file. Neither
    /// input may itself declare a further <c>basis</c> or <c>imports</c> — this is a single composition step, not a
    /// recursive resolve, since the caller (a browser runtime) has no file system to resolve one against; either
    /// carrying one refuses by name through the same <see cref="TryComposeLayers"/> path a directory load runs.
    /// </summary>
    /// <param name="hostBytes">The host document's raw bytes (the basis the fragment composes under — e.g. an
    /// official <c>standard.basis.json</c> fetched by the caller).</param>
    /// <param name="fragmentBytes">The fragment's raw bytes (a district or game document carrying <c>exports</c>).</param>
    /// <param name="alias">The alias the fragment composes under — every row it declares appears in the result as
    /// <c>&lt;alias&gt;_&lt;name&gt;</c>.</param>
    /// <param name="composed">The composed tree (host plus the aliased fragment) on success; <see langword="null"/> on failure.</param>
    /// <param name="reason">The one-line refusal reason, or empty on success.</param>
    /// <returns><see langword="true"/> when the fragment composed under the host.</returns>
    public static bool TryComposeFragmentBytes(byte[] hostBytes, byte[] fragmentBytes, string alias, out JsonObject? composed, out string reason) {
        ArgumentNullException.ThrowIfNull(argument: hostBytes);
        ArgumentNullException.ThrowIfNull(argument: fragmentBytes);
        ArgumentException.ThrowIfNullOrEmpty(argument: alias);

        var root = new JsonObject {
            [WorldDocumentBasis.BasisMemberName] = "host",
            [WorldDocumentBasis.ImportsMemberName] = new JsonArray {
                new JsonObject {
                    [WorldImport.DocumentMemberName] = "fragment",
                    [WorldImport.AsMemberName] = alias,
                },
            },
        };

        return TryComposeLayers(
            ancestors: [],
            bytes: Encoding.UTF8.GetBytes(s: root.ToJsonString()),
            composed: out composed,
            reach: out _,
            reason: out reason,
            resolvedPath: "(in-memory fragment composition)",
            serveHeldImage: true,
            source: new InMemoryDocumentSource(host: hostBytes, fragment: fragmentBytes),
            stack: out _,
            touched: out _,
            touchedPaths: out _
        );
    }
    /// <summary>Reads the document at <paramref name="path"/> just far enough to resolve its <c>basis</c> member —
    /// the save-side peek <c>world.save</c> uses to decide between a derivation-preserving delta write and a flat
    /// write. The file is the one source of truth for its own derivation; nothing caches this between load and
    /// save.</summary>
    /// <param name="path">The document file to peek.</param>
    /// <param name="basisPath">The resolved absolute basis path, or <see langword="null"/> when the document
    /// declares none.</param>
    /// <param name="reason">The one-line failure reason, or empty on success.</param>
    /// <returns><see langword="true"/> when the file was readable and its root answered; a missing <c>basis</c>
    /// member is a success with a <see langword="null"/> <paramref name="basisPath"/>.</returns>
    public static bool TryPeekBasis(string path, out string? basisPath, out string reason) {
        basisPath = null;

        try {
            var json = File.ReadAllText(path: path);

            if (JsonNode.Parse(json: json) is not JsonObject root) {
                reason = $"{path} does not hold a JSON object.";

                return false;
            }

            if (!root.TryGetPropertyValue(
                jsonNode: out var basisNode,
                propertyName: WorldDocumentBasis.BasisMemberName
            )) {
                reason = string.Empty;

                return true;
            }

            if (!TryResolveBasisPath(
                basisNode: basisNode,
                basisPath: out var resolved,
                reason: out reason,
                referrerPath: path
            )) {
                return false;
            }

            basisPath = resolved;
            reason = string.Empty;

            return true;
        } catch (Exception exception) when ((exception is IOException or UnauthorizedAccessException or JsonException or ArgumentException or NotSupportedException)) {
            reason = $"cannot peek {path}: {exception.Message.ReplaceLineEndings(replacementText: " ")}";

            return false;
        }
    }
    /// <summary>Reads the document at <paramref name="path"/> just far enough to resolve its <c>imports</c> list —
    /// the composition-graph read-back's peek (<c>world.imports</c>), and the save-side peek a derivation-preserving
    /// save uses beside <see cref="TryPeekBasis"/>. The file is the one source of truth for its own derivation;
    /// nothing caches this between load and save.</summary>
    /// <param name="path">The document file to peek.</param>
    /// <param name="imports">Each import's resolved absolute path and alias in authored order, or an empty list
    /// when the document declares none.</param>
    /// <param name="reason">The one-line failure reason, or empty on success.</param>
    /// <returns><see langword="true"/> when the file was readable and its root answered; a missing <c>imports</c>
    /// member is a success with an empty <paramref name="imports"/>.</returns>
    public static bool TryPeekImports(string path, out IReadOnlyList<(string Path, string? Alias)> imports, out string reason) {
        imports = [];

        try {
            var json = File.ReadAllText(path: path);

            if (JsonNode.Parse(json: json) is not JsonObject root) {
                reason = $"{path} does not hold a JSON object.";

                return false;
            }

            if (!root.TryGetPropertyValue(
                jsonNode: out var importsNode,
                propertyName: WorldDocumentBasis.ImportsMemberName
            )) {
                reason = string.Empty;

                return true;
            }

            if (importsNode is not JsonArray importsArray) {
                reason = $"'{WorldDocumentBasis.ImportsMemberName}' in {path} must be an array of import entries.";

                return false;
            }

            var resolved = new List<(string Path, string? Alias)>(capacity: importsArray.Count);

            foreach (var entry in importsArray) {
                if (!TryReadImportEntry(
                    alias: out var alias,
                    document: out var document,
                    entry: entry,
                    reason: out reason,
                    referrerPath: path
                )) {
                    return false;
                }

                if (!TryResolveBasisPath(
                    basisNode: JsonValue.Create(value: document),
                    basisPath: out var resolvedEntry,
                    reason: out reason,
                    referrerPath: path
                )) {
                    return false;
                }

                resolved.Add(item: (resolvedEntry!, alias));
            }

            imports = resolved;
            reason = string.Empty;

            return true;
        } catch (Exception exception) when ((exception is IOException or UnauthorizedAccessException or JsonException or ArgumentException or NotSupportedException)) {
            reason = $"cannot peek {path}: {exception.Message.ReplaceLineEndings(replacementText: " ")}";

            return false;
        }
    }
    /// <summary>Walks <paramref name="path"/>'s basis chain over the local filesystem WITHOUT merging — returns each
    /// file's own base name and raw bytes, derived document first, in resolution order. The push-side twin of
    /// <see cref="TryComposeChain"/>: a push wants each chain link's OWN authored bytes (to round-trip the authored
    /// delta shape into the cloud), never the merged composite a load produces. Every non-root link must resolve to
    /// a direct child of <paramref name="path"/>'s own directory's <c>basis</c> subdirectory — refused by name
    /// otherwise — so two differently-rooted chains can never flatten onto the same pushed blob name by
    /// coincidence. When a chain has more than one link, the ROOT's own returned bytes carry its <c>basis</c> member
    /// rewritten from the local, directory-crossing spelling (<c>"basis/&lt;name&gt;"</c>) to the bare canonical
    /// name a flat cloud namespace addresses it by — every deeper link's own authored <c>basis</c> member (if any)
    /// is already a bare sibling spelling and needs no rewrite. The rewrite re-serializes the root through
    /// <c>ToJsonString()</c>, so a pushed root's bytes are not its authored file's own bytes, and its
    /// content-address pin (<see cref="ComputeChainContentHash"/>) differs from the local chain's.</summary>
    /// <param name="path">The file to walk.</param>
    /// <param name="chain">Each chain link's own file NAME (<see cref="Path.GetFileName(string?)"/>, not the full
    /// path) paired with its raw bytes, derived document first.</param>
    /// <param name="reason">The one-line refusal reason (unreadable, cycle, depth, an ancestor outside the basis
    /// subdirectory), or empty on success.</param>
    /// <returns><see langword="true"/> when the chain resolved.</returns>
    public static bool TryResolveChainFiles(string path, out IReadOnlyList<(string Name, byte[] Bytes)> chain, out string reason) {
        chain = [];

        if (!File.Exists(path: path)) {
            reason = $"no file at {path}";

            return false;
        }

        byte[] bytes;

        try {
            bytes = File.ReadAllBytes(path: path);
        } catch (Exception exception) when ((exception is IOException or UnauthorizedAccessException)) {
            reason = $"cannot read {path}: {exception.Message.ReplaceLineEndings(replacementText: " ")}";

            return false;
        }

        var rootResolvedName = Path.GetFullPath(path: path);

        if (!TryWalkChain(
            source: new DirectoryDocumentSource(),
            rootResolvedName: rootResolvedName,
            rootBytes: bytes,
            chain: out var links,
            reason: out reason
        )) {
            return false;
        }

        if (links.Count > 1) {
            var basisDirectory = Path.Combine(
                path1: (Path.GetDirectoryName(path: rootResolvedName) ?? "."),
                path2: "basis"
            );

            for (var index = 1; (index < links.Count); index++) {
                var linkDirectory = Path.GetDirectoryName(path: links[index].ResolvedName);

                if (!string.Equals(
                    a: linkDirectory,
                    b: basisDirectory,
                    comparisonType: StringComparison.OrdinalIgnoreCase
                )) {
                    reason = $"'{links[index].ResolvedName}' does not live directly under '{basisDirectory}' — a pushed chain link must sit in the owned world's basis directory so its cloud key can never collide with another chain's link";

                    return false;
                }
            }

            // The root's own authored `basis` spelling crosses from the owned-worlds directory into its `basis/`
            // subdirectory (a LOCAL-only spelling, meaningless in the cloud's flat namespace) — rewritten to the
            // deeper link's own bare file name before push. Every deeper link already names its own basis, if any,
            // as a bare sibling (every link lives in the SAME `basis/` directory per the check above), so only the
            // root needs rewriting.
            var rootObject = links[0].Parsed!;

            rootObject[propertyName: WorldDocumentBasis.BasisMemberName] = Path.GetFileName(path: links[1].ResolvedName);
            links[0] = (links[0].ResolvedName, Encoding.UTF8.GetBytes(s: rootObject.ToJsonString()), rootObject);
        }

        chain = [.. links.Select(selector: static link => (Path.GetFileName(path: link.ResolvedName), link.Bytes))];

        return true;
    }
}
