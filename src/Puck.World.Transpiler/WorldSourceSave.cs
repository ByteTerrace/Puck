using System.Text;
using System.Text.Json.Nodes;
using Puck.Abstractions;
using Puck.Abstractions.Machines;
using Puck.World.Transpiler.Composition;
using Puck.World.Transpiler.Decompiler;

namespace Puck.World.Transpiler;

/// <summary>Writes a live snapshot back into the sources that author it, after proving the edited sources compose to
/// the snapshot.</summary>
/// <remarks>Each changed value is written in the document that authored it, which the composition's scalar origins
/// (<see cref="WorldDocumentOrigins"/>) name: an import's row in the import, a basis's in the basis. A new row joins
/// the document that authors every other row of its list, or the saved source. Inside each document
/// <see cref="WorldSourceEdits"/> rewrites only the changed nodes, so every <c>let</c>, <c>template</c>, <c>for</c>
/// and comment outside them stays as written, and a node a generator writes refuses by name. A source holds the world
/// as it starts, so the <c>state</c> the simulation moves is not written into an existing source; a JSON save keeps
/// it. Nothing is written unless every edited source compiles to the document the save asked of it and the whole
/// tree composes to the snapshot.</remarks>
public static class WorldSourceSave {
    // One source of the tree: its text and its own lowered document, which the save edits.
    private sealed class Source {
        public required string Path { get; init; }
        public required string Text { get; init; }
        public required bool ByteOrderMark { get; init; }
        public required JsonObject Document { get; init; }
        public bool Edited { get; set; }
        public string Rewritten { get; set; } = string.Empty;
    }

    /// <summary>Saves a snapshot into the source tree rooted at <paramref name="path"/>, or into a new source there.</summary>
    /// <param name="definition">The live snapshot to save.</param>
    /// <param name="path">The root source file to update, or to create when it does not exist.</param>
    /// <param name="bytesWritten">The bytes written across every rewritten source, or zero on refusal.</param>
    /// <param name="reason">The refusal, or empty after a verified write.</param>
    /// <param name="catalogFingerprint">The machine metadata fingerprint used during composition.</param>
    /// <param name="catalog">The host's machine validation catalog.</param>
    /// <returns>Whether the sources were verified and written.</returns>
    public static bool TrySave(WorldDefinition definition, string path, out long bytesWritten, out string reason, string catalogFingerprint = "", IMachineValidationCatalog? catalog = null) =>
        TrySave(bytesWritten: out bytesWritten, catalog: catalog, catalogFingerprint: catalogFingerprint, definition: definition, path: path, reason: out reason, written: out _);
    /// <summary>Saves a snapshot into the source tree rooted at <paramref name="path"/>, or into a new source there,
    /// naming every source it rewrote.</summary>
    /// <param name="definition">The live snapshot to save.</param>
    /// <param name="path">The root source file to update, or to create when it does not exist.</param>
    /// <param name="bytesWritten">The bytes written across every rewritten source, or zero on refusal.</param>
    /// <param name="written">The rewritten sources, in path order; empty on refusal or when nothing changed.</param>
    /// <param name="reason">The refusal, or empty after a verified write.</param>
    /// <param name="catalogFingerprint">The machine metadata fingerprint used during composition.</param>
    /// <param name="catalog">The host's machine validation catalog.</param>
    /// <returns>Whether the sources were verified and written.</returns>
    public static bool TrySave(WorldDefinition definition, string path, out long bytesWritten, out IReadOnlyList<string> written, out string reason, string catalogFingerprint = "", IMachineValidationCatalog? catalog = null) {
        ArgumentNullException.ThrowIfNull(argument: definition);
        ArgumentException.ThrowIfNullOrWhiteSpace(argument: path);
        bytesWritten = 0;
        written = [];

        var target = JsonNode.Parse(utf8Json: WorldDefinitionSerialization.SerializeBeside(definition: definition, path: path))!.AsObject();

        if (!File.Exists(path: path)) {
            return TryCreate(bytesWritten: out bytesWritten, catalog: catalog, catalogFingerprint: catalogFingerprint, path: path, reason: out reason, target: target, written: out written);
        }

        var sources = new Dictionary<string, Source>(comparer: PuckPaths.Comparer);

        if (!TryLoad(path: path, reason: out reason, source: out var root)) {
            return false;
        }
        sources[root!.Path] = root;

        var origins = new WorldDocumentOrigins();

        if (!TryCompose(catalog: catalog, catalogFingerprint: catalogFingerprint, composed: out var before, origins: origins, reason: out reason, root: root, sources: sources) ||
            !TryCanonical(composed: before!, path: path, reason: out reason, canonical: out var canonical)) {
            reason = ("the source does not compose to one world: " + reason);
            return false;
        }

        KeepAuthoredSimulation(authored: canonical!, target: target);

        var delta = WorldDocumentBasis.Diff(basis: canonical!, target: target);

        if (!WorldDocumentBasis.TryMerge(basis: before!, composed: out var after, overlay: delta, reason: out reason, origins: origins)) {
            return false;
        }

        if (!new SourceTree(origins: origins, root: root, sources: sources).TryApply(now: after, old: before, pointer: string.Empty, reason: out reason)) {
            return false;
        }

        var edited = sources.Values.Where(predicate: static source => source.Edited).OrderBy(comparer: PuckPaths.Comparer, keySelector: static source => source.Path).ToArray();

        foreach (var source in edited) {
            if (!WorldSourceEdits.TryRewrite(reason: out reason, rewritten: out var rewritten, source: source.Text, sourcePath: source.Path, target: source.Document)) {
                reason = $"{WorldDocumentLabel.Of(path: source.Path)}: {reason}";
                return false;
            }
            source.Rewritten = rewritten;
        }
        if (!TryProve(catalog: catalog, catalogFingerprint: catalogFingerprint, path: path, reason: out reason, root: root, sources: sources, target: target)) {
            return false;
        }

        foreach (var source in edited) {
            bytesWritten += Write(bom: source.ByteOrderMark, path: source.Path, text: source.Rewritten);
        }
        written = [.. edited.Select(selector: static source => source.Path)];
        reason = string.Empty;
        return true;
    }

