using System.Text.Json;

using Puck.Cli.Source;

namespace Puck.Cli.Format;

// Explicit targets never widen a rewrite to sibling or linked source files. Project trees still
// supply the semantic context; the selected paths alone supply the write set.
internal static class FormatSelection {
    internal static string[] Read(string root, string manifest) {
        root = Path.TrimEndingDirectorySeparator(path: Path.GetFullPath(path: root));
        if (!Directory.Exists(path: root)) { throw new ArgumentException(message: $"Format root not found: {root}"); }
        using var document = JsonDocument.Parse(json: File.ReadAllText(path: manifest));
        var paths = new SortedSet<string>(comparer: StringComparer.Ordinal);

        foreach (var entry in document.RootElement.EnumerateArray()) {
            var relative = (entry.GetString() ?? throw new ArgumentException(message: "A format path cannot be null."));

            if (Path.IsPathRooted(path: relative) || relative.Split('/', '\\').Any(predicate: static segment => (segment is ".." or "." or ""))) {
                throw new ArgumentException(message: $"Expected a contained relative source path: {relative}");
            }
            var path = Path.GetFullPath(basePath: root, path: relative);

            if (!path.EndsWith(comparisonType: StringComparison.Ordinal, value: ".cs") || !File.Exists(path: path)) {
                throw new ArgumentException(message: $"C# source not found: {relative}");
            }
            for (var current = path; (current != root); current = Path.GetDirectoryName(path: current)!) {
                if ((File.GetAttributes(path: current) & FileAttributes.ReparsePoint) != 0) {
                    throw new ArgumentException(message: $"Formatting through a symbolic link is unsupported: {relative}");
                }
            }
            paths.Add(item: path);
        }
        return [.. paths];
    }
    internal static int Run(string root, string manifest, HashSet<string> selected, bool whatIf, bool verify) {
        var targets = Read(manifest: manifest, root: root);
        var ordinary = new List<string>();
        var standalone = new List<string>();

        foreach (var target in targets) {
            if (File.ReadLines(path: target).Any(predicate: static line => (line.StartsWith(comparisonType: StringComparison.Ordinal, value: "#!") || line.StartsWith(comparisonType: StringComparison.Ordinal, value: "#:")))
                || (SourceFiles.FindOwningProjectDirectory(start: Path.GetDirectoryName(path: target)!) is null)) {
                standalone.Add(item: target);
            } else {
                ordinary.Add(item: target);
            }
        }
        var result = ((ordinary.Count == 0) ? 0 : FormatCommand.RunPhases(root: root, selected: selected, targets: [.. ordinary], verify: verify, whatIf: whatIf));

        foreach (var target in standalone) {
            result = Math.Max(val1: result, val2: FormatFileProject.Run(file: target, selected: selected, verify: verify, whatIf: whatIf));
        }
        return result;
    }
}
