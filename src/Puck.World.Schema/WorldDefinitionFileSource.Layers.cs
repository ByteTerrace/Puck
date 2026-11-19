using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Puck.Abstractions.Machines;
using Puck.Assets;

namespace Puck.World;

// A composite document's composition held by layer, so a later composition of the same document recomposes only what a
// changed layer writes. Composition is member-wise at the document's root: each root member of the composed document is
// merged from that member of the basis, of each import and of the file's own body, and from nothing else (MergeObject and
// MergeSiblingObject walk the overlay's members one at a time, and the shades rule and every collision path are rooted at
// the member). So when a recomposition finds every layer it shaped afresh still writing the same root members in the same
// order with the same nullness, the composed document keeps its member order and every member no changed layer writes
// differently, and only the members whose bytes moved in some layer merge again. Anything else (a layer that writes a new
// root member or stops writing one, a root member spelled with '$', a layer without a held image) merges whole, from the
// layers' shaped trees, through the same merge as a first composition.
public static partial class WorldDefinitionFileSource {
    // One root member of a compact JSON object: its name, where its key's spelling starts, and where its value lies in
    // the object's bytes. The key runs from KeyStart to Start, colon included, exactly as the object spelled it.
    internal readonly record struct Member(string Name, int KeyStart, int Start, int Length) {
        public bool IsNull(byte[] json) => json.AsSpan(start: Start, length: Length).SequenceEqual(other: "null"u8);
    }

    // One layer of a held composition as it composed: the document it was read from and the image its composition was
    // served or held as (null for the file's own body), the shaped tree's compact JSON and root members, its walk with
    // the sites dropped (null when its composition checked no exports), its exports (an import's) and how refusals name
    // it.
    internal sealed record ComposedLayer(
        string Path,
        WorldComposedDocument? Image,
        byte[] Json,
        Member[] Members,
        WorldModuleExports.Layer? Walk,
        WorldExports? Exports,
        string Description
    ) {
        // Whether `other` writes the same root members as this layer, in the same order, each null exactly where this
        // layer's is.
        public bool WritesSameMembers(ComposedLayer other) {
            if (Members.Length != other.Members.Length) { return false; }
            for (var index = 0; (index < Members.Length); index++) {
                if (!string.Equals(a: Members[index].Name, b: other.Members[index].Name, comparisonType: StringComparison.Ordinal) ||
                    (Members[index].IsNull(json: Json) != other.Members[index].IsNull(json: other.Json))) {
                    return false;
                }
            }
            return true;
        }
        public ReadOnlySpan<byte> Value(int member) => Json.AsSpan(start: Members[member].Start, length: Members[member].Length);
        public int IndexOf(string name) => Array.FindIndex(array: Members, match: member => string.Equals(a: member.Name, b: name, comparisonType: StringComparison.Ordinal));
        public JsonObject Tree() => ((JsonObject)JsonNode.Parse(utf8Json: Json)!);
    }

    // A composite document's held composition: the root bytes it was composed from, the basis name, whether they author
    // an imports member and the entries it holds, each layer, and the composed document's root members (ranges in the
    // image's ComposedJson).
    internal sealed record ComposedLayers(
        byte[] OwnBytes,
        string? BasisName,
        bool HasImports,
        IReadOnlyList<(string Document, string? Alias)> Imports,
        ComposedLayer? Basis,
        ComposedLayer[] ImportLayers,
        ComposedLayer Own,
        Member[] Composed
    );

