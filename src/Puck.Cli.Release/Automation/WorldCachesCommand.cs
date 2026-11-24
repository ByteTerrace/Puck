using System.CommandLine;
using System.Globalization;
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;
using Puck.Assets;
using Puck.Cli.Mesh;
using Puck.Launcher.Release;

namespace Puck.Cli.Automation;

/// <summary><c>puck world caches</c>: carries a player build's baked mesh store, which only a machine with a GPU can
/// produce, to the release pipeline that has none. <c>produce</c> packs a baked player folder's mesh sets, one cache set
/// per backend named by the build's mesh bake identity, and with <c>--push</c> uploads each as the asset of a GitHub
/// prerelease tagged <c>caches/&lt;backend&gt;-&lt;identity&gt;</c>; <c>fetch</c>, in the release job, finds the cache set
/// whose identity matches the release's own build, verifies every file's content pin and unpacks it into the folder, then
/// checks, with no device, that the set was produced for the groups the release's own world plans
/// (<see cref="WorldCachePlan"/>). A backend with no matching set, or whose set was produced for another world, holds the
/// channel's publish: the verb names it and exits non-zero, since a player never bakes.</summary>
/// <remarks>A cache set is a cache: its entries are keyed by the bake identity and each group's geometry, so a set baked
/// from an older world document only misses the groups that changed. The identity, never a commit, decides whether a set
/// fits: it is the hash of the bake's code and kernel bytecode the store keys sets by (<see cref="MeshBuildIdentity"/>),
/// equal on every machine that built the same bake from the same sources. The plan the set records is what decides whether
/// a matching set is whole.</remarks>
public static class WorldCachesCommand {
    private const string Verb = "world caches";
    /// <summary>The cache set's record inside its archive.</summary>
    public const string RecordName = "cache-set.json";
    /// <summary>The archive's file name, the one asset of a cache set's release.</summary>
    public const string AssetName = "cache-set.zip";
    private const string Schema = "puck.player-cache-set.v1";
    private const string TagPrefix = "caches/";
    private static readonly JsonSerializerOptions RecordFormat = new() { WriteIndented = true };
    private static readonly TimeSpan PollInterval = TimeSpan.FromMinutes(minutes: 1);

    /// <summary>Creates the <c>caches</c> verb and its <c>produce</c>, <c>status</c> and <c>fetch</c> subverbs.</summary>
    /// <param name="clock">The CLI host's clock, which a fetch's wait runs on.</param>
    /// <returns>The verb.</returns>
    public static Command Create(TimeProvider clock) => new(name: "caches", description: "Carry a player build's baked mesh store from a GPU machine to the release pipeline as identity-named cache sets.") {
        ProduceCommand(), StatusCommand(), FetchCommand(clock: clock),
    };

    /// <summary>Returns the release tag a backend's cache set is published under.</summary>
    /// <param name="backend">The backend token (<c>directx</c> or <c>vulkan</c>).</param>
    /// <param name="identity">The mesh bake identity.</param>
    /// <returns>The tag.</returns>
    public static string Tag(string backend, UInt128 identity) => $"{TagPrefix}{backend}-{identity.ToString(format: "x32", provider: CultureInfo.InvariantCulture)}";