    // A source writes the world as it starts, so what the simulation writes keeps its authored value: the `state`
    // section, and the response mask (`holding`) the sweep writes on a placement row from that state.
    private static void KeepAuthoredSimulation(JsonObject authored, JsonObject target) {
        Keep(authored: authored, name: "state", target: target);
        if ((target["placements"]?["rows"] is not JsonArray rows) || (authored["placements"]?["rows"] is not JsonArray authoredRows)) {
            return;
        }

        var holding = authoredRows.OfType<JsonObject>().Where(predicate: static row => (row["id"] is JsonValue)).ToDictionary(
            comparer: StringComparer.Ordinal,
            elementSelector: static row => row,
            keySelector: static row => row["id"]!.GetValue<string>()
        );

        foreach (var row in rows.OfType<JsonObject>()) {
            if ((row["id"] is JsonValue id) && holding.TryGetValue(key: id.GetValue<string>(), value: out var authoredRow)) {
                Keep(authored: authoredRow, name: "holding", target: row);
            }
        }
    }
    private static void Keep(JsonObject authored, JsonObject target, string name) {
        if (authored[name] is { } value) {
            target[name] = value.DeepClone();
        } else {
            _ = target.Remove(propertyName: name);
        }
    }
    // A path with no source yet is printed whole from the snapshot.
    private static bool TryCreate(string path, JsonObject target, string catalogFingerprint, IMachineValidationCatalog? catalog, out long bytesWritten, out IReadOnlyList<string> written, out string reason) {
        bytesWritten = 0;
        written = [];

        string candidate;

        // The snapshot spells an absent member as null, which composes the same as no member and keeps a row from
        // printing as its construct.
        try { candidate = WorldDecompiler.Decompile(root: (JsonObject)WithoutNulls(node: target)!); } catch (WorldDecompileRefusedException exception) {
            reason = $"{exception.Message}; rename the generated row before saving source, or save a JSON delta";
            return false;
        }

        var compilation = WorldCompiler.Compile(source: candidate, sourcePath: path);

        if (!compilation.Success || (compilation.Json is null)) {
            reason = ("the printed source does not compile: " + compilation.Diagnostics.FormatReport());
            return false;
        }
        if (!WorldSourceLoader.TryReadAuthored(path: path, document: Encoding.UTF8.GetBytes(s: compilation.Json.ToJsonString()), authored: out var proof, reason: out reason, catalogFingerprint: catalogFingerprint, catalog: catalog, sourceCompilation: WorldCompiledSource.From(compilation: compilation))) {
            return false;
        }
        if (!JsonNode.DeepEquals(node1: JsonNode.Parse(utf8Json: WorldDefinitionSerialization.SerializeBeside(definition: proof, path: path)), node2: target)) {
            reason = "the printed source does not reproduce the live snapshot; nothing written";
            return false;
        }
        bytesWritten = Write(bom: false, path: path, text: candidate);
        written = [PuckPaths.Normalize(path: path)];
        reason = string.Empty;
        return true;
    }
    private static bool TryLoad(string path, out Source? source, out string reason) {
        source = null;

        var bytes = File.ReadAllBytes(path: path);
        var bom = bytes.AsSpan().StartsWith(value: Encoding.UTF8.Preamble);
        var text = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetString(bytes: bytes.AsSpan(start: (bom ? Encoding.UTF8.Preamble.Length : 0)));
        var compilation = WorldCompiler.Compile(source: text, sourcePath: path);

        if (!compilation.Success || (compilation.Json is null)) {
            reason = $"{WorldDocumentLabel.Of(path: path)} does not compile to one document: {compilation.Diagnostics.FormatReport()}";
            return false;
        }
        source = new Source { ByteOrderMark = bom, Document = (JsonObject)compilation.Json.DeepClone(), Path = PuckPaths.Normalize(path: path), Text = text };
        reason = string.Empty;
        return true;
    }
    // The tree the root composes to, with every retained scalar's origin. The sources the save has already edited are
    // read as edited.
    // Origins also make a root that names no basis or import compose to a tree of its own.
    private static bool TryCompose(Source root, Dictionary<string, Source> sources, string catalogFingerprint, IMachineValidationCatalog? catalog, WorldDocumentOrigins? origins, out JsonObject? composed, out string reason) =>
        WorldDefinitionFileSource.TryComposeChainWithImports(
            catalog: catalog,
            catalogFingerprint: catalogFingerprint,
            chainBytes: out _,
            composed: out composed,
            origins: (origins ?? new WorldDocumentOrigins()),
            reason: out reason,
            rootBytes: Encoding.UTF8.GetBytes(s: root.Document.ToJsonString()),
            rootResolvedName: root.Path,
            source: new EditedDocuments(sources: sources)
        );
    private static bool TryCanonical(JsonObject composed, string path, out JsonObject? canonical, out string reason) {
        canonical = null;

        if (!WorldDefinitionFileSource.TryParseDocument(definition: out var parsed, json: composed.ToJsonString(), reason: out reason, sourceName: path)) {
            return false;
        }
        canonical = JsonNode.Parse(utf8Json: WorldDefinitionSerialization.SerializeBeside(definition: (parsed! with { DocumentDirectory = WorldDocumentPaths.DirectoryOf(documentPath: path) }), path: path))!.AsObject();
        return true;
    }
    // The rewritten sources, composed as they will be read once written, reproduce the snapshot.
    private static bool TryProve(Source root, Dictionary<string, Source> sources, JsonObject target, string path, string catalogFingerprint, IMachineValidationCatalog? catalog, out string reason) {
        var rewritten = new Dictionary<string, Source>(comparer: PuckPaths.Comparer);

        foreach (var source in sources.Values) {
            if (!source.Edited) {
                continue;
            }

            var compilation = WorldCompiler.Compile(source: source.Rewritten, sourcePath: source.Path);

            rewritten[source.Path] = new Source { ByteOrderMark = source.ByteOrderMark, Document = compilation.Json!, Path = source.Path, Text = source.Rewritten };
        }

        var provedRoot = (rewritten.TryGetValue(key: root.Path, value: out var edited) ? edited : root);

        if (!TryCompose(catalog: catalog, catalogFingerprint: catalogFingerprint, composed: out var composed, origins: null, reason: out reason, root: provedRoot, sources: rewritten) ||
            !TryCanonical(canonical: out var canonical, composed: composed!, path: path, reason: out reason)) {
            reason = ("the rewritten sources do not compose: " + reason);
            return false;
        }
        if (!JsonNode.DeepEquals(node1: canonical, node2: target)) {
            reason = "the rewritten sources do not reproduce the live snapshot; nothing written";
            return false;
        }
        reason = string.Empty;
        return true;
    }
    private static JsonNode? WithoutNulls(JsonNode? node) => node switch {
        JsonObject obj => new JsonObject(properties: obj.Where(predicate: static member => (member.Value is not null)).Select(selector: static member => KeyValuePair.Create(key: member.Key, value: WithoutNulls(node: member.Value)))),
        JsonArray array => new JsonArray(items: [.. array.Select(selector: WithoutNulls)]),
        _ => node?.DeepClone(),
    };
    private static long Write(string path, string text, bool bom) {
        var bytes = Encoding.UTF8.GetBytes(s: text);

        if (bom) { bytes = [.. Encoding.UTF8.Preamble, .. bytes]; }
        Puck.Assets.AtomicFile.WriteAllBytes(path: path, bytes: bytes);
        return bytes.LongLength;
    }

