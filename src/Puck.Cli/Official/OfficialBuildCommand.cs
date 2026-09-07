using Puck.Assets.Documents;
using Puck.Cli.Registry;
using Puck.Cli.Schema;
using Puck.Launcher.Release;
using Puck.World;

namespace Puck.Cli.Official;

// The `puck official build` verb: writes a puck.official.v1 tree from the shipped browser-wasm engine, every world
// document under the worlds directory, the one composed root world, and the off-disk assets it references. No
// upload, no signing, no GitHub workflow — those are a separate, later concern.
// Exit 0 wrote the tree, 1 the build refused (ledger drift, a dirty tree without --allow-dirty, a composition or
// asset-hash mismatch), 2 usage error or an unreadable/unwritable path.
internal static class OfficialBuildCommand {
    private const string HelpText =
        """
        puck official build — write a puck.official.v1 tree

        Usage: puck official build --out <dir> --channel <name> --engine <dir> [options]

        Required:
          --out <dir>       the official tree's root (created if missing)
          --channel <name>  the channel this build belongs to (e.g. dev, stable, next)
          --engine <dir>    a browser-wasm AppBundle directory (main.mjs + _framework/)

        Options:
          --worlds <dir>    the worlds directory (default: src/Puck.World/Assets/worlds)
          --allow-dirty     proceed even though the working tree carries uncommitted changes
          -h, --help        this text

        Refuses unless `puck schema --check` and `puck registry --check` both pass in-process first — a document
        field added without regenerating its ledger never ships. Refuses on a dirty working tree unless
        --allow-dirty is given. Refuses by name on a composition failure or an asset row whose declared hash does
        not match its document's recomputed canonical hash.

        Writes <out>/<channel>/manifest.json, <out>/builds/<commit>/manifest.json (an immutable copy), and every
        referenced object under <out>/objects/sha256/<hex[0..2]>/<hex64> — objects already present (by hash) are
        left untouched. A DirectoryReleaseSource-shaped reader and `puck official serve`/`puck official verify` all
        read this tree as-is.

        Exit codes: 0 wrote the tree, 1 the build refused, 2 usage error or an unreadable/unwritable path.
        """;

