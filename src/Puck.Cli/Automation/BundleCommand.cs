using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Puck.Cli.Automation;

internal static class BundleCommand {
    internal static int Create(string directory, string commit) {
        ValidateCommit(commit: commit);
        var root = Path.GetFullPath(path: directory);

        if (File.Exists(path: Path.Combine(path1: root, path2: "release.json"))) { throw new IOException(message: "Bundle already has a release manifest."); }
        var files = new JsonArray();

        foreach (var file in Directory.EnumerateFiles(path: root, searchOption: SearchOption.AllDirectories, searchPattern: "*").Order(comparer: StringComparer.Ordinal)) {
            files.Add(item: new JsonObject {
                ["path"] = Path.GetRelativePath(path: file, relativeTo: root).Replace(newChar: '/', oldChar: '\\'),
                ["sha256"] = Hash(path: file),
            });
        }
        var release = new JsonObject { ["commit"] = commit, ["channel"] = "stable", ["files"] = files };

        File.WriteAllText(path: Path.Combine(path1: root, path2: "release.json"), contents: (release.ToJsonString(options: new JsonSerializerOptions { WriteIndented = true }) + "\n"));
        return Verify(commit: commit, directory: directory);
    }
    internal static int Verify(string directory, string commit) {
        ValidateCommit(commit: commit);
        var root = Path.GetFullPath(path: directory);
        var manifestPath = Path.Combine(path1: root, path2: "release.json");
        var release = JsonNode.Parse(json: File.ReadAllText(path: manifestPath))!;

        if ((((string?)release["commit"]) != commit) || (((string?)release["channel"]) != "stable")) {
            throw new InvalidDataException(message: "Expected a stable production bundle for this workflow commit.");
        }
        var paths = new HashSet<string>(comparer: StringComparer.Ordinal);

        foreach (var file in release["files"]!.AsArray()) {
            var relative = ((string)file!["path"]!).Replace(newChar: '/', oldChar: '\\');
            var path = Path.GetFullPath(path: Path.Combine(path1: root, path2: relative));

            if (!path.StartsWith(comparisonType: StringComparison.Ordinal, value: (root + Path.DirectorySeparatorChar)) ||
                !paths.Add(item: relative) || (relative == "release.json") ||
                relative.Split('/').Any(predicate: segment => (segment is "." or ".." or ""))) {
                throw new InvalidDataException(message: $"Invalid or duplicate artifact path: {relative}");
            }
            if ((((string?)file["sha256"]) is not { } hash) || !Regex.IsMatch(input: hash, pattern: "\\A[a-f0-9]{64}\\z") || (Hash(path: path) != hash)) {
                throw new InvalidDataException(message: $"Artifact checksum mismatch: {relative}");
            }
        }
        var actual = Directory.EnumerateFiles(path: root, searchOption: SearchOption.AllDirectories, searchPattern: "*")
            .Where(predicate: file => (file != manifestPath)).Select(selector: file => Path.GetRelativePath(path: file, relativeTo: root).Replace(newChar: '/', oldChar: '\\')).ToArray();

        if ((paths.Count == 0) || !paths.SetEquals(other: actual)) { throw new InvalidDataException(message: "The artifact file inventory is incomplete."); }
        Console.WriteLine(value: $"Verified {paths.Count} files for {commit}.");
        return 0;
    }

    private static void ValidateCommit(string commit) {
        if (!Regex.IsMatch(input: commit, pattern: "\\A[a-f0-9]{40}\\z")) { throw new ArgumentException(message: "Expected a full lowercase commit SHA."); }
    }
    private static string Hash(string path) {
        using var stream = File.OpenRead(path: path);

        return Convert.ToHexStringLower(inArray: SHA256.HashData(source: stream));
    }
}
