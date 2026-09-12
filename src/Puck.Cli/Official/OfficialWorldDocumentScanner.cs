using System.Text.Json.Nodes;

using Puck.Launcher.Release;
using Puck.World;

namespace Puck.Cli.Official;

// Resolves documents[] (every world document under the worlds directory, by its own raw JSON — never a full
// WorldDefinition parse, which most fragments refuse standalone by design) and composed[] (the root puck.world.json,
// resolved through its whole basis-and-imports graph, parsed, migrated, validated, and re-serialized — the one
// document this tree proves boots).
internal static class OfficialWorldDocumentScanner {
    private const string BasisMemberName = "basis";
    private const string ImportsMemberName = "imports";
    private const string RootDocumentName = "puck.world.json";
    private const string BasisDocumentName = "standard.basis.json";
    private static readonly string[] FragmentSubdirectories = ["games", "modules"];

    public static bool TryScan(
        string worldsDirectory,
        OfficialObjectWriter writer,
        out IReadOnlyList<OfficialDocumentEntry> documents,
        out IReadOnlyList<OfficialComposedEntry> composed,
        out WorldDefinition? composedDefinition,
        out string reason
    ) {
        documents = [];
        composed = [];
        composedDefinition = null;

        var machines = CliWorldVocabulary.EnsureInstalled();

        var full = Path.GetFullPath(path: worldsDirectory);
        var rootPath = Path.Combine(path1: full, path2: RootDocumentName);
        var basisPath = Path.Combine(path1: full, path2: BasisDocumentName);

        if (!File.Exists(path: rootPath)) {
            reason = $"'{RootDocumentName}' does not exist under {full}.";

            return false;
        }

        if (!File.Exists(path: basisPath)) {
            reason = $"'{BasisDocumentName}' does not exist under {full}.";

            return false;
        }

        var candidates = new List<string> { rootPath, basisPath };

        foreach (var subdirectory in FragmentSubdirectories) {
            var directory = Path.Combine(path1: full, path2: subdirectory);

            if (Directory.Exists(path: directory)) {
                candidates.AddRange(collection: Directory.EnumerateFiles(path: directory, searchOption: SearchOption.TopDirectoryOnly, searchPattern: "*.json").Order(comparer: StringComparer.Ordinal));
            }
        }

        var shardsDirectory = Path.Combine(path1: full, path2: "shards");

        if (Directory.Exists(path: shardsDirectory)) {
            candidates.AddRange(collection: Directory.EnumerateFiles(path: shardsDirectory, searchOption: SearchOption.TopDirectoryOnly, searchPattern: "*.json").Order(comparer: StringComparer.Ordinal));
        }

        var entries = new List<OfficialDocumentEntry>();

        foreach (var path in candidates) {
            if (!TryDescribeDocument(full: full, path: path, writer: writer, entry: out var entry, reason: out reason)) {
                return false;
            }

            entries.Add(item: entry!);
        }

        if (!WorldDefinitionFileSource.TryComposeDocumentTree(path: rootPath, tree: out var tree, reason: out reason)) {
            reason = $"composing {RootDocumentName}: {reason}";

            return false;
        }

        if (!WorldDefinitionFileSource.TryParseComposed(definition: out var definition, json: tree!.ToJsonString(), neighbours: null, reason: out reason, sourceName: rootPath, validateAdjacencyClaims: false)) {
            reason = $"parsing composed {RootDocumentName}: {reason}";

            return false;
        }

        if (!WorldDefinitionValidator.TryValidateLocally(definition!, machines, out reason)) {
            reason = $"machine admission in {RootDocumentName}: {reason}";
            return false;
        }

        var canonicalBytes = WorldDefinitionSerialization.Serialize(definition: definition!);
        var pin = WorldDefinitionFileSource.ComputeContentHash(content: canonicalBytes);
        var (objectPath, hash, size) = writer.Put(bytes: canonicalBytes);
        var identity = (string.IsNullOrWhiteSpace(value: definition!.DocumentId)
            ? null
            : $"puck:world/{Uri.EscapeDataString(stringToEscape: definition.DocumentId)}?schema={definition.Schema}&hash={pin}");

        documents = entries;
        composed = [
            new OfficialComposedEntry(
                ContentType: "application/json",
                DocumentId: (definition.DocumentId ?? string.Empty),
                Hash: hash,
                Identity: (identity is null ? null : JsonValue.Create(value: identity)),
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
    private static bool TryDescribeDocument(string full, string path, OfficialObjectWriter writer, out OfficialDocumentEntry? entry, out string reason) {
        entry = null;

        byte[] bytes;

        try {
            bytes = File.ReadAllBytes(path: path);
        } catch (IOException exception) {
            reason = $"cannot read {path}: {exception.Message}";

            return false;
        }

        JsonObject? root;

        try {
            root = (JsonNode.Parse(utf8Json: bytes) as JsonObject);
        } catch (Exception exception) when ((exception is System.Text.Json.JsonException or ArgumentException)) {
            reason = $"{path} is not valid JSON: {exception.Message}";

            return false;
        }

        if (root is null) {
            reason = $"{path} does not hold a JSON object.";

            return false;
        }

        var name = Path.GetRelativePath(path: path, relativeTo: full).Replace(oldChar: '\\', newChar: '/');
        var documentId = (root["documentId"] as JsonValue)?.GetValue<string>();
        var hasBasis = root.ContainsKey(propertyName: BasisMemberName);
        var hasImports = root.ContainsKey(propertyName: ImportsMemberName);
        var role = (name.StartsWith(value: "shards/", comparisonType: StringComparison.Ordinal)
            ? OfficialDocumentRoles.Shard
            : string.Equals(a: name, b: BasisDocumentName, comparisonType: StringComparison.Ordinal)
                ? OfficialDocumentRoles.Basis
                : (documentId is { Length: > 0 })
                    ? OfficialDocumentRoles.World
                    : OfficialDocumentRoles.Fragment);
        var imports = new List<OfficialImportRef>();

        if ((root[ImportsMemberName] as JsonArray) is { } importsArray) {
            foreach (var item in importsArray) {
                if (item is JsonObject importObject) {
                    var document = (importObject["document"] as JsonValue)?.GetValue<string>() ?? string.Empty;
                    var alias = (importObject["as"] as JsonValue)?.GetValue<string>();

                    imports.Add(item: new OfficialImportRef(As: alias, Document: document));
                }
            }
        }

        var exports = new List<string>();

        if ((root["exports"] as JsonObject) is { } exportsObject) {
            foreach (var facet in (ReadOnlySpan<string>)["reads", "actions", "bindings"]) {
                if ((exportsObject[facet] as JsonArray) is { } facetArray) {
                    foreach (var item in facetArray) {
                        if ((item as JsonValue)?.GetValue<string>() is { Length: > 0 } exportName) {
                            exports.Add(item: exportName);
                        }
                    }
                }
            }
        }

        var pin = ((hasBasis || hasImports) ? null : WorldDefinitionFileSource.ComputeContentHash(content: bytes));
        var (objectPath, hash, size) = writer.Put(bytes: bytes);

        entry = new OfficialDocumentEntry(
            ContentType: "application/json",
            DocumentId: documentId,
            Exports: exports,
            Hash: hash,
            Imports: imports,
            Name: name,
            Path: objectPath,
            Pin: pin,
            Role: role,
            Size: size
        );
        reason = string.Empty;

        return true;
    }
}