    public static int Run(string[] args) {
        var scanner = new ArgScanner()
            .Flag(name: "h").Flag(name: "help").Flag(name: "allow-dirty")
            .Value(name: "out").Value(name: "channel").Value(name: "engine").Value(name: "worlds");

        if (!scanner.Parse(args: args)) {
            Console.Error.WriteLine(value: $"official build: {scanner.Error}");

            return 2;
        }

        if (scanner.Has(name: "h") || scanner.Has(name: "help")) {
            Console.Out.WriteLine(value: HelpText);

            return 0;
        }

        var outRoot = scanner.Get(name: "out");
        var channel = scanner.Get(name: "channel");
        var engineDirectory = scanner.Get(name: "engine");
        var missing = new List<string>();

        if (outRoot is null) { missing.Add(item: "--out"); }
        if (channel is null) { missing.Add(item: "--channel"); }
        if (engineDirectory is null) { missing.Add(item: "--engine"); }

        if (missing.Count > 0) {
            Console.Error.WriteLine(value: $"official build: missing required argument(s): {string.Join(separator: ", ", values: missing)}.");

            return 2;
        }

        if (!CliPaths.TryGetRepositoryRoot(repositoryRoot: out var repositoryRoot)) {
            return 2;
        }

        var worldsDirectory = Path.GetFullPath(path: scanner.Get(name: "worlds") ?? Path.Combine(paths: [repositoryRoot, "src", "Puck.World", "Assets", "worlds"]));

        if (!Directory.Exists(path: worldsDirectory)) {
            Console.Error.WriteLine(value: $"official build: worlds directory '{worldsDirectory}' does not exist.");

            return 2;
        }

        Console.Out.WriteLine(value: "official build: checking generated ledgers (puck schema --check, puck registry --check)...");

        if (SchemaCommand.Run(args: ["--check"]) != 0) {
            Console.Error.WriteLine(value: "official build: refused — 'puck schema --check' found drift; regenerate with 'puck schema' first.");

            return 1;
        }

        if (RegistryCommand.Run(args: ["--check"]) != 0) {
            Console.Error.WriteLine(value: "official build: refused — 'puck registry --check' found drift; regenerate with 'puck registry' first.");

            return 1;
        }

        if (!TryCheckDirty(allowDirty: scanner.Has(name: "allow-dirty"), dirty: out var dirty, isRefusal: out var isDirtyRefusal, repositoryRoot: repositoryRoot, refusal: out var dirtyRefusal)) {
            Console.Error.WriteLine(value: $"official build: {dirtyRefusal}");

            return (isDirtyRefusal ? 1 : 2);
        }

        Directory.CreateDirectory(path: outRoot!);

        var writer = new OfficialObjectWriter(root: outRoot!);

        try {
            var (bundle, commit, generator, worldSchemaId) = OfficialSchemaBundle.Build(repositoryRoot: repositoryRoot);
            var bundleBytes = System.Text.Encoding.UTF8.GetBytes(s: WorldSchema.ToCanonicalText(node: bundle));
            var (bundlePath, bundleHash, bundleSize) = writer.Put(bytes: bundleBytes);
            var worldSchemaBundle = new OfficialObjectRef(ContentType: "application/json", Hash: bundleHash, Path: bundlePath, Size: bundleSize);

            if (!OfficialWorldDocumentScanner.TryScan(composed: out var composed, composedDefinition: out var definition, documents: out var documents, reason: out var scanReason, worldsDirectory: worldsDirectory, writer: writer)) {
                Console.Error.WriteLine(value: $"official build: refused — {scanReason}");

                return 1;
            }

            var worldProjectRoot = Path.GetFullPath(path: Path.Combine(path1: worldsDirectory, path2: "..", path3: ".."));

            if (!OfficialAssetResolver.TryResolve(assets: out var assets, definition: definition!, reason: out var assetReason, worldProjectRoot: worldProjectRoot, writer: writer)) {
                Console.Error.WriteLine(value: $"official build: refused — {assetReason}");

                return 1;
            }

            if (!OfficialEngineScanner.TryScan(appBundleDirectory: engineDirectory!, engine: out var engine, reason: out var engineReason, writer: writer)) {
                Console.Error.WriteLine(value: $"official build: refused — {engineReason}");

                return 1;
            }

            var manifest = new OfficialManifest(
                Assets: assets,
                Build: new OfficialBuildInfo(Commit: commit, Dirty: dirty, Generator: generator, WorldSchema: worldSchemaId),
                Channel: channel!,
                Composed: composed,
                Documents: documents,
                Engine: engine!,
                Schema: OfficialManifest.CurrentSchema,
                Signature: null,
                WorldSchemaBundle: worldSchemaBundle
            );

            CanonicalDocument<OfficialManifest> canonical;

            try {
                canonical = OfficialCanonicalizer.Canonicalize(document: manifest);
            } catch (DocumentValidationException exception) {
                Console.Error.WriteLine(value: $"official build: refused — {exception.Message}");

                return 1;
            }

            var buildsDirectory = Path.Combine(path1: outRoot!, path2: "builds", path3: commit);

            Directory.CreateDirectory(path: buildsDirectory);
            File.WriteAllBytes(path: Path.Combine(path1: buildsDirectory, path2: "manifest.json"), bytes: canonical.Bytes);

            var channelDirectory = Path.Combine(path1: outRoot!, path2: channel!);

            Directory.CreateDirectory(path: channelDirectory);
            File.WriteAllBytes(path: Path.Combine(path1: channelDirectory, path2: "manifest.json"), bytes: canonical.Bytes);

            Console.Out.WriteLine(value: $"official build: wrote {channel} at commit {commit} (dirty={dirty}) — {documents.Count} document(s), {composed.Count} composed world(s), {assets.Count} asset(s), {engine!.Files.Count} engine file(s), manifest hash {canonical.Hash} — to {outRoot}.");

            return 0;
        } catch (InvalidDataException exception) {
            Console.Error.WriteLine(value: $"official build: refused — {exception.Message}");

            return 1;
        }
    }
    // isRefusal distinguishes a business-level refusal (dirty tree, exit 1) from an environment problem (git
    // missing or erroring, exit 2) — the two callers of this check answer to different exit codes.
    private static bool TryCheckDirty(string repositoryRoot, bool allowDirty, out bool dirty, out bool isRefusal, out string? refusal) {
        dirty = false;
        isRefusal = false;
        refusal = null;

        CliRawProcessResult result;

        try {
            result = CliProcess.RunCapturedRaw(fileName: "git", arguments: ["-C", repositoryRoot, "status", "--porcelain"]);
        } catch (Exception exception) when ((exception is System.ComponentModel.Win32Exception or InvalidOperationException)) {
            refusal = $"cannot run 'git status --porcelain' to check for a dirty tree: {exception.Message}";

            return false;
        }

        if (result.ExitCode != 0) {
            refusal = $"'git status --porcelain' exited {result.ExitCode}: {result.Stderr.Trim()}";

            return false;
        }

        dirty = IsDirtyPorcelainOutput(porcelainStdout: result.Stdout);

        if (dirty && !allowDirty) {
            isRefusal = true;
            refusal = "refused — the working tree carries uncommitted changes; pass --allow-dirty for a local dry run.";

            return false;
        }

        return true;
    }
    // The porcelain-output interpretation alone, pulled out of TryCheckDirty so it is testable without shelling out
    // to git or depending on this checkout's own (shared, concurrently-mutated) working-tree state: `git status
    // --porcelain` prints one line per changed/untracked path and nothing at all for a clean tree.
    internal static bool IsDirtyPorcelainOutput(string porcelainStdout) => (porcelainStdout.Trim().Length > 0);
}
