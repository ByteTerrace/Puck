using System.CommandLine;
using System.Globalization;
using System.Text.Json.Nodes;
using Puck.Cli.Motion;
using Puck.Cli.Transpiler;
using Puck.World;

namespace Puck.Cli.Automation;

/// <summary><c>puck world player</c>: assembles the folder a consumer machine runs, a self-contained engine with a
/// world's compiled package beside it and every cache a player build may ship prewarmed, so the machine never builds
/// from source, compiles a cartridge or cold-bakes.</summary>
/// <remarks>The caches are filled by the published engine itself (its baking boots run with the folder's own cache
/// root), so every identity it derives is the one the consumer derives: the mesh store keys by the bake's code and
/// kernel bytecode, never by the adapter. One boot a backend fills the store under the engine's ladder policy
/// (<see cref="Puck.SdfVm.SdfMeshCachePolicy.Ladder"/>): every tier of every mesh it draws, at every place the world's
/// documents author, so the tiers a target's window size, distance or memory budget asks are all there, with no boot
/// per target and no list of tiers kept here. Each <c>--target</c> is then booted as a player's build boots
/// (<see cref="Puck.SdfVm.SdfMeshCachePolicy.Stored"/>), and the folder is complete only when every such boot settles
/// at every place having dispatched no lattice, baked no group and missed no tier. The driver's own pipeline cache is
/// per adapter and driver, so it is never shipped.</remarks>
public static class WorldPlayerCommand {
    private const string Verb = "world player";
    // Written first as incomplete and last as complete; a rerun rebuilds only a folder that holds it.
    private const string MarkerName = Puck.Launcher.Release.PlayerBuildLayout.RecordName;
    private const string CacheName = Puck.Launcher.Release.PlayerBuildLayout.CacheDirectoryName;
    private const string LogsName = Puck.Launcher.Release.PlayerBuildLayout.LogsDirectoryName;
    private const string LauncherName = Puck.Launcher.Release.PlayerBuildLayout.LauncherDirectoryName;
    private static readonly TimeSpan BuildTimeout = TimeSpan.FromMinutes(minutes: 45);

    /// <summary>One baking boot: the device profile it applies (or the adapter's own, <c>auto</c>) and its window size.</summary>
    /// <param name="Profile">The world's <c>render.devices</c> profile name, or <c>auto</c>.</param>
    /// <param name="Width">The window width, in pixels.</param>
    /// <param name="Height">The window height, in pixels.</param>
    public sealed record BakeTarget(string Profile, int Width, int Height) {
        /// <summary>Parses <c>[profile@]WIDTHxHEIGHT</c>.</summary>
        /// <param name="text">The spelling.</param>
        /// <param name="target">The target, or <see langword="null"/>.</param>
        /// <returns><see langword="true"/> when the spelling is well formed.</returns>
        public static bool TryParse(string text, out BakeTarget? target) {
            target = null;
            var at = text.LastIndexOf(value: '@');
            var profile = ((at < 0) ? "auto" : text[..at]);
            var size = text[(at + 1)..].Split(separator: 'x');

            if ((profile.Length == 0) || (size.Length != 2) ||
                !int.TryParse(s: size[0], provider: CultureInfo.InvariantCulture, result: out var width) ||
                !int.TryParse(s: size[1], provider: CultureInfo.InvariantCulture, result: out var height) ||
                (width is < 1 or > 16384) || (height is < 1 or > 16384)) {
                return false;
            }
            target = new BakeTarget(Height: height, Profile: profile, Width: width);
            return true;
        }

        /// <inheritdoc/>
        public override string ToString() => string.Create(provider: CultureInfo.InvariantCulture, handler: $"{Profile}@{Width}x{Height}");
    }

    /// <summary>Creates the <c>world player</c> verb.</summary>
    /// <returns>The verb.</returns>
    public static Command Create() {
        var package = new Argument<string>(name: "package") { Description = "The world's asset package folder (worlds/debug). Its parent directory holds the packages it imports, and the whole directory is compiled into the folder." };
        var output = CliOptions.Output(description: "The player folder, created if missing. A folder that already holds a player build (player.json) is rebuilt in place, keeping its cache/; any other non-empty folder is refused.", required: true);
        var extensions = new Option<string[]>(name: "--extension") { AllowMultipleArgumentsPerToken = true, Description = "An extension project to install beside the engine (extensions/<name>/), such as src/Puck.World.Azure for a hosted authority's sign-in. Repeatable." };
        var backends = new Option<string[]>(name: "--backend") { AllowMultipleArgumentsPerToken = true, Description = "The backends whose caches are prewarmed, each baked in its own windowed boot: directx and vulkan. Omitted bakes both on Windows and vulkan elsewhere." };
        var targets = new Option<string[]>(name: "--target") { AllowMultipleArgumentsPerToken = true, Description = "A device the build is checked for: its profile and window size, [profile@]WIDTHxHEIGHT (rtx2060@1920x1080); a profile is a name of the world's render.devices, or auto for this adapter's own. Each is booted once per backend as a player's build boots. Repeatable; omitted checks auto@2560x1440." };
        var noBake = new Option<bool>(name: "--no-bake") { Description = "Skip the baking boots, which need a GPU: one headless boot derives the device-free caches instead, and the mesh store stays empty until a cache set fills it (puck world caches fetch)." };
        var bakeSeconds = CliOptions.Count(defaultValue: 600, description: "Seconds a boot waits for its bakes to settle at each place it stands, 1 to 600.", maximum: 600, minimum: 1, name: "--bake-seconds");
        var channel = new Option<string?>(name: "--channel-url") { Description = "The release channel this build is published to (a release tree's root URL, with --channel-name): the README then names the one command that installs it. Absent, the README says how to install from a channel without naming one." };
        var channelName = new Option<string>(name: "--channel-name") { DefaultValueFactory = static _ => "player", Description = "The release channel's name under --channel-url." };
        var command = new Command(name: "player", description: "Assemble the folder a consumer machine runs: a self-contained Release engine, its launcher (stub and installer), the package compiled, and its caches prewarmed (the AGB BIOS and Rust guests, specialized shading kernels, compiled worlds and compiles, prepared cartridges and the baked mesh store per backend and target).") {
            package, output, extensions, backends, targets, noBake, bakeSeconds, channel, channelName,
        };

        command.Detail(detail: """
            Steps, in order: build Puck.World in Release (SPIR-V included) under the build lease and publish it
            self-contained for this machine's runtime as one composite ReadyToRun image; publish the stub and the installer
            (Puck.Launcher.Setup) self-contained into launcher/; publish each --extension into extensions/<name>/; compile
            the package's parent directory into worlds/ (documents, compiled worlds and the shader package store); compose
            the package's worlds, export their specialized shading kernels into derived/ and prewarm the AGB BIOS and the
            Rust guests they reference there (needs clang, ld.lld and Rust with wasm32-unknown-unknown when a world
            references them); then boot the published engine
            windowed with --cache-dir cache, once per backend with --mesh-cache ladder (the fill) and then once per backend
            and --target with --mesh-cache stored on the target's device profile (the check). Every boot waits for its
            bakes to settle where it spawns and at each spawn point the world's documents author, posing the first body
            there. The fill bakes every tier of every mesh those places draw into cache/ (meshes, compiled-worlds,
            compilations, derived) under the identities a consumer's boot of the same folder derives; a bake's
            intermediates land in cache/meshes-intermediates/, which a rerun reuses and a release never ships. The check
            is what a player's boot does: it draws stored tiers alone, and reads the engine's sdf.mesh-cache counters.
            With --no-bake, one headless boot fills everything but the meshes. A consumer starts the folder with --world
            worlds/<package> --cache-dir cache; `puck publish` turns it into a release and the installer installs that.
            Writes player.json first as incomplete and last with the commit, runtime, backends, targets and each boot's
            reading (places, settled, dispatches, groupsBaked, tiersMissed, tiersMissedLive, loadsDeclined, tiersFilled,
            loaded, and missed: each tier a check asked that the fill did not produce, with the place, residency,
            geometry and tiers asked and drawn), and prints each part's size. Before the fill it removes every mesh set
            but this build's own from cache/meshes. The record says complete only when every boot settled at every place,
            each backend's set still stands after the fills, and every check dispatched no lattice, baked no group and
            missed no tier but a moved field's (live geometry); otherwise it lists why under incomplete, and
            `puck world caches produce` refuses the folder. Each boot's logs are in player-logs/. Exit 0 wrote a whole
            player build, 1 a step failed or the build is not complete, 2 invalid arguments.
            """);
        command.SetAction(action: parse => Run(
            backendNames: parse.GetValue(option: backends) ?? [],
            bake: !parse.GetValue(option: noBake),
            bakeSeconds: parse.GetValue(option: bakeSeconds),
            channel: ((parse.GetValue(option: channel) is { } url) ? (url, parse.GetValue(option: channelName)!) : null),
            extensions: parse.GetValue(option: extensions) ?? [],
            output: Path.GetFullPath(path: parse.GetRequiredValue(option: output)),
            package: Path.GetFullPath(path: parse.GetRequiredValue(argument: package)),
            targetNames: parse.GetValue(option: targets) ?? []
        ));
        return command;
    }

