using System.CommandLine;

using Puck.Assets.Documents;
using Puck.Launcher.Release;

namespace Puck.Cli.PublishRelease;

// The `puck publish` verb: walks a built RID's output directory into a puck.release.manifest.v1 payload file list
// (hash+size per file via Puck.Assets.ContentAddressedStore) and writes the canonical, UNSIGNED manifest plus the
// payload objects into a release-source tree shaped exactly like DirectoryReleaseSource reads
// (<output>/<channel>/manifest.json, <output>/objects/sha256/<hex[0..2]>/<hex64>). Signing and upload belong to
// `puck channel push`, which holds the root key through a key custody and writes the tree to a host.
// Exit 0 wrote the tree, 2 an unreadable/unwritable path.
public static class PublishCommand {
    private static int Run(string app, string channel, string input, string? minimumSupported, string? notes, string outputRoot, string rid, int rolloutPercent, int stateGeneration, string version,
        IReadOnlyList<string> excludes, string? bootstrap) {
        if (!Directory.Exists(path: input)) {
            Console.Error.WriteLine(value: $"publish: --input '{input}' does not exist.");

            return 2;
        }

        var excluded = excludes.Select(selector: static path => path.Replace(oldChar: '\\', newChar: '/').Trim('/')).Where(predicate: static path => path.Length > 0).ToArray();
        var manifest = new ReleaseManifest(
            App: app,
            Channel: channel,
            MinimumSupported: minimumSupported,
            Notes: notes,
            Payloads: [],
            Revoked: null,
            Rollout: new ReleaseRollout(Percent: rolloutPercent),
            Schema: ReleaseManifest.CurrentSchema,
            Signature: null,
            StateGeneration: stateGeneration,
            Version: version
        );

        CanonicalDocument<ReleaseManifest> canonical;
        var bootstrapped = 0;

        try {
            manifest = new DirectoryReleaseSource(root: outputRoot).Publish(input, rid, manifest, include: relative => !excluded.Any(predicate: prefix =>
                string.Equals(a: relative, b: prefix, comparisonType: StringComparison.Ordinal) || relative.StartsWith(value: (prefix + "/"), comparisonType: StringComparison.Ordinal)));
            canonical = ReleaseCanonicalizer.Canonicalize(document: manifest);
            if (bootstrap is not null) {
                bootstrapped = Bootstrap(channel: channel, from: Path.Combine(path1: input, path2: bootstrap), outputRoot: outputRoot, rid: rid);
            }
        } catch (Exception exception) when ((exception is DocumentValidationException or IOException or UnauthorizedAccessException)) {
            Console.Error.WriteLine(value: $"publish: {exception.Message}");

            return 2;
        }

        Console.Out.WriteLine(value: $"publish: wrote {app} {channel} {version} (unsigned, hash {canonical.Hash}) — {manifest.Payloads[0].Files.Count} file(s), {manifest.Payloads[0].Files.Sum(selector: static file => file.Size)} bytes, rid {rid}{((bootstrapped > 0) ? $", {bootstrapped} bootstrap file(s)" : string.Empty)} — to {outputRoot}.");

        return 0;
    }
    // A first install has no installer yet: the release's own bootstrap files (its installer) are published at a stable
    // path, <channel>/<rid>/<file>, which the install command downloads. They are copies of payload files, so the installer a
    // consumer downloads is the one the signed manifest names.
    private static int Bootstrap(string outputRoot, string channel, string rid, string from) {
        if (!Directory.Exists(path: from)) { throw new IOException(message: $"--bootstrap '{from}' does not exist."); }
        var target = Path.Combine(path1: outputRoot, path2: channel, path3: rid);
        var count = 0;

        Directory.CreateDirectory(path: target);
        foreach (var file in Directory.EnumerateFiles(path: from)) {
            Puck.Confinement.AtomicFile.WriteAllBytes(bytes: File.ReadAllBytes(path: file), path: Path.Combine(path1: target, path2: Path.GetFileName(path: file)));
            count++;
        }
        return count;
    }

    public static Command Create() {
        var appOption = new Option<string>(name: "--app") { Description = "The application id (e.g. puck.world).", Required = true };
        var channelOption = new Option<string>(name: "--channel") { Description = "The release channel (e.g. stable).", Required = true };
        var inputOption = new Option<string>(name: "--input") { Description = "The built output directory to walk into the payload file list.", Required = true };
        var minimumSupportedOption = new Option<string?>(name: "--minimum-supported") { Description = "The oldest version this release upgrades from. Absent leaves no floor." };
        var notesOption = new Option<string?>(name: "--notes") { Description = "The release notes. Absent leaves them unauthored." };
        var outputOption = CliOptions.Output(
            description: "The release-source tree's root, created if missing.",
            required: true
        );
        var ridOption = new Option<string>(name: "--rid") { Description = "The .NET runtime identifier this payload targets (e.g. win-x64).", Required = true };
        var rolloutPercentOption = CliOptions.Count(defaultValue: 100, description: "The percentage of clients this release rolls out to, 0 to 100.", maximum: 100, minimum: 0, name: "--rollout-percent");
        var stateGenerationOption = CliOptions.Count(defaultValue: 0, description: "The state generation this release's payload reads and writes.", minimum: 0, name: "--state-generation");
        var versionOption = new Option<string>(name: "--version") { Description = "The release's semantic version.", Required = true };
        var excludeOption = new Option<string[]>(name: "--exclude") { AllowMultipleArgumentsPerToken = true, Description = "A path under --input, a file or a directory, the payload leaves out (a player build's player-logs and cache/meshes-intermediates). Repeatable." };
        var bootstrapOption = new Option<string?>(name: "--bootstrap") { Description = "A directory under --input whose files are also written to <output>/<channel>/<rid>/, the stable path a first install downloads its installer from (a player build's launcher)." };
        var command = new Command(
            description: "Write an unsigned puck.release.manifest.v1 release-source tree from a built RID's output.",
            name: "publish"
        ) { appOption, channelOption, inputOption, minimumSupportedOption, notesOption, outputOption, ridOption, rolloutPercentOption, stateGenerationOption, versionOption, excludeOption, bootstrapOption };

        command.Detail(detail: """
            Writes <output>/<channel>/manifest.json (canonical, UNSIGNED: Signature is null) and every payload file under
            <output>/objects/sha256/<hex[0..2]>/<hex64>. A DirectoryReleaseSource rooted at <output> reads this tree as-is;
            `puck channel push` signs it and writes it to a host.

            Exit codes: 0 wrote the tree, 2 an unreadable/unwritable path.
            """);

        command.SetAction(action: parseResult => Run(
            app: parseResult.GetRequiredValue(option: appOption),
            bootstrap: parseResult.GetValue(option: bootstrapOption),
            channel: parseResult.GetRequiredValue(option: channelOption),
            excludes: parseResult.GetValue(option: excludeOption) ?? [],
            input: parseResult.GetRequiredValue(option: inputOption),
            minimumSupported: parseResult.GetValue(option: minimumSupportedOption),
            notes: parseResult.GetValue(option: notesOption),
            outputRoot: parseResult.GetRequiredValue(option: outputOption),
            rid: parseResult.GetRequiredValue(option: ridOption),
            rolloutPercent: parseResult.GetRequiredValue(option: rolloutPercentOption),
            stateGeneration: parseResult.GetRequiredValue(option: stateGenerationOption),
            version: parseResult.GetRequiredValue(option: versionOption)
        ));

        return command;
    }
}