    // The root members of a compact JSON object, in order. Null when the bytes are not one JSON object.
    private static Member[]? MembersOf(byte[] json) {
        var reader = new Utf8JsonReader(jsonData: json);
        var members = new List<Member>();

        try {
            if (!reader.Read() || (reader.TokenType != JsonTokenType.StartObject)) { return null; }
            while (reader.Read() && (reader.TokenType == JsonTokenType.PropertyName)) {
                var name = reader.GetString()!;
                var keyStart = ((int)reader.TokenStartIndex);

                _ = reader.Read();

                var start = ((int)reader.TokenStartIndex);

                reader.Skip();
                members.Add(item: new Member(KeyStart: keyStart, Length: (((int)reader.BytesConsumed) - start), Name: name, Start: start));
            }
        } catch (JsonException) {
            return null;
        }

        return [.. members];
    }
    // A shaped tree held as a layer: its compact JSON, which is what JsonObject.ToJsonString writes, and its members.
    private static ComposedLayer HoldLayer(JsonObject tree, string path, WorldComposedDocument? image, WorldModuleExports.Layer? walk, WorldExports? exports, string description) {
        var json = Encoding.UTF8.GetBytes(s: tree.ToJsonString());

        return new ComposedLayer(
            Description: description,
            Exports: exports,
            Image: image,
            Json: json,
            Members: (MembersOf(json: json) ?? []),
            Path: path,
            Walk: walk?.Detached()
        );
    }
    // Whether any layer writes a root member spelled with '$', which the root merge reads as composition vocabulary
    // rather than a member ($replace, $drop, $before): such a composition always merges whole.
    private static bool SpellsVocabulary(ComposedLayer layer) => layer.Members.Any(predicate: static member => member.Name.StartsWith(value: '$'));

    // The composed document `previous` held (`composedJson`), with each member a layer shaped afresh writes differently
    // merged again from every layer that writes it. `basis`, `imports` and `own` are this composition's layers; `held`
    // holds, by slot (the basis, each import, then the own body), the layer `previous` held there, and a slot whose layer
    // is that same instance was not shaped afresh. Null when a member refuses to merge, which the whole merge then
    // refuses by name. `merged` counts the members merged.
    private static byte[]? RecomposeMembers(byte[] composedJson, ComposedLayers previous, ComposedLayer? basis, IReadOnlyList<ComposedLayer> imports, ComposedLayer own, IReadOnlyList<ComposedLayer?> held, out int merged) {
        merged = 0;

        var moved = new SortedSet<string>(comparer: StringComparer.Ordinal);
        var slots = new List<ComposedLayer?>(capacity: (imports.Count + 2)) { basis };

        slots.AddRange(collection: imports);
        slots.Add(item: own);
        for (var slot = 0; (slot < slots.Count); slot++) {
            if ((slots[slot] is not { } layer) || ReferenceEquals(objA: layer, objB: held[slot])) { continue; }
            for (var member = 0; (member < layer.Members.Length); member++) {
                if (!layer.Value(member: member).SequenceEqual(other: held[slot]!.Value(member: member))) { _ = moved.Add(item: layer.Members[member].Name); }
            }
        }

        var values = new Dictionary<string, byte[]>(comparer: StringComparer.Ordinal);
        var composed = new HashSet<string>(collection: previous.Composed.Select(selector: static member => member.Name), comparer: StringComparer.Ordinal);

        // A member the held composition lacks was removed by a null that every layer still writes where it did, so it
        // stays removed.
        foreach (var name in moved.Where(predicate: composed.Contains)) {
            JsonObject Part(ComposedLayer layer) {
                var index = layer.IndexOf(name: name);
                var part = new JsonObject();

                if (index >= 0) { part[name] = JsonNode.Parse(utf8Json: layer.Value(member: index)); }
                return part;
            }

            var target = ((basis is null) ? new JsonObject() : Part(layer: basis));
            var restated = Part(layer: own);

            if (!WorldDocumentBasis.TryMergeImports(
                    composed: out var importsLayer,
                    imports: [.. imports.Where(predicate: layer => (layer.IndexOf(name: name) >= 0)).Select(selector: layer => (layer.Description, Part(layer: layer)))],
                    reason: out _,
                    restated: restated
                ) ||
                !WorldDocumentBasis.TryMergeInto(overlay: importsLayer!, reason: out _, target: target) ||
                !WorldDocumentBasis.TryMergeInto(overlay: restated, reason: out _, target: target) ||
                !target.TryGetPropertyValue(jsonNode: out var value, propertyName: name)) {
                return null;
            }

            values[name] = Encoding.UTF8.GetBytes(s: (value?.ToJsonString() ?? "null"));
            merged++;
        }

        // The members keep the order the held composition wrote them in, and every member no layer moved keeps its
        // held bytes.
        using var buffer = new MemoryStream(capacity: composedJson.Length);

        buffer.WriteByte(value: (byte)'{');
        for (var index = 0; (index < previous.Composed.Length); index++) {
            var member = previous.Composed[index];

            if (index > 0) { buffer.WriteByte(value: (byte)','); }
            buffer.Write(buffer: composedJson.AsSpan(start: member.KeyStart, length: (member.Start - member.KeyStart)));
            buffer.Write(buffer: (values.TryGetValue(key: member.Name, value: out var remerged) ? remerged : composedJson.AsSpan(start: member.Start, length: member.Length)));
        }
        buffer.WriteByte(value: (byte)'}');

        return buffer.ToArray();
    }

