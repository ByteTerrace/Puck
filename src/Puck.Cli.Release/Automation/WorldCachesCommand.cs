using System.CommandLine;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Puck.Assets;
using Puck.Cli.Channel;
using Puck.Cli.Mesh;
using Puck.Launcher.Release;

namespace Puck.Cli.Automation;

/// <summary><c>puck world caches</c>: carries a player build's baked mesh store, which only a machine with a GPU can
/// produce, to the release pipeline that has none, through the release channel's own store. <c>produce</c> writes a baked
/// player folder's meshes as content-addressed objects (<c>objects/sha256/…</c>, the objects every release on the channel
/// shares) and, for each backend, a cache set's manifest named by the build's mesh bake identity
/// (<c>caches/&lt;backend&gt;-&lt;identity&gt;/manifest.json</c>), then with <c>--host</c> or <c>--to</c> writes them through
/// a channel host, objects the store lacks first and manifests last. <c>status</c> reads only the manifests; <c>fetch</c>, in
/// the release job, reads the manifest whose identity matches the release's own build, checks with no device that the set
/// was produced for the groups the release's world plans (<see cref="WorldCachePlan"/>), and only then takes every mesh its
/// folder lacks, each verified against its pin. A backend with no matching set, or whose set was produced for another
/// world, holds the channel's publish: the verb names it and exits non-zero, since a player never bakes.</summary>
/// <remarks>A cache set is a cache: its entries are keyed by the bake identity and each group's geometry, so a set baked
/// from an older world document only misses the groups that changed. The identity, never a commit, decides whether a set
/// fits: it is the hash of the bake's code and kernel bytecode the store keys sets by (<see cref="MeshBuildIdentity"/>),
/// equal on every machine that built the same bake from the same sources. The plan the set records is what decides whether
/// a matching set is whole. A mesh is stored once by its bytes, so a new identity whose bake writes the same bytes, the
/// other backend's identical meshes, and the release that later ships the set all reuse the objects already stored.</remarks>
public static class WorldCachesCommand {
    private const string Verb = "world caches";
    private const string Schema = "puck.player-cache-set.v2";
    // A channel tree holds its cache sets beside its channels, each read as a channel's latest document is.
    private const string SetsDirectory = "caches";
    // A mesh the source fails to send is asked again this many times before the fetch fails: thousands of objects ride one
    // fetch, and one dropped connection should not fail the release.
    private const int FetchAttempts = 3;
    private static readonly TimeSpan PollInterval = TimeSpan.FromMinutes(minutes: 1);

    /// <summary>Creates the <c>caches</c> verb and its <c>produce</c>, <c>status</c> and <c>fetch</c> subverbs.</summary>
    /// <param name="clock">The CLI host's clock, which a fetch's wait runs on.</param>
    /// <returns>The verb.</returns>
    public static Command Create(TimeProvider clock) => new(name: "caches", description: "Carry a player build's baked mesh store from a GPU machine to the release pipeline as identity-named cache sets in the release channel's store.") {
        ProduceCommand(), StatusCommand(), FetchCommand(clock: clock),
    };

    /// <summary>Returns the name a backend's cache set is stored under in a channel tree: the directory of its manifest,
    /// read as a channel's latest document is (<see cref="IReleaseSource.TryGetLatestManifestAsync"/>).</summary>
    /// <param name="backend">The backend token (<c>directx</c> or <c>vulkan</c>).</param>
    /// <param name="identity">The mesh bake identity.</param>
    /// <returns>The set's name, <c>caches/&lt;backend&gt;-&lt;identity&gt;</c>.</returns>
    public static string SetName(string backend, UInt128 identity) => $"{SetsDirectory}/{backend}-{Hex(identity: identity)}";

