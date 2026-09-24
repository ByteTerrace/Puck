using System.CommandLine;

using Puck.Assets.Documents;
using Puck.Cli.Registry;
using Puck.Cli.Schema;
using Puck.Hosting;
using Puck.Launcher.Release;
using Puck.World;

namespace Puck.Cli.Official;

// The `puck official build` verb: writes a puck.official.manifest.v1 tree from the shipped browser-wasm engine, the
// authoring workspace under the worlds directory, the world documents it authors, the one composed root world, and
// the off-disk assets it references. No upload, no signing, no GitHub workflow — those are a separate, later concern.
// The manifest's build.commit and build.dirty name the worlds tree it was built from (TryReadTree). Exit 0 wrote the
// tree, 2 a refusal (ledger drift, a worlds tree that differs from its commit or has none without --allow-dirty, a
// composition or asset-hash mismatch) or an unreadable/unwritable path.
internal static class OfficialBuildCommand {
    // The porcelain-output interpretation alone, pulled out of TryReadTree so it is testable without shelling out to
    // git: `git status --porcelain` prints one line per changed, untracked or ignored path and nothing at all for a
    // clean tree.
    internal static bool IsDirtyPorcelainOutput(string porcelainStdout) => (porcelainStdout.Trim().Length > 0);

    private static int Refuse(string why) => CliExit.Refuse(
        verb: "official build",
        what: "refused",
        why: why
    );
    private static int Run(bool allowDirty, string channel, string engineDirectory, string treeRoot, string? worlds) {
        if (!CliPaths.TryGetRepositoryRoot(repositoryRoot: out var repositoryRoot)) {
            return 2;
        }

        var worldsDirectory = Path.GetFullPath(path: (worlds ?? Path.Combine(paths: [repositoryRoot, "src", "Puck.World", "Assets", "worlds"])));

        if (!Directory.Exists(path: worldsDirectory)) {
            return CliExit.Refuse(
                verb: "official build",
                what: CliPaths.ToDisplay(fullPath: worldsDirectory),
                why: "the worlds directory does not exist"
            );
        }

        Console.Out.WriteLine(value: "official build: checking generated ledgers (puck schema --check, puck registry --check)...");

        if (SchemaCommand.Create().Parse(args: ["--check"]).Invoke() != 0) {
            return Refuse(why: "'puck schema --check' found drift; regenerate with 'puck schema' first.");
        }

        if (RegistryCommand.Create().Parse(args: ["--check"]).Invoke() != 0) {
            return Refuse(why: "'puck registry --check' found drift; regenerate with 'puck registry' first.");
        }

        if (!TryReadTree(
            commit: out var commit,
            dirty: out var dirty,
            refusal: out var treeRefusal,
            worldsDirectory: worldsDirectory
        )) {
            return Refuse(why: treeRefusal!);
        }

        if (
            dirty &&
            !allowDirty
        ) {
            return Refuse(why: ((commit == OfficialBuildInfo.NoCommit)
                ? $"no commit holds the worlds directory {CliPaths.ToDisplay(fullPath: worldsDirectory)}; pass --allow-dirty for a local dry run"
                : $"the worlds directory {CliPaths.ToDisplay(fullPath: worldsDirectory)} differs from commit {commit}; pass --allow-dirty for a local dry run"
            ));
        }

        Directory.CreateDirectory(path: treeRoot);

        var writer = new OfficialObjectWriter(root: treeRoot);

        try {
            var (bundle, generator, worldSchemaId) = OfficialSchemaBundle.Build(repositoryRoot: repositoryRoot);
            var bundleBytes = System.Text.Encoding.UTF8.GetBytes(s: WorldSchema.ToCanonicalText(node: bundle));

            var (bundlePath, bundleHash, bundleSize) = writer.Put(bytes: bundleBytes);
            var worldSchemaBundle = new OfficialObjectRef(
                ContentType: "application/json",
                Hash: bundleHash,
                Path: bundlePath,
                Size: bundleSize
            );

            if (!OfficialWorldDocumentScanner.TryScan(
                composed: out var composed,
                composedDefinition: out var definition,
                documents: out var documents,
                reason: out var scanReason,
                sources: out var sources,
                worldsDirectory: worldsDirectory,
                writer: writer
            )) {
                return Refuse(why: scanReason!);
            }

            if (!OfficialAssetResolver.TryResolve(
                assets: out var assets,
                definition: definition!,
                reason: out var assetReason,
                writer: writer
            )) {
                return Refuse(why: assetReason!);
            }

            if (!OfficialEngineScanner.TryScan(
                appBundleDirectory: engineDirectory,
                engine: out var engine,
                reason: out var engineReason,
                writer: writer
            )) {
                return Refuse(why: engineReason!);
            }

            var manifest = new OfficialManifest(
                Assets: assets,
                Build: new OfficialBuildInfo(
                    Commit: commit,
                    Dirty: dirty,
                    Generator: generator,
                    WorldSchema: worldSchemaId
                ),
                Channel: channel,
                Composed: composed,
                Documents: documents,
                Engine: engine!,
                Schema: OfficialManifest.CurrentSchema,
                Signature: null,
                Sources: sources,
                WorldSchemaBundle: worldSchemaBundle
            );

            CanonicalDocument<OfficialManifest> canonical;

            try {
                canonical = OfficialCanonicalizer.Canonicalize(document: manifest);
            } catch (DocumentValidationException exception) {
                return Refuse(why: exception.Message);
            }

            var buildsDirectory = Path.Combine(
                path1: treeRoot,
                path2: "builds",
                path3: commit
            );

            Directory.CreateDirectory(path: buildsDirectory);
            File.WriteAllBytes(
                path: Path.Combine(
                    path1: buildsDirectory,
                    path2: "manifest.json"
                ),
                bytes: canonical.Bytes
            );

            var channelDirectory = Path.Combine(
                path1: treeRoot,
                path2: channel
            );

            Directory.CreateDirectory(path: channelDirectory);
            File.WriteAllBytes(
                path: Path.Combine(
                    path1: channelDirectory,
                    path2: "manifest.json"
                ),
                bytes: canonical.Bytes
            );

            Console.Out.WriteLine(value: $"official build: wrote {channel} at commit {commit} (dirty={dirty}) — {sources.Count} source file(s), {documents.Count} document(s), {composed.Count} composed world(s), {assets.Count} asset(s), {engine!.Files.Count} engine file(s), manifest hash {canonical.Hash} — to {treeRoot}.");

            return 0;
        } catch (InvalidDataException exception) {
            return Refuse(why: exception.Message);
        }
    }
    // Runs git against the worlds directory; a git that cannot start is a refusal rather than an answer.
    private static bool TryRunGit(string worldsDirectory, string[] arguments, out ChildProcessResult result, out string? refusal) {
        result = default;
        refusal = null;

        try {
            result = CliGit.Run(
                arguments: arguments,
                repository: worldsDirectory
            );
        } catch (Exception exception) when ((exception is System.ComponentModel.Win32Exception or InvalidOperationException)) {
            refusal = $"cannot run 'git {string.Join(separator: ' ', values: arguments)}' to read the worlds tree: {exception.Message}";

            return false;
        }

        return true;
    }

