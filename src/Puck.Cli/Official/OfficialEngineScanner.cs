using Puck.Launcher.Release;

namespace Puck.Cli.Official;

// Walks a browser-wasm AppBundle directory into the engine[] the official manifest ships: every file except
// .stamp/*.map/*.symbols and the literal name package.json (excluded by name, not by extension — a shipped
// runtimeconfig.json is an ordinary .json file and stays in), by relative forward-slashed path from the bundle
// root.
internal static class OfficialEngineScanner {
    private const string EntryFileName = "main.mjs";

    public static bool TryScan(string appBundleDirectory, OfficialObjectWriter writer, out OfficialEngine? engine, out string reason) {
        engine = null;

        if (!Directory.Exists(path: appBundleDirectory)) {
            reason = $"engine directory '{appBundleDirectory}' does not exist.";

            return false;
        }

        var files = new List<OfficialEngineFile>();
        var full = Path.GetFullPath(path: appBundleDirectory);

        foreach (var filePath in Directory.EnumerateFiles(path: full, searchOption: SearchOption.AllDirectories, searchPattern: "*")) {
            var fileName = Path.GetFileName(path: filePath);
            var extension = Path.GetExtension(path: filePath);

            if (string.Equals(a: fileName, b: "package.json", comparisonType: StringComparison.OrdinalIgnoreCase) ||
                string.Equals(a: extension, b: ".stamp", comparisonType: StringComparison.OrdinalIgnoreCase) ||
                string.Equals(a: extension, b: ".map", comparisonType: StringComparison.OrdinalIgnoreCase) ||
                string.Equals(a: extension, b: ".symbols", comparisonType: StringComparison.OrdinalIgnoreCase)
            ) {
                continue;
            }

            var name = Path.GetRelativePath(path: filePath, relativeTo: full).Replace(oldChar: '\\', newChar: '/');
            var bytes = File.ReadAllBytes(path: filePath);
            var (objectPath, hash, size) = writer.Put(bytes: bytes);

            files.Add(item: new OfficialEngineFile(ContentType: ContentTypeOf(extension: extension), Hash: hash, Name: name, Path: objectPath, Size: size));
        }

        if (files.Count == 0) {
            reason = $"engine directory '{appBundleDirectory}' contains no shippable files.";

            return false;
        }

        if (!files.Any(predicate: static file => string.Equals(a: file.Name, b: EntryFileName, comparisonType: StringComparison.Ordinal))) {
            reason = $"engine directory '{appBundleDirectory}' does not contain '{EntryFileName}'.";

            return false;
        }

        engine = new OfficialEngine(Entry: EntryFileName, Files: files);
        reason = string.Empty;

        return true;
    }
    private static string ContentTypeOf(string extension) => extension.ToLowerInvariant() switch {
        ".js" or ".mjs" => "text/javascript",
        ".wasm" => "application/wasm",
        ".json" => "application/json",
        _ => "application/octet-stream",
    };
}
