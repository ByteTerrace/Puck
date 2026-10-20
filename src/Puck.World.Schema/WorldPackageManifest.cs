using System.Diagnostics.CodeAnalysis;
using System.Text.Json;

namespace Puck.World;

/// <summary>An asset package's manifest (<c>puck.asset-package.v1</c>): the <see cref="FileName"/> at a package
/// folder's root naming the package and the document it boots. A package is a folder that carries a world and every
/// file it names, so it boots from wherever it lies: <c>Puck.World --world &lt;folder&gt;</c> reads the manifest and
/// boots its <see cref="Entry"/>, resolved beside the manifest. The folder is the payload, so the manifest lists no
/// files; a world's basis and imports are its source's own business.</summary>
/// <param name="Id">The package's identity.</param>
/// <param name="Name">The package's display name.</param>
/// <param name="Entry">The document the package boots, by document name (<see cref="WorldDocumentName"/>), never a
/// file: a source tree resolves it to its <c>.puck</c> source and a compiled package to its <c>.world.json</c>
/// document.</param>
public sealed record WorldPackageManifest(string Id, string Name, string Entry) {
    /// <summary>The manifest's file name at a package folder's root.</summary>
    public const string FileName = "manifest.json";
    /// <summary>The schema id a manifest declares.</summary>
    public const string SchemaId = "puck.asset-package.v1";

    private static readonly string[] Members = ["schema", "id", "name", "entry"];

    /// <summary>Returns whether <paramref name="path"/> names a package: a folder, or a package manifest file.</summary>
    /// <param name="path">A full path.</param>
    /// <returns><see langword="true"/> when the path is an existing directory or a file named
    /// <see cref="FileName"/>.</returns>
    public static bool IsPackagePath(string path) => (Directory.Exists(path: path) || string.Equals(
        a: Path.GetFileName(path: path),
        b: FileName,
        comparisonType: StringComparison.OrdinalIgnoreCase
    ));
    /// <summary>Reads a package's manifest and resolves the file carrying its entry document: the entry's
    /// <c>.puck</c> source where one lies beside the manifest, else its <c>.world.json</c> document.</summary>
    /// <param name="path">The package folder, or its manifest file.</param>
    /// <param name="manifest">The read manifest on success.</param>
    /// <param name="entryPath">The full path of the file carrying the entry document on success.</param>
    /// <param name="reason">The named refusal, or empty on success.</param>
    /// <returns><see langword="true"/> when the manifest read and its entry names a document beside it.</returns>
    public static bool TryResolveEntry(string path, [NotNullWhen(returnValue: true)] out WorldPackageManifest? manifest, out string entryPath, out string reason) {
        entryPath = string.Empty;

        var manifestPath = Path.GetFullPath(path: (Directory.Exists(path: path)
            ? Path.Combine(
                path1: path,
                path2: FileName
            )
            : path
        ));

        if (!TryRead(
            manifest: out manifest,
            path: manifestPath,
            reason: out reason
        )) {
            return false;
        }

        var directory = Path.GetDirectoryName(path: manifestPath)!;

        foreach (var candidate in (string[])[WorldDocumentName.SourceFile(name: manifest.Entry), WorldDocumentName.DocumentFile(name: manifest.Entry)]) {
            var full = Path.GetFullPath(path: Path.Combine(
                path1: directory,
                path2: candidate
            ));

            if (File.Exists(path: full)) {
                entryPath = full;

                return true;
            }
        }

        reason = $"package '{manifest.Id}' ({manifestPath}) names entry '{manifest.Entry}', and neither {WorldDocumentName.SourceFile(name: manifest.Entry)} nor {WorldDocumentName.DocumentFile(name: manifest.Entry)} lies beside the manifest";
        manifest = null;

        return false;
    }
    /// <summary>Reads a manifest file strictly: a JSON object holding exactly the string members <c>schema</c>
    /// (<see cref="SchemaId"/>), <c>id</c>, <c>name</c> and <c>entry</c>, the entry a document name inside the
    /// package.</summary>
    /// <param name="path">The manifest file.</param>
    /// <param name="manifest">The manifest on success.</param>
    /// <param name="reason">The named refusal, or empty on success.</param>
    /// <returns><see langword="true"/> when the file holds a valid manifest.</returns>
    public static bool TryRead(string path, [NotNullWhen(returnValue: true)] out WorldPackageManifest? manifest, out string reason) {
        manifest = null;

        if (!File.Exists(path: path)) {
            reason = $"no package manifest: {path} does not exist; a package folder carries a {FileName} naming its entry document";

            return false;
        }

        var values = new Dictionary<string, string>(comparer: StringComparer.Ordinal);

        try {
            using var document = JsonDocument.Parse(utf8Json: File.ReadAllBytes(path: path));

            if (document.RootElement.ValueKind != JsonValueKind.Object) {
                reason = $"package manifest {path} is not a JSON object";

                return false;
            }

            foreach (var member in document.RootElement.EnumerateObject()) {
                if (!Members.Contains(value: member.Name)) {
                    reason = $"package manifest {path} carries '{member.Name}', which {SchemaId} does not define (it holds {string.Join(separator: ", ", values: Members)})";

                    return false;
                }

                if (member.Value.ValueKind != JsonValueKind.String) {
                    reason = $"package manifest {path}: '{member.Name}' is not a string";

                    return false;
                }

                values[member.Name] = member.Value.GetString()!;
            }
        } catch (Exception exception) when ((exception is IOException or UnauthorizedAccessException or JsonException)) {
            reason = $"package manifest {path} cannot be read: {exception.Message.ReplaceLineEndings(replacementText: " ")}";

            return false;
        }

        foreach (var name in Members) {
            if (!values.TryGetValue(key: name, value: out var value) || string.IsNullOrWhiteSpace(value: value)) {
                reason = $"package manifest {path} names no '{name}'";

                return false;
            }
        }

        if (!string.Equals(a: values["schema"], b: SchemaId, comparisonType: StringComparison.Ordinal)) {
            reason = $"package manifest {path} declares schema '{values["schema"]}', not '{SchemaId}'";

            return false;
        }

        var entry = values["entry"];

        if (!WorldDocumentName.TryValidate(
            name: entry,
            reason: out var entryReason
        )) {
            reason = $"package manifest {path}: entry {entryReason}";

            return false;
        }

        if (Path.IsPathRooted(path: entry) || entry.Split(separator: '/').Contains(value: "..")) {
            reason = $"package manifest {path}: entry '{entry}' leaves the package; it names a document inside the package folder";

            return false;
        }

        manifest = new WorldPackageManifest(
            Entry: entry,
            Id: values["id"],
            Name: values["name"]
        );
        reason = string.Empty;

        return true;
    }
}