    private static Command ProduceCommand() {
        var folder = new Argument<string>(name: "folder") { Description = "A player folder `puck world player` baked (its player.json complete)." };
        var output = CliOptions.Output(description: "Where the cache sets are written, one directory per backend. Absent, the folder's player-caches/.", required: false);
        var push = new Option<bool>(name: "--push") { Description = "Upload each cache set with gh: a prerelease tagged caches/<backend>-<identity>, created when missing, whose cache-set.zip asset is replaced." };
        var command = new Command(name: "produce", description: "Pack a baked player folder's mesh sets into one cache set per backend, named by the build's mesh bake identity; --push uploads them.") { folder, output, push };

        command.SetAction(action: async (parse, cancellationToken) => {
            var root = Path.GetFullPath(path: parse.GetRequiredValue(argument: folder));
            var target = Path.GetFullPath(path: (parse.GetValue(option: output) ?? Path.Combine(path1: root, path2: "player-caches")));

            return await ProduceAsync(cancellationToken: cancellationToken, folder: root, output: target, push: parse.GetValue(option: push)).ConfigureAwait(continueOnCapturedContext: false);
        });
        return command;
    }
    private static Command StatusCommand() {
        var world = new Argument<string>(name: "world") { Description = "The world a release would ship: an asset package folder, or a .puck source or .world.json document." };
        var backends = new Option<string[]>(name: "--backend") { AllowMultipleArgumentsPerToken = true, DefaultValueFactory = static _ => ["directx", "vulkan"], Description = "The backends a release ships, each needing its own set: directx and vulkan by default, as a Windows player build bakes." };
        var build = new Option<string?>(name: "--build") { Description = "The World build whose mesh bake identity names the sets (its Puck.SdfVm.dll's directory, with its bake kernels for each backend); absent, this tool's own build." };
        var command = new Command(name: "status", description: "Report, with no device and without downloading a set, whether a whole mesh cache set exists for a build and the world: the set named by the build's mesh bake identity, recorded as produced for the groups the world plans. Exit 1 names each backend that lacks one and what to run.") { world, backends, build };

        command.SetAction(action: async (parse, cancellationToken) =>
            await StatusAsync(backends: parse.GetValue(option: backends)!, build: Path.GetFullPath(path: (parse.GetValue(option: build) ?? AppContext.BaseDirectory)), cancellationToken: cancellationToken,
                world: Path.GetFullPath(path: parse.GetRequiredValue(argument: world))).ConfigureAwait(continueOnCapturedContext: false));
        return command;
    }
    private static Command FetchCommand(TimeProvider clock) {
        var folder = new Argument<string>(name: "folder") { Description = "A player folder assembled without baking (`puck world player --no-bake`)." };
        var notes = new Option<string?>(name: "--notes") { Description = "A file to write the release-notes line into: each backend's cache set, or, when the publish is held, why and what to run." };
        var from = new Option<string?>(name: "--from") { Description = "Read the cache sets from a directory `produce` wrote (<from>/<backend>-<identity>/cache-set.zip) instead of downloading them with gh: a producer in the same job, or a local check." };
        var wait = CliOptions.Count(defaultValue: 0, description: "Minutes to keep looking, once a minute, while a backend lacks a whole set, so a set the GPU machine is still producing for this build is taken when it lands; 0 looks once.", maximum: 720, minimum: 0, name: "--wait");
        var command = new Command(name: "fetch", description: "Find the cache set whose mesh bake identity matches the folder's build for each backend, verify its content pins, unpack it into cache/meshes and check it was produced for the world's bake groups; a backend with no whole set holds the publish (exit 1) and the verb names it.") { folder, notes, from, wait };

        command.SetAction(action: async (parse, cancellationToken) =>
            await FetchAsync(cancellationToken: cancellationToken, clock: clock, folder: Path.GetFullPath(path: parse.GetRequiredValue(argument: folder)), from: ((parse.GetValue(option: from) is { } directory) ? Path.GetFullPath(path: directory) : null),
                notes: parse.GetValue(option: notes), wait: TimeSpan.FromMinutes(minutes: parse.GetValue(option: wait))).ConfigureAwait(continueOnCapturedContext: false));
        return command;
    }

