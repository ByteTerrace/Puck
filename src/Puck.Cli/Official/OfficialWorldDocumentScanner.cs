using System.Text.Json.Nodes;

using System.Text;

using Puck.Launcher.Release;
using Puck.World;
using Puck.World.Transpiler.Assets;
using Puck.World.Transpiler.Composition;
using Puck.World.Transpiler.Embeddings;

namespace Puck.Cli.Official;

// Resolves sources[] (the authoring workspace: every .puck source under the worlds directory, recursively, the
// embedding and asset locks a compile reads beside one, and every .world.json document with no .puck source emitting
// its name — each
// published byte for byte), documents[] (every document that workspace authors, named by its document name
// (WorldDocumentName) and described by its own JSON — never a full WorldDefinition parse, which most fragments refuse
// standalone by design — each naming the sources[] file that authors it), and composed[] (the root document `puck`,
// resolved through its whole basis-and-imports graph, parsed, migrated, validated, and re-serialized — the one
// document this tree proves boots). A .puck source authors exactly the names it emits (WorldCompilation.DocumentNames):
// its stem, or one per world a composition source declares, or none for a module library, so a .world.json file named
// like a library, or like a composition that declares no world of its stem, is that name's carrier; a .world.json file
// beside the source of its name emitting that name is never read. Every authored document name is unique ignoring
// case (DocumentName.Comparer), a composition's declared worlds included, under the carriers' own rule.
internal static class OfficialWorldDocumentScanner {
    private const string BasisDocumentName = "standard";
    private const string BasisMemberName = "basis";
    private const string ImportsMemberName = "imports";
    private const string RootDocumentName = "puck";

    // One document the workspace authors: its document name, the sources[] file that authors it, and its JSON.
    private readonly record struct AuthoredDocument(string Name, string Source, byte[] Bytes);

