using System.Text.Json;
using System.Text.Json.Nodes;

using Puck.Assets;
using Puck.Assets.Documents;
using Puck.Launcher.Release;
using Puck.World;
using Puck.World.Authoring;

namespace Puck.Cli.Official;

// The `puck official verify` verb: re-hashes and re-checks a puck.official.v1 tree's own claims, so a mirrored or
// hand-edited tree is caught before a client trusts it. Reads only — never writes.
// Exit 0 every check passed, 1 one or more checks failed (every discrepancy named), 2 usage error or an unreadable
// path.
internal static class OfficialVerifyCommand {
    private const string HelpText =
        """
        puck official verify — re-hash and re-check a puck.official.v1 tree

        Usage: puck official verify --base <dir> --channel <name> [--expect-commit <hex>]

        Required:
          --base <dir>          the official tree's root
          --channel <name>      the channel to verify

        Options:
          --expect-commit <hex> refuse unless build.commit equals this
          -h, --help            this text

        Checks: every object the manifest names re-hashes and re-sizes to what it claims; every document/composed
        pin recomputes to what WorldDefinitionFileSource.ComputeContentHash mints for that object's own bytes; every
        asset row's pin recomputes to its family's own canonical document hash; the world schema bundle's own
        x-puck.commit equals build.commit; and builds/<commit>/manifest.json is byte-identical to the channel
        manifest.

        Exit codes: 0 every check passed, 1 one or more checks failed (every discrepancy named), 2 usage error or an
        unreadable path.
        """;

