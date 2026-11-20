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
/// kernel bytecode, never by the adapter, and the per-device mesh tiers a profile and window size demand are covered by
/// baking once per <c>--target</c>. The driver's own pipeline cache is per adapter and driver, so it is never shipped.</remarks>
public static class WorldPlayerCommand {
    private const string Verb = "world player";
    // Written last, so a folder holding it is a whole player build; a rerun replaces only a folder that holds it.
    private const string MarkerName = "player.json";
    private const string CacheName = "cache";
    private const string LogsName = "player-logs";
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
        var output = CliOptions.Output(description: "The player folder, created if missing. A folder that already holds a player build is replaced; any other non-empty folder is refused.", required: true);
        var extensions = new Option<string[]>(name: "--extension") { AllowMultipleArgumentsPerToken = true, Description = "An extension project to install beside the engine (extensions/<name>/), such as src/Puck.World.Azure for a hosted authority's sign-in. Repeatable." };
        var backends = new Option<string[]>(name: "--backend") { AllowMultipleArgumentsPerToken = true, Description = "The backends whose caches are prewarmed, each baked in its own windowed boot: directx and vulkan. Omitted bakes both on Windows and vulkan elsewhere." };
        var targets = new Option<string[]>(name: "--target") { AllowMultipleArgumentsPerToken = true, Description = "A baking boot's device profile and window size, [profile@]WIDTHxHEIGHT (rtx2060@1920x1080); a profile is a name of the world's render.devices, or auto for this adapter's own. Repeatable; omitted bakes once at auto@2560x1440." };
        var noBake = new Option<bool>(name: "--no-bake") { Description = "Skip the baking boots: the folder carries the engine, the compiled package and the shipping cache, and its mesh store is empty." };
        var bakeSeconds = CliOptions.Count(defaultValue: 600, description: "Seconds each baking boot waits for its bakes to settle, 1 to 600.", maximum: 600, minimum: 1, name: "--bake-seconds");
        var command = new Command(name: "player", description: "Assemble the folder a consumer machine runs: a self-contained Release engine, the package compiled, and its caches prewarmed (the AGB BIOS and Rust guests, specialized shading kernels, compiled worlds and compiles, prepared cartridges and the baked mesh store per backend and target).") {
            package, output, extensions, backends, targets, noBake, bakeSeconds,
        };