    // One slot of a composition being composed: the layer held for it (the held layer itself when it was reused, a new
    // one when it was shaped afresh and is held), the shaped tree when it was shaped afresh, and its live walk.
    private sealed class Slot(string path, string description, WorldComposedDocument? image) {
        public string Path { get; } = path;
        public string Description { get; } = description;
        public WorldComposedDocument? Image { get; } = image;
        public ComposedLayer? Reused { get; init; }
        public JsonObject? Tree { get; set; }
        public WorldExports? Exports { get; init; }
        public WorldModuleExports.Layer? Walk { get; set; }
        public ComposedLayer? Held { get; set; }

        // The tree as it composes: the one shaped afresh, or the reused layer's, parsed from its bytes.
        public JsonObject ShapedTree() => (Tree ??= Reused!.Tree());
        // The walk, from the reused layer when it holds one (its sites dropped), else walked from the tree.
        public WorldModuleExports.Layer WalkOf(IMachineValidationCatalog? catalog, bool live) {
            if ((Walk is null) && !live && (Reused?.Walk is { } held)) { return held; }
            return (Walk ??= WorldModuleExports.Walk(catalog: catalog, name: Description, tree: ShapedTree()));
        }
    }

    // Whether the layer `held` composed from is the one a slot reads now: the same document, composed to the same image
    // object, which holds the same bytes it held then, so shaping it again gives the same tree.
    private static bool Reuses(ComposedLayer? held, WorldComposedDocument? image, string path) =>
        ((held is not null) && (image is not null) && ReferenceEquals(objA: held.Image, objB: image) && string.Equals(a: held.Path, b: path, comparisonType: StringComparison.Ordinal));

