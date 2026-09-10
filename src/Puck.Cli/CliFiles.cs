using System.Text.Json;
using System.Text.Json.Nodes;

namespace Puck.Cli;

// Whole-file JSON and directory-tree operations the automation verbs share. Written JSON is indented and
// newline-terminated so a committed artifact diffs line by line.
internal static class CliFiles {
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    public static JsonNode ReadJson(string path) =>
        (JsonNode.Parse(json: File.ReadAllText(path: path)) ?? throw new InvalidDataException(message: $"Empty JSON: {path}"));
    public static void WriteJson(string path, JsonNode value) {
        Directory.CreateDirectory(path: Path.GetDirectoryName(path: Path.GetFullPath(path: path))!);
        File.WriteAllText(contents: (value.ToJsonString(options: Indented) + "\n"), path: path);
    }
    public static void CopyDirectory(string source, string destination) {
        Directory.CreateDirectory(path: destination);
        foreach (var file in Directory.EnumerateFiles(path: source, searchOption: SearchOption.AllDirectories, searchPattern: "*")) {
            var target = Path.Combine(path1: destination, path2: Path.GetRelativePath(path: file, relativeTo: source));

            Directory.CreateDirectory(path: Path.GetDirectoryName(path: target)!);
            File.Copy(destFileName: target, sourceFileName: file);
        }
    }
}