    // Reads every document of the tree as the composition reads it, except a source the save holds a document for,
    // which is read as that document. It names files of its own, so no composition over it is held.
    private sealed class EditedDocuments(IReadOnlyDictionary<string, Source> sources) : IWorldDocumentSource {
        public bool TryRead(string name, string referrerName, out string resolvedName, out byte[]? content, out string reason) {
            if (!PuckDocumentComposer.Instance.TryRead(content: out content, name: name, reason: out reason, referrerName: referrerName, resolvedName: out resolvedName)) {
                return false;
            }
            if (sources.TryGetValue(key: PuckPaths.Normalize(path: resolvedName), value: out var source)) {
                content = Encoding.UTF8.GetBytes(s: source.Document.ToJsonString());
            }
            return true;
        }
    }

    // The documents a composed tree's changes belong in. Each change is made to the lowered document that authored
    // the node it changes, which is loaded the first time a change reaches it.
    private sealed class SourceTree(WorldDocumentOrigins origins, Source root, Dictionary<string, Source> sources) {
        // Carries every difference between `old`, the composed node at `pointer` before the save, and `now`, the
        // node the save asks for there, into the documents that author it.
        public bool TryApply(JsonNode? old, JsonNode? now, string pointer, out string reason) {
            reason = string.Empty;
            if (JsonNode.DeepEquals(node1: old, node2: now)) {
                return true;
            }
            if ((old is JsonObject left) && (now is JsonObject right)) {
                foreach (var name in left.Select(selector: static member => member.Key).Union(second: right.Select(selector: static member => member.Key), comparer: StringComparer.Ordinal).ToArray()) {
                    var child = ((pointer + "/") + WorldSourceEdits.Escape(value: name));

                    if (!right.ContainsKey(propertyName: name)) {
                        if (!TryRemove(node: left[name], pointer: child, reason: out reason)) { return false; }
                    } else if (!left.ContainsKey(propertyName: name)) {
                        if (!TryAdd(name: name, parent: left, pointer: pointer, reason: out reason, value: right[name])) { return false; }
                    } else if (!TryApply(now: right[name], old: left[name], pointer: child, reason: out reason)) {
                        return false;
                    }
                }
                return true;
            }
            if ((old is JsonArray rows) && (now is JsonArray next)) {
                if (WorldDocumentBasis.TryFindRowKey(ambiguity: out _, basis: rows, key: out var key, overlay: next)) {
                    return TryApplyRows(key: key, next: next, pointer: pointer, reason: out reason, rows: rows);
                }
                if (rows.Count == next.Count) {
                    for (var index = 0; (index < rows.Count); index++) {
                        if (!TryApply(now: next[index], old: rows[index], pointer: $"{pointer}/{index}", reason: out reason)) { return false; }
                    }
                    return true;
                }
            }
            if ((old is JsonValue value) && origins.TryGetOrigin(origin: out var authored, value: value) &&
                !string.Equals(a: authored.Json, b: value.ToJsonString(), comparisonType: StringComparison.Ordinal)) {
                reason = $"{pointer} is rewritten where its source is composed, so it cannot be saved back to it";
                return false;
            }
            if (!TryOwner(at: out var at, node: old, pointer: pointer, reason: out reason, source: out var owner)) {
                return false;
            }
            Replace(node: at!, value: now);
            owner!.Edited = true;
            return true;
        }