    private static int Run(string package, string output, IReadOnlyList<string> extensions, IReadOnlyList<string> backendNames, IReadOnlyList<string> targetNames, bool bake, int bakeSeconds, (string Url, string Name)? channel) {
        if (!CliPaths.TryGetRepositoryRoot(repositoryRoot: out var repository)) { return 2; }
        if (!Directory.Exists(path: package)) {
            return CliExit.Refuse(verb: Verb, what: CliPaths.ToDisplay(fullPath: package), why: "is not an asset package folder.");
        }
        if (!WorldPrepareCommand.TryResolvePrimary(primary: package, entry: out var entry, reason: out var refusal)) {
            return CliExit.Refuse(verb: Verb, what: CliPaths.ToDisplay(fullPath: package), why: refusal);
        }

        var targets = new List<BakeTarget>();

        foreach (var name in ((targetNames.Count == 0) ? ["auto@2560x1440"] : targetNames)) {
            if (!BakeTarget.TryParse(text: name, target: out var target)) {
                return CliExit.Refuse(verb: Verb, what: $"--target {name}", why: "is not [profile@]WIDTHxHEIGHT.");
            }
            targets.Add(item: target!);
        }

        WorldBackendPreference[] backends;

        try {
            backends = ((backendNames.Count == 0)
                ? (OperatingSystem.IsWindows() ? [WorldBackendPreference.DirectX, WorldBackendPreference.Vulkan] : [WorldBackendPreference.Vulkan])
                : [.. backendNames.Select(selector: static name => WorldLaunch.ParseBackend(token: name)!.Value).Distinct()]);
        } catch (ArgumentException exception) {
            return CliExit.Refuse(verb: Verb, what: "--backend", why: exception.Message);
        }
        if (backends.Contains(value: WorldBackendPreference.Auto)) {
            return CliExit.Refuse(verb: Verb, what: "--backend auto", why: "names no backend: a store is prewarmed per backend, so name directx or vulkan.");
        }

        var worldsRoot = Path.GetDirectoryName(path: Path.TrimEndingDirectorySeparator(path: package))!;

        if (Puck.Abstractions.PuckPaths.IsWithin(path: output, root: worldsRoot) || Puck.Abstractions.PuckPaths.IsWithin(path: worldsRoot, root: output)) {
            return CliExit.Refuse(verb: Verb, what: CliPaths.ToDisplay(fullPath: output), why: $"overlaps the worlds directory {CliPaths.ToDisplay(fullPath: worldsRoot)} the folder compiles.");
        }
        if (Directory.Exists(path: output) && Directory.EnumerateFileSystemEntries(path: output).Any()) {
            if (!File.Exists(path: Path.Combine(path1: output, path2: MarkerName))) {
                return CliExit.Refuse(verb: Verb, what: CliPaths.ToDisplay(fullPath: output), why: $"is not empty and holds no {MarkerName}, so it is not a player build this verb may replace.");
            }
            // Everything but the device caches is rebuilt. Each cache entry is keyed by its inputs and the code that
            // derives it, so the rebuilt engine reads only the entries its own identities name and a rerun bakes only
            // what changed or was never baked.
            foreach (var part in Directory.EnumerateFileSystemEntries(path: output)) {
                if (string.Equals(a: Path.GetFileName(path: part), b: CacheName, comparisonType: StringComparison.Ordinal)) { continue; }
                if (Directory.Exists(path: part)) { Directory.Delete(path: part, recursive: true); } else { File.Delete(path: part); }
            }
        }
        _ = Directory.CreateDirectory(path: output);
        // Marks the folder as this verb's from its first write, so a run that stops partway is rebuilt in place.
        CliFiles.WriteJson(path: Path.Combine(path1: output, path2: MarkerName), value: new JsonObject { ["schema"] = "puck.player-build.v1", ["complete"] = false });

        var logs = Path.Combine(path1: output, path2: LogsName);

        try {
            Publish(repository: repository, output: output);
            var installed = extensions.Select(selector: extension => PublishExtension(repository: repository, project: extension, output: output)).ToArray();

            Console.WriteLine(value: $"{Verb}: compiling {CliPaths.ToDisplay(fullPath: worldsRoot)} into worlds/.");
            if (CompileCommand.Create().Parse(args: ["--tree", worldsRoot, "--output", Path.Combine(path1: output, path2: "worlds")]).Invoke() != 0) {
                throw new InvalidOperationException(message: "compiling the worlds directory failed; its errors are above.");
            }

            var composed = WorldPrepareCommand.Compose(primary: entry);

            WorldPrepareCommand.PrepareShading(output: output, sets: composed.ShadeSets);
            PlayerDerivedProducts.Prepare(output: output, agbBios: composed.Needs.AgbBios, guests: [.. composed.Needs.Guests]);
            PrepareCartridges(cache: Path.Combine(path1: output, path2: CacheName, path3: "derived"), composed: composed);

            var world = Path.Combine(path1: output, path2: "worlds", path3: Path.GetFileName(path: Path.TrimEndingDirectorySeparator(path: package)));

            var verified = new JsonObject();
            // Why the folder is not a build a player may run: a boot that did not settle, or a check boot that asked its
            // cache for more than a hit.
            var incomplete = new List<string>();

            if (bake) {
                // The places a body may be posed at, as the primary world's documents author them: each boot stands at
                // every one, so the residencies a place opens (a portal's far side, a screen's session) are drawn there.
                // The composition lists the primary world first.
                var places = composed.Worlds[0].Definition.SpawnPoints.Select(selector: static point => point.Id).ToArray();
                // One boot a backend fills the store: under the ladder policy it bakes every tier of every mesh it draws,
                // whatever the window or the device's memory would have demanded, so no boot per target is needed to
                // cover a target's tiers. It runs on this adapter's own profile, at the largest target's window.
                var widest = targets.MaxBy(keySelector: static target => (((long)target.Width) * target.Height))!;
                var sets = KeepOwnMeshSets(backends: backends, output: output);

                foreach (var backend in backends) {
                    var key = $"fill {WorldBackendTokens.Token(backend: backend)}";
                    var reading = Boot(backend: backend, bakeSeconds: bakeSeconds, logs: logs, output: output, pass: "fill", places: places, policy: "ladder", target: (widest with { Profile = "auto" }), world: world);

                    verified[key] = reading.ToJson();
                    incomplete.AddRange(collection: reading.Unsettled.Select(selector: place => $"{key}: not settled at {place}"));
                }
                // The store keeps the writer's own set whole and evicts others past its bound, so a later backend's fill
                // can take an earlier one's set: each must still stand before its checks mean anything.
                foreach (var (backend, set) in sets) {
                    if (!Directory.Exists(path: set) || !Directory.EnumerateFiles(path: set).Any()) {
                        incomplete.Add(item: $"fill {WorldBackendTokens.Token(backend: backend)}: its mesh set {Path.GetFileName(path: set)} is not in the store, which a later fill evicted past the store's bound");
                    }
                }
                // Then each backend and target boots as a player's build does, drawing stored tiers alone, on the target's
                // profile and window, and stands at the same places: a whole cache settles everywhere with no lattice
                // dispatched, no group baked and no tier missed.
                foreach (var backend in backends) {
                    foreach (var target in targets) {
                        var key = $"check {WorldBackendTokens.Token(backend: backend)}-{target}";
                        var reading = Boot(backend: backend, bakeSeconds: bakeSeconds, logs: logs, output: output, pass: "check", places: places, policy: "stored", target: target, world: world);

                        verified[key] = reading.ToJson();
                        incomplete.AddRange(collection: reading.Unsettled.Select(selector: place => $"{key}: not settled at {place}"));
                        if (!reading.Hits) { incomplete.Add(item: $"{key}: the cache missed ({reading.Describe()}){((reading.Missed.Count > 0) ? $"; the fill did not produce {reading.DescribeMissed(count: 4)}" : string.Empty)}"); }
                    }
                }
            } else {
                verified["warm"] = Warm(logs: logs, output: output, world: world);
            }

            var commit = CliProcess.RunCheckedAsync(arguments: ["rev-parse", "HEAD"], capture: true, fileName: "git", workingDirectory: repository).GetAwaiter().GetResult().Trim();

            WriteReadme(channel: channel, commit: commit, extensions: installed, output: output, targets: (bake ? targets : []), world: Puck.Abstractions.PuckPaths.Relative(path: world, relativeTo: output));
            CliFiles.WriteJson(path: Path.Combine(path1: output, path2: MarkerName), value: new JsonObject {
                ["schema"] = "puck.player-build.v1",
                ["complete"] = (incomplete.Count == 0),
                ["incomplete"] = new JsonArray([.. incomplete.Select(selector: static reason => JsonValue.Create(value: reason))]),
                ["commit"] = commit,
                ["runtime"] = System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier,
                ["world"] = Puck.Abstractions.PuckPaths.Relative(path: world, relativeTo: output),
                ["cacheDirectory"] = CacheName,
                ["backends"] = new JsonArray([.. backends.Select(selector: static backend => JsonValue.Create(value: WorldBackendTokens.Token(backend: backend)))]),
                ["targets"] = new JsonArray([.. (bake ? targets : []).Select(selector: static target => JsonValue.Create(value: target.ToString()))]),
                ["extensions"] = new JsonArray([.. installed.Select(selector: static name => JsonValue.Create(value: name))]),
                ["launcher"] = LauncherName,
                ["checked"] = verified,
            });
            ReportSizes(output: output);
            if (incomplete.Count == 0) { return 0; }
            Console.Error.WriteLine(value: $"{Verb}: the folder is not complete, and {MarkerName} says so: a player's build never bakes, so one whose boots do not settle on a whole cache does not ship.");
            foreach (var reason in incomplete) { Console.Error.WriteLine(value: $"{Verb}:   {reason}"); }
            return 1;
        } catch (Exception exception) when (exception is InvalidOperationException or InvalidDataException or IOException or TimeoutException or UnauthorizedAccessException) {
            Console.Error.WriteLine(value: $"{Verb}: {exception.Message}");
            return 1;
        }
    }