    // The tree the build names: the HEAD commit of the git checkout the worlds directory is read from, and whether the
    // worlds directory differs from it. A checkout is marked by a `.git` entry (a directory, or the file a linked
    // worktree carries) in the worlds directory or above it; with none, or with a HEAD that names no commit yet, the
    // commit is OfficialBuildInfo.NoCommit and the tree counts as differing, since no commit holds what was read.
    // Inside a checkout git must answer, and one that cannot is a refusal, never a guess. "Differs" covers everything
    // git status reports under the worlds directory: a tracked file modified, staged or deleted, and an untracked or
    // ignored file, since the scan publishes every file there whatever git ignores. Changes elsewhere in the checkout
    // are not the worlds tree's.
    internal static bool TryReadTree(string worldsDirectory, out string commit, out bool dirty, out string? refusal) {
        commit = OfficialBuildInfo.NoCommit;
        dirty = true;
        refusal = null;

        if (RepositoryPaths.Ascend(
            probe: static directory => (Path.Exists(path: Path.Combine(
                path1: directory.FullName,
                path2: ".git"
            ))
                ? directory.FullName
                : null
            ),
            start: worldsDirectory
        ) is null) {
            return true;
        }

        string[] headArguments = ["rev-parse", "--verify", "--quiet", "HEAD^{commit}"];

        if (!TryRunGit(
            arguments: headArguments,
            refusal: out refusal,
            result: out var head,
            worldsDirectory: worldsDirectory
        )) {
            return false;
        }

        // `rev-parse --verify --quiet` exits 1, silently, for a HEAD that names no commit (a checkout with none yet).
        if (head.ExitCode == 1) {
            return true;
        }

        if (head.ExitCode != 0) {
            refusal = $"'git rev-parse --verify --quiet HEAD^{{commit}}' exited {head.ExitCode} in {CliPaths.ToDisplay(fullPath: worldsDirectory)}: {head.Stderr.Trim()}";

            return false;
        }

        string[] statusArguments = ["status", "--porcelain", "--untracked-files=all", "--ignored", "--", "."];

        if (!TryRunGit(
            arguments: statusArguments,
            refusal: out refusal,
            result: out var status,
            worldsDirectory: worldsDirectory
        )) {
            return false;
        }

        if (status.ExitCode != 0) {
            refusal = $"'git status --porcelain' exited {status.ExitCode} in {CliPaths.ToDisplay(fullPath: worldsDirectory)}: {status.Stderr.Trim()}";

            return false;
        }

        commit = head.Stdout.Trim();
        dirty = IsDirtyPorcelainOutput(porcelainStdout: status.Stdout);

        return true;
    }