        // A keyed list: each removed row leaves the document that authored it, each surviving row's changes reach
        // their own documents, and each new row joins the document that authors every other row of the list, or the
        // saved source. A list whose surviving rows change order is replaced whole where it is authored.
        private bool TryApplyRows(JsonArray rows, JsonArray next, string key, string pointer, out string reason) {
            var surviving = new List<(int Before, int After)>();

            for (var index = 0; (index < next.Count); index++) {
                var at = IndexOf(identity: WorldDocumentBasis.RowIdentity(key: key, row: next[index]), key: key, rows: rows);

                if (at >= 0) { surviving.Add(item: (at, index)); }
            }
            if (surviving.Zip(second: surviving.Skip(count: 1)).Any(predicate: static pair => (pair.First.Before > pair.Second.Before))) {
                if (!TryOwner(at: out var whole, node: rows, pointer: pointer, reason: out reason, source: out var owner)) {
                    return false;
                }
                Replace(node: whole!, value: next);
                owner!.Edited = true;
                return true;
            }
            for (var index = 0; (index < rows.Count); index++) {
                if (!surviving.Any(predicate: pair => (pair.Before == index)) && !TryRemove(node: rows[index], pointer: $"{pointer}/{index}", reason: out reason)) {
                    return false;
                }
            }
            foreach (var (from, to) in surviving) {
                if (!TryApply(now: next[to], old: rows[from], pointer: $"{pointer}/{from}", reason: out reason)) {
                    return false;
                }
            }
            for (var index = 0; (index < next.Count); index++) {
                if (!surviving.Any(predicate: pair => (pair.After == index)) && !TryAppend(pointer: pointer, reason: out reason, row: next[index]!, rows: rows)) {
                    return false;
                }
            }
            reason = string.Empty;
            return true;
        }
        private static int IndexOf(JsonArray rows, string key, JsonNode? identity) {
            for (var index = 0; (index < rows.Count); index++) {
                if (JsonNode.DeepEquals(node1: WorldDocumentBasis.RowIdentity(key: key, row: rows[index]), node2: identity)) { return index; }
            }
            return -1;
        }
        private bool TryRemove(JsonNode? node, string pointer, out string reason) {
            if (!TryOwner(at: out var at, node: node, pointer: pointer, reason: out reason, source: out var owner)) {
                return false;
            }
            _ = ((at!.Parent is JsonObject parent) ? parent.Remove(propertyName: at.GetPropertyName()) : ((JsonArray)at.Parent!).Remove(item: at));
            owner!.Edited = true;
            return true;
        }
        // A member the composed object did not carry joins the node that authored the object, or the saved source
        // when the object is the document itself.
        private bool TryAdd(JsonObject parent, string name, string pointer, JsonNode? value, out string reason) {
            if (pointer.Length == 0) {
                root.Document[name] = value?.DeepClone();
                root.Edited = true;
                reason = string.Empty;
                return true;
            }
            if (!TryOwner(at: out var at, node: parent, pointer: pointer, reason: out reason, source: out var owner)) {
                return false;
            }
            at![name] = value?.DeepClone();
            owner!.Edited = true;
            return true;
        }
        // A new row joins the list in the one document that authors every row of it, or the saved source's own list,
        // which the saved source starts when it writes none.
        private bool TryAppend(JsonArray rows, string pointer, JsonNode row, out string reason) {
            row = Lowered(pointer: pointer, row: row);
            if ((rows.Count > 0) && TryOwner(at: out var at, node: rows, pointer: pointer, reason: out _, source: out var owner) && (at is JsonArray list)) {
                list.Add(item: row.DeepClone());
                owner!.Edited = true;
                reason = string.Empty;
                return true;
            }

            // The composed tree carries the list, so the saved source holds objects, or nothing, on the way to it.
            var segments = pointer.Split(options: StringSplitOptions.RemoveEmptyEntries, separator: '/').Select(selector: WorldSourceEdits.Unescape).ToArray();
            JsonNode current = root.Document;

            for (var index = 0; (index < segments.Length); index++) {
                current = (current[segments[index]] ??= ((index == (segments.Length - 1)) ? new JsonArray() : new JsonObject()));
            }
            current.AsArray().Add(item: row.DeepClone());
            root.Edited = true;
            reason = string.Empty;
            return true;
        }