        command.Detail(detail: """
            Steps, in order: build Puck.World in Release (SPIR-V included) under the build lease and publish it
            self-contained for this machine's runtime, which runs its player prewarm (the AGB BIOS and every Rust guest
            into derived/; needs clang, ld.lld and Rust with wasm32-unknown-unknown); publish each --extension into
            extensions/<name>/; compile the package's parent directory into worlds/ (documents, compiled worlds and the
            shader package store); compose the package's worlds and export their specialized shading kernels into
            derived/; then boot the published engine windowed, paused at its first tick, once per backend and --target,
            with --cache-dir cache, applying the target's device profile and waiting for its bakes to settle. Those boots
            fill cache/ (meshes, compiled-worlds, compilations, derived) with the identities a consumer's boot of the same
            folder derives. A consumer starts the folder with --world worlds/<package> --cache-dir cache.
            Writes player.json last (the commit, runtime, backends and targets) and prints each part's size. Each boot's
            logs are in player-logs/. Exit 0 wrote a whole player build, 1 a step failed, 2 invalid arguments.
            """);
        command.SetAction(action: parse => Run(
            backendNames: parse.GetValue(option: backends) ?? [],
            bake: !parse.GetValue(option: noBake),
            bakeSeconds: parse.GetValue(option: bakeSeconds),
            extensions: parse.GetValue(option: extensions) ?? [],
            output: Path.GetFullPath(path: parse.GetRequiredValue(option: output)),
            package: Path.GetFullPath(path: parse.GetRequiredValue(argument: package)),
            targetNames: parse.GetValue(option: targets) ?? []
        ));
        return command;
    }

    private static int Run(string package, string output, IReadOnlyList<string> extensions, IReadOnlyList<string> backendNames, IReadOnlyList<string> targetNames, bool bake, int bakeSeconds) {
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
            Directory.Delete(path: output, recursive: true);
        }
        _ = Directory.CreateDirectory(path: output);

        var logs = Path.Combine(path1: output, path2: LogsName);

        try {
            Publish(repository: repository, output: output);
            foreach (var extension in extensions) {
                PublishExtension(repository: repository, project: extension, output: output);
            }

            Console.WriteLine(value: $"{Verb}: compiling {CliPaths.ToDisplay(fullPath: worldsRoot)} into worlds/.");
            if (CompileCommand.Create().Parse(args: ["--tree", worldsRoot, "--output", Path.Combine(path1: output, path2: "worlds")]).Invoke() != 0) {
                throw new InvalidOperationException(message: "compiling the worlds directory failed; its errors are above.");
            }

            var composed = WorldPrepareCommand.Compose(primary: entry);

            WorldPrepareCommand.PrepareShading(output: output, sets: composed.ShadeSets);
            PrepareCartridges(cache: Path.Combine(path1: output, path2: CacheName, path3: "derived"), composed: composed);

            var world = Path.Combine(path1: output, path2: "worlds", path3: Path.GetFileName(path: Path.TrimEndingDirectorySeparator(path: package)));

            var verified = new JsonObject();

            if (bake) {
                foreach (var backend in backends) {
                    foreach (var target in targets) {
                        var (baking, loading, bakes) = Bake(backend: backend, bakeSeconds: bakeSeconds, logs: logs, output: output, pass: "bake", target: target, world: world);

                        verified[$"bake {WorldBackendTokens.Token(backend: backend)}-{target}"] = new JsonObject { ["baked"] = baking, ["loaded"] = loading, ["bakes"] = bakes };
                    }
                }
                // A second boot of each pair over the finished store is what a consumer's boot does: every group it demands
                // should load. The counts it reports are the evidence the store hits.
                foreach (var backend in backends) {
                    foreach (var target in targets) {
                        var (baking, loading, bakes) = Bake(backend: backend, bakeSeconds: bakeSeconds, logs: logs, output: output, pass: "check", target: target, world: world);

                        verified[$"check {WorldBackendTokens.Token(backend: backend)}-{target}"] = new JsonObject { ["baked"] = baking, ["loaded"] = loading, ["bakes"] = bakes };
                    }
                }
            }

            var commit = CliProcess.RunCheckedAsync(arguments: ["rev-parse", "HEAD"], capture: true, fileName: "git", workingDirectory: repository).GetAwaiter().GetResult().Trim();

            WriteReadme(commit: commit, extensions: extensions, output: output, targets: (bake ? targets : []), world: Puck.Abstractions.PuckPaths.Relative(path: world, relativeTo: output));
            CliFiles.WriteJson(path: Path.Combine(path1: output, path2: MarkerName), value: new JsonObject {
                ["schema"] = "puck.player-build.v1",
                ["commit"] = commit,
                ["runtime"] = System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier,
                ["world"] = Puck.Abstractions.PuckPaths.Relative(path: world, relativeTo: output),
                ["cacheDirectory"] = CacheName,
                ["backends"] = new JsonArray([.. backends.Select(selector: static backend => JsonValue.Create(value: WorldBackendTokens.Token(backend: backend)))]),
                ["targets"] = new JsonArray([.. (bake ? targets : []).Select(selector: static target => JsonValue.Create(value: target.ToString()))]),
                ["extensions"] = new JsonArray([.. extensions.Select(selector: static extension => JsonValue.Create(value: Path.GetFileNameWithoutExtension(path: Path.TrimEndingDirectorySeparator(path: extension))))]),
                ["checked"] = verified,
            });
            ReportSizes(output: output);
            return 0;
        } catch (Exception exception) when (exception is InvalidOperationException or InvalidDataException or IOException or TimeoutException or UnauthorizedAccessException) {
            Console.Error.WriteLine(value: $"{Verb}: {exception.Message}");
            return 1;
        }
    }

    // One Release publish with the World's own self-contained switch (PuckSelfContained, see Puck.World.csproj): a global
    // SelfContained or RuntimeIdentifier would flow into every referenced project's locked restore. Puck.World's own publish
    // target runs the player prewarm into derived/.
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

        Console.WriteLine(value: $"{Verb}: publishing Puck.World (Release, SPIR-V included) self-contained for {System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier}.");
        _ = CliProcess.RunCheckedAsync(
            arguments: ["publish", CliOptions.NoNodeReuse, "-m:2", "--disable-build-servers", project, "-c", "Release", "--nologo",
                "-p:PuckSelfContained=true", "-p:PuckShaderSpirvEnabled=true", "-o", output],
            fileName: "dotnet",
            timeout: BuildTimeout,
            workingDirectory: repository
        ).GetAwaiter().GetResult();
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
    // An extension installs as the host discovers one (PuckExtensionDiscovery): extensions/<name>/<name>.dll beside its
    // dependency manifest.
    private static void PublishExtension(string repository, string project, string output) {
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
    }
    // One windowed boot of the published engine, held at its first tick so the view is the spawn's, with the folder's own
    // cache root: it applies the target's device profile (whose memory bars set the mesh budget) at the target's window
    // size (whose pixels set the tiers the bake demands) and waits until every resident bake has landed. Returns the groups
    // its bakes baked and the groups they loaded from the store, summed over every bake start it reported, and its settle
    // answer.
    private static (int Baking, int Loading, string Bakes) Bake(WorldBackendPreference backend, BakeTarget target, string output, string world, string logs, int bakeSeconds, string pass) {
        var name = $"{WorldBackendTokens.Token(backend: backend)}-{target.Profile}-{target.Width}x{target.Height}";
        var directory = Path.Combine(path1: logs, path2: $"{pass}-{name}");
        var deadline = TimeSpan.FromSeconds(seconds: (bakeSeconds + 300));
        var request = new WorldLaunchRequest(Artifact: Path.Combine(path1: output, path2: "Puck.World.dll"), Step: Verb) {
            Arguments = ["--paused", "--width", target.Width.ToString(provider: CultureInfo.InvariantCulture), "--height", target.Height.ToString(provider: CultureInfo.InvariantCulture),
                "--cache-dir", Path.Combine(path1: output, path2: CacheName)],
            Backend = backend,
            Executable = Path.Combine(path1: output, path2: (OperatingSystem.IsWindows() ? "Puck.World.exe" : "Puck.World")),
            ExitAfterSeconds = WorldLaunch.Backstop(deadline: deadline),
            Shape = WorldShape.Windowed,
            StateDirectory = Path.Combine(path1: directory, path2: "state"),
            WorkingDirectory = output,
            World = world,
        };

        Console.WriteLine(value: $"{Verb}: {pass} {name}.");

        WorldProcess started;

        using (var launch = WorldLaunch.Admit(request: request)) {
            started = launch.Start(logDirectory: directory);
        }
        using var process = started;

        try {
            _ = process.RunScript(lines: ["world.wait ready 300"], timeout: TimeSpan.FromSeconds(seconds: 320));
            if (!string.Equals(a: target.Profile, b: "auto", comparisonType: StringComparison.Ordinal)) {
                var applied = process.RunScript(lines: [$"world.device-profile {target.Profile}"], timeout: TimeSpan.FromSeconds(seconds: 60));

                var answer = applied.Lines.FirstOrDefault(predicate: static line => line.Text.StartsWith(comparisonType: StringComparison.Ordinal, value: "[world.device-profile:"))?.Text;

                if ((answer is null) || answer.Contains(value: "authors no profile", comparisonType: StringComparison.Ordinal)) {
                    throw new InvalidOperationException(message: $"{name}: the World did not apply the profile {target.Profile}: {answer ?? "no answer"}");
                }
                Console.WriteLine(value: $"{Verb}: {answer}");
            }

            var mark = process.LineCount;

            _ = process.RunScript(lines: [string.Create(provider: CultureInfo.InvariantCulture, handler: $"world.wait bakes {bakeSeconds}")], timeout: TimeSpan.FromSeconds(seconds: (bakeSeconds + 60)));

            var baked = process.WaitFor(accept: static row => row.Text.StartsWith(comparisonType: StringComparison.Ordinal, value: "[bakes:"), from: mark, timeout: TimeSpan.FromSeconds(seconds: 60));

            var lines = process.LinesFrom(from: 0);

            // Every bake the boot started has landed once a done line follows the last start. The settle barrier also waits
            // for the detail to stop moving between tiers, which a profile whose memory bars press the mesh budget may not
            // reach; the store then holds every tier the boot demanded, and the answer is kept as the boot's record.
            if ((baked is null) ||
                (lines.LastOrDefault(predicate: static row => row.Text.StartsWith(comparisonType: StringComparison.Ordinal, value: "[sdf.mesh-bake] start ")) is { } last &&
                 !lines.Any(predicate: row => (row.Index > last.Index) && row.Text.StartsWith(comparisonType: StringComparison.Ordinal, value: "[sdf.mesh-bake] done ")))) {
                throw new InvalidOperationException(message: $"{name}: the bakes did not land: {baked?.Text ?? "no answer"}; its logs are in {CliPaths.ToDisplay(fullPath: directory)}.");
            }
            var (baking, loading) = (0, 0);

            foreach (var line in lines.Where(predicate: static row => row.Text.StartsWith(comparisonType: StringComparison.Ordinal, value: "[sdf.mesh-bake] start "))) {
                baking += CliLineFields.Count(line: line.Text, name: "baking");
                loading += CliLineFields.Count(line: line.Text, name: "loading");
            }

            // The answer without the residency report a not-settled answer appends after its reason.
            var report = baked.Text.IndexOf(value: ": [sdf.mesh-bake]", comparisonType: StringComparison.Ordinal);
            var bakes = ((report > 0) ? baked.Text[..report] : baked.Text);

            Console.WriteLine(value: $"{Verb}: {pass} {name}: {bakes}; groups baked {baking}, loaded from the store {loading}");
            return (baking, loading, bakes);
        } finally {
            // A windowed World can outlive its quit; one that does not exit is stopped by its process.
            if (!process.Send(line: "quit") || (process.WaitForExit(timeout: TimeSpan.FromSeconds(seconds: 60)) is null)) {
                process.Kill();
            }
        }
    }
    // The page a consumer reads first: how to start the folder locally and against a remote authority.
    private static void WriteReadme(string output, string world, string commit, IReadOnlyList<string> extensions, IReadOnlyList<BakeTarget> targets) {
        var executable = (OperatingSystem.IsWindows() ? "Puck.World.exe" : "./Puck.World");
        var baked = ((targets.Count == 0) ? "none (the store is empty, so the first boot bakes)" : string.Join(separator: ", ", values: targets));
        var signIn = (extensions.Any(predicate: static extension => extension.Contains(value: "Puck.World.Azure", comparisonType: StringComparison.OrdinalIgnoreCase))
            ? "The Azure sign-in extension is installed in `extensions/`. Sign in with an account in the deployment's user group (the provider uses the ambient Azure sign-in, such as `az login`), and take `world-authentication.json` from the deployed world release; it names the tenant, the API and the host's public key fingerprint."
            : "No sign-in extension is installed, so a remote authority that requires sign-in refuses this folder; rebuild it with `--extension src/Puck.World.Azure`.");
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
            | `cache/` | The device caches: baked meshes, compiled worlds, compiles, prepared cartridges. Baked for: {{baked}}. |
            | `extensions/` | Installed extensions, if any. |

            ## Start it locally

            From this folder:

            ```text
            {{executable}} --world {{world}} --cache-dir cache
            ```

            The world runs with its authority in the same process. `--cache-dir cache` points the boot at the prewarmed
            caches; without it the boot uses the per-user caches and bakes every mesh again. `--backend directx` or
            `--backend vulkan` picks the backend (each has its own baked meshes), and `--width`/`--height` set the window.
            A boot on a device or window size the caches were not baked for bakes the tiers it lacks, once, into `cache/`.

            ## Attach to a remote authority

            ```text
            {{executable}} --world {{world}} --cache-dir cache --connect <host:port> --authentication-config-file <world-authentication.json>
            ```

            `--world` supplies the initial definition until the authority's own revision arrives. {{signIn}}

            ## What a boot reports

            The console prints `[derived] ... hit` for each shipped product it reads and `miss` for one it had to derive;
            `world.residency` and `[bakes: settled ...]` show the mesh store's state.
            """;

        File.WriteAllText(path: Path.Combine(path1: output, path2: "README.md"), contents: text.ReplaceLineEndings(replacementText: "\n") + "\n");
    }
    private static void ReportSizes(string output) {
        static long Bytes(string path) => (Directory.Exists(path: path)
            ? Directory.EnumerateFiles(path: path, searchOption: SearchOption.AllDirectories, searchPattern: "*").Sum(selector: static file => new FileInfo(fileName: file).Length)
            : 0L);

        var cache = Path.Combine(path1: output, path2: CacheName);
        string[] parts = ["worlds", "derived", "extensions", LogsName];
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