    private static Command ProduceCommand() {
        var folder = new Argument<string>(name: "folder") { Description = "A player folder `puck world player` baked (its player.json complete)." };
        var output = CliOptions.Output(description: "Where the store's tree is staged: every mesh as an object (objects/sha256/…) and each set's manifest (caches/<backend>-<identity>/manifest.json). Absent, the folder's player-caches/.", required: false);
        var host = new Option<string?>(name: "--host") { Description = "Write the sets through this installed channel host (a deployment provider's, such as azure): every object the store lacks, then each set's manifest." };
        var to = new Option<string?>(name: "--to") { Description = "Write the sets into this directory's channel tree instead of a provider's host: a local store that status and fetch read with --store." };
        var command = new Command(name: "produce", description: "Write a baked player folder's meshes as content-addressed objects and one cache set manifest per backend, named by the build's mesh bake identity; --host or --to writes them to a store.") { folder, output, host, to };

        command.SetAction(action: async (parse, cancellationToken) => {
            var root = Path.GetFullPath(path: parse.GetRequiredValue(argument: folder));
            var target = Path.GetFullPath(path: (parse.GetValue(option: output) ?? Path.Combine(path1: root, path2: "player-caches")));
            var name = parse.GetValue(option: host);
            var directory = parse.GetValue(option: to);
            ReleaseChannelHost? store = null;

            if ((name is not null) && (directory is not null)) { return CliExit.Refuse(verb: Verb, what: "--host", why: "and --to both name a store; give one."); }
            if ((name is not null) || (directory is not null)) {
                try {
                    store = ReleaseChannelHost.Open(directory: ((directory is null) ? null : Path.GetFullPath(path: directory)), name: name);
                } catch (InvalidOperationException exception) {
                    return CliExit.Refuse(verb: Verb, what: "--host", why: exception.Message);
                }
            }
            return await ProduceAsync(cancellationToken: cancellationToken, folder: root, output: target, store: store).ConfigureAwait(continueOnCapturedContext: false);
        });
        return command;
    }
    private static Option<string> StoreOption() => new(name: "--store") {
        Description = "The store the cache sets live in: a release channel tree's root, as an https URL (the deployment's channel, such as <officialContentBaseUrl>/releases) or a directory `produce --to` wrote.",
        Required = true,
    };
    private static Command StatusCommand() {
        var world = new Argument<string>(name: "world") { Description = "The world a release would ship: an asset package folder, or a .puck source or .world.json document." };
        var backends = new Option<string[]>(name: "--backend") { AllowMultipleArgumentsPerToken = true, DefaultValueFactory = static _ => ["directx", "vulkan"], Description = "The backends a release ships, each needing its own set: directx and vulkan by default, as a Windows player build bakes." };
        var build = new Option<string?>(name: "--build") { Description = "The World build whose mesh bake identity names the sets (its Puck.SdfVm.dll's directory, with its bake kernels for each backend); absent, this tool's own build." };
        var store = StoreOption();
        var command = new Command(name: "status", description: "Report, with no device and without downloading a mesh, whether a whole mesh cache set exists in the store for a build and the world: the set named by the build's mesh bake identity, recorded as produced for the groups the world plans. Exit 1 names each backend that lacks one and what to run.") { world, backends, build, store };

        command.SetAction(action: async (parse, cancellationToken) =>
            await StatusAsync(backends: parse.GetValue(option: backends)!, build: Path.GetFullPath(path: (parse.GetValue(option: build) ?? AppContext.BaseDirectory)), cancellationToken: cancellationToken,
                store: parse.GetRequiredValue(option: store), world: Path.GetFullPath(path: parse.GetRequiredValue(argument: world))).ConfigureAwait(continueOnCapturedContext: false));
        return command;
    }
    private static Command FetchCommand(TimeProvider clock) {
        var folder = new Argument<string>(name: "folder") { Description = "A player folder assembled without baking (`puck world player --no-bake`)." };
        var notes = new Option<string?>(name: "--notes") { Description = "A file to write the release-notes line into: each backend's cache set, or, when the publish is held, why and what to run." };
        var store = StoreOption();
        var wait = CliOptions.Count(defaultValue: 0, description: "Minutes to keep looking, once a minute, while a backend lacks a whole set, so a set the GPU machine is still producing for this build is taken when it lands; 0 looks once.", maximum: 720, minimum: 0, name: "--wait");
        var command = new Command(name: "fetch", description: "Find the cache set whose mesh bake identity matches the folder's build for each backend, check it was produced for the world's bake groups, then take every mesh the folder lacks into cache/meshes, each verified against its pin; a backend with no whole set holds the publish (exit 1) and the verb names it.") { folder, notes, store, wait };

        command.SetAction(action: async (parse, cancellationToken) =>
            await FetchAsync(cancellationToken: cancellationToken, clock: clock, folder: Path.GetFullPath(path: parse.GetRequiredValue(argument: folder)), notes: parse.GetValue(option: notes),
                store: parse.GetRequiredValue(option: store), wait: TimeSpan.FromMinutes(minutes: parse.GetValue(option: wait))).ConfigureAwait(continueOnCapturedContext: false));
        return command;
    }

