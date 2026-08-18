using Puck.Assets;
using Puck.Assets.Documents;
using Puck.Launcher.Release;

namespace Puck.Cli.PublishRelease;

// The `puck publish` verb: walks a built RID's output directory into a puck.release.v1 payload file list
// (hash+size per file via Puck.Assets.ContentAddressedStore) and writes the canonical, UNSIGNED manifest plus the
// payload objects into a release-source tree shaped exactly like DirectoryReleaseSource reads
// (<out>/<channel>/manifest.json, <out>/objects/sha256/<hex[0..2]>/<hex64>). Dry-run only — --sign (a live publish
// against a real signing custody path) is not this verb's job.
// Exit 0 wrote the tree, 2 usage error or an unreadable/unwritable path.
internal static class PublishCommand {
    private const string HelpText =
        """
        puck publish — write an unsigned puck.release.v1 release-source tree from a built RID's output

        Usage: puck publish --rid <rid> --input <dir> --out <dir> --app <id> --channel <channel> --version <version> [options]

        Required:
          --rid <rid>              the .NET runtime identifier this payload targets (e.g. win-x64)
          --input <dir>            the built output directory to walk into the payload file list
          --out <dir>              the release-source tree's root (created if missing)
          --app <id>               the application id (e.g. puck.world)
          --channel <channel>      the release channel (e.g. stable)
          --version <version>      the release's semantic version

        Options:
          --state-generation <n>   defaults to 0
          --minimum-supported <v>  defaults to unauthored (no floor)
          --rollout-percent <n>    defaults to 100
          --notes <text>           defaults to unauthored
          -h, --help               this text

        Writes <out>/<channel>/manifest.json (canonical, UNSIGNED — Signature is null) and every payload file under
        <out>/objects/sha256/<hex[0..2]>/<hex64>. A DirectoryReleaseSource rooted at <out> reads this tree as-is.

        Exit codes: 0 wrote the tree, 2 usage error or an unreadable/unwritable path.
        """;

    public static int Run(string[] args) {
        var scanner = new ArgScanner()
            .Flag(name: "h").Flag(name: "help")
            .Value(name: "rid").Value(name: "input").Value(name: "out")
            .Value(name: "app").Value(name: "channel").Value(name: "version")
            .Value(name: "state-generation").Value(name: "minimum-supported")
            .Value(name: "rollout-percent").Value(name: "notes");

        if (!scanner.Parse(args: args)) {
            Console.Error.WriteLine(value: $"publish: {scanner.Error}");

            return 2;
        }
        if (scanner.Has(name: "h") || scanner.Has(name: "help")) {
            Console.Out.WriteLine(value: HelpText);

            return 0;
        }

        var rid = scanner.Get(name: "rid");
        var input = scanner.Get(name: "input");
        var outputRoot = scanner.Get(name: "out");
        var app = scanner.Get(name: "app");
        var channel = scanner.Get(name: "channel");
        var version = scanner.Get(name: "version");
        var missing = new List<string>();

        if (rid is null) { missing.Add(item: "--rid"); }
        if (input is null) { missing.Add(item: "--input"); }
        if (outputRoot is null) { missing.Add(item: "--out"); }
        if (app is null) { missing.Add(item: "--app"); }
        if (channel is null) { missing.Add(item: "--channel"); }
        if (version is null) { missing.Add(item: "--version"); }

        if (missing.Count > 0) {
            Console.Error.WriteLine(value: $"publish: missing required argument(s): {string.Join(separator: ", ", values: missing)}.");

            return 2;
        }
        if (!Directory.Exists(path: input)) {
            Console.Error.WriteLine(value: $"publish: --input '{input}' does not exist.");

            return 2;
        }

        var stateGeneration = 0;

        if (scanner.Has(name: "state-generation") && !scanner.TryGetInt(name: "state-generation", value: out stateGeneration)) {
            Console.Error.WriteLine(value: $"publish: --state-generation '{scanner.Get(name: "state-generation")}' is not an integer.");

            return 2;
        }

        var rolloutPercent = 100;

        if (scanner.Has(name: "rollout-percent") && !scanner.TryGetInt(name: "rollout-percent", value: out rolloutPercent)) {
            Console.Error.WriteLine(value: $"publish: --rollout-percent '{scanner.Get(name: "rollout-percent")}' is not an integer.");

            return 2;
        }

        var objectsStore = new ContentAddressedStore(root: outputRoot!);
        var files = new List<ReleasePayloadFile>();
        var inputFullPath = Path.GetFullPath(path: input!);

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
            App: app!,
            Channel: channel!,
            MinimumSupported: scanner.Get(name: "minimum-supported"),
            Notes: scanner.Get(name: "notes"),
            Payloads: [new ReleasePayload(Files: files, Rid: rid!)],
            Revoked: null,
            Rollout: new ReleaseRollout(Percent: rolloutPercent),
            Schema: ReleaseManifest.CurrentSchema,
            Signature: null,
            StateGeneration: stateGeneration,
            Version: version!
        );

        CanonicalDocument<ReleaseManifest> canonical;

        try {
            canonical = ReleaseCanonicalizer.Canonicalize(document: manifest);
        } catch (DocumentValidationException exception) {
            Console.Error.WriteLine(value: $"publish: {exception.Message}");

            return 2;
        }

        var channelDirectory = Path.Combine(path1: outputRoot!, path2: channel!);

        Directory.CreateDirectory(path: channelDirectory);
        File.WriteAllBytes(path: Path.Combine(path1: channelDirectory, path2: "manifest.json"), bytes: canonical.Bytes);

        Console.Out.WriteLine(value: $"publish: wrote {app} {channel} {version} (unsigned, hash {canonical.Hash}) — {files.Count} file(s), rid {rid} — to {outputRoot}.");

        return 0;
    }
}