    // The composition of a document no held image answered (TryComposeLayersCore has already applied the walk rules): a
    // flat file as it stands, or its basis, its imports and its own body, each read and composed through this same
    // composer, shaped (relocated, aliased, exports taken), checked against the modules' exports and merged in that
    // order. A composition held by layer reuses every layer whose document composed to the image it composed from, and
    // merges again only the root members a layer shaped afresh writes differently.
    private static bool TryComposeBody(IWorldDocumentSource source, string resolvedPath, byte[] bytes, IReadOnlyList<string> ancestors, bool serveHeldImage, bool parseHeld, bool retainImage, CompileInputLog reads, List<byte[]> touched, List<string> touchedPaths, ref int reach, out JsonObject? stack, out JsonObject? composed, out WorldComposedDocument? image, out string reason, string catalogFingerprint, IMachineValidationCatalog? catalog, WorldDocumentOrigins? origins) {
        stack = null;
        composed = null;
        image = null;

        // Layers are held only for a composition that carries no provenance and yields no pre-own-body stack, over a
        // source whose images are held: the image this composition replaces holds the layers it composed from.
        var byLayer = (retainImage && serveHeldImage && (origins is null) && source.ResolvesFiles);
        var previous = ((byLayer && ComposedDocuments.TryGetValue(key: CacheKey(fingerprint: catalogFingerprint, path: resolvedPath, source: source), value: out var stale)) ? stale : null);
        var held = previous?.Layers;
        var recordsInputs = (source.ResolvesFiles && source.RecordInputs(content: bytes, resolvedName: resolvedPath));
        var sameRoot = ((held is not null) && held.OwnBytes.AsSpan().SequenceEqual(other: bytes));
        JsonObject? ownBody = null;
        JsonNode? importsNode = null;
        string? basisName;
        bool hasImports;

        if (sameRoot) {
            basisName = held!.BasisName;
            hasImports = held.HasImports;
        } else {
            JsonObject? root;

            try {
                root = (JsonNode.Parse(json: DecodeJson(bytes: bytes)) as JsonObject);
            } catch (JsonException) {
                root = null;
            }

            if (root is null) {
                reason = $"{WorldDocumentLabel.Of(path: resolvedPath)} does not hold a JSON object.";

                return false;
            }

            origins?.Capture(node: root, documentPath: resolvedPath);

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
                composed = root;
                reason = string.Empty;
                WorldBootWork.Count(kind: WorldBootWork.Compositions);
                if (retainImage) {
                    image = HoldComposedImage(
                        catalogFingerprint: catalogFingerprint,
                        composed: composed,
                        composedJson: Encoding.UTF8.GetBytes(s: composed.ToJsonString()),
                        inputs: (recordsInputs ? reads.Inputs : null),
                        layers: null,
                        origins: origins,
                        reach: reach,
                        resolvedPath: resolvedPath,
                        source: source,
                        touched: touched,
                        touchedPaths: touchedPaths
                    );
                }

                return true;
            }

            basisName = null;
            if (root.TryGetPropertyValue(jsonNode: out var basisNode, propertyName: WorldDocumentBasis.BasisMemberName)) {
                if ((basisNode is not JsonValue basisValue) || !basisValue.TryGetValue<string>(value: out basisName) || (basisName.Length == 0)) {
                    reason = $"'{WorldDocumentBasis.BasisMemberName}' in {WorldDocumentLabel.Of(path: resolvedPath)} must be a non-empty document name.";

                    return false;
                }
            }

            hasImports = root.TryGetPropertyValue(jsonNode: out importsNode, propertyName: WorldDocumentBasis.ImportsMemberName);
            ownBody = root;
            ownBody.Remove(propertyName: WorldDocumentBasis.BasisMemberName);
            ownBody.Remove(propertyName: WorldDocumentBasis.ImportsMemberName);
        }

        var nextAncestors = ((IReadOnlyList<string>)[.. ancestors, resolvedPath]);
        Slot? basis = null;