    private static IReadOnlyList<string> Backends(JsonNode record) =>
        [.. (record["backends"]?.AsArray() ?? []).Select(selector: static node => (string?)node).OfType<string>()];
    private static bool TryIdentity(string folder, string backend, out UInt128 identity, out string? why) {
        identity = default;
        if (!CliWorldDocuments.TryExtension(backend: backend, extension: out var extension, verb: Verb)) { why = $"names the unknown backend {backend}"; return false; }
        try {
            identity = MeshBuildIdentity.Read(buildDirectory: folder, extension: extension).Value;
            why = null;
            return true;
        } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or BadImageFormatException or InvalidOperationException) {
            why = $"has no readable {backend} mesh bake identity: {exception.Message}";
            return false;
        }
    }
    private static string SetDirectory(string folder, UInt128 identity) => Path.Combine(path1: folder, path2: PlayerBuildLayout.CacheDirectoryName, path3: PlayerBuildLayout.MeshesDirectoryName, path4: identity.ToString(format: "x32", provider: CultureInfo.InvariantCulture));
    private static string SetPrefix(UInt128 identity) => $"{PlayerBuildLayout.CacheDirectoryName}/{PlayerBuildLayout.MeshesDirectoryName}/{identity.ToString(format: "x32", provider: CultureInfo.InvariantCulture)}/";

    private static async Task<int> ProduceAsync(string folder, string output, bool push, CancellationToken cancellationToken) {
        if (!PlayerBuildLayout.TryReadComplete(folder: folder, record: out var record, why: out var refusal)) { return CliExit.Refuse(verb: Verb, what: CliPaths.ToDisplay(fullPath: folder), why: refusal!); }

        // The groups the folder's world plans, recorded so a release can tell whether it plans the same ones.
        if (!WorldCachePlan.TryRead(folder: folder, world: ((string?)record["world"] ?? string.Empty), keys: out var plan, why: out var unplanned)) {
            return CliExit.Refuse(verb: Verb, what: CliPaths.ToDisplay(fullPath: folder), why: $"cannot name the groups its world plans, because {unplanned}.");
        }

        var produced = new List<(string Tag, string Archive, int Files)>();

        foreach (var backend in Backends(record: record)) {
            if (!TryIdentity(backend: backend, folder: folder, identity: out var identity, why: out var why)) { return CliExit.Refuse(verb: Verb, what: CliPaths.ToDisplay(fullPath: folder), why: why!); }
            var set = SetDirectory(folder: folder, identity: identity);
            var meshes = (Directory.Exists(path: set) ? Directory.GetFiles(path: set, searchPattern: $"*{Puck.SdfVm.SdfMeshStore.Extension}") : []);

            if (meshes.Length == 0) {
                Console.Error.WriteLine(value: $"{Verb}: {backend}: the folder holds no meshes under its build's identity {identity:x32}; bake it with `puck world player` first.");
                continue;
            }
            Array.Sort(array: meshes, comparer: StringComparer.Ordinal);
            var tag = Tag(backend: backend, identity: identity);
            var directory = Path.Combine(path1: output, path2: $"{backend}-{identity:x32}");
            var archive = Path.Combine(path1: directory, path2: AssetName);
            var files = new JsonArray();

            Directory.CreateDirectory(path: directory);
            File.Delete(path: archive);
            using (var zip = ZipFile.Open(archiveFileName: archive, mode: ZipArchiveMode.Create)) {
                foreach (var mesh in meshes) {
                    var bytes = await File.ReadAllBytesAsync(cancellationToken: cancellationToken, path: mesh).ConfigureAwait(continueOnCapturedContext: false);
                    var path = (SetPrefix(identity: identity) + Path.GetFileName(path: mesh));

                    await using (var entry = zip.CreateEntry(compressionLevel: CompressionLevel.NoCompression, entryName: path).Open()) {
                        await entry.WriteAsync(buffer: bytes, cancellationToken: cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
                    }
                    files.Add(value: new JsonObject { ["path"] = path, ["hash"] = ContentPin.Compute(content: bytes).ToString(), ["size"] = bytes.LongLength });
                }
                var setRecord = new JsonObject {
                    ["schema"] = Schema,
                    ["backend"] = backend,
                    ["identity"] = identity.ToString(format: "x32", provider: CultureInfo.InvariantCulture),
                    ["commit"] = record["commit"]?.DeepClone(),
                    ["runtime"] = record["runtime"]?.DeepClone(),
                    ["targets"] = record["targets"]?.DeepClone(),
                    // What the producer's boots read of the finished store: each target's check settled having
                    // dispatched, baked and missed nothing, or the folder would not be complete.
                    ["checked"] = record["checked"]?.DeepClone(),
                    ["plan"] = WorldCachePlan.ToJson(keys: plan),
                    ["files"] = files,
                };

                await using (var recordEntry = zip.CreateEntry(compressionLevel: CompressionLevel.Optimal, entryName: RecordName).Open()) {
                    await JsonSerializer.SerializeAsync(cancellationToken: cancellationToken, utf8Json: recordEntry, value: setRecord, options: RecordFormat).ConfigureAwait(continueOnCapturedContext: false);
                }
                // The same record beside the archive, uploaded as its own small asset, so a check reads which world a set
                // was produced for without downloading the set (status).
                CliFiles.WriteJson(path: Path.Combine(path1: directory, path2: RecordName), value: setRecord);
            }
            Console.Out.WriteLine(value: string.Create(provider: CultureInfo.InvariantCulture, handler: $"{Verb}: {backend}: {meshes.Length} meshes under identity {identity:x32}, {new FileInfo(fileName: archive).Length} bytes, in {CliPaths.ToDisplay(fullPath: archive)} for {tag}."));
            produced.Add(item: (tag, archive, meshes.Length));
        }
        if (produced.Count == 0) { return CliExit.Refuse(verb: Verb, what: CliPaths.ToDisplay(fullPath: folder), why: "holds no baked meshes for any backend it names."); }
        if (!push) { return 0; }

        foreach (var (tag, archive, count) in produced) {
            var existing = await CliProcess.RunAsync(arguments: ["release", "view", tag, "--json", "tagName"], cancellationToken: cancellationToken, fileName: "gh").ConfigureAwait(continueOnCapturedContext: false);

            if (existing.ExitCode != 0) {
                if (!existing.Stderr.Contains(comparisonType: StringComparison.OrdinalIgnoreCase, value: "not found")) {
                    Console.Error.WriteLine(value: $"{Verb}: gh release view {tag} failed: {existing.Stderr.Trim()}");
                    return 1;
                }
                _ = await CliProcess.RunCheckedAsync(cancellationToken: cancellationToken, fileName: "gh",
                    arguments: ["release", "create", tag, "--prerelease", "--title", $"Player mesh caches {tag[TagPrefix.Length..]}",
                        "--notes", $"A player build's baked mesh store for one backend, named by its mesh bake identity. Produced by `puck world caches produce --push` from commit {record["commit"]}; the release pipeline's `puck world caches fetch` unpacks it into a release whose build has the same identity. A cache: never the truth, never committed."]).ConfigureAwait(continueOnCapturedContext: false);
            }
            // The archive first: a reader that finds the record finds the set it describes.
            _ = await CliProcess.RunCheckedAsync(arguments: ["release", "upload", tag, archive, "--clobber"], cancellationToken: cancellationToken, fileName: "gh").ConfigureAwait(continueOnCapturedContext: false);
            _ = await CliProcess.RunCheckedAsync(arguments: ["release", "upload", tag, Path.Combine(path1: Path.GetDirectoryName(path: archive)!, path2: RecordName), "--clobber"], cancellationToken: cancellationToken, fileName: "gh").ConfigureAwait(continueOnCapturedContext: false);
            Console.Out.WriteLine(value: $"{Verb}: pushed {count} meshes as {tag}.");
        }
        return 0;
    }

    // Downloads one asset of a cache set's release into a directory; returns the downloaded file, null when the release or
    // the asset does not exist, and throws on any other gh failure.
    private static async Task<string?> DownloadAssetAsync(string tag, string asset, string directory, CancellationToken cancellationToken) {
        var fetched = await CliProcess.RunAsync(arguments: ["release", "download", tag, "--pattern", asset, "--dir", directory, "--clobber"], cancellationToken: cancellationToken, fileName: "gh").ConfigureAwait(continueOnCapturedContext: false);
        var path = Path.Combine(path1: directory, path2: asset);

        if ((fetched.ExitCode != 0) && !fetched.Stderr.Contains(comparisonType: StringComparison.OrdinalIgnoreCase, value: "not found") && !fetched.Stderr.Contains(comparisonType: StringComparison.OrdinalIgnoreCase, value: "no assets")) {
            throw new IOException(message: $"gh release download {tag} failed: {fetched.Stderr.Trim()}");
        }
        return File.Exists(path: path) ? path : null;
    }
    // Judges a set whose identity matches against the world's plan: null when the set was produced for exactly the groups
    // the world plans, otherwise the line naming why it is not whole.
    private static string? Lacks(string backend, string tag, JsonNode? recorded, IReadOnlyList<RestGroupKey> planned) =>
        WorldCachePlan.Lacking(recorded: recorded, release: planned) switch {
            null => $"{backend}: {tag} matches this build's identity but records no plan of the world's groups, so it cannot be known to cover this world; produce it again",
            { Count: > 0 } lacking => $"{backend}: {tag} matches this build's identity but was produced for another world, so it lacks {WorldCachePlan.Describe(lacking: lacking, total: planned.Count)}",
            _ => null,
        };
    private static string Missing(string backend, UInt128 identity, string tag) => $"{backend}: no mesh cache set matches this build's identity {identity:x32} (looked for {tag})";

    private static async Task<int> StatusAsync(string world, string build, IReadOnlyList<string> backends, CancellationToken cancellationToken) {
        if (!WorldCachePlan.TryRead(folder: Environment.CurrentDirectory, world: world, keys: out var planned, why: out var unplanned)) {
            return CliExit.Refuse(verb: Verb, what: CliPaths.ToDisplay(fullPath: world), why: $"cannot name the groups it plans, because {unplanned}.");
        }
        var held = new List<string>();

        foreach (var backend in backends) {
            if (!TryIdentity(backend: backend, folder: build, identity: out var identity, why: out var why)) { return CliExit.Refuse(verb: Verb, what: CliPaths.ToDisplay(fullPath: build), why: why!); }
            var tag = Tag(backend: backend, identity: identity);
            using var download = RunDirectory.Create(keepOnFailure: false, prefix: "puck-cache-set-");
            var path = await DownloadAssetAsync(asset: RecordName, cancellationToken: cancellationToken, directory: download.Path, tag: tag).ConfigureAwait(continueOnCapturedContext: false);

            if (path is null) {
                held.Add(item: Missing(backend: backend, identity: identity, tag: tag));
                continue;
            }
            var record = JsonNode.Parse(json: await File.ReadAllTextAsync(cancellationToken: cancellationToken, path: path).ConfigureAwait(continueOnCapturedContext: false));

            if (Lacks(backend: backend, planned: planned, recorded: record?["plan"], tag: tag) is { } lacks) {
                held.Add(item: lacks);
                continue;
            }
            Console.Out.WriteLine(value: $"{Verb}: {backend}: {tag} was produced for this world's {planned.Count} bake groups.");
        }
        if (held.Count == 0) { return 0; }

        var text = MissingText(
            closing: "\nA release of this build holds the player channel until they exist. Produce them on the GPU machine before pushing `main`; the release's player job also waits for them:\n\n",
            headline: "## This build has no complete mesh cache set\n\n",
            held: held
        );

        CliGitHub.Summarize(message: text);
        // An annotation reaches the run's front page, where the lead sees it before pushing main.
        if (CliGitHub.IsActions) {
            foreach (var line in held) { Console.Out.WriteLine(value: $"::warning title=No complete mesh cache set::{line}"); }
        }
        return 1;
    }
    private static async Task<int> FetchAsync(string folder, string? from, string? notes, TimeSpan wait, TimeProvider clock, CancellationToken cancellationToken) {
        if (!PlayerBuildLayout.TryReadComplete(folder: folder, record: out var record, why: out var refusal)) { return CliExit.Refuse(verb: Verb, what: CliPaths.ToDisplay(fullPath: folder), why: refusal!); }

        var deadline = (clock.GetUtcNow() + wait);

        while (true) {
            var (exit, held, lines, outcomes) = await FetchOnceAsync(cancellationToken: cancellationToken, folder: folder, from: from, record: record).ConfigureAwait(continueOnCapturedContext: false);

            if (exit is { } failed) { return failed; }
            if ((held.Count > 0) && (clock.GetUtcNow() < deadline)) {
                Console.Out.WriteLine(value: $"{Verb}: {string.Join(separator: "; ", values: held)}; looking again each minute until {deadline:u}.");
                await Task.Delay(delay: PollInterval, timeProvider: clock, cancellationToken: cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
                continue;
            }
            return await FinishFetchAsync(cancellationToken: cancellationToken, folder: folder, held: held, lines: lines, notes: notes, outcomes: outcomes, record: record).ConfigureAwait(continueOnCapturedContext: false);
        }
    }
    // One look for every backend's set: the exit code of a failure that holds no matter how long it waits, or the held lines,
    // the hit lines and each backend's outcome.
    private static async Task<(int? Exit, List<string> Held, List<string> Lines, JsonObject Outcomes)> FetchOnceAsync(string folder, string? from, JsonNode record, CancellationToken cancellationToken) {
        var lines = new List<string>();
        var held = new List<string>();
        var outcomes = new JsonObject();
        IReadOnlyList<RestGroupKey>? planned = null;

        foreach (var backend in Backends(record: record)) {
            if (!TryIdentity(backend: backend, folder: folder, identity: out var identity, why: out var why)) { return (CliExit.Refuse(verb: Verb, what: CliPaths.ToDisplay(fullPath: folder), why: why!), held, lines, outcomes); }
            var tag = Tag(backend: backend, identity: identity);
            using var download = RunDirectory.Create(keepOnFailure: false, prefix: "puck-cache-set-");
            string archive;

            if (from is null) {
                try {
                    archive = (await DownloadAssetAsync(asset: AssetName, cancellationToken: cancellationToken, directory: download.Path, tag: tag).ConfigureAwait(continueOnCapturedContext: false)) ?? Path.Combine(path1: download.Path, path2: AssetName);
                } catch (IOException failure) {
                    Console.Error.WriteLine(value: $"{Verb}: {failure.Message}");
                    return (1, held, lines, outcomes);
                }
            } else {
                archive = Path.Combine(path1: from, path2: $"{backend}-{identity:x32}", path3: AssetName);
            }
            if (!File.Exists(path: archive)) {
                held.Add(item: Missing(backend: backend, identity: identity, tag: tag));
                outcomes[backend] = new JsonObject { ["identity"] = identity.ToString(format: "x32", provider: CultureInfo.InvariantCulture), ["meshes"] = 0, ["held"] = "no set matches the identity" };
                continue;
            }

            var (count, targets, error) = Unpack(archive: archive, backend: backend, folder: folder, identity: identity, plan: out var recorded);

            if (error is not null) {
                Console.Error.WriteLine(value: $"{Verb}: {tag}: {error}; nothing of it was kept.");
                return (1, held, lines, outcomes);
            }
            if ((planned is null) && !WorldCachePlan.TryRead(folder: folder, world: ((string?)record["world"] ?? string.Empty), keys: out planned, why: out var unplanned)) {
                Console.Error.WriteLine(value: $"{Verb}: {tag}: cannot tell whether the set was produced for this build's world, because {unplanned}; a set that is not known to be whole does not ship.");
                return (1, held, lines, outcomes);
            }

            var identityText = identity.ToString(format: "x32", provider: CultureInfo.InvariantCulture);

            if (Lacks(backend: backend, planned: planned, recorded: recorded, tag: tag) is { } lacks) {
                held.Add(item: lacks);
                outcomes[backend] = new JsonObject { ["identity"] = identityText, ["meshes"] = count, ["tag"] = tag, ["groups"] = planned.Count, ["held"] = "the set was not produced for this world's groups" };
                continue;
            }

            var hit = $"{backend}: {count} meshes from {tag}, produced for this world's {planned.Count} bake groups (baked for {(string.IsNullOrEmpty(value: targets) ? "the producer's targets" : targets)})";

            Console.Out.WriteLine(value: $"{Verb}: {hit}.");
            lines.Add(item: hit);
            outcomes[backend] = new JsonObject { ["identity"] = identityText, ["meshes"] = count, ["tag"] = tag, ["targets"] = targets, ["groups"] = planned.Count, ["missing"] = 0 };
        }
        return (null, held, lines, outcomes);
    }
    private static async Task<int> FinishFetchAsync(string folder, JsonNode record, List<string> held, List<string> lines, JsonObject outcomes, string? notes, CancellationToken cancellationToken) {
        record["caches"] = outcomes;
        CliFiles.WriteJson(path: Path.Combine(path1: folder, path2: PlayerBuildLayout.RecordName), value: record);
        if (held.Count == 0) {
            if (notes is not null) {
                await File.WriteAllTextAsync(cancellationToken: cancellationToken, contents: ("Mesh caches: " + string.Join(separator: "; ", values: lines) + "."), path: notes).ConfigureAwait(continueOnCapturedContext: false);
            }
            return 0;
        }

        var hold = MissingText(
            closing: "\nThe channel keeps its last complete release. To release this build, produce its sets on the GPU machine, then run the release again:\n\n",
            headline: "## The player channel's publish is held for this build\n\n",
            held: held
        );

        if (notes is not null) { await File.WriteAllTextAsync(cancellationToken: cancellationToken, contents: hold, path: notes).ConfigureAwait(continueOnCapturedContext: false); }
        if (File.Exists(path: Path.Combine(path1: folder, path2: "README.md"))) {
            await File.AppendAllTextAsync(cancellationToken: cancellationToken, contents: ("\n" + hold), path: Path.Combine(path1: folder, path2: "README.md")).ConfigureAwait(continueOnCapturedContext: false);
        }
        CliGitHub.Summarize(message: hold);
        Console.Error.WriteLine(value: $"{Verb}: held, so the player channel publishes nothing for this build ({held.Count} of {outcomes.Count} backend(s) lack a complete cache set).");
        return 1;
    }
    // The text a held build (its README, notes file, job summary and error) or a status check carries: what is missing and
    // what the lead runs, since a player never cold-bakes and a consumer release therefore waits for a complete set.
    private static string MissingText(string headline, IReadOnlyList<string> held, string closing) =>
        headline +
        "A player never bakes meshes, so a release ships only with a mesh cache set produced for this build's bake identity and for the groups its world plans, for each backend it ships. This build has none for:\n\n" +
        string.Concat(values: held.Select(selector: static line => $"- {line}\n")) +
        closing +
        "```text\n" +
        "puck world player worlds/debug --output <folder> --target rtx2060@1920x1080 --target rtx4070@2560x1440\n" +
        "puck world caches produce <folder> --push\n" +
        "```\n";
    // Every entry the archive holds must be the record or a file the record lists under the identity's own set, and every
    // listed file must match its content pin and size; the set lands only once the whole archive verified, so a damaged or
    // foreign archive leaves the folder as it was.
    // The plan is the record's list of the world's groups the set was produced for, null when the record holds none.
    private static (int Count, string Targets, string? Error) Unpack(string archive, string backend, string folder, UInt128 identity, out JsonNode? plan) {
        plan = null;
        using var zip = ZipFile.OpenRead(archiveFileName: archive);
        var recordEntry = zip.GetEntry(entryName: RecordName);

        if (recordEntry is null) { return (0, string.Empty, $"the archive holds no {RecordName}"); }
        JsonNode? record;

        using (var stream = recordEntry.Open()) {
            try {
                record = JsonNode.Parse(utf8Json: stream);
            } catch (JsonException exception) {
                return (0, string.Empty, $"its {RecordName} does not parse: {exception.Message}");
            }
        }
        if (((string?)record?["schema"] != Schema) || ((string?)record?["backend"] != backend) ||
            ((string?)record?["identity"] != identity.ToString(format: "x32", provider: CultureInfo.InvariantCulture))) {
            return (0, string.Empty, $"its {RecordName} is not a {Schema} for {backend} identity {identity:x32}");
        }
        plan = record!["plan"];
        var prefix = SetPrefix(identity: identity);
        var listed = new Dictionary<string, (ContentPin Pin, long Size)>(comparer: StringComparer.Ordinal);

        foreach (var file in record["files"]?.AsArray() ?? []) {
            var path = (string?)file?["path"];
            var name = (path?.StartsWith(value: prefix, comparisonType: StringComparison.Ordinal) == true) ? path[prefix.Length..] : null;

            if ((name is null) || (name.Length == 0) || name.Contains(value: '/') || name.Contains(value: '\\') || (name == "..") || !name.EndsWith(value: Puck.SdfVm.SdfMeshStore.Extension, comparisonType: StringComparison.Ordinal) ||
                !ContentPin.TryParse(pin: out var pin, text: (string?)file?["hash"]) || ((long?)file?["size"] is not { } size) || !listed.TryAdd(key: path!, value: (pin, size))) {
                return (0, string.Empty, $"its {RecordName} lists the entry '{path}', which is not one mesh of the set");
            }
        }
        if (zip.Entries.Count != (listed.Count + 1)) { return (0, string.Empty, "the archive holds entries its record does not list"); }

        var staging = (SetDirectory(folder: folder, identity: identity) + ".fetching");

        if (Directory.Exists(path: staging)) { Directory.Delete(path: staging, recursive: true); }
        Directory.CreateDirectory(path: staging);
        try {
            foreach (var (path, (pin, size)) in listed) {
                var entry = zip.GetEntry(entryName: path);

                if ((entry is null) || (entry.Length != size)) { return (0, string.Empty, $"the entry '{path}' is missing or not {size} bytes"); }
                using var stream = entry.Open();
                using var buffer = new MemoryStream(capacity: (int)size);

                stream.CopyTo(destination: buffer);
                var bytes = buffer.ToArray();

                if (ContentPin.Compute(content: bytes) != pin) { return (0, string.Empty, $"the entry '{path}' does not match its content pin {pin}"); }
                File.WriteAllBytes(bytes: bytes, path: Path.Combine(path1: staging, path2: path[prefix.Length..]));
            }
            var set = SetDirectory(folder: folder, identity: identity);

            if (Directory.Exists(path: set)) { Directory.Delete(path: set, recursive: true); }
            Directory.Move(destDirName: set, sourceDirName: staging);
        } finally {
            if (Directory.Exists(path: staging)) { Directory.Delete(path: staging, recursive: true); }
        }
        var targets = string.Join(separator: ", ", values: (record["targets"]?.AsArray() ?? []).Select(selector: static node => (string?)node).OfType<string>());

        return (listed.Count, targets, null);
    }
}