    // Removes from the folder's mesh store every set but this build's own, one a backend by its mesh bake identity, and
    // answers each backend's set directory. A rerun keeps cache/, and an earlier build's sets ship nothing but count
    // against the store's bound, which evicts whole sets other than the writer's own.
    private static List<(WorldBackendPreference Backend, string Set)> KeepOwnMeshSets(IReadOnlyList<WorldBackendPreference> backends, string output) {
        var sets = new List<(WorldBackendPreference Backend, string Set)>();

        foreach (var backend in backends) {
            if (!WorldCachesCommand.TryIdentity(backend: WorldBackendTokens.Token(backend: backend), folder: output, identity: out var identity, why: out var why)) {
                throw new InvalidOperationException(message: $"the build {why}.");
            }
            sets.Add(item: (backend, WorldCachesCommand.SetDirectory(folder: output, identity: identity)));
        }

        var meshes = Path.Combine(path1: output, path2: CacheName, path3: Puck.Launcher.Release.PlayerBuildLayout.MeshesDirectoryName);
        var own = sets.Select(selector: static set => Path.GetFileName(path: set.Set)).ToHashSet(comparer: StringComparer.OrdinalIgnoreCase);

        if (!Directory.Exists(path: meshes)) { return sets; }
        foreach (var entry in Directory.EnumerateFileSystemEntries(path: meshes)) {
            var name = Path.GetFileName(path: entry);
            var dot = name.IndexOf(value: '.');

            if (own.Contains(item: ((dot < 0) ? name : name[..dot]))) { continue; }
            Console.WriteLine(value: $"{Verb}: removing the mesh set entry {name}, which this build's identities do not name.");
            if (Directory.Exists(path: entry)) { Directory.Delete(path: entry, recursive: true); } else { File.Delete(path: entry); }
        }
        return sets;
    }
    // One Release publish with the World's own self-contained switch (PuckSelfContained, see Puck.World.csproj): a global
    // SelfContained or RuntimeIdentifier would flow into every referenced project's locked restore. The ReadyToRun image is
    // composite: the framework and Puck share one version bubble, so the precompiler reaches the generic instantiations
    // over framework types that a per-assembly image leaves to the JIT (the World README holds the counts).
    private static void Publish(string repository, string output) {
        var project = Path.Combine(path1: repository, path2: "src/Puck.World/Puck.World.csproj");

        using var lease = CliLease.AcquireAsync(
            ancestor: ProcessInspection.IsAncestor,
            checkout: repository,
            clock: TimeProvider.System,
            holder: $"{Verb} {CliPaths.ToDisplay(fullPath: output)}",
            lane: CliLease.LaneOf(checkout: repository),
            name: "build",
            root: CliLease.RootAsync(checkout: repository).GetAwaiter().GetResult(),
            wait: CliLease.DefaultWait
        ).GetAwaiter().GetResult();

        Console.WriteLine(value: $"{Verb}: publishing Puck.World (Release, SPIR-V included, composite ReadyToRun) self-contained for {System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier}.");
        _ = CliProcess.RunCheckedAsync(
            arguments: ["publish", CliOptions.NoNodeReuse, "-m:2", "--disable-build-servers", project, "-c", "Release", "--nologo",
                "-p:PuckSelfContained=true", "-p:PublishReadyToRunComposite=true", "-p:PuckShaderSpirvEnabled=true", "-o", output],
            fileName: "dotnet",
            timeout: BuildTimeout,
            workingDirectory: repository
        ).GetAwaiter().GetResult();
        PublishLauncher(repository: repository, output: output);
    }
    // Every file a machine of the composed worlds can mount (its content folders, WorldMachineContentFolder.TryList) is
    // prepared through its engine's content provider into the cache root's derived store, so a cartridge swapped in at
    // play is read, not compiled, as the ones the baking boots bind at spawn are. The products key by their inputs and the
    // provider's code, never by this process, so the published engine reads them.
    private static void PrepareCartridges(WorldPrepareCommand.Composition composed, string cache) {
        var catalog = CliWorldVocabulary.EnsureInstalled();
        var previous = Puck.Assets.DerivedBytes.DefaultDirectory;
        var prepared = new HashSet<string>(comparer: Puck.Abstractions.PuckPaths.Comparer);

        Puck.Assets.DerivedBytes.DefaultDirectory = cache;
        try {
            foreach (var (name, definition, directory) in composed.Worlds) {
                foreach (var machine in definition.Machines) {
                    if (!catalog.TryContentProvider(engineId: machine.Engine, provider: out var provider) ||
                        !WorldMachineContentFolder.TryList(catalog: catalog, documentDirectory: directory, entries: out var entries, folders: out _, machine: machine, reason: out _)) {
                        continue;
                    }
                    foreach (var entry in entries.Where(predicate: entry => (entry.Refusal is null) && provider.Recognizes(contentPath: entry.ContentPath))) {
                        var path = WorldDocumentPaths.Resolve(documentDirectory: directory, path: entry.ContentPath);

                        if (!prepared.Add(item: path)) { continue; }
                        try {
                            _ = provider.Prepare(content: File.ReadAllBytes(path: path), contentPath: path);
                        } catch (Puck.Abstractions.Machines.MachineContentException exception) {
                            Console.Error.WriteLine(value: $"{Verb}: {name}: {entry.ContentPath} does not prepare, so a consumer compiles it at mount and is refused the same way: {exception.Message}");
                        }
                    }
                }
            }
        } finally {
            Puck.Assets.DerivedBytes.DefaultDirectory = previous;
        }
        Console.WriteLine(value: $"{Verb}: prepared {prepared.Count} machine content file(s) into {CliPaths.ToDisplay(fullPath: cache)}.");
    }
    // One headless boot of the published engine with the folder's own cache root, for a folder built without baking boots:
    // it derives what a boot derives without a device (compiled worlds, compiles, prepared machine content), so a consumer's
    // first boot reads them; the mesh store stays empty until a cache set fills it (`puck world caches`). Returns the boot's
    // derived-cache counts.
    private static JsonObject Warm(string output, string world, string logs) {
        var directory = Path.Combine(path1: logs, path2: "warm-headless");
        var request = new WorldLaunchRequest(Artifact: Path.Combine(path1: output, path2: "Puck.World.dll"), Step: Verb) {
            Arguments = ["--paused", "--cache-dir", Path.Combine(path1: output, path2: CacheName)],
            ExitAfterSeconds = WorldLaunch.Backstop(deadline: TimeSpan.FromMinutes(minutes: 10)),
            Shape = WorldShape.Headless,
            StateDirectory = Path.Combine(path1: directory, path2: "state"),
            WorkingDirectory = output,
            World = world,
        };

        Console.WriteLine(value: $"{Verb}: warm headless.");

        WorldProcess started;

        using (var launch = WorldLaunch.Admit(request: request)) {
            started = launch.Start(logDirectory: directory);
        }
        using var process = started;

        try {
            // A headless host composes no renderer, so it is ready once it answers its first console line: the boot has
            // composed the world and mounted its machines by then.
            var ready = process.RunScript(lines: ["world.status"], timeout: TimeSpan.FromSeconds(seconds: 320));
            var lines = process.LinesFrom(from: 0);
            var hits = lines.Count(predicate: static row => row.Text.StartsWith(comparisonType: StringComparison.Ordinal, value: "[derived] ") && row.Text.Contains(value: ": hit;", comparisonType: StringComparison.Ordinal));
            var misses = lines.Count(predicate: static row => row.Text.StartsWith(comparisonType: StringComparison.Ordinal, value: "[derived] ") && row.Text.Contains(value: ": miss;", comparisonType: StringComparison.Ordinal));
            var status = ready.Lines.LastOrDefault(predicate: static line => line.Text.StartsWith(comparisonType: StringComparison.Ordinal, value: "[world.status"))?.Text;

            if (status is null) { throw new InvalidOperationException(message: $"the warm headless boot did not answer world.status; its logs are in {CliPaths.ToDisplay(fullPath: directory)}."); }
            var answer = (status.Length > 120) ? status[..120] : status;

            Console.WriteLine(value: $"{Verb}: warm headless: {answer}; derived hits {hits}, misses {misses}");
            return new JsonObject { ["ready"] = answer, ["derivedHits"] = hits, ["derivedMisses"] = misses };
        } finally {
            if (!process.Send(line: "quit") || (process.WaitForExit(timeout: TimeSpan.FromSeconds(seconds: 60)) is null)) {
                process.Kill();
            }
        }
    }
    // The launcher a release carries in launcher/: the stub that starts an installed version and the installer that
    // installs and updates one (Puck.Launcher.Setup), each published as one self-contained file, since a consumer machine
    // has no .NET. The installer lays them beside the install's release record.
    private static void PublishLauncher(string repository, string output) {
        // The installer first: it references the stub, so the stub publish that follows writes the stub file last.
        foreach (var project in new[] { "src/Puck.Launcher.Setup/Puck.Launcher.Setup.csproj", "src/Puck.Launcher.Stub/Puck.Launcher.Stub.csproj" }) {
            Console.WriteLine(value: $"{Verb}: publishing {Path.GetFileNameWithoutExtension(path: project)} self-contained into {LauncherName}/.");
            _ = CliProcess.RunCheckedAsync(
                arguments: ["publish", CliOptions.NoNodeReuse, "-m:2", "--disable-build-servers", Path.Combine(path1: repository, path2: project), "-c", "Release", "--nologo",
                    "-p:PuckSelfContained=true", "-p:DebugType=none", "-p:PublishDocumentationFile=false", "-p:PublishReferencesDocumentationFiles=false", "-o", Path.Combine(path1: output, path2: LauncherName)],
                fileName: "dotnet",
                timeout: BuildTimeout,
                workingDirectory: repository
            ).GetAwaiter().GetResult();
        }
    }
    // An extension installs as the host discovers one (PuckExtensionDiscovery): extensions/<name>/<name>.dll beside its
    // dependency manifest.
    private static string PublishExtension(string repository, string project, string output) {
        var path = Path.GetFullPath(path: Path.Combine(path1: repository, path2: project));
        var file = (Directory.Exists(path: path) ? Directory.EnumerateFiles(path: path, searchPattern: "*.csproj").SingleOrDefault() : path);

        if ((file is null) || !File.Exists(path: file)) {
            throw new InvalidOperationException(message: $"--extension {project} names no project.");
        }

        var name = Path.GetFileNameWithoutExtension(path: file);

        Console.WriteLine(value: $"{Verb}: installing the extension {name}.");
        _ = CliProcess.RunCheckedAsync(
            arguments: ["publish", CliOptions.NoNodeReuse, "--disable-build-servers", file, "-c", "Release", "--nologo", "-o", Path.Combine(path1: output, path2: "extensions", path3: name)],
            fileName: "dotnet",
            timeout: BuildTimeout,
            workingDirectory: repository
        ).GetAwaiter().GetResult();
        return name;
    }
    /// <summary>What one boot of the published engine asked of the folder's mesh cache: the places it stood at, those
    /// its bakes did not settle at, and the engine's own <c>sdf.mesh-cache</c> counts as the boot ended.</summary>
    /// <param name="Places">The places the boot stood at, its spawn included.</param>
    /// <param name="Unsettled">Each place <c>world.wait bakes</c> released at unsettled, with the engine's reason.</param>
    /// <param name="Dispatches">The lattices the boot's bakes handed the GPU.</param>
    /// <param name="LiveDispatches">The lattices among them of geometry the world made as it ran.</param>
    /// <param name="GroupsBaked">The groups that began a bake, live geometry apart.</param>
    /// <param name="LiveBaked">The groups that began a bake because an operand or a sampled field they read moved.</param>
    /// <param name="RungsMissed">The tiers a stored-only boot asked that the store lacked.</param>
    /// <param name="RungsMissedLive">The tiers among them asked by a sampled field's reader whose field then moved, which
    /// are live geometry's.</param>
    /// <param name="LoadsDeclined">The stored entries a stored-only boot could not read for want of memory.</param>
    /// <param name="RungsFilled">The tiers a ladder boot baked beyond its demand.</param>
    /// <param name="Loaded">The groups the boot's bakes loaded from the store, summed over every start it reported.</param>
    /// <param name="Missed">The tiers missed that a cache answers for, each with where the boot stood when it asked.</param>
    public sealed record BootReading(int Places, IReadOnlyList<string> Unsettled, long Dispatches, long LiveDispatches, long GroupsBaked, long LiveBaked, long RungsMissed, long RungsMissedLive, long LoadsDeclined, long RungsFilled, int Loaded, IReadOnlyList<MissedTier> Missed) {
        /// <summary>Gets whether the boot took from the store everything a cache can hold: no lattice dispatched and
        /// no group baked but for geometry the world made as it ran, and no tier missed but live geometry's.</summary>
        public bool Hits => ((Dispatches == LiveDispatches) && (GroupsBaked == 0L) && (RungsMissed == RungsMissedLive));