    public static int Run(string[] args) {
        var scanner = new ArgScanner().Flag(name: "h").Flag(name: "help").Value(name: "base").Value(name: "channel").Value(name: "expect-commit");

        if (!scanner.Parse(args: args)) {
            Console.Error.WriteLine(value: $"official verify: {scanner.Error}");

            return 2;
        }

        if (scanner.Has(name: "h") || scanner.Has(name: "help")) {
            Console.Out.WriteLine(value: HelpText);

            return 0;
        }

        var baseDirectory = scanner.Get(name: "base");
        var channel = scanner.Get(name: "channel");
        var missing = new List<string>();

        if (baseDirectory is null) { missing.Add(item: "--base"); }
        if (channel is null) { missing.Add(item: "--channel"); }

        if (missing.Count > 0) {
            Console.Error.WriteLine(value: $"official verify: missing required argument(s): {string.Join(separator: ", ", values: missing)}.");

            return 2;
        }

        var root = Path.GetFullPath(path: baseDirectory!);
        var manifestPath = Path.Combine(path1: root, path2: channel!, path3: "manifest.json");

        if (!File.Exists(path: manifestPath)) {
            Console.Error.WriteLine(value: $"official verify: no manifest at '{manifestPath}'.");

            return 2;
        }

        var manifestBytes = File.ReadAllBytes(path: manifestPath);
        OfficialManifest manifest;

        try {
            manifest = (JsonSerializer.Deserialize<OfficialManifest>(utf8Json: manifestBytes, options: DocumentJsonOptions.Shared)
                ?? throw new InvalidDataException(message: "the manifest deserialized to null."));
        } catch (JsonException exception) {
            Console.Error.WriteLine(value: $"official verify: '{manifestPath}' is not a valid puck.official.v1 document: {exception.Message}");

            return 2;
        }

        var problems = new List<string>();

        if ((scanner.Get(name: "expect-commit") is { Length: > 0 } expectCommit) && !string.Equals(a: manifest.Build.Commit, b: expectCommit, comparisonType: StringComparison.OrdinalIgnoreCase)) {
            problems.Add(item: $"build.commit '{manifest.Build.Commit}' does not match --expect-commit '{expectCommit}'.");
        }

        CheckObject(label: "worldSchemaBundle", objectRef: manifest.WorldSchemaBundle, problems: problems, root: root);
        VerifySchemaBundleCommit(build: manifest.Build, problems: problems, root: root, worldSchemaBundle: manifest.WorldSchemaBundle);

        foreach (var file in manifest.Engine.Files) {
            CheckObject(label: $"engine.files[{file.Name}]", objectRef: new OfficialObjectRef(ContentType: file.ContentType, Hash: file.Hash, Path: file.Path, Size: file.Size), problems: problems, root: root);
        }

        foreach (var entry in manifest.Documents) {
            var label = $"documents[{entry.Name}]";

            CheckObject(label: label, objectRef: new OfficialObjectRef(ContentType: entry.ContentType, Hash: entry.Hash, Path: entry.Path, Size: entry.Size), problems: problems, root: root);
            CheckShortPin(label: label, objectPath: entry.Path, pin: entry.Pin, problems: problems, root: root);
        }

        foreach (var entry in manifest.Composed) {
            var label = $"composed[{entry.Name}]";

            CheckObject(label: label, objectRef: new OfficialObjectRef(ContentType: entry.ContentType, Hash: entry.Hash, Path: entry.Path, Size: entry.Size), problems: problems, root: root);
            CheckShortPin(label: label, objectPath: entry.Path, pin: entry.Pin, problems: problems, root: root);
        }

        foreach (var entry in manifest.Assets) {
            var label = $"assets[{entry.Family}:{entry.Name}]";

            CheckObject(label: label, objectRef: new OfficialObjectRef(ContentType: entry.ContentType, Hash: entry.Hash, Path: entry.Path, Size: entry.Size), problems: problems, root: root);
            CheckAssetPin(entry: entry, label: label, problems: problems, root: root);
        }

        var buildsManifestPath = Path.Combine(path1: root, path2: "builds", path3: manifest.Build.Commit, path4: "manifest.json");

        if (!File.Exists(path: buildsManifestPath)) {
            problems.Add(item: $"builds/{manifest.Build.Commit}/manifest.json is MISSING.");
        } else if (!File.ReadAllBytes(path: buildsManifestPath).AsSpan().SequenceEqual(other: manifestBytes)) {
            problems.Add(item: $"builds/{manifest.Build.Commit}/manifest.json is not byte-identical to {channel}/manifest.json.");
        }

        if (problems.Count == 0) {
            Console.Out.WriteLine(value: $"official verify: {channel} at commit {manifest.Build.Commit} — {manifest.Documents.Count} document(s), {manifest.Composed.Count} composed world(s), {manifest.Assets.Count} asset(s), {manifest.Engine.Files.Count} engine file(s) — all checks passed.");

            return 0;
        }

        Console.Error.WriteLine(value: $"official verify: {problems.Count} problem(s) found in '{manifestPath}'.");

        foreach (var problem in problems) {
            Console.Error.WriteLine(value: $"  {problem}");
        }

        return 1;
    }
    private static void CheckObject(string label, OfficialObjectRef objectRef, List<string> problems, string root) {
        var fullPath = Path.Combine(path1: root, path2: objectRef.Path);

        if (!File.Exists(path: fullPath)) {
            problems.Add(item: $"{label}: object '{objectRef.Path}' is MISSING.");

            return;
        }

        var bytes = File.ReadAllBytes(path: fullPath);

        if (bytes.LongLength != objectRef.Size) {
            problems.Add(item: $"{label}: '{objectRef.Path}' is {bytes.LongLength} byte(s), manifest claims {objectRef.Size}.");
        }

        var hash = $"sha256/{ContentAddressedStore.ComputeHash(content: bytes)}";

        if (!string.Equals(a: hash, b: objectRef.Hash, comparisonType: StringComparison.Ordinal)) {
            problems.Add(item: $"{label}: '{objectRef.Path}' hashes to '{hash}', manifest claims '{objectRef.Hash}'.");
        }
    }
    private static void CheckShortPin(string label, string objectPath, string? pin, List<string> problems, string root) {
        if (pin is null) {
            return;
        }

        var fullPath = Path.Combine(path1: root, path2: objectPath);

        if (!File.Exists(path: fullPath)) {
            return;
        }

        var recomputed = WorldDefinitionFileSource.ComputeContentHash(content: File.ReadAllBytes(path: fullPath));

        if (!string.Equals(a: recomputed, b: pin, comparisonType: StringComparison.Ordinal)) {
            problems.Add(item: $"{label}: pin '{pin}' does not match the recomputed '{recomputed}'.");
        }
    }
    private static void CheckAssetPin(OfficialAssetEntry entry, string label, List<string> problems, string root) {
        if (entry.Pin is null) {
            return;
        }

        var fullPath = Path.Combine(path1: root, path2: entry.Path);

        if (!File.Exists(path: fullPath)) {
            return;
        }

        var bytes = File.ReadAllBytes(path: fullPath);
        string? recomputed;

        try {
            recomputed = entry.Family switch {
                OfficialAssetFamilies.Music => $"sha256/{MusicCanonicalizer.Canonicalize(document: JsonSerializer.Deserialize<MusicDocument>(utf8Json: bytes, options: DocumentJsonOptions.Shared)!, source: entry.Name).Hash}",
                OfficialAssetFamilies.Table => $"sha256/{TableCanonicalizer.Canonicalize(document: JsonSerializer.Deserialize<TableDocument>(utf8Json: bytes, options: DocumentJsonOptions.Shared)!, source: entry.Name).Hash}",
                OfficialAssetFamilies.Tune => $"sha256/{AudioCanonicalizer.Canonicalize(document: JsonSerializer.Deserialize<AudioDocument>(utf8Json: bytes, options: DocumentJsonOptions.Shared)!, source: entry.Name).Hash}",
                OfficialAssetFamilies.Patch => $"sha256/{SynthPatchCanonicalizer.Canonicalize(document: JsonSerializer.Deserialize<SynthPatchDocument>(utf8Json: bytes, options: DocumentJsonOptions.Shared)!, source: entry.Name).Hash}",
                _ => null,
            };
        } catch (JsonException exception) {
            problems.Add(item: $"{label}: '{entry.Path}' is not a valid {entry.Family} document: {exception.Message}");

            return;
        }

        if ((recomputed is not null) && !string.Equals(a: recomputed, b: entry.Pin, comparisonType: StringComparison.Ordinal)) {
            problems.Add(item: $"{label}: pin '{entry.Pin}' does not match the recomputed '{recomputed}'.");
        }
    }
    private static void VerifySchemaBundleCommit(OfficialBuildInfo build, List<string> problems, string root, OfficialObjectRef worldSchemaBundle) {
        var fullPath = Path.Combine(path1: root, path2: worldSchemaBundle.Path);

        if (!File.Exists(path: fullPath)) {
            return;
        }

        JsonObject? bundle;

        try {
            bundle = (JsonNode.Parse(json: File.ReadAllText(path: fullPath)) as JsonObject);
        } catch (JsonException) {
            bundle = null;
        }

        var identity = (bundle?["x-puck"] as JsonObject);
        var commit = (identity?["commit"] as JsonValue)?.GetValue<string>();

        if ((commit is not null) && !string.Equals(a: commit, b: build.Commit, comparisonType: StringComparison.Ordinal)) {
            problems.Add(item: $"worldSchemaBundle: x-puck.commit '{commit}' does not match build.commit '{build.Commit}'.");
        }
    }
}