    private static string Hex(UInt128 identity) => identity.ToString(format: "x32", provider: CultureInfo.InvariantCulture);
    private static IReadOnlyList<string> Backends(JsonNode record) =>
        [.. (record["backends"]?.AsArray() ?? []).Select(selector: static node => (string?)node).OfType<string>()];
    internal static bool TryIdentity(string folder, string backend, out UInt128 identity, out string? why) {
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
    internal static string SetDirectory(string folder, UInt128 identity) => Path.Combine(path1: folder, path2: PlayerBuildLayout.CacheDirectoryName, path3: PlayerBuildLayout.MeshesDirectoryName, path4: Hex(identity: identity));
    private static IReleaseSource OpenStore(string store) => ChannelSubscription.OpenSource(httpClient: HttpReleaseSource.SharedClient, source: store);

    private static async Task<int> ProduceAsync(string folder, string output, ReleaseChannelHost? store, CancellationToken cancellationToken) {
        if (!PlayerBuildLayout.TryReadComplete(folder: folder, record: out var record, why: out var refusal)) { return CliExit.Refuse(verb: Verb, what: CliPaths.ToDisplay(fullPath: folder), why: refusal!); }

        // The groups the folder's world plans, recorded so a release can tell whether it plans the same ones.
        if (!WorldCachePlan.TryRead(folder: folder, world: ((string?)record["world"] ?? string.Empty), keys: out var plan, why: out var unplanned)) {
            return CliExit.Refuse(verb: Verb, what: CliPaths.ToDisplay(fullPath: folder), why: $"cannot name the groups its world plans, because {unplanned}.");
        }

        var objects = new ContentAddressedStore(root: output);
        var written = new SortedSet<string>(comparer: StringComparer.Ordinal);
        var manifests = new List<string>();

        foreach (var backend in Backends(record: record)) {
            if (!TryIdentity(backend: backend, folder: folder, identity: out var identity, why: out var why)) { return CliExit.Refuse(verb: Verb, what: CliPaths.ToDisplay(fullPath: folder), why: why!); }
            var set = SetDirectory(folder: folder, identity: identity);
            var meshes = (Directory.Exists(path: set) ? Directory.GetFiles(path: set, searchPattern: $"*{Puck.SdfVm.SdfMeshStore.Extension}") : []);

            if (meshes.Length == 0) {
                Console.Error.WriteLine(value: $"{Verb}: {backend}: the folder holds no meshes under its build's identity {Hex(identity: identity)}; bake it with `puck world player` first.");
                continue;
            }
            Array.Sort(array: meshes, comparer: StringComparer.Ordinal);
            var files = new JsonArray();
            long bytes = 0;

            foreach (var mesh in meshes) {
                var content = await File.ReadAllBytesAsync(cancellationToken: cancellationToken, path: mesh).ConfigureAwait(continueOnCapturedContext: false);
                var pin = objects.Put(content: content);

                _ = written.Add(item: ContentAddressedStore.ObjectRelativePath(pin: pin));
                files.Add(value: new JsonObject { ["name"] = Path.GetFileName(path: mesh), ["hash"] = pin.ToString(), ["size"] = content.LongLength });
                bytes += content.LongLength;
            }

            var name = SetName(backend: backend, identity: identity);

            CliFiles.WriteJson(path: Path.Combine(path1: output, path2: name, path3: ChannelSubscription.ManifestFileName), value: new JsonObject {
                ["schema"] = Schema,
                ["backend"] = backend,
                ["identity"] = Hex(identity: identity),
                ["commit"] = record["commit"]?.DeepClone(),
                ["runtime"] = record["runtime"]?.DeepClone(),
                ["targets"] = record["targets"]?.DeepClone(),
                // What the producer's boots read of the finished store: each target's check settled having dispatched,
                // baked and missed nothing, or the folder would not be complete.
                ["checked"] = record["checked"]?.DeepClone(),
                ["plan"] = WorldCachePlan.ToJson(keys: plan),
                ["files"] = files,
            });
            manifests.Add(item: $"{name}/{ChannelSubscription.ManifestFileName}");
            Console.Out.WriteLine(value: string.Create(provider: CultureInfo.InvariantCulture, handler: $"{Verb}: {backend}: {meshes.Length} meshes ({bytes} bytes) under identity {Hex(identity: identity)}, staged as {name}."));
        }
        if (manifests.Count == 0) { return CliExit.Refuse(verb: Verb, what: CliPaths.ToDisplay(fullPath: folder), why: "holds no baked meshes for any backend it names."); }
        Console.Out.WriteLine(value: $"{Verb}: {written.Count} distinct objects for {manifests.Count} set(s) in {CliPaths.ToDisplay(fullPath: output)}.");
        if (store is null) { return 0; }

        try {
            // The host writes every object before any manifest, so a reader that finds a set's manifest finds its meshes.
            await store.UploadAsync(cancellationToken: cancellationToken, documents: manifests, objects: [.. written], tree: output).ConfigureAwait(continueOnCapturedContext: false);
        } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or InvalidDataException) {
            Console.Error.WriteLine(value: $"{Verb}: writing to {store.Describe} failed: {exception.Message}");
            return 1;
        }
        Console.Out.WriteLine(value: $"{Verb}: wrote {string.Join(separator: ", ", values: manifests.Select(selector: static manifest => manifest[..manifest.LastIndexOf(value: '/')]))} to {store.Describe}.");
        return 0;
    }