        /// <summary>Describes the counts in one line, as the verb prints them.</summary>
        /// <returns>The line.</returns>
        public string Describe() => string.Create(provider: CultureInfo.InvariantCulture, handler: $"dispatches {Dispatches} ({LiveDispatches} live), groups baked {GroupsBaked} ({LiveBaked} more live), tiers missed {RungsMissed} ({RungsMissedLive} live), loads declined {LoadsDeclined}, tiers filled {RungsFilled}, groups loaded {Loaded}");
        /// <summary>Names the first missed tiers a cache answers for: where the boot stood, the residency, the geometry,
        /// the tier asked and the tier drawn in its place.</summary>
        /// <param name="count">The most to name.</param>
        /// <returns>The fragment, or an empty string when none was missed.</returns>
        public string DescribeMissed(int count) {
            if (Missed.Count == 0) { return string.Empty; }

            var places = Missed.Select(selector: static missed => missed.Place).Distinct(comparer: StringComparer.Ordinal).Count();
            var geometries = Missed.Select(selector: static missed => missed.Geometry).Distinct(comparer: StringComparer.Ordinal).Count();

            return string.Create(provider: CultureInfo.InvariantCulture, handler: $"{Missed.Sum(selector: static missed => missed.Times)} ask(s) of {geometries} geometr{((geometries == 1) ? "y" : "ies")} at {places} place(s): {string.Join(separator: "; ", values: Missed.Take(count: count).Select(selector: static missed => missed.ToString()))}{((Missed.Count > count) ? "; ..." : string.Empty)}");
        }
        /// <summary>Writes the reading as <c>player.json</c> records it.</summary>
        /// <returns>The object.</returns>
        public JsonObject ToJson() => new() {
            ["places"] = Places,
            ["settled"] = (Unsettled.Count == 0),
            ["unsettled"] = new JsonArray([.. Unsettled.Select(selector: static place => JsonValue.Create(value: place))]),
            ["dispatches"] = Dispatches,
            ["liveDispatches"] = LiveDispatches,
            ["groupsBaked"] = GroupsBaked,
            ["liveBaked"] = LiveBaked,
            ["tiersMissed"] = RungsMissed,
            ["tiersMissedLive"] = RungsMissedLive,
            ["loadsDeclined"] = LoadsDeclined,
            ["tiersFilled"] = RungsFilled,
            ["loaded"] = Loaded,
            ["missed"] = new JsonArray([.. Missed.Select(selector: static missed => missed.ToJson())]),
        };
    }