    // The authoring workspace a compile reads, keyed by worlds-relative name and sorted ordinally: the file carrying
    // each document anywhere under the worlds directory, as the composer resolves a name (PuckDocumentComposer.TryCarriers
    // — the .puck source that emits it where one does, whatever its stem, its .world.json document otherwise; a document beside the source
    // of its name emitting that name is never read, so it is no part of the workspace), every module library, which carries no document name
    // but is read by the sources importing it, and each lock a source's compile reads beside it (EmbeddingLock and
    // AssetLock own the sidecar suffixes).
    private static bool TrySourcesIn(string full, out SortedDictionary<string, string> sources, out string reason) {
        sources = new SortedDictionary<string, string>(comparer: StringComparer.Ordinal);

        if (!PuckDocumentComposer.TryCarriers(
            carriers: out var carriers,
            directory: full,
            libraries: out var libraries,
            option: SearchOption.AllDirectories,
            reason: out reason
        )) {
            return false;
        }

        foreach (var path in carriers.Select(selector: static carrier => carrier.Path).Concat(second: libraries)) {
            sources[WorkspaceName(
                full: full,
                path: path
            )] = path;

            if (!WorldDocumentName.IsSourceFile(path: path)) {
                continue;
            }

            foreach (var sidecar in ((ReadOnlySpan<string>)[EmbeddingLock.DeriveLockPath(sourcePath: path), AssetLock.DeriveLockPath(sourcePath: path)])) {
                if (File.Exists(path: sidecar)) {
                    sources[WorkspaceName(
                        full: full,
                        path: sidecar
                    )] = sidecar;
                }
            }
        }

        return true;
    }
    // Every document one workspace file authors. A sourceless .world.json authors itself; a .puck source authors one
    // document per name it emits (WorldCompilation.DocumentNames), beside it the way `puck compile` writes them: the
    // document an ordinary source lowers to under its stem, or each world a composition declares. A module library
    // emits no name and authors nothing; a lock authors nothing either.
    private static bool TryAuthor(string name, string path, List<AuthoredDocument> documents, out string reason) {
        reason = string.Empty;

        if (WorldDocumentName.IsDocumentFile(path: name)) {
            try {
                documents.Add(item: new AuthoredDocument(
                    Bytes: File.ReadAllBytes(path: path),
                    Name: WorldDocumentName.OfDocumentFile(path: name),
                    Source: name
                ));
            } catch (IOException exception) {
                reason = $"cannot read {path}: {exception.Message}";

                return false;
            }

            return true;
        }

        if (!WorldDocumentName.IsSourceFile(path: name)) {
            return true;
        }

        // The workspace enumeration only parsed this source to learn the names it emits (WorldSourceIndex); its compile
        // reads the same declaration from the same tree, so it authors a document under exactly those names.
        if (!WorldCompileCache.Shared.TryCompile(
            compiled: out var compiled,
            failure: out var failure,
            path: path
        )) {
            reason = $"{path} does not compile: {string.Join(separator: "; ", values: failure!.Diagnostics.Select(selector: static diagnostic => $"{diagnostic.Code} {diagnostic.Message}"))}";

            return false;
        }

        var directory = name[..(name.LastIndexOf(value: '/') + 1)];

        foreach (var emitted in compiled!.DocumentNames(sourcePath: path)) {
            documents.Add(item: new AuthoredDocument(
                Bytes: compiled.DocumentNamed(name: emitted)!,
                Name: (directory + emitted),
                Source: name
            ));
        }

        return true;
    }
    private static bool TryDescribeDocument(AuthoredDocument document, OfficialObjectWriter writer, out OfficialDocumentEntry? entry, out string reason) {
        entry = null;

        JsonObject? root;

        try {
            root = (JsonNode.Parse(utf8Json: document.Bytes) as JsonObject);
        } catch (Exception exception) when ((exception is System.Text.Json.JsonException or ArgumentException)) {
            reason = $"document '{document.Name}' ({document.Source}) is not valid JSON: {exception.Message}";

            return false;
        }

        if (root is null) {
            reason = $"document '{document.Name}' ({document.Source}) does not hold a JSON object.";

            return false;
        }

        var documentId = (root["documentId"] as JsonValue)?.GetValue<string>();
        var hasBasis = root.ContainsKey(propertyName: BasisMemberName);
        var hasImports = root.ContainsKey(propertyName: ImportsMemberName);
        var role = (document.Name.StartsWith(
            comparisonType: StringComparison.Ordinal,
            value: "shards/"
        )
            ? OfficialDocumentRoles.Shard
            : (string.Equals(
                a: document.Name,
                b: BasisDocumentName,
                comparisonType: StringComparison.Ordinal
            )
                ? OfficialDocumentRoles.Basis
                : ((documentId is { Length: > 0 })
                    ? OfficialDocumentRoles.World
                    : OfficialDocumentRoles.Fragment
        )));
        var imports = new List<OfficialImportRef>();

        if ((root[ImportsMemberName] as JsonArray) is { } importsArray) {
            foreach (var item in importsArray) {
                if (item is JsonObject importObject) {
                    var imported = ((importObject["document"] as JsonValue)?.GetValue<string>() ?? string.Empty);
                    var alias = (importObject["as"] as JsonValue)?.GetValue<string>();

                    imports.Add(item: new OfficialImportRef(
                        As: alias,
                        Document: imported
                    ));
                }
            }
        }

        var exports = new List<string>();

        if ((root["exports"] as JsonObject) is { } exportsObject) {
            foreach (var facet in ((ReadOnlySpan<string>)["reads", "actions", "bindings"])) {
                if ((exportsObject[facet] as JsonArray) is { } facetArray) {
                    foreach (var item in facetArray) {
                        if ((item as JsonValue)?.GetValue<string>() is { Length: > 0 } exportName) {
                            exports.Add(item: exportName);
                        }
                    }
                }
            }
        }

        var pin = ((hasBasis || hasImports)
            ? null
            : WorldDefinitionFileSource.ComputeContentHash(content: document.Bytes)
        );

        var (objectPath, hash, size) = writer.Put(bytes: document.Bytes);

        entry = new OfficialDocumentEntry(
            ContentType: "application/json",
            DocumentId: documentId,
            Exports: exports,
            Hash: hash,
            Imports: imports,
            Name: document.Name,
            Path: objectPath,
            Pin: pin,
            Role: role,
            Size: size,
            Source: document.Source
        );
        reason = string.Empty;

        return true;
    }
    // A file's forward-slash path relative to the worlds directory — the name sources[] carries.
    private static string WorkspaceName(string full, string path) => Path.GetRelativePath(
        path: path,
        relativeTo: full
    ).Replace(
        newChar: '/',
        oldChar: '\\'
    );

