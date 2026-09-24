using System.CommandLine;
using System.Text.Json;

using Puck.Assets;
using Puck.Assets.Documents;
using Puck.Launcher.Release;
using Puck.World;
using Puck.World.Authoring;

namespace Puck.Cli.Official;

// The `puck official verify` verb: re-hashes and re-checks a puck.official.manifest.v1 tree's own claims, so a mirrored or
// hand-edited tree is caught before a client trusts it. Reads only — never writes.
// Exit 0 every check passed, 1 one or more checks failed (every discrepancy named), 2 an unreadable path.
internal static class OfficialVerifyCommand {
    private static void CheckAssetPin(OfficialAssetEntry entry, string label, List<string> problems, string root) {
        if (entry.Pin is null) {
            return;
        }

        var fullPath = Path.Combine(
            path1: root,
            path2: entry.Path
        );

        if (!File.Exists(path: fullPath)) {
            return;
        }

        var bytes = File.ReadAllBytes(path: fullPath);
        string? recomputed;

        try {
            recomputed = entry.Family switch {
                AssetRowFamilies.Music => (ContentPin.Prefix + MusicCanonicalizer.Canonicalize(
                document: JsonSerializer.Deserialize<MusicDocument>(
                    utf8Json: bytes,
                    options: DocumentJsonOptions.Shared
                )!,
                source: entry.Name
            ).Hash),
                AssetRowFamilies.Table => (ContentPin.Prefix + TableCanonicalizer.Canonicalize(
                document: JsonSerializer.Deserialize<TableDocument>(
                    utf8Json: bytes,
                    options: DocumentJsonOptions.Shared
                )!,
                source: entry.Name
            ).Hash),
                AssetRowFamilies.Tune => (ContentPin.Prefix + AudioCanonicalizer.Canonicalize(
                document: JsonSerializer.Deserialize<AudioDocument>(
                    utf8Json: bytes,
                    options: DocumentJsonOptions.Shared
                )!,
                source: entry.Name
            ).Hash),
                AssetRowFamilies.Patch => (ContentPin.Prefix + SynthPatchCanonicalizer.Canonicalize(
                document: JsonSerializer.Deserialize<SynthPatchDocument>(
                    utf8Json: bytes,
                    options: DocumentJsonOptions.Shared
                )!,
                source: entry.Name
            ).Hash),
                _ => null,
            };
        } catch (JsonException exception) {
            problems.Add(item: $"{label}: '{entry.Path}' is not a valid {entry.Family} document: {exception.Message}");

            return;
        }

        if (
            (recomputed is not null) &&
            !string.Equals(
            a: recomputed,
            b: entry.Pin,
            comparisonType: StringComparison.Ordinal
        )
        ) {
            problems.Add(item: $"{label}: pin '{entry.Pin}' does not match the recomputed '{recomputed}'.");
        }
    }
    // A document is named by its document name, and its source is a file that can author that name: its own
    // .world.json file, which it then is byte for byte, or a .puck source in the same directory — the one named after
    // it, or a composition declaring it as a world.
    private static void CheckAuthorship(OfficialDocumentEntry entry, string label, List<string> problems, IReadOnlyDictionary<string, OfficialSourceEntry> sourcesByName) {
        if (!WorldDocumentName.TryValidate(
            name: entry.Name,
            reason: out var nameReason
        )) {
            problems.Add(item: $"{label}: {nameReason}.");

            return;
        }

        if (!sourcesByName.TryGetValue(
            key: entry.Source,
            value: out var source
        )) {
            return;
        }

        if (WorldDocumentName.IsDocumentFile(path: source.Name)) {
            if (!string.Equals(
                a: source.Name,
                b: WorldDocumentName.DocumentFile(name: entry.Name),
                comparisonType: StringComparison.Ordinal
            )) {
                problems.Add(item: $"{label}: source '{source.Name}' is another document's file.");
            } else if (!string.Equals(
                a: source.Hash,
                b: entry.Hash,
                comparisonType: StringComparison.Ordinal
            )) {
                problems.Add(item: $"{label}: authored by its own file, but its hash '{entry.Hash}' is not that source's '{source.Hash}'.");
            }

            return;
        }

        if (!WorldDocumentName.IsSourceFile(path: source.Name)) {
            problems.Add(item: $"{label}: source '{source.Name}' is neither a .puck source nor a document file, so it authors no document.");
        } else if (!string.Equals(
            a: source.Name[..(source.Name.LastIndexOf(value: '/') + 1)],
            b: entry.Name[..(entry.Name.LastIndexOf(value: '/') + 1)],
            comparisonType: StringComparison.Ordinal
        )) {
            problems.Add(item: $"{label}: source '{source.Name}' sits in another directory, and a source authors documents only beside itself.");
        }
    }
    private static void CheckObject(string label, OfficialObjectRef objectRef, List<string> problems, string root) {
        var fullPath = Path.Combine(
            path1: root,
            path2: objectRef.Path
        );

        if (!File.Exists(path: fullPath)) {
            problems.Add(item: $"{label}: object '{objectRef.Path}' is MISSING.");

            return;
        }

        var bytes = File.ReadAllBytes(path: fullPath);

        if (bytes.LongLength != objectRef.Size) {
            problems.Add(item: $"{label}: '{objectRef.Path}' is {bytes.LongLength} byte(s), manifest claims {objectRef.Size}.");
        }

        var hash = ContentPin.Compute(content: bytes).ToString();

        if (!string.Equals(
            a: hash,
            b: objectRef.Hash,
            comparisonType: StringComparison.Ordinal
        )) {
            problems.Add(item: $"{label}: '{objectRef.Path}' hashes to '{hash}', manifest claims '{objectRef.Hash}'.");
        }
    }
    private static void CheckShortPin(string label, string objectPath, string? pin, List<string> problems, string root) {
        if (pin is null) {
            return;
        }

        var fullPath = Path.Combine(
            path1: root,
            path2: objectPath
        );

        if (!File.Exists(path: fullPath)) {
            return;
        }

        var recomputed = WorldDefinitionFileSource.ComputeContentHash(content: File.ReadAllBytes(path: fullPath));

        if (!string.Equals(
            a: recomputed,
            b: pin,
            comparisonType: StringComparison.Ordinal
        )) {
            problems.Add(item: $"{label}: pin '{pin}' does not match the recomputed '{recomputed}'.");
        }
    }
    private static int Run(string baseDirectory, string channel, string? expectCommit) {
        var root = Path.GetFullPath(path: baseDirectory);
        var manifestPath = Path.Combine(
            path1: root,
            path2: channel,
            path3: "manifest.json"
        );

        if (!File.Exists(path: manifestPath)) {
            Console.Error.WriteLine(value: $"official verify: no manifest at '{manifestPath}'.");

            return 2;
        }

        var manifestBytes = File.ReadAllBytes(path: manifestPath);
        OfficialManifest manifest;

        try {
            manifest = (JsonSerializer.Deserialize<OfficialManifest>(
                utf8Json: manifestBytes,
                options: DocumentJsonOptions.Shared
            )
                ?? throw new InvalidDataException(message: "the manifest deserialized to null."));
        } catch (JsonException exception) {
            Console.Error.WriteLine(value: $"official verify: '{manifestPath}' is not a valid puck.official.manifest.v1 document: {exception.Message}");

            return 2;
        }

        var problems = new List<string>();

        if (
            (expectCommit is { Length: > 0 }) &&
            !string.Equals(
            a: manifest.Build.Commit,
            b: expectCommit,
            comparisonType: StringComparison.OrdinalIgnoreCase
        )
        ) {
            problems.Add(item: $"build.commit '{manifest.Build.Commit}' does not match --expect-commit '{expectCommit}'.");
        }

        CheckObject(
            label: "worldSchemaBundle",
            objectRef: manifest.WorldSchemaBundle,
            problems: problems,
            root: root
        );

        foreach (var file in manifest.Engine.Files) {
            CheckObject(
                label: $"engine.files[{file.Name}]",
                objectRef: new OfficialObjectRef(
                    ContentType: file.ContentType,
                    Hash: file.Hash,
                    Path: file.Path,
                    Size: file.Size
                ),
                problems: problems,
                root: root
            );
        }

        foreach (var error in OfficialCanonicalizer.Validate(document: manifest)) {
            problems.Add(item: $"{error.Path}: {error.Message}");
        }

        // A manifest missing its sources list is already a named problem above; its objects are then none.
        var sources = (manifest.Sources ?? []);

        foreach (var entry in sources) {
            CheckObject(
                label: $"sources[{entry.Name}]",
                objectRef: new OfficialObjectRef(
                    ContentType: entry.ContentType,
                    Hash: entry.Hash,
                    Path: entry.Path,
                    Size: entry.Size
                ),
                problems: problems,
                root: root
            );
        }

        var sourcesByName = sources
            .GroupBy(keySelector: static entry => entry.Name, comparer: StringComparer.Ordinal)
            .ToDictionary(
                comparer: StringComparer.Ordinal,
                elementSelector: static group => group.First(),
                keySelector: static group => group.Key
            );

        foreach (var entry in manifest.Documents) {
            var label = $"documents[{entry.Name}]";

            CheckAuthorship(
                entry: entry,
                label: label,
                problems: problems,
                sourcesByName: sourcesByName
            );
            CheckObject(
                label: label,
                objectRef: new OfficialObjectRef(
                    ContentType: entry.ContentType,
                    Hash: entry.Hash,
                    Path: entry.Path,
                    Size: entry.Size
                ),
                problems: problems,
                root: root
            );
            CheckShortPin(
                label: label,
                objectPath: entry.Path,
                pin: entry.Pin,
                problems: problems,
                root: root
            );
        }

        foreach (var entry in manifest.Composed) {
            var label = $"composed[{entry.Name}]";

            CheckObject(
                label: label,
                objectRef: new OfficialObjectRef(
                    ContentType: entry.ContentType,
                    Hash: entry.Hash,
                    Path: entry.Path,
                    Size: entry.Size
                ),
                problems: problems,
                root: root
            );
            CheckShortPin(
                label: label,
                objectPath: entry.Path,
                pin: entry.Pin,
                problems: problems,
                root: root
            );
        }

        foreach (var entry in manifest.Assets) {
            var label = $"assets[{entry.Family}:{entry.Name}]";

            CheckObject(
                label: label,
                objectRef: new OfficialObjectRef(
                    ContentType: entry.ContentType,
                    Hash: entry.Hash,
                    Path: entry.Path,
                    Size: entry.Size
                ),
                problems: problems,
                root: root
            );
            CheckAssetPin(
                entry: entry,
                label: label,
                problems: problems,
                root: root
            );
        }

        var buildsManifestPath = Path.Combine(
            path1: root,
            path2: "builds",
            path3: manifest.Build.Commit,
            path4: "manifest.json"
        );

        if (!File.Exists(path: buildsManifestPath)) {
            problems.Add(item: $"builds/{manifest.Build.Commit}/manifest.json is MISSING.");
        } else if (!File.ReadAllBytes(path: buildsManifestPath).AsSpan().SequenceEqual(other: manifestBytes)) {
            problems.Add(item: $"builds/{manifest.Build.Commit}/manifest.json is not byte-identical to {channel}/manifest.json.");
        }

        if (problems.Count == 0) {
            Console.Out.WriteLine(value: $"official verify: {channel} at commit {manifest.Build.Commit} — {sources.Count} source file(s), {manifest.Documents.Count} document(s), {manifest.Composed.Count} composed world(s), {manifest.Assets.Count} asset(s), {manifest.Engine.Files.Count} engine file(s) — all checks passed.");

            return 0;
        }

        Console.Error.WriteLine(value: $"official verify: {problems.Count} problem(s) found in '{manifestPath}'.");

        foreach (var problem in problems) {
            Console.Error.WriteLine(value: $"  {problem}");
        }

        return 1;
    }