    public static Command Create() {
        var allowDirtyOption = new Option<bool>(name: "--allow-dirty") { Description = "Proceed even though the worlds directory differs from its commit, or no commit holds it." };
        var channelOption = new Option<string>(name: "--channel") { Description = "The channel this build belongs to (e.g. dev, stable, next).", Required = true };
        var engineOption = new Option<string>(name: "--engine") { Description = "A browser-wasm AppBundle directory (main.mjs + _framework/).", Required = true };
        var treeOption = new Option<string>(name: "--tree") { Description = "The official tree's root, created if missing.", Required = true };
        var worldsOption = new Option<string?>(name: "--worlds") { Description = "The worlds directory. Absent, src/Puck.World/Assets/worlds under the repository root." };
        var command = new Command(
            description: "Write a puck.official.manifest.v1 tree from the shipped engine, the authoring workspace, world documents, and their assets.",
            name: "build"
        ) { allowDirtyOption, channelOption, engineOption, treeOption, worldsOption };

        command.Detail(detail: """
            Refuses unless `puck schema --check` and `puck registry --check` both pass in-process first — a document
            field added without regenerating its ledger never ships. Refuses by name on a composition failure or an
            asset row whose declared hash does not match its document's recomputed canonical hash.

            build.commit names the tree the worlds were read from: the HEAD commit of the git checkout holding the
            worlds directory, or "none" when no checkout holds it or its HEAD names no commit. build.dirty is true when
            git status reports anything under the worlds directory (a tracked file modified, staged or deleted, or an
            untracked or ignored file), and always with "none". A dirty tree is refused unless --allow-dirty is given.
            The world schema bundle's own x-puck.commit keeps naming the build of the generator that wrote it.

            Writes <tree>/<channel>/manifest.json, <tree>/builds/<commit>/manifest.json (an immutable copy), and every
            referenced object under <tree>/objects/sha256/<hex[0..2]>/<hex64> — objects already present (by hash) are
            left untouched. A DirectoryReleaseSource-shaped reader and `puck official serve`/`puck official verify` all
            read this tree as-is.

            Exit codes: 0 wrote the tree, 2 the build refused or a path was unreadable or unwritable.
            """);

        command.SetAction(action: parseResult => Run(
            allowDirty: parseResult.GetValue(option: allowDirtyOption),
            channel: parseResult.GetRequiredValue(option: channelOption),
            engineDirectory: parseResult.GetRequiredValue(option: engineOption),
            treeRoot: parseResult.GetRequiredValue(option: treeOption),
            worlds: parseResult.GetValue(option: worldsOption)
        ));

        return command;
    }
}