    /// <summary>A tier a stored-only boot asked that the store lacked, as its engine named it
    /// (<c>[sdf.mesh-bake] rung-missed</c>): the fill's guarantee is that a check asks only for tiers the fill produced,
    /// and each of these breaks it.</summary>
    /// <param name="Place">Where the boot stood: an authored spawn point, or its spawn.</param>
    /// <param name="Residency">The residency that asked.</param>
    /// <param name="Geometry">The geometry asked, at the tier asked: its store entry's name.</param>
    /// <param name="Shape">The shape, which every tier of the geometry shares.</param>
    /// <param name="Reads">The frame inputs the geometry reads, or <c>none</c>.</param>
    /// <param name="Asked">The tier asked.</param>
    /// <param name="Drawn">The stored tier drawn in its place.</param>
    /// <param name="Tiers">The geometry's ladder, finest to coarsest.</param>
    /// <param name="Times">How many times the boot asked it there.</param>
    public sealed record MissedTier(string Place, string Residency, string Geometry, string Shape, string Reads, int Asked, int Drawn, string Tiers, int Times) {
        /// <inheritdoc/>
        public override string ToString() => string.Create(provider: CultureInfo.InvariantCulture, handler: $"at {Place} {Residency} geometry {Geometry} (reads {Reads}) asked tier {Asked} of {Tiers}, drew {Drawn}{((Times > 1) ? $", {Times} times" : string.Empty)}");
        /// <summary>Writes the tier as <c>player.json</c> records it.</summary>
        /// <returns>The object.</returns>
        public JsonObject ToJson() => new() {
            ["place"] = Place, ["residency"] = Residency, ["geometry"] = Geometry, ["shape"] = Shape, ["reads"] = Reads,
            ["asked"] = Asked, ["drawn"] = Drawn, ["tiers"] = Tiers, ["times"] = Times,
        };
    }