    public static Command Create() {
        var treeOption = new Option<string>(name: "--tree") { Description = "The official tree's root.", Required = true };
        var channelOption = new Option<string>(name: "--channel") { Description = "The channel to verify.", Required = true };
        var expectCommitOption = new Option<string?>(name: "--expect-commit") { Description = "Refuse unless build.commit equals this." };
        var command = new Command(
            description: "Re-hash and re-check a puck.official.manifest.v1 tree's own claims, writing nothing.",
            name: "verify"
        ) { treeOption, channelOption, expectCommitOption };

        command.Detail(detail: """
            Checks: the manifest is a structurally valid puck.official.manifest.v1 document (every name distinct, every
            documents[].source naming a sources[] file, composed[].name naming a document); every document is named
            by its document name, and its source can author it — its own .world.json file, which it then is byte for
            byte, or a .puck source beside it; every object the manifest names re-hashes and re-sizes to what it
            claims; every document/composed pin recomputes to what WorldDefinitionFileSource.ComputeContentHash mints
            for that object's own bytes; every asset row's pin recomputes to its family's own canonical document hash;
            and builds/<commit>/manifest.json is byte-identical to the channel manifest. build.commit names the worlds
            tree the manifest was built from, so --expect-commit checks it against that tree's commit ("none" for a
            tree no commit holds); the world schema bundle's own x-puck.commit names the generator's build and is not
            compared with it.

            Exit codes: 0 every check passed, 1 one or more checks failed (every discrepancy named), 2 an unreadable
            path.
            """);

        command.SetAction(action: parseResult => Run(
            baseDirectory: parseResult.GetRequiredValue(option: treeOption),
            channel: parseResult.GetRequiredValue(option: channelOption),
            expectCommit: parseResult.GetValue(option: expectCommitOption)
        ));

        return command;
    }
}
