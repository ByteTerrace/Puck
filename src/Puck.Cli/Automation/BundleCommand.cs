using System.CommandLine;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Puck.Cli.Automation;

/// <summary>
/// <c>puck bundle</c> — the release manifest a deployment bundle carries: every file with its SHA-256, bound to one
/// commit, so a consumer can prove it deploys exactly what the producer built.
/// </summary>
internal static class BundleCommand {
    public static Command Create() {
        var directoryArgument = new Argument<string>(name: "directory") { Description = "The bundle directory." };
        var commitArgument = new Argument<string>(name: "commit") { Description = "The full lowercase commit SHA the bundle was built from." };
        var create = new Command(description: "Write release.json over every file in the directory, then verify it.", name: "create") { directoryArgument, commitArgument };
        var verify = new Command(description: "Verify release.json names this commit and matches every file in the directory.", name: "verify") { directoryArgument, commitArgument };
        var command = new Command(description: "Create or verify deployment artifact manifests.", name: "bundle") { create, verify };

        create.SetAction(action: parseResult => CreateManifest(commit: parseResult.GetRequiredValue(argument: commitArgument), directory: parseResult.GetRequiredValue(argument: directoryArgument)));
        verify.SetAction(action: parseResult => VerifyManifest(commit: parseResult.GetRequiredValue(argument: commitArgument), directory: parseResult.GetRequiredValue(argument: directoryArgument)));
        return command;
    }

    internal static int CreateManifest(string directory, string commit) {
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

        File.WriteAllText(contents: (release.ToJsonString(options: new JsonSerializerOptions { WriteIndented = true }) + "\n"), path: Path.Combine(path1: root, path2: "release.json"));
        return VerifyManifest(commit: commit, directory: directory);
    }
    internal static int VerifyManifest(string directory, string commit) {
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