    // One windowed boot of the published engine with the folder's own cache root, under a mesh cache policy
    // (--mesh-cache): it applies the target's device profile (whose memory bars set the mesh budget) at the target's
    // window size, waits for its bakes to settle where it spawns, then poses the first body at each authored place in
    // turn and waits again there, so every residency a place opens is drawn. A place that does not settle ends the
    // visits, since the boot's answer is already no. The counts are read from the engine's own counters once the last
    // wait ends, so a settle is told apart from a bake: a boot can settle having baked, and can bake nothing while its
    // detail still moves between stored tiers.
    private static BootReading Boot(WorldBackendPreference backend, BakeTarget target, string policy, IReadOnlyList<string> places, string output, string world, string logs, int bakeSeconds, string pass) {
        var name = $"{WorldBackendTokens.Token(backend: backend)}-{target.Profile}-{target.Width}x{target.Height}";
        var directory = Path.Combine(path1: logs, path2: $"{pass}-{name}");
        // One wait and its answer at each place, and at the spawn; the World's own exit backstop, should the verb die.
        var deadline = TimeSpan.FromSeconds(seconds: (300L + ((places.Count + 1L) * (bakeSeconds + 90L))));
        var request = new WorldLaunchRequest(Artifact: Path.Combine(path1: output, path2: "Puck.World.dll"), Step: Verb) {
            Arguments = ["--width", target.Width.ToString(provider: CultureInfo.InvariantCulture), "--height", target.Height.ToString(provider: CultureInfo.InvariantCulture),
                "--cache-dir", Path.Combine(path1: output, path2: CacheName), "--mesh-cache", policy],
            Backend = backend,
            ExitAfterSeconds = WorldLaunch.Backstop(deadline: deadline),
            Shape = WorldShape.Windowed,
            StateDirectory = Path.Combine(path1: directory, path2: "state"),
            WorkingDirectory = output,
            World = world,
        };

        Console.WriteLine(value: $"{Verb}: {pass} {name} ({policy}, {places.Count} places).");

        WorldProcess started;

        using (var launch = WorldLaunch.Admit(request: request)) {
            started = launch.Start(logDirectory: directory);
        }
        using var process = started;

        try {
            _ = process.RunScript(lines: ["world.wait ready 300"], timeout: TimeSpan.FromSeconds(seconds: 320));
            if (!string.Equals(a: target.Profile, b: "auto", comparisonType: StringComparison.Ordinal)) {
                var applied = process.RunScript(lines: [$"world.device-profile {target.Profile}"], timeout: TimeSpan.FromSeconds(seconds: 60));

                var answer = applied.Lines.LastOrDefault(predicate: static line => line.Text.StartsWith(comparisonType: StringComparison.Ordinal, value: "[world.device-profile:"))?.Text;

                if ((answer is null) || answer.Contains(value: "authors no profile", comparisonType: StringComparison.Ordinal)) {
                    throw new InvalidOperationException(message: $"{name}: the World did not apply the profile {target.Profile}: {answer ?? "no answer"}");
                }
                Console.WriteLine(value: $"{Verb}: {answer}");
            }

            var unsettled = new List<string>();
            var stood = 0;
            // The tiers the boot missed, by where it stood, its geometry and the tier asked, and the reader geometries the
            // engine then named live; read from the boot's lines since the last place (Collect).
            var missed = new Dictionary<(string Place, string Residency, string Geometry, int Asked), MissedTier>();
            var live = new HashSet<string>(comparer: StringComparer.Ordinal);
            var collected = 0;

            void Collect(string place) {
                var rows = process.LinesFrom(from: collected);

                collected += rows.Count;
                foreach (var row in rows) {
                    var text = row.Text;

                    if (text.StartsWith(comparisonType: StringComparison.Ordinal, value: "[sdf.mesh-bake] rung-missed-live ")) {
                        _ = live.Add(item: CliLineFields.Field(line: text, name: "geometry") ?? string.Empty);
                        continue;
                    }
                    if (!text.StartsWith(comparisonType: StringComparison.Ordinal, value: "[sdf.mesh-bake] rung-missed ")) { continue; }

                    var residency = (CliLineFields.Field(line: text, name: "residency") ?? "?");
                    var geometry = (CliLineFields.Field(line: text, name: "geometry") ?? "?");
                    var asked = CliLineFields.Count(line: text, name: "asked");
                    var key = (place, residency, geometry, asked);

                    missed[key] = (missed.TryGetValue(key: key, value: out var seen)
                        ? (seen with { Times = (seen.Times + 1) })
                        : new MissedTier(
                            Asked: asked,
                            Drawn: CliLineFields.Count(line: text, name: "drawn"),
                            Geometry: geometry,
                            Place: place,
                            Reads: (CliLineFields.Field(line: text, name: "reads") ?? "none"),
                            Residency: residency,
                            Shape: (CliLineFields.Field(line: text, name: "shape") ?? "?"),
                            Tiers: (CliLineFields.Field(line: text, name: "tiers") ?? "?"),
                            Times: 1
                        ));
                }
            }

            // The engine's own counts of what the boot has asked of its mesh cache so far.
            BootReading Read() {
                var counted = process.RunScript(lines: [$"world.counters {Puck.SdfVm.SdfMeshCache.SourceName}"], timeout: TimeSpan.FromSeconds(seconds: 60));

                long Counted(Puck.Abstractions.Counting.WorkKind kind) => (CountOf(kind: kind.Name, lines: counted.Lines.Select(selector: static line => line.Text))
                    ?? throw new InvalidOperationException(message: $"{name}: world.counters answered no {kind.Name}; its logs are in {CliPaths.ToDisplay(fullPath: directory)}."));

                return new BootReading(
                    Dispatches: Counted(kind: Puck.SdfVm.SdfMeshCache.Dispatches),
                    GroupsBaked: Counted(kind: Puck.SdfVm.SdfMeshCache.GroupsBaked),
                    LiveBaked: Counted(kind: Puck.SdfVm.SdfMeshCache.LiveBaked),
                    LiveDispatches: Counted(kind: Puck.SdfVm.SdfMeshCache.LiveDispatches),
                    Loaded: process.LinesFrom(from: 0).Where(predicate: static row => row.Text.StartsWith(comparisonType: StringComparison.Ordinal, value: "[sdf.mesh-bake] start ")).Sum(selector: static row => CliLineFields.Count(line: row.Text, name: "loading")),
                    LoadsDeclined: Counted(kind: Puck.SdfVm.SdfMeshCache.LoadsDeclined),
                    Missed: [.. missed.Values.Where(predicate: tier => !live.Contains(item: tier.Geometry))],
                    Places: stood,
                    RungsFilled: Counted(kind: Puck.SdfVm.SdfMeshCache.RungsFilled),
                    RungsMissed: Counted(kind: Puck.SdfVm.SdfMeshCache.RungsMissed),
                    RungsMissedLive: Counted(kind: Puck.SdfVm.SdfMeshCache.RungsMissedLive),
                    Unsettled: unsettled
                );
            }

            var before = Read();

            // Waits for the bakes where the body stands; false when they did not settle. A wait that released while
            // the bakes were still landing work (a fill's first place bakes the whole world's tiers, past one wait's
            // longest hold) waits again, for as long as each wait dispatched or filled more; one that released with
            // nothing landed since is the boot's answer.
            bool Settle(string place) {
                var settled = Wait(place: place);

                Collect(place: place);
                return settled;
            }
            bool Wait(string place) {
                stood++;
                while (true) {
                    var mark = process.LineCount;

                    _ = process.RunScript(lines: [string.Create(provider: CultureInfo.InvariantCulture, handler: $"world.wait bakes {bakeSeconds}")], timeout: TimeSpan.FromSeconds(seconds: (bakeSeconds + 60)));

                    var answer = process.WaitFor(accept: static row => row.Text.StartsWith(comparisonType: StringComparison.Ordinal, value: "[bakes:"), from: mark, timeout: TimeSpan.FromSeconds(seconds: 60));

                    if (answer is null) { throw new InvalidOperationException(message: $"{name}: world.wait bakes gave no answer at {place}; its logs are in {CliPaths.ToDisplay(fullPath: directory)}."); }
                    if (answer.Text.StartsWith(comparisonType: StringComparison.Ordinal, value: "[bakes: settled")) { return true; }

                    var after = Read();
                    var landed = ((after.Dispatches > before.Dispatches) || (after.RungsFilled > before.RungsFilled));

                    before = after;
                    if (landed) { continue; }

                    // The answer, with what holds each bake, without the residency reports it appends after them.
                    var report = answer.Text.IndexOf(value: " [sdf.mesh-bake]", comparisonType: StringComparison.Ordinal);

                    unsettled.Add(item: $"{place} ({((report > 0) ? answer.Text[..report] : answer.Text).Trim(trimChars: ['[', ']'])})");
                    return false;
                }
            }

            if (Settle(place: "its spawn")) {
                foreach (var place in places) {
                    _ = process.RunScript(lines: [$"body.pose spawn:{place} 0"], timeout: TimeSpan.FromSeconds(seconds: 60));
                    if (!Settle(place: place)) { break; }
                }
            }

            var reading = Read();

            Console.WriteLine(value: $"{Verb}: {pass} {name}: {((unsettled.Count == 0) ? $"settled at {stood} places" : $"not settled at {string.Join(separator: "; ", values: unsettled)}")}; {reading.Describe()}{((reading.Missed.Count > 0) ? $"; missed {reading.DescribeMissed(count: 8)}" : string.Empty)}");
            return reading;
        } finally {
            // A windowed World can outlive its quit; one that does not exit is stopped by its process.
            if (!process.Send(line: "quit") || (process.WaitForExit(timeout: TimeSpan.FromSeconds(seconds: 60)) is null)) {
                process.Kill();
            }
        }
    }
    // A counter's lifetime total in world.counters' answer: the whole number after the kind's name on its line, the name
    // a whole token (rungs-missed is a prefix of rungs-missed-live).
    private static long? CountOf(IEnumerable<string> lines, string kind) {
        foreach (var line in lines) {
            var at = line.IndexOf(value: kind, comparisonType: StringComparison.Ordinal);

            if ((at < 0) || (((at + kind.Length) < line.Length) && !char.IsWhiteSpace(c: line[at + kind.Length]))) { continue; }
            var rest = line.AsSpan(start: (at + kind.Length));
            var first = rest.IndexOfAnyInRange(lowInclusive: '0', highInclusive: '9');

            if (first < 0) { continue; }
            var digits = rest[first..];
            var length = digits.IndexOfAnyExceptInRange(lowInclusive: '0', highInclusive: '9');

            if (long.TryParse(s: ((length < 0) ? digits : digits[..length]), provider: CultureInfo.InvariantCulture, result: out var value)) { return value; }
        }
        return null;
    }
    // The page a consumer reads first: how to install the build from its channel, and how to start it locally and against
    // a remote authority.
    private static void WriteReadme(string output, string world, string commit, IReadOnlyList<string> extensions, IReadOnlyList<BakeTarget> targets, (string Url, string Name)? channel) {
        var executable = (OperatingSystem.IsWindows() ? "Puck.World.exe" : "./Puck.World");
        var setup = (OperatingSystem.IsWindows() ? "Puck.Launcher.Setup.exe" : "Puck.Launcher.Setup");
        var stub = (OperatingSystem.IsWindows() ? "Puck.Launcher.Stub.exe" : "./Puck.Launcher.Stub");
        var rid = System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier;
        var baked = ((targets.Count == 0)
            ? "the targets of the cache set its release carries (the release notes name it; a release ships only with a set that covers the world, and `puck world caches fetch` holds the publish otherwise)"
            : string.Join(separator: ", ", values: targets.Select(selector: static target => $"profile `{target.Profile}` at {target.Width} by {target.Height}")));
        var install = (channel is { } named)
            ? (OperatingSystem.IsWindows()
                ? $"$setup = \"$env:TEMP\\{setup}\"; Invoke-WebRequest {named.Url.TrimEnd('/')}/{named.Name}/{rid}/{setup} -OutFile $setup; & $setup {named.Url.TrimEnd('/')} --channel {named.Name}"
                : $"curl -fsSLo /tmp/{setup} {named.Url.TrimEnd('/')}/{named.Name}/{rid}/{setup} && chmod +x /tmp/{setup} && /tmp/{setup} {named.Url.TrimEnd('/')} --channel {named.Name}")
            : $"{setup} <channel address> --channel <name>";
        var signIn = ((extensions.Count == 0)
            ? "No extension is installed, so an authority that requires sign-in refuses this folder; rebuild it with the deployment's provider extension (`--extension <project>`)."
            : $"Installed in `extensions/`: {string.Join(separator: ", ", values: extensions.Select(selector: static name => $"`{name}`"))}. An authority that requires sign-in names its provider and settings in the authentication configuration its deployment publishes; the provider acquires the credential itself, from the sign-in it documents.");
        var text = $$"""
            # Puck player build

            A prepared release build of `{{world}}` for a consumer machine. It needs no .NET SDK, no shader compiler, no
            LLVM and no Rust: the engine is self-contained, the package is compiled, and its caches are prewarmed. Built
            from commit `{{commit}}` by `puck world player`; `player.json` records how.

            | Part | Holds |
            |---|---|
            | `{{executable}}` and its files | The engine and its own .NET runtime. |
            | `worlds/` | The compiled package: documents, compiled worlds, the shader package store, cartridge sources. |
            | `derived/` | The shipping cache: the AGB BIOS, the Rust addon guests, specialized shading kernels. |
            | `cache/` | The device caches: baked meshes for each backend, compiles, prepared cartridges and boot ROMs. |
            | `launcher/` | The stub that starts an installed version and the installer, each one self-contained file. |
            | `extensions/` | Installed extensions, if any. |

            ## Install it from its channel

            The build is published as a signed release; one command downloads the installer and installs the newest
            release into the per-user Puck `player` directory (`--to <directory>` picks another):

            ```text
            {{install}}
            ```

            The installer pins the channel's root key on first install and verifies every later release against it,
            downloads only the files it does not already hold, and lays the stub beside the install. Run the installed
            `{{setup}}` again to update; a running World also updates from the same channel (`update.check`,
            `update.apply`), and the stub starts the new version at its next launch. Start the install with:

            ```text
            {{stub}} --world {{world}} --cache-dir cache
            ```

            ## Start it locally

            From this folder, without installing:

            ```text
            {{executable}} --world {{world}} --cache-dir cache
            ```

            The world runs with its authority in the same process. `--cache-dir cache` points the boot at the prewarmed
            caches; without it the boot uses the per-user caches and bakes every mesh again. `--backend directx` or
            `--backend vulkan` picks the backend (each has its own baked meshes), and `--width`/`--height` set the window.
            The store holds every detail tier of every mesh the world's authored places draw, and the build was checked
            for {{baked}}; the profile follows the adapter's name. The store is a warm start: where a tier is missing
            the build draws the nearest stored one at once and bakes the missing tier into `cache/` on the memory it
            plays in.

            ## Attach to a remote authority

            ```text
            {{executable}} --world {{world}} --cache-dir cache --connect <host:port> --authentication-config-file <world-authentication.json>
            ```

            `--world` supplies the initial definition until the authority's own revision arrives. {{signIn}}

            ## What a boot reports

            The console prints `[derived] ... hit` for each shipped product it reads and `miss` for one it had to derive;
            `world.residency` and `[bakes: settled ...]` show the mesh store's state, and `world.counters sdf.mesh-cache`
            counts what the boot asked of the mesh store beyond a hit: `dispatches` and `groups-baked` are zero while it
            bakes nothing, and `rungs-missed` counts each tier it drew another stored tier in place of (`rungs-missed-live`
            those of a field state the world then moved past, which no cache holds).
            """;

        File.WriteAllText(path: Path.Combine(path1: output, path2: "README.md"), contents: text.ReplaceLineEndings(replacementText: "\n") + "\n");
    }
    private static void ReportSizes(string output) {
        static long Bytes(string path) => (Directory.Exists(path: path)
            ? Directory.EnumerateFiles(path: path, searchOption: SearchOption.AllDirectories, searchPattern: "*").Sum(selector: static file => new FileInfo(fileName: file).Length)
            : 0L);

        var cache = Path.Combine(path1: output, path2: CacheName);
        string[] parts = ["worlds", "derived", LauncherName, "extensions", LogsName];
        var named = parts.Select(selector: part => Path.Combine(path1: output, path2: part)).Append(element: cache).ToHashSet(comparer: Puck.Abstractions.PuckPaths.Comparer);
        var engine = Directory.EnumerateFileSystemEntries(path: output)
            .Where(predicate: path => !named.Contains(item: path) && !string.Equals(a: Path.GetFileName(path: path), b: MarkerName, comparisonType: StringComparison.Ordinal))
            .Sum(selector: path => (File.Exists(path: path) ? new FileInfo(fileName: path).Length : Bytes(path: path)));

        Console.WriteLine(value: $"{Verb}: engine {Megabytes(bytes: engine)}");
        foreach (var part in parts) {
            Console.WriteLine(value: $"{Verb}: {part}/ {Megabytes(bytes: Bytes(path: Path.Combine(path1: output, path2: part)))}");
        }
        if (Directory.Exists(path: cache)) {
            foreach (var part in Directory.EnumerateDirectories(path: cache).Order(comparer: StringComparer.Ordinal)) {
                Console.WriteLine(value: $"{Verb}: {CacheName}/{Path.GetFileName(path: part)}/ {Megabytes(bytes: Bytes(path: part))}");
            }
        }
        Console.WriteLine(value: $"{Verb}: total {Megabytes(bytes: Bytes(path: output))} in {CliPaths.ToDisplay(fullPath: output)}");

        static string Megabytes(long bytes) => string.Create(provider: CultureInfo.InvariantCulture, handler: $"{(bytes / 1048576.0):F1} MiB ({bytes:N0} bytes)");
    }
}
