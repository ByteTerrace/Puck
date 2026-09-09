using System.Xml.Linq;

namespace Puck.Cli.Packaging;

// Both release packing and generated package documentation consume this explicit opt-in catalog.
internal static class PackableProjects {
    internal static IEnumerable<(string File, XDocument Document)> Discover(string root) {
        var source = Path.Combine(path1: root, path2: "src");

        if (!Directory.Exists(path: source)) { yield break; }
        foreach (var file in Directory.EnumerateFiles(path: source, searchOption: SearchOption.AllDirectories, searchPattern: "*.csproj").Order(comparer: StringComparer.Ordinal)) {
            var segments = Path.GetRelativePath(path: file, relativeTo: source).Split(Path.DirectorySeparatorChar);

            if (segments.Any(predicate: segment => (segment.Equals(comparisonType: StringComparison.OrdinalIgnoreCase, value: "bin") || segment.Equals(comparisonType: StringComparison.OrdinalIgnoreCase, value: "obj")))) { continue; }
            var document = XDocument.Load(uri: file);

            if (string.Equals(a: Property(document: document, name: "IsPackable"), b: "true", comparisonType: StringComparison.OrdinalIgnoreCase)) { yield return (file, document); }
        }
    }
    internal static string? Property(XDocument document, string name) => document.Root?.Elements()
        .Where(predicate: element => (element.Name.LocalName == "PropertyGroup")).SelectMany(selector: group => group.Elements())
        .LastOrDefault(predicate: element => (element.Name.LocalName == name))?.Value.Trim();
}