        // A new row as the source printing it lowers it. The snapshot spells every member, an absent one as null and a
        // default one as its value; the printed row leaves both to the lowering, which the rewrite's own check
        // compares against.
        private static JsonNode Lowered(string pointer, JsonNode row) {
            var bare = WithoutNulls(node: row)!;
            JsonNode fragment = new JsonArray(bare.DeepClone());

            foreach (var segment in pointer.Split(options: StringSplitOptions.RemoveEmptyEntries, separator: '/').Reverse()) {
                fragment = new JsonObject { [WorldSourceEdits.Unescape(value: segment)] = fragment };
            }
            fragment["schema"] = WorldDefinition.SchemaVersion;
            try {
                var compiled = WorldCompiler.Compile(source: WorldDecompiler.Decompile(root: (JsonObject)fragment));

                return ((compiled.Success && (WorldSourceEdits.At(pointer: (pointer + "/0"), root: compiled.Json) is { } lowered)) ? lowered.DeepClone() : bare);
            } catch (WorldDecompileRefusedException) {
                return bare;
            }
        }

        // The document, and the node in it, that authored `node`: every value inside `node` must name that one
        // document and agree on where `node` stands in it.
        private bool TryOwner(JsonNode? node, string pointer, out Source? source, out JsonNode? at, out string reason) {
            source = null;
            at = null;

            string? document = null;
            string? place = null;
            var agree = true;

            Walk(node: node, suffix: string.Empty, visit: (value, suffix) => {
                if (!origins.TryGetOrigin(origin: out var origin, value: value)) {
                    return;
                }

                var candidate = (origin.JsonPointer.EndsWith(comparisonType: StringComparison.Ordinal, value: suffix)
                    ? origin.JsonPointer[..(origin.JsonPointer.Length - suffix.Length)]
                    : null);

                agree &= ((candidate is not null) && ((document is null) || PuckPaths.Comparer.Equals(x: document, y: origin.DocumentPath)) &&
                    ((place is null) || string.Equals(a: place, b: candidate, comparisonType: StringComparison.Ordinal)));
                document ??= origin.DocumentPath;
                place ??= candidate;
            });
            if (!agree || (document is null) || (place is null)) {
                reason = (agree
                    ? $"{pointer} holds no authored value to find its source by"
                    : $"{pointer} is authored in more than one place; edit it in source");
                return false;
            }
            if (!TrySource(path: document, reason: out reason, source: out source)) {
                return false;
            }
            at = ((place.Length == 0) ? source!.Document : WorldSourceEdits.At(pointer: place, root: source!.Document));
            return true;
        }
        private bool TrySource(string path, out Source? source, out string reason) {
            var key = PuckPaths.Normalize(path: path);

            if (sources.TryGetValue(key: key, value: out source)) {
                reason = string.Empty;
                return true;
            }
            if (!TryLoad(path: key, reason: out reason, source: out source)) {
                return false;
            }
            sources[key] = source!;
            return true;
        }
        private static void Walk(JsonNode? node, string suffix, Action<JsonValue, string> visit) {
            if (node is JsonValue value) {
                visit(arg1: value, arg2: suffix);
            } else if (node is JsonObject obj) {
                foreach (var (name, child) in obj) { Walk(node: child, suffix: ((suffix + "/") + WorldSourceEdits.Escape(value: name)), visit: visit); }
            } else if (node is JsonArray array) {
                for (var index = 0; (index < array.Count); index++) { Walk(node: array[index], suffix: ((suffix + "/") + index.ToString(provider: System.Globalization.CultureInfo.InvariantCulture)), visit: visit); }
            }
        }
        // A composed node's owner is a member or an element of its document, never the document itself.
        private static void Replace(JsonNode node, JsonNode? value) {
            if (node.Parent is JsonObject parent) {
                parent[node.GetPropertyName()] = value?.DeepClone();
            } else {
                var list = (JsonArray)node.Parent!;

                list[list.IndexOf(item: node)] = value?.DeepClone();
            }
        }
    }
}
