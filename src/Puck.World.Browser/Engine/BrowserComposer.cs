using System.Text;
using System.Text.Json.Nodes;

namespace Puck.World.Browser.Engine;

/// <summary>One <c>ComposeTree</c> outcome: the composed standalone document's raw JSON and the parsed/validated
/// echo on success (the same shape <see cref="BrowserParseResult"/> carries), or the split, document-attributed
/// errors on failure. Never both.</summary>
/// <param name="Ok">Whether the tree composed, parsed, and validated.</param>
/// <param name="Composed">The composed standalone document's own JSON text on success.</param>
/// <param name="Document">The canonical parsed/validated echo (see <see cref="BrowserParser.Parse"/>) on success.</param>
/// <param name="Errors">The split, document-attributed validator messages on failure.</param>
/// <param name="Deferred">Platform-deferred admission notices on success.</param>
public sealed record BrowserComposeResult(bool Ok, string? Composed, string? Document, IReadOnlyList<BrowserErrorPath>? Errors, IReadOnlyList<string>? Deferred) {
    internal static BrowserComposeResult Failure(string message) => new(Ok: false, Composed: null, Document: null, Errors: [new BrowserErrorPath(Path: null, Message: message)], Deferred: null);
}

/// <summary>The pure C# core behind the <c>ComposeTree</c> export: composes a whole basis-and-imports graph purely
/// from a caller-supplied map of worlds-relative document names to their raw JSON text — no file system, so a
/// browser studio can compose the real island (or any subtree of it) from documents it fetched over the network,
/// optionally substituting one document's text for an in-progress edit before composing.</summary>
public static class BrowserComposer {
    /// <summary>Composes <paramref name="rootName"/>'s whole basis-and-imports graph over <paramref name="documents"/>,
    /// then parses and validates the result exactly like <see cref="BrowserParser.Parse"/> would.</summary>
    /// <param name="rootName">The root document's own worlds-relative name (a key of <paramref name="documents"/>,
    /// unless it is itself the edited document).</param>
    /// <param name="documents">Every document of the import tree, keyed by its worlds-relative name, as UTF-8 JSON
    /// bytes.</param>
    /// <param name="editedName">The document whose text <paramref name="editedUtf8"/> replaces during this
    /// composition, or <see langword="null"/> to compose <paramref name="documents"/> unmodified.</param>
    /// <param name="editedUtf8">The edited document's candidate UTF-8 JSON text; ignored when
    /// <paramref name="editedName"/> is <see langword="null"/>.</param>
    /// <returns>The compose result.</returns>
    public static BrowserComposeResult ComposeTree(string rootName, IReadOnlyDictionary<string, byte[]> documents, string? editedName, byte[]? editedUtf8) {
        ArgumentNullException.ThrowIfNull(argument: rootName);
        ArgumentNullException.ThrowIfNull(argument: documents);

        bool TryResolve(string resolvedName, out ReadOnlyMemory<byte> content) {
            if ((editedName is { Length: > 0 }) && string.Equals(a: resolvedName, b: editedName, comparisonType: StringComparison.Ordinal)) {
                content = editedUtf8;

                return true;
            }

            if (documents.TryGetValue(key: resolvedName, value: out var bytes)) {
                content = bytes;

                return true;
            }

            content = default;

            return false;
        }

        if (!TryResolve(resolvedName: rootName, content: out var rootBytes)) {
            return BrowserComposeResult.Failure(message: $"'{rootName}' names no document this composition holds.");
        }

        if (!WorldDefinitionFileSource.TryComposeDocumentTree(
            resolver: TryResolve,
            rootBytes: rootBytes,
            rootName: rootName,
            tree: out var tree,
            reason: out var composeReason
        )) {
            return BrowserComposeResult.Failure(message: composeReason);
        }

        var composedJson = tree!.ToJsonString();
        var errors = new List<string>();
        var deferred = new List<string>();

        if (!BrowserParser.TryParseAndValidate(
            compilation: out _,
            definition: out var definition,
            deferred: deferred,
            errors: errors,
            utf8Json: Encoding.UTF8.GetBytes(s: composedJson)
        )) {
            return new BrowserComposeResult(
                Composed: null,
                Deferred: null,
                Document: null,
                Errors: MapDiagnostics(documents: documents, editedName: editedName, messages: errors, rootName: rootName),
                Ok: false
            );
        }

        return new BrowserComposeResult(
            Composed: composedJson,
            Deferred: deferred,
            Document: Encoding.UTF8.GetString(bytes: WorldDefinitionSerialization.Serialize(definition: definition!)),
            Errors: null,
            Ok: true
        );
    }

