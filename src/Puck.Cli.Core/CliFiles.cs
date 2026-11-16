using System.Text.Json;
using System.Text.Json.Nodes;

namespace Puck.Cli;

// Whole-file JSON and directory-tree operations the automation verbs share. Written JSON is indented and
// newline-terminated so a committed artifact diffs line by line.
public static class CliFiles {
    private static readonly JsonSerializerOptions Indented = new() { NewLine = "\n", WriteIndented = true };

    // Copies the files beneath source to the same relative paths beneath destination and returns how many it copied.
    // Without a selection it copies the whole tree, empty directories included; a selection (any files beneath source)
    // copies only those files and the directories that hold them.
    public static int CopyDirectory(string source, string destination, IEnumerable<string>? files = null) {
        var copied = 0;

        Directory.CreateDirectory(path: destination);
        if (files is null) {
            foreach (var directory in Directory.EnumerateDirectories(path: source, searchOption: SearchOption.AllDirectories, searchPattern: "*")) {
                Directory.CreateDirectory(path: Path.Combine(path1: destination, path2: Path.GetRelativePath(path: directory, relativeTo: source)));
            }
        }
        foreach (var file in files ?? Directory.EnumerateFiles(
            path: source,
            searchOption: SearchOption.AllDirectories,
            searchPattern: "*"
        )) {
            var target = Path.Combine(
                path1: destination,
                path2: Path.GetRelativePath(
                    path: file,
                    relativeTo: source
                )
            );

            Directory.CreateDirectory(path: Path.GetDirectoryName(path: target)!);
            // A target under a build output can be a hard link that shares its file with the NuGet cache, a source or
            // another output; deleting it first replaces only this name, where a copy over it would rewrite them all.
            File.Delete(path: target);
            File.Copy(
                destFileName: target,
                overwrite: false,
                sourceFileName: file
            );
            copied++;
        }
        return copied;
    }
    public static JsonNode ReadJson(string path) =>
        (JsonNode.Parse(json: File.ReadAllText(path: path)) ?? throw new InvalidDataException(message: $"Empty JSON: {path}"));
    public static void WriteJson(string path, JsonNode value) {
        Directory.CreateDirectory(path: Path.GetDirectoryName(path: Path.GetFullPath(path: path))!);
        File.WriteAllText(
            contents: (value.ToJsonString(options: Indented) + "\n"),
            path: path
        );
    }
}
