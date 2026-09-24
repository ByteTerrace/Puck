using System.Text.Json.Nodes;
using Puck.Abstractions;

namespace Puck.World;

/// <summary>The one resolver for a relative path a world document authors: an asset row's source, an addon's module,
/// a pipeline, graph or probe track, a window icon, a machine's content, a neighbour or basis reference. Every such
/// path resolves beside the document that authored it, so a document and the files it names move together. A rooted
/// path is honored as written. A document with no directory (read from stdin, built in memory, delivered over the
/// wire) resolves no relative path: the refusal names the path. Files the engine ships beside its executable resolve
/// through <see cref="PuckPaths.Shipped"/> instead.</summary>
public static class WorldDocumentPaths {
    /// <summary>Returns the directory a document file's relative paths resolve against.</summary>
    /// <param name="documentPath">The document file's path, absolute or relative to the current directory.</param>
    /// <returns>The full, forward-slashed path of the file's directory.</returns>
    /// <exception cref="ArgumentException"><paramref name="documentPath"/> is empty or names no directory the platform
    /// can form.</exception>
    public static string DirectoryOf(string documentPath) {
        ArgumentException.ThrowIfNullOrWhiteSpace(argument: documentPath);

        var full = Path.GetFullPath(path: documentPath);

        return PuckPaths.Normalize(path: (Path.GetDirectoryName(path: full) ?? full));
    }
    /// <summary>Resolves a path a document authored against the document's directory.</summary>
    /// <param name="documentDirectory">The document's directory (<see cref="WorldDefinition.DocumentDirectory"/>), or
    /// <see langword="null"/> for a document that has none.</param>
    /// <param name="path">The path as the document spells it.</param>
    /// <param name="resolved">The full, forward-slashed path on success; empty on refusal.</param>
    /// <param name="reason">The named refusal, or empty on success.</param>
    /// <returns><see langword="true"/> when the path resolved: it is rooted, or relative with a directory to resolve
    /// against.</returns>
    public static bool TryResolve(string? documentDirectory, string path, out string resolved, out string reason) {
        resolved = string.Empty;

        if (string.IsNullOrWhiteSpace(value: path)) {
            reason = "the path is empty";
            return false;
        }

        try {
            if (Path.IsPathRooted(path: path)) {
                resolved = PuckPaths.Normalize(path: path);
            } else if (documentDirectory is null) {
                reason = $"'{path}' is relative, and this document has no directory to resolve it beside; load the document from a file or author an absolute path";
                return false;
            } else {
                resolved = PuckPaths.Normalize(path: Path.Combine(
                    path1: documentDirectory,
                    path2: path
                ));
            }
        } catch (Exception exception) when ((exception is ArgumentException or NotSupportedException or PathTooLongException)) {
            reason = $"'{path}' does not form a path: {exception.Message.ReplaceLineEndings(replacementText: " ")}";
            return false;
        }

        reason = string.Empty;
        return true;
    }
    /// <summary>Resolves a path a document authored against a directory the caller holds.</summary>
    /// <param name="documentDirectory">The document's directory.</param>
    /// <param name="path">The path as the document spells it.</param>
    /// <returns>The full, forward-slashed path.</returns>
    /// <exception cref="InvalidOperationException"><paramref name="path"/> is empty, relative while
    /// <paramref name="documentDirectory"/> is <see langword="null"/>, or does not form a path.</exception>
    public static string Resolve(string? documentDirectory, string path) => (TryResolve(
        documentDirectory: documentDirectory,
        path: path,
        reason: out var reason,
        resolved: out var resolved
    )
        ? resolved
        : throw new InvalidOperationException(message: reason));
    /// <summary>Re-expresses a path authored by one document as the same file named from another document, so a
    /// basis or an import merged into a document in another directory keeps naming the files it named. A rooted path
    /// passes through unchanged.</summary>
    /// <param name="path">The path as the source document spells it.</param>
    /// <param name="sourceDocumentPath">The authoring document's resolved name or path.</param>
    /// <param name="targetDocumentPath">The receiving document's resolved name or path.</param>
    /// <returns>The path relative to the receiving document's directory, spelled with <c>/</c>.</returns>
    public static string Relocate(string path, string sourceDocumentPath, string targetDocumentPath) {
        if (Path.IsPathRooted(path: path)) {
            return path;
        }

        if (
            Path.IsPathRooted(path: sourceDocumentPath) ||
            Path.IsPathRooted(path: targetDocumentPath)
        ) {
            var source = Path.GetFullPath(path: Path.Combine(
                path1: (Path.GetDirectoryName(path: Path.GetFullPath(path: sourceDocumentPath)) ?? "."),
                path2: path
            ));
            var target = (Path.GetDirectoryName(path: Path.GetFullPath(path: targetDocumentPath)) ?? ".");

            return Path.GetRelativePath(
                path: source,
                relativeTo: target
            ).Replace(
                newChar: '/',
                oldChar: '\\'
            );
        }

        var sourceAsset = WorldDefinitionFileSource.CombineRelativeDocumentName(
            name: path,
            referrerName: sourceDocumentPath
        );
        var targetDirectory = targetDocumentPath.Replace(
            newChar: '/',
            oldChar: '\\'
        );
        var slash = targetDirectory.LastIndexOf(value: '/');

        targetDirectory = ((slash >= 0)
            ? targetDirectory[..slash]
            : string.Empty
        );
        var from = targetDirectory.Split(
            options: StringSplitOptions.RemoveEmptyEntries,
            separator: '/'
        );
        var to = sourceAsset.Split(
            options: StringSplitOptions.RemoveEmptyEntries,
            separator: '/'
        );
        var common = 0;

        while (
            (common < from.Length) &&
            (common < to.Length) &&
            string.Equals(
            a: from[common],
            b: to[common],
            comparisonType: StringComparison.OrdinalIgnoreCase
        )
        ) {
            common++;
        }

        var segments = new List<string>();

        for (var i = common; (i < from.Length); i++) {
            segments.Add(item: "..");
        }

        for (var i = common; (i < to.Length); i++) {
            segments.Add(item: to[i]);
        }

        return ((segments.Count == 0)
            ? "."
            : string.Join(
                separator: "/",
                values: segments
            )
        );
    }
    /// <summary>Re-expresses every document-authored file path of a composed fragment (asset rows, addon modules,
    /// pipeline and graph sources, probe tracks, text fonts, the window icon) from the document that authored it to the
    /// document it merges into. Document names (<c>references</c>, a <c>schedule</c> instance) stay as written, since a
    /// world reaches its siblings by name. Machine configuration paths are relocated with the machine catalog
    /// (<see cref="WorldModuleNamespace.TryRelocateConfigurationAssets"/>).</summary>
    /// <param name="module">The fragment's composed tree, rewritten in place.</param>
    /// <param name="sourceDocumentPath">The fragment's resolved name or path.</param>
    /// <param name="targetDocumentPath">The receiving document's resolved name or path.</param>
    public static void RelocateDocumentFields(JsonObject module, string sourceDocumentPath, string targetDocumentPath) {
        ArgumentNullException.ThrowIfNull(argument: module);

        foreach (var family in AssetRowFamilies) {
            RelocateRows(member: "source", rows: (module[family] as JsonArray), sourceDocumentPath: sourceDocumentPath, targetDocumentPath: targetDocumentPath);
        }

        RelocateRows(member: "modulePath", rows: (module["addons"] as JsonArray), sourceDocumentPath: sourceDocumentPath, targetDocumentPath: targetDocumentPath);
        RelocateRows(member: "track", rows: (module["probes"] as JsonArray), sourceDocumentPath: sourceDocumentPath, targetDocumentPath: targetDocumentPath);

        if (module["views"] is JsonObject views) {
            RelocateRows(member: "source", rows: (views["pipelines"] as JsonArray), sourceDocumentPath: sourceDocumentPath, targetDocumentPath: targetDocumentPath);
            RelocateRows(member: "source", rows: (views["graphs"] as JsonArray), sourceDocumentPath: sourceDocumentPath, targetDocumentPath: targetDocumentPath);
        }

        if (module["text"] is JsonObject text) {
            RelocateRows(member: "source", rows: (text["fonts"] as JsonArray), sourceDocumentPath: sourceDocumentPath, targetDocumentPath: targetDocumentPath);
        }

        if (module["host"] is JsonObject host) {
            RelocateMember(member: "icon", row: host, sourceDocumentPath: sourceDocumentPath, targetDocumentPath: targetDocumentPath);
        }
    }

    // The asset-row sections, in the order WorldAssetRowLoader.Rows lists them.
    private static readonly string[] AssetRowFamilies = ["music", "tables", "tunes", "patches"];

    private static void RelocateRows(JsonArray? rows, string member, string sourceDocumentPath, string targetDocumentPath) {
        foreach (var row in (rows ?? [])) {
            if (row is JsonObject item) {
                RelocateMember(member: member, row: item, sourceDocumentPath: sourceDocumentPath, targetDocumentPath: targetDocumentPath);
            }
        }
    }
    private static void RelocateMember(JsonObject row, string member, string sourceDocumentPath, string targetDocumentPath) {
        if (
            (row[member] is JsonValue value) &&
            value.TryGetValue<string>(value: out var path) &&
            (path.Length > 0)
        ) {
            row[member] = JsonValue.Create(value: Relocate(
                path: path,
                sourceDocumentPath: sourceDocumentPath,
                targetDocumentPath: targetDocumentPath
            ));
        }
    }
}