    public static bool TryScan(
        string worldsDirectory,
        OfficialObjectWriter writer,
        out IReadOnlyList<OfficialSourceEntry> sources,
        out IReadOnlyList<OfficialDocumentEntry> documents,
        out IReadOnlyList<OfficialComposedEntry> composed,
        out WorldDefinition? composedDefinition,
        out string reason
    ) {
        sources = [];
        documents = [];
        composed = [];
        composedDefinition = null;

        var machines = CliWorldVocabulary.EnsureInstalled();
        var catalogFingerprint = CliWorldVocabulary.Fingerprint(catalog: machines);

        var full = Path.GetFullPath(path: worldsDirectory);
        var sourceEntries = new List<OfficialSourceEntry>();
        var authored = new List<AuthoredDocument>();

        if (!TrySourcesIn(
            full: full,
            reason: out reason,
            sources: out var workspace
        )) {
            return false;
        }

        foreach (var (name, path) in workspace) {
            byte[] bytes;

            try {
                bytes = File.ReadAllBytes(path: path);
            } catch (IOException exception) {
                reason = $"cannot read {path}: {exception.Message}";

                return false;
            }

            var (sourcePath, sourceHash, sourceSize) = writer.Put(bytes: bytes);

            sourceEntries.Add(item: new OfficialSourceEntry(
                ContentType: (WorldDocumentName.IsSourceFile(path: name)
                    ? OfficialSourceContentTypes.Puck
                    : OfficialSourceContentTypes.Json
                ),
                Hash: sourceHash,
                Name: name,
                Path: sourcePath,
                Size: sourceSize
            ));

            if (!TryAuthor(
                documents: authored,
                name: name,
                path: path,
                reason: out reason
            )) {
                return false;
            }
        }

        // The root and the basis are documents like any other: each resolves by name to its source when it has one.
        foreach (var required in ((ReadOnlySpan<string>)[RootDocumentName, BasisDocumentName])) {
            if (!authored.Exists(match: document => string.Equals(
                a: document.Name,
                b: required,
                comparisonType: StringComparison.Ordinal
            ))) {
                reason = $"no document named '{required}' ({WorldDocumentName.SourceFile(name: required)} or {WorldDocumentName.DocumentFile(name: required)}) is authored under {full}.";

                return false;
            }
        }

        var entries = new List<OfficialDocumentEntry>();

        foreach (var document in authored) {
            if (!TryDescribeDocument(
                document: document,
                entry: out var entry,
                reason: out reason,
                writer: writer
            )) {
                return false;
            }

            entries.Add(item: entry!);
        }

        var root = authored.Find(match: static document => string.Equals(
            a: document.Name,
            b: RootDocumentName,
            comparisonType: StringComparison.Ordinal
        ));
        var rootPath = Path.Combine(
            path1: full,
            path2: root.Source
        );

        if (!PuckDocumentComposer.TryComposeWorldDocument(
            catalog: machines,
            catalogFingerprint: catalogFingerprint,
            chainBytes: out _,
            composed: out var tree,
            reason: out reason,
            rootBytes: root.Bytes,
            rootResolvedPath: rootPath
        )) {
            reason = $"composing '{RootDocumentName}' ({root.Source}): {reason}";

            return false;
        }

        if (!WorldDefinitionFileSource.TryParseComposed(
            definition: out var definition,
            json: ((tree is null)
                ? Encoding.UTF8.GetString(bytes: root.Bytes)
                : tree.ToJsonString()),
            neighbours: null,
            reason: out reason,
            sourceName: rootPath,
            validateAdjacencyClaims: false,
            catalog: machines,
            documentDirectory: WorldDocumentPaths.DirectoryOf(documentPath: rootPath)
        )) {
            reason = $"parsing composed '{RootDocumentName}' ({root.Source}): {reason}";

            return false;
        }

        if (!WorldDefinitionValidator.TryValidateLocally(
            definition: definition!,
            machines: machines,
            reason: out reason
        )) {
            reason = $"machine admission in '{RootDocumentName}' ({root.Source}): {reason}";
            return false;
        }

        var canonicalBytes = WorldDefinitionSerialization.Serialize(definition: definition!);
        var pin = WorldDefinitionFileSource.ComputeContentHash(content: canonicalBytes);

        var (objectPath, hash, size) = writer.Put(bytes: canonicalBytes);
        var identity = (string.IsNullOrWhiteSpace(value: definition!.DocumentId)
            ? null
            : $"puck:world/{Uri.EscapeDataString(stringToEscape: definition.DocumentId)}?schema={definition.Schema}&hash={pin}"
        );

        sources = sourceEntries;
        documents = entries;
        composed = [
            new OfficialComposedEntry(
                ContentType: "application/json",
                DocumentId: (definition.DocumentId ?? string.Empty),
                Hash: hash,
                Identity: ((identity is null)
            ? null
            : JsonValue.Create(value: identity)),
                Name: RootDocumentName,
                Path: objectPath,
                Pin: pin,
                Size: size
            ),
        ];
        composedDefinition = definition;
        reason = string.Empty;

        return true;
    }
}
