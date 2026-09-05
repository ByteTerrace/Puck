using System.Buffers.Binary;
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
    /// (<see cref="WorldDefinitionValidator.TryValidateLocally"/>).</param>
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
    private static bool TryComposeLayers(IWorldDocumentSource source, string resolvedPath, byte[] bytes, IReadOnlyList<string> ancestors, out JsonObject? stack, out JsonObject? composed, out List<byte[]> touched, out string reason) {
        stack = null;
        composed = null;
        touched = [bytes];

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
                reason: out reason,
                resolvedPath: basisResolvedName,
                source: source,
                stack: out _,
                touched: out var basisTouched
            )) {
                return false;
            }

            basisComposed = basisResult!;
            touched.AddRange(collection: basisTouched);
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
                reason = $"'{WorldDocumentBasis.ImportsMemberName}' in {resolvedPath} must be an array of file path strings.";

                return false;
            }

            var importTrees = new List<(string Name, JsonObject Tree)>();

            foreach (var entry in importsArray) {
                if (
                    (entry is not JsonValue entryValue) ||
                    !entryValue.TryGetValue<string>(value: out var importName) ||
                    (importName.Length == 0)
                ) {
                    reason = $"'{WorldDocumentBasis.ImportsMemberName}' in {resolvedPath} must hold only non-empty file path strings.";

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
                    reason: out reason,
                    resolvedPath: importResolvedName,
                    source: source,
                    stack: out _,
                    touched: out var importTouched
                )) {
                    return false;
                }

                importTrees.Add(item: (importResolvedName, importResult!));
                touched.AddRange(collection: importTouched);
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
            reason: out reason,
            resolvedPath: rootResolvedName,
            source: source,
            stack: out _,
            touched: out var touched
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
                reason: out reason,
                resolvedPath: Path.GetFullPath(path: path),
                source: new DirectoryDocumentSource(),
                stack: out var result,
                touched: out _
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

            if (!TryComposeLayers(
                ancestors: [],
                bytes: bytes,
                composed: out var composed,
                reason: out reason,
                resolvedPath: Path.GetFullPath(path: path),
                source: new DirectoryDocumentSource(),
                stack: out _,
                touched: out _
            )) {
                return false;
            }

            tree = composed;
            reason = string.Empty;

            return true;
        } catch (Exception exception) when ((exception is IOException or UnauthorizedAccessException or JsonException or ArgumentException or NotSupportedException)) {
            tree = null;
            reason = $"cannot compose {path}: {exception.Message.ReplaceLineEndings(replacementText: " ")}";

            return false;
        }
    }
    // Shares TryComposeLayers' cycle/depth walk and basis-then-imports-then-own-body recursion order, but collects
    // a (path, own top-level keys) entry per file instead of merging JSON — the read-back `world.imports` prints.
    // Each entry's keys are exactly what that file's own JSON declares at the root (basis/imports members excluded),
    // in MERGE order: a later entry's same key overrides an earlier one's.
    private static bool TryDescribeLayers(IWorldDocumentSource source, string resolvedPath, byte[] bytes, IReadOnlyList<string> ancestors, List<(string Path, IReadOnlyList<string> Keys)> layers, out string reason) {
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
                reason = $"'{WorldDocumentBasis.ImportsMemberName}' in {resolvedPath} must be an array of file path strings.";

                return false;
            }

            foreach (var entry in importsArray) {
                if (
                    (entry is not JsonValue entryValue) ||
                    !entryValue.TryGetValue<string>(value: out var importName) ||
                    (importName.Length == 0)
                ) {
                    reason = $"'{WorldDocumentBasis.ImportsMemberName}' in {resolvedPath} must hold only non-empty file path strings.";

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
                !string.Equals(a: name, b: WorldDocumentBasis.ImportsMemberName, comparisonType: StringComparison.Ordinal)
            ))
            .ToArray();

        layers.Add(item: (resolvedPath, ownKeys));
        reason = string.Empty;

        return true;
    }
    /// <summary>Describes <paramref name="path"/>'s whole basis-and-imports graph in merge order — the read-back
    /// <c>world.imports</c> prints: each resolved file path paired with the top-level keys ITS OWN JSON declares
    /// (basis/imports members excluded), from the deepest basis ancestor through every import to the file's own
    /// body last. A later entry's same key overrides an earlier one's (see <see cref="WorldDocumentBasis"/>'s
    /// remarks).</summary>
    /// <param name="path">The document file to describe.</param>
    /// <param name="layers">Each layer in merge order on success; empty on failure.</param>
    /// <param name="reason">The one-line failure reason, or empty on success.</param>
    /// <returns><see langword="true"/> when the file was readable and its graph resolved.</returns>
    public static bool TryDescribeComposition(string path, out IReadOnlyList<(string Path, IReadOnlyList<string> Keys)> layers, out string reason) {
        var collected = new List<(string Path, IReadOnlyList<string> Keys)>();

        try {
            var bytes = File.ReadAllBytes(path: path);

            if (!TryDescribeLayers(
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
    /// <param name="imports">The resolved absolute import paths in authored order, or an empty list when the
    /// document declares none.</param>
    /// <param name="reason">The one-line failure reason, or empty on success.</param>
    /// <returns><see langword="true"/> when the file was readable and its root answered; a missing <c>imports</c>
    /// member is a success with an empty <paramref name="imports"/>.</returns>
    public static bool TryPeekImports(string path, out IReadOnlyList<string> imports, out string reason) {
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
                reason = $"'{WorldDocumentBasis.ImportsMemberName}' in {path} must be an array of file path strings.";

                return false;
            }

            var resolved = new List<string>(capacity: importsArray.Count);

            foreach (var entry in importsArray) {
                if (!TryResolveBasisPath(
                    basisNode: entry,
                    basisPath: out var resolvedEntry,
                    reason: out reason,
                    referrerPath: path
                )) {
                    return false;
                }

                resolved.Add(item: resolvedEntry!);
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