    // Maps every collected message back to the document it names, mirroring BrowserErrorPaths.SplitFragment's own
    // heuristic one level up: this composition's ONLY aliasing happens where the root names an import with an "as"
    // (the real shipped tree aliases exactly there — a district/game module imported once by puck.world.json — and
    // never re-aliases a module's own further imports), so the alias map below is a one-level read of the root's
    // own "imports" array, not a walk of the whole tree. A message naming the edited document's own alias has that
    // alias stripped (its own bare path, like ParseFragment's fragment); a message naming a DIFFERENT aliased
    // import's namespace is attributed to that document by name; everything else (the root's own bare rows, or an
    // unaliased import's — the edited document's included, when it composed unaliased) is left as its own already-
    // correct path.
    private static IReadOnlyList<BrowserErrorPath> MapDiagnostics(IReadOnlyDictionary<string, byte[]> documents, string? editedName, string rootName, IReadOnlyList<string> messages) {
        var aliases = ReadRootImportAliases(documents: documents, rootName: rootName);
        var result = new List<BrowserErrorPath>(capacity: messages.Count);

        foreach (var message in messages) {
            var mapped = message;
            var attributedTo = (string?)null;

            foreach (var (documentName, alias) in aliases) {
                if ((alias is not { Length: > 0 }) || !mapped.Contains(value: $"{alias}_", comparisonType: StringComparison.Ordinal)) {
                    continue;
                }

                if (string.Equals(a: documentName, b: editedName, comparisonType: StringComparison.Ordinal)) {
                    mapped = mapped.Replace(oldValue: $"{alias}_", newValue: string.Empty, comparisonType: StringComparison.Ordinal);
                } else {
                    attributedTo = documentName;
                }

                break;
            }

            var split = BrowserErrorPaths.Split(message: mapped);

            result.Add(item: (attributedTo is { } owner)
                ? new BrowserErrorPath(Path: split.Path, Message: $"{owner}: {split.Message}")
                : split
            );
        }

        return result;
    }
    private static IReadOnlyList<(string DocumentName, string? Alias)> ReadRootImportAliases(IReadOnlyDictionary<string, byte[]> documents, string rootName) {
        if (
            !documents.TryGetValue(key: rootName, value: out var rootBytes) ||
            (JsonNode.Parse(json: Encoding.UTF8.GetString(bytes: rootBytes)) is not JsonObject root) ||
            !root.TryGetPropertyValue(propertyName: WorldDocumentBasis.ImportsMemberName, jsonNode: out var importsNode) ||
            (importsNode is not JsonArray imports)
        ) {
            return [];
        }

        var result = new List<(string, string?)>(capacity: imports.Count);

        foreach (var entry in imports) {
            if (
                (entry is not JsonObject entryObject) ||
                !entryObject.TryGetPropertyValue(propertyName: WorldImport.DocumentMemberName, jsonNode: out var documentNode) ||
                (documentNode is not JsonValue documentValue) ||
                !documentValue.TryGetValue<string>(value: out var documentName)
            ) {
                continue;
            }

            string? alias = null;

            if (
                entryObject.TryGetPropertyValue(propertyName: WorldImport.AsMemberName, jsonNode: out var aliasNode) &&
                (aliasNode is JsonValue aliasValue) &&
                aliasValue.TryGetValue<string>(value: out var aliasText)
            ) {
                alias = aliasText;
            }

            result.Add(item: (WorldDefinitionFileSource.CombineRelativeDocumentName(referrerName: rootName, name: documentName), alias));
        }

        return result;
    }
}