    private sealed record SetFile(string Name, ContentPin Pin, long Size);
    private sealed record CacheSet(JsonNode? Plan, string Targets, IReadOnlyList<SetFile> Files);
    // Reads a backend's set manifest from the store: the set, or why there is none (Missing, which waiting may change) or
    // why the manifest cannot be one (Damaged, which waiting does not).
    private static async Task<(CacheSet? Set, string? Missing, string? Damaged)> ReadSetAsync(IReleaseSource store, string backend, UInt128 identity, CancellationToken cancellationToken) {
        var name = SetName(backend: backend, identity: identity);
        var read = await store.TryGetLatestManifestAsync(cancellationToken: cancellationToken, channel: name).ConfigureAwait(continueOnCapturedContext: false);

        if (!read.Found) { return (null, $"{backend}: no mesh cache set matches this build's identity {Hex(identity: identity)} ({read.RefusalReason})", null); }
        JsonNode? record;

        try {
            record = JsonNode.Parse(utf8Json: read.ManifestBytes);
        } catch (JsonException exception) {
            return (null, null, $"its manifest does not parse: {exception.Message}");
        }
        if (((string?)record?["schema"] != Schema) || ((string?)record?["backend"] != backend) || ((string?)record?["identity"] != Hex(identity: identity))) {
            return (null, null, $"its manifest is not a {Schema} for {backend} identity {Hex(identity: identity)}");
        }

        var files = new List<SetFile>();
        var names = new HashSet<string>(comparer: StringComparer.Ordinal);

        foreach (var file in record!["files"]?.AsArray() ?? []) {
            var fileName = (string?)file?["name"];

            // A name is one mesh file of the set's own directory, never a path out of it.
            if (string.IsNullOrEmpty(value: fileName) || fileName.Contains(value: '/') || fileName.Contains(value: '\\') || (fileName == "..") || !fileName.EndsWith(value: Puck.SdfVm.SdfMeshStore.Extension, comparisonType: StringComparison.Ordinal) ||
                !ContentPin.TryParse(pin: out var pin, text: (string?)file?["hash"]) || !((long?)file?["size"] is { } size and >= 0) || !names.Add(item: fileName)) {
                return (null, null, $"its manifest lists the entry '{fileName}', which is not one mesh of the set");
            }
            files.Add(item: new SetFile(Name: fileName, Pin: pin, Size: size));
        }

        var targets = string.Join(separator: ", ", values: (record["targets"]?.AsArray() ?? []).Select(selector: static node => (string?)node).OfType<string>());

        return (new CacheSet(Files: files, Plan: record["plan"], Targets: targets), null, null);
    }
    // Judges a set whose identity matches against the world's plan: null when the set was produced for exactly the groups
    // the world plans, otherwise the line naming why it is not whole.
    private static string? Lacks(string backend, string name, JsonNode? recorded, IReadOnlyList<RestGroupKey> planned) =>
        WorldCachePlan.Lacking(recorded: recorded, release: planned) switch {
            null => $"{backend}: {name} matches this build's identity but records no plan of the world's groups, so it cannot be known to cover this world; produce it again",
            { Count: > 0 } lacking => $"{backend}: {name} matches this build's identity but was produced for another world, so it lacks {WorldCachePlan.Describe(lacking: lacking, total: planned.Count)}",
            _ => null,
        };

