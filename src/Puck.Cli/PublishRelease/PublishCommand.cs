using System.CommandLine;

using Puck.Assets;
using Puck.Assets.Documents;
using Puck.Launcher.Release;

namespace Puck.Cli.PublishRelease;

// The `puck publish` verb: walks a built RID's output directory into a puck.release.v1 payload file list
// (hash+size per file via Puck.Assets.ContentAddressedStore) and writes the canonical, UNSIGNED manifest plus the
// payload objects into a release-source tree shaped exactly like DirectoryReleaseSource reads
// (<out>/<channel>/manifest.json, <out>/objects/sha256/<hex[0..2]>/<hex64>). Dry-run only — --sign (a live publish
// against a real signing custody path) is not this verb's job.
// Exit 0 wrote the tree, 2 an unreadable/unwritable path.
internal static class PublishCommand {
    public static Command Create() {
        var appOption = new Option<string>(name: "--app") { Description = "The application id (e.g. puck.world).", Required = true };
        var channelOption = new Option<string>(name: "--channel") { Description = "The release channel (e.g. stable).", Required = true };
        var inputOption = new Option<string>(name: "--input") { Description = "The built output directory to walk into the payload file list.", Required = true };
        var minimumSupportedOption = new Option<string?>(name: "--minimum-supported") { Description = "The oldest version this release upgrades from. Absent leaves no floor." };
        var notesOption = new Option<string?>(name: "--notes") { Description = "The release notes. Absent leaves them unauthored." };
        var outOption = new Option<string>(name: "--out") { Description = "The release-source tree's root, created if missing.", Required = true };
        var ridOption = new Option<string>(name: "--rid") { Description = "The .NET runtime identifier this payload targets (e.g. win-x64).", Required = true };
        var rolloutPercentOption = new Option<int>(name: "--rollout-percent") { DefaultValueFactory = _ => 100, Description = "The percentage of clients this release rolls out to." };
        var stateGenerationOption = new Option<int>(name: "--state-generation") { DefaultValueFactory = _ => 0, Description = "The state generation this release's payload reads and writes." };
        var versionOption = new Option<string>(name: "--version") { Description = "The release's semantic version.", Required = true };
        var command = new Command(description: """
            Write an unsigned puck.release.v1 release-source tree from a built RID's output.

            Writes <out>/<channel>/manifest.json (canonical, UNSIGNED — Signature is null) and every payload file under
            <out>/objects/sha256/<hex[0..2]>/<hex64>. A DirectoryReleaseSource rooted at <out> reads this tree as-is.

            Exit codes: 0 wrote the tree, 2 an unreadable/unwritable path.
            """, name: "publish") { appOption, channelOption, inputOption, minimumSupportedOption, notesOption, outOption, ridOption, rolloutPercentOption, stateGenerationOption, versionOption };

        command.SetAction(action: parseResult => Run(app: parseResult.GetRequiredValue(option: appOption), channel: parseResult.GetRequiredValue(option: channelOption), input: parseResult.GetRequiredValue(option: inputOption), minimumSupported: parseResult.GetValue(option: minimumSupportedOption), notes: parseResult.GetValue(option: notesOption), outputRoot: parseResult.GetRequiredValue(option: outOption), rid: parseResult.GetRequiredValue(option: ridOption), rolloutPercent: parseResult.GetRequiredValue(option: rolloutPercentOption), stateGeneration: parseResult.GetRequiredValue(option: stateGenerationOption), version: parseResult.GetRequiredValue(option: versionOption)));

        return command;
    }

    private static int Run(string app, string channel, string input, string? minimumSupported, string? notes, string outputRoot, string rid, int rolloutPercent, int stateGeneration, string version) {
        if (!Directory.Exists(path: input)) {
            Console.Error.WriteLine(value: $"publish: --input '{input}' does not exist.");

            return 2;
        }

        var objectsStore = new ContentAddressedStore(root: outputRoot);
        var files = new List<ReleasePayloadFile>();
        var inputFullPath = Path.GetFullPath(path: input);

        foreach (var filePath in Directory.EnumerateFiles(path: inputFullPath, searchOption: SearchOption.AllDirectories, searchPattern: "*")) {
            var bytes = File.ReadAllBytes(path: filePath);
            var hash = objectsStore.Put(content: bytes);
            var relativePath = Path.GetRelativePath(path: filePath, relativeTo: inputFullPath).Replace(newChar: '/', oldChar: Path.DirectorySeparatorChar);

            files.Add(item: new ReleasePayloadFile(Hash: hash, Path: relativePath, Size: bytes.Length));
        }

        if (files.Count == 0) {
            Console.Error.WriteLine(value: $"publish: --input '{input}' contains no files.");

            return 2;
        }

        var manifest = new ReleaseManifest(
            App: app,
            Channel: channel,
            MinimumSupported: minimumSupported,
            Notes: notes,
            Payloads: [new ReleasePayload(Files: files, Rid: rid)],
            Revoked: null,
            Rollout: new ReleaseRollout(Percent: rolloutPercent),
            Schema: ReleaseManifest.CurrentSchema,
            Signature: null,
            StateGeneration: stateGeneration,
            Version: version
        );

        CanonicalDocument<ReleaseManifest> canonical;

        try {
            canonical = ReleaseCanonicalizer.Canonicalize(document: manifest);
        } catch (DocumentValidationException exception) {
            Console.Error.WriteLine(value: $"publish: {exception.Message}");

            return 2;
        }

        var channelDirectory = Path.Combine(path1: outputRoot, path2: channel);

        Directory.CreateDirectory(path: channelDirectory);
        File.WriteAllBytes(path: Path.Combine(path1: channelDirectory, path2: "manifest.json"), bytes: canonical.Bytes);

        Console.Out.WriteLine(value: $"publish: wrote {app} {channel} {version} (unsigned, hash {canonical.Hash}) — {files.Count} file(s), rid {rid} — to {outputRoot}.");

        return 0;
    }
}