        if (basisName is not null) {
            if (!TryReadDocument(
                content: out var basisContent,
                name: basisName,
                reason: out reason,
                referrerName: resolvedPath,
                resolvedName: out var basisResolvedName,
                source: source
            ) || !TryComposeLayersHeld(
                ancestors: nextAncestors,
                bytes: basisContent!,
                catalog: catalog,
                catalogFingerprint: catalogFingerprint,
                composed: out var basisResult,
                image: out var basisImage,
                origins: origins,
                parseHeld: !byLayer,
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

            basis = new Slot(description: $"the basis of {resolvedPath}", image: basisImage, path: basisResolvedName) {
                Reused = (Reuses(held: held?.Basis, image: basisImage, path: basisResolvedName) ? held!.Basis : null),
            };
            if (basis.Reused is null) {
                var basisTree = (basisResult ?? ((JsonObject)JsonNode.Parse(utf8Json: basisImage!.ComposedJson)!));

                WorldDocumentPaths.RelocateDocumentFields(
                    module: basisTree,
                    origins: origins,
                    sourceDocumentPath: basisResolvedName,
                    targetDocumentPath: resolvedPath
                );
                if (
                    (catalog is not null) &&
                    !WorldModuleNamespace.TryRelocateConfigurationAssets(
                    catalog: catalog,
                    module: basisTree,
                    reason: out var basisMetadataReason,
                    sourceDocumentPath: basisResolvedName,
                    targetDocumentPath: resolvedPath
                )
                ) {
                    reason = $"{WorldDocumentLabel.Of(path: resolvedPath)} basis {WorldDocumentLabel.Of(path: basisResolvedName)}: {basisMetadataReason}";
                    return false;
                }
                basis.Tree = basisTree;
            }
            reach = Math.Max(
                val1: reach,
                val2: (basisReach + 1)
            );
            touched.AddRange(collection: basisTouched);
            touchedPaths.AddRange(collection: basisTouchedPaths);
        }

        var imports = new List<Slot>();
        var entries = new List<(string Document, string? Alias)>();

        if (hasImports) {
            IReadOnlyList<(string Document, string? Alias)> authored;

            if (sameRoot) {
                authored = held!.Imports;
            } else {
                if (importsNode is not JsonArray importsArray) {
                    reason = $"'{WorldDocumentBasis.ImportsMemberName}' in {WorldDocumentLabel.Of(path: resolvedPath)} must be an array of import entries.";

                    return false;
                }

                var read = new List<(string Document, string? Alias)>(capacity: importsArray.Count);

                foreach (var entry in importsArray) {
                    if (!TryReadImportEntry(
                        alias: out var entryAlias,
                        document: out var entryName,
                        entry: entry,
                        reason: out reason,
                        referrerPath: resolvedPath
                    )) {
                        return false;
                    }
                    read.Add(item: (entryName, entryAlias));
                }
                authored = read;
            }

            var importDescriptions = new HashSet<string>(comparer: StringComparer.Ordinal);

            for (var index = 0; (index < authored.Count); index++) {
                var (importName, importAlias) = authored[index];

                if (!TryReadDocument(
                    content: out var importContent,
                    name: importName,
                    reason: out reason,
                    referrerName: resolvedPath,
                    resolvedName: out var importResolvedName,
                    source: source
                ) || !TryComposeLayersHeld(
                    ancestors: nextAncestors,
                    bytes: importContent!,
                    catalog: catalog,
                    catalogFingerprint: catalogFingerprint,
                    composed: out var importResult,
                    image: out var importImage,
                    origins: origins,
                    parseHeld: !byLayer,
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

                var importDescription = DescribeImport(
                    alias: importAlias,
                    importName: importName,
                    resolvedName: importResolvedName,
                    taken: importDescriptions
                );
                var heldImport = (((held is not null) && (index < held.ImportLayers.Length) && (held.Imports[index] == (importName, importAlias)) &&
                    Reuses(held: held.ImportLayers[index], image: importImage, path: importResolvedName) &&
                    string.Equals(a: held.ImportLayers[index].Description, b: importDescription, comparisonType: StringComparison.Ordinal))
                    ? held.ImportLayers[index]
                    : null);

                if (heldImport is not null) {
                    imports.Add(item: new Slot(description: importDescription, image: importImage, path: importResolvedName) { Exports = heldImport.Exports, Reused = heldImport });
                } else {
                    var importTree = (importResult ?? ((JsonObject)JsonNode.Parse(utf8Json: importImage!.ComposedJson)!));

                    WorldDocumentPaths.RelocateDocumentFields(
                        module: importTree,
                        origins: origins,
                        sourceDocumentPath: importResolvedName,
                        targetDocumentPath: resolvedPath
                    );

                    if (
                        (importAlias is not null) &&
                        !((catalog is not null)
                        ? WorldModuleNamespace.TryApply(
                            alias: importAlias,
                            catalog: catalog,
                            module: importTree,
                            reason: out var aliasReason,
                            sourceDocumentPath: importResolvedName,
                            targetDocumentPath: resolvedPath
                        )
                        : WorldModuleNamespace.TryApply(
                            alias: importAlias,
                            module: importTree,
                            reason: out aliasReason
                        ))
                    ) {
                        reason = $"{WorldDocumentLabel.Of(path: resolvedPath)} imports {WorldDocumentLabel.Of(path: importResolvedName)} as '{importAlias}': {aliasReason}";

                        return false;
                    }

                    if (
                        (catalog is not null) &&
                        (importAlias is null) &&
                        !WorldModuleNamespace.TryRelocateConfigurationAssets(
                        catalog: catalog,
                        module: importTree,
                        reason: out var importMetadataReason,
                        sourceDocumentPath: importResolvedName,
                        targetDocumentPath: resolvedPath
                    )
                    ) {
                        reason = $"{WorldDocumentLabel.Of(path: resolvedPath)} imports {WorldDocumentLabel.Of(path: importResolvedName)}: {importMetadataReason}";
                        return false;
                    }

                    if (!WorldModuleExports.TryTake(
                        exports: out var exports,
                        module: importTree,
                        moduleName: importDescription,
                        reason: out var exportsReason
                    )) {
                        reason = $"{WorldDocumentLabel.Of(path: resolvedPath)} imports {exportsReason}";

                        return false;
                    }

                    imports.Add(item: new Slot(description: importDescription, image: importImage, path: importResolvedName) { Exports = exports, Tree = importTree });
                }
                entries.Add(item: (importName, importAlias));
                touched.AddRange(collection: importTouched);
                touchedPaths.AddRange(collection: importTouchedPaths);
            }
        }

        var own = new Slot(description: resolvedPath, image: null, path: resolvedPath) {
            Reused = (sameRoot ? held!.Own : null),
            Tree = ownBody,
        };

        if (hasImports) {
            bool Check(bool live, out string refusal) => WorldModuleExports.TryCheckWalks(
                basis: ((basis is null) ? null : (((basis.Tree?.Count ?? basis.Reused!.Members.Length) > 0) ? basis.WalkOf(catalog: catalog, live: live) : null)),
                hostPath: resolvedPath,
                imports: [.. imports.Select(selector: slot => (slot.Description, slot.WalkOf(catalog: catalog, live: live), slot.Exports!))],
                own: own.WalkOf(catalog: catalog, live: live),
                reason: out refusal,
                surfaces: out _
            );

            // A held walk names no site, so a refusal it reaches is checked again from the layers' trees, which name it.
            if (!Check(live: false, refusal: out reason)) {
                _ = Check(live: true, refusal: out reason);

                return false;
            }
        }

        // Each layer shaped afresh is held before the merge, which merges into the basis's tree.
        ComposedLayer HeldOf(Slot slot) => (slot.Held ??= (slot.Reused ?? HoldLayer(
            description: slot.Description,
            exports: slot.Exports,
            image: slot.Image,
            path: slot.Path,
            tree: slot.Tree!,
            walk: slot.Walk
        )));

        if (byLayer) {
            if (basis is not null) { _ = HeldOf(slot: basis); }
            foreach (var slot in imports) { _ = HeldOf(slot: slot); }
            _ = HeldOf(slot: own);
        }

        byte[]? composedJson = null;
        int merged;

        // Only the members a layer shaped afresh writes differently merge again, while every such layer writes the same
        // root members it wrote, each null where it was, and nothing in the composition is spelled as root vocabulary.
        if (
            byLayer && (held is not null) && (held.HasImports == hasImports) && ((basis is null) == (held.Basis is null)) &&
            (imports.Count == held.ImportLayers.Length) &&
            ((basis is null) || basis.Held!.WritesSameMembers(other: held.Basis!)) &&
            imports.Select(selector: static (slot, index) => (slot, index)).All(predicate: pair => pair.slot.Held!.WritesSameMembers(other: held.ImportLayers[pair.index])) &&
            own.Held!.WritesSameMembers(other: held.Own) &&
            !imports.Any(predicate: static slot => SpellsVocabulary(layer: slot.Held!)) && !SpellsVocabulary(layer: own.Held!) &&
            ((basis is null) || !SpellsVocabulary(layer: basis.Held!))
        ) {
            composedJson = RecomposeMembers(
                basis: basis?.Held,
                composedJson: previous!.ComposedJson,
                held: [held.Basis, .. held.ImportLayers, held.Own],
                imports: [.. imports.Select(selector: static slot => slot.Held!)],
                merged: out merged,
                own: own.Held!,
                previous: held
            );
        } else {
            merged = 0;
        }

        if (composedJson is null) {
            var basisComposed = (basis?.ShapedTree() ?? new JsonObject());
            var body = own.ShapedTree();
            var importsLayer = new JsonObject();

            if (hasImports) {
                if (!WorldDocumentBasis.TryMergeImports(
                    composed: out var mergedImports,
                    imports: [.. imports.Select(selector: static slot => (slot.Description, slot.ShapedTree()))],
                    origins: origins,
                    reason: out var mergeReason,
                    restated: body
                )) {
                    reason = $"{WorldDocumentLabel.Of(path: resolvedPath)}: {mergeReason}";

                    return false;
                }

                importsLayer = mergedImports!;
            }

            if (!WorldDocumentBasis.TryMergeInto(
                target: basisComposed,
                origins: origins,
                overlay: importsLayer,
                reason: out var stackReason
            )) {
                reason = $"{WorldDocumentLabel.Of(path: resolvedPath)}: {stackReason}";

                return false;
            }

            // Only the derivation-preserving save requests the pre-own-body stack (serveHeldImage=false).
            // Every other reader consumes the same owned accumulator, keeping inherited subtrees in place.
            stack = serveHeldImage ? null : (JsonObject)WorldDocumentOrigins.Clone(basisComposed, origins)!;

            if (!WorldDocumentBasis.TryMergeInto(
                target: basisComposed,
                origins: origins,
                overlay: body,
                reason: out var finalReason
            )) {
                reason = $"{WorldDocumentLabel.Of(path: resolvedPath)}: {finalReason}";

                return false;
            }

            composed = basisComposed;
            merged = composed.Count;
        }

        reason = string.Empty;
        WorldBootWork.Count(kind: WorldBootWork.Compositions);
        WorldBootWork.Count(amount: merged, kind: WorldBootWork.CompositionMembers);
        if (retainImage) {
            composedJson ??= Encoding.UTF8.GetBytes(s: composed!.ToJsonString());
            image = HoldComposedImage(
                catalogFingerprint: catalogFingerprint,
                composed: composed,
                composedJson: composedJson,
                inputs: (recordsInputs ? reads.Inputs : null),
                layers: (byLayer
                    ? new ComposedLayers(
                        Basis: basis?.Held,
                        BasisName: basisName,
                        Composed: (MembersOf(json: composedJson) ?? []),
                        HasImports: hasImports,
                        ImportLayers: [.. imports.Select(selector: static slot => slot.Held!)],
                        Imports: entries,
                        Own: own.Held!,
                        OwnBytes: bytes
                    )
                    : null),
                origins: origins,
                reach: reach,
                resolvedPath: resolvedPath,
                source: source,
                touched: touched,
                touchedPaths: touchedPaths
            );
        }
        if ((composed is null) && parseHeld) {
            composed = ((JsonObject)JsonNode.Parse(utf8Json: composedJson!)!);
        }

        return true;
    }
}