    private static async Task<int> StatusAsync(string world, string build, IReadOnlyList<string> backends, string store, CancellationToken cancellationToken) {
        if (!WorldCachePlan.TryRead(folder: Environment.CurrentDirectory, world: world, keys: out var planned, why: out var unplanned)) {
            return CliExit.Refuse(verb: Verb, what: CliPaths.ToDisplay(fullPath: world), why: $"cannot name the groups it plans, because {unplanned}.");
        }
        var source = OpenStore(store: store);
        var held = new List<string>();

        foreach (var backend in backends) {
            if (!TryIdentity(backend: backend, folder: build, identity: out var identity, why: out var why)) { return CliExit.Refuse(verb: Verb, what: CliPaths.ToDisplay(fullPath: build), why: why!); }
            var name = SetName(backend: backend, identity: identity);
            var (set, missing, damaged) = await ReadSetAsync(backend: backend, cancellationToken: cancellationToken, identity: identity, store: source).ConfigureAwait(continueOnCapturedContext: false);

            if (set is null) {
                held.Add(item: (missing ?? $"{backend}: {name} cannot be read, because {damaged}; produce it again"));
                continue;
            }
            if (Lacks(backend: backend, name: name, planned: planned, recorded: set.Plan) is { } lacks) {
                held.Add(item: lacks);
                continue;
            }
            Console.Out.WriteLine(value: string.Create(provider: CultureInfo.InvariantCulture, handler: $"{Verb}: {backend}: {name} holds {set.Files.Count} meshes ({set.Files.Sum(selector: static file => file.Size)} bytes), produced for this world's {planned.Count} bake groups."));
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
    private static async Task<int> FetchAsync(string folder, string store, string? notes, TimeSpan wait, TimeProvider clock, CancellationToken cancellationToken) {
        if (!PlayerBuildLayout.TryReadComplete(folder: folder, record: out var record, why: out var refusal)) { return CliExit.Refuse(verb: Verb, what: CliPaths.ToDisplay(fullPath: folder), why: refusal!); }

        var source = OpenStore(store: store);
        var deadline = (clock.GetUtcNow() + wait);

        while (true) {
            var (exit, held, lines, outcomes) = await FetchOnceAsync(cancellationToken: cancellationToken, folder: folder, record: record, store: source).ConfigureAwait(continueOnCapturedContext: false);

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
    private static async Task<(int? Exit, List<string> Held, List<string> Lines, JsonObject Outcomes)> FetchOnceAsync(string folder, IReleaseSource store, JsonNode record, CancellationToken cancellationToken) {
        var lines = new List<string>();
        var held = new List<string>();
        var outcomes = new JsonObject();
        IReadOnlyList<RestGroupKey>? planned = null;

        foreach (var backend in Backends(record: record)) {
            if (!TryIdentity(backend: backend, folder: folder, identity: out var identity, why: out var why)) { return (CliExit.Refuse(verb: Verb, what: CliPaths.ToDisplay(fullPath: folder), why: why!), held, lines, outcomes); }
            var name = SetName(backend: backend, identity: identity);
            var identityText = Hex(identity: identity);
            var (set, missing, damaged) = await ReadSetAsync(backend: backend, cancellationToken: cancellationToken, identity: identity, store: store).ConfigureAwait(continueOnCapturedContext: false);

            if (damaged is not null) {
                Console.Error.WriteLine(value: $"{Verb}: {name}: {damaged}; nothing of it was taken.");
                return (1, held, lines, outcomes);
            }
            if (set is null) {
                held.Add(item: missing!);
                outcomes[backend] = new JsonObject { ["identity"] = identityText, ["meshes"] = 0, ["held"] = "no set matches the identity" };
                continue;
            }
            if ((planned is null) && !WorldCachePlan.TryRead(folder: folder, world: ((string?)record["world"] ?? string.Empty), keys: out planned, why: out var unplanned)) {
                Console.Error.WriteLine(value: $"{Verb}: {name}: cannot tell whether the set was produced for this build's world, because {unplanned}; a set that is not known to be whole does not ship.");
                return (1, held, lines, outcomes);
            }
            // The plan is judged before any mesh moves, so a set produced for another world costs one manifest read.
            if (Lacks(backend: backend, name: name, planned: planned, recorded: set.Plan) is { } lacks) {
                held.Add(item: lacks);
                outcomes[backend] = new JsonObject { ["identity"] = identityText, ["meshes"] = set.Files.Count, ["set"] = name, ["groups"] = planned.Count, ["held"] = "the set was not produced for this world's groups" };
                continue;
            }

            var (fetched, kept, error) = await TakeAsync(cancellationToken: cancellationToken, files: set.Files, set: SetDirectory(folder: folder, identity: identity), store: store).ConfigureAwait(continueOnCapturedContext: false);

            if (error is not null) {
                Console.Error.WriteLine(value: $"{Verb}: {name}: {error}; the folder's set was left as it was.");
                return (1, held, lines, outcomes);
            }

            var hit = string.Create(provider: CultureInfo.InvariantCulture, handler: $"{backend}: {set.Files.Count} meshes from {name} ({fetched} fetched, {kept} already held), produced for this world's {planned.Count} bake groups (baked for {(string.IsNullOrEmpty(value: set.Targets) ? "the producer's targets" : set.Targets)})");

            Console.Out.WriteLine(value: $"{Verb}: {hit}.");
            lines.Add(item: hit);
            outcomes[backend] = new JsonObject { ["identity"] = identityText, ["meshes"] = set.Files.Count, ["fetched"] = fetched, ["set"] = name, ["targets"] = set.Targets, ["groups"] = planned.Count, ["missing"] = 0 };
        }
        return (null, held, lines, outcomes);
    }
    // Makes a folder's set directory hold exactly a set's meshes: a mesh already there with its pin's bytes is kept, every
    // other one is fetched and verified into a staging directory beside it, and only once all have verified do they move in
    // and the files the set does not list leave. A failure leaves the set directory as it was.
    private static async Task<(int Fetched, int Kept, string? Error)> TakeAsync(IReleaseSource store, string set, IReadOnlyList<SetFile> files, CancellationToken cancellationToken) {
        var staging = (set + ".fetching");
        var fetched = 0;
        var kept = 0;
        string? error = null;

        if (Directory.Exists(path: staging)) { Directory.Delete(path: staging, recursive: true); }
        Directory.CreateDirectory(path: staging);
        try {
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(token: cancellationToken);

            try {
                await Parallel.ForEachAsync(
                    body: async (file, token) => {
                        var held = Path.Combine(path1: set, path2: file.Name);

                        if (File.Exists(path: held) && (new FileInfo(fileName: held).Length == file.Size) && (ContentPin.OfFile(path: held) == file.Pin)) {
                            _ = Interlocked.Increment(location: ref kept);
                            return;
                        }

                        (byte[]? Content, string? Refusal) result = (null, null);

                        for (var attempt = 0; ((attempt < FetchAttempts) && (result.Content is null)); attempt++) {
                            result = await ReleaseObjectFetch.TryFetchAsync(cancellationToken: token, length: file.Size, pin: file.Pin, source: store).ConfigureAwait(continueOnCapturedContext: false);
                        }
                        if (result.Content is null) {
                            _ = Interlocked.CompareExchange(comparand: null, location1: ref error, value: $"the mesh '{file.Name}' (hash {file.Pin}) {result.Refusal}");
                            await stop.CancelAsync().ConfigureAwait(continueOnCapturedContext: false);
                            return;
                        }
                        await File.WriteAllBytesAsync(bytes: result.Content, cancellationToken: token, path: Path.Combine(path1: staging, path2: file.Name)).ConfigureAwait(continueOnCapturedContext: false);
                        _ = Interlocked.Increment(location: ref fetched);
                    },
                    parallelOptions: new ParallelOptions { CancellationToken = stop.Token, MaxDegreeOfParallelism = ReleaseObjectFetch.Parallelism },
                    source: files
                ).ConfigureAwait(continueOnCapturedContext: false);
            } catch (OperationCanceledException) when ((error is not null) && !cancellationToken.IsCancellationRequested) {
                // A mesh that could not be taken stopped the other fetches; the error names it.
            }
            if (error is not null) { return (fetched, kept, error); }

            Directory.CreateDirectory(path: set);
            foreach (var staged in Directory.EnumerateFiles(path: staging)) {
                File.Move(destFileName: Path.Combine(path1: set, path2: Path.GetFileName(path: staged)), overwrite: true, sourceFileName: staged);
            }

            var listed = files.Select(selector: static file => file.Name).ToHashSet(comparer: StringComparer.Ordinal);

            foreach (var stale in Directory.EnumerateFiles(path: set).Where(predicate: path => !listed.Contains(item: Path.GetFileName(path: path))).ToArray()) {
                File.Delete(path: stale);
            }
            return (fetched, kept, null);
        } finally {
            if (Directory.Exists(path: staging)) { Directory.Delete(path: staging, recursive: true); }
        }
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
        "puck world caches produce <folder> --host azure\n" +
        "```\n";
}
