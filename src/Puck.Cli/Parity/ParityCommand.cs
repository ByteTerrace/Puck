using System.CommandLine;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Puck.World;

namespace Puck.Cli.Parity;

/// <summary><c>puck parity</c> — the cross-backend check over the authored parity world. It boots the real
/// <c>Puck.World</c> once per graphics backend with <c>host.presentation: offscreen</c> (no window is ever shown),
/// lets the world's own <c>captures</c> rows land every tick-scheduled capture and write a
/// <c>puck.parity.manifest.v1</c>, and then hands the two manifest directories to
/// <see cref="ParityCompareCommand"/> — the content-gate / exact-state-hash / per-tile-pixel comparator — under the
/// contract versioned beside the world (<c>tests/Puck.Parity/parity.contract.json</c>). Because both backends
/// capture the same simulation ticks, the pair observes one moment by construction rather than by fence.</summary>
internal static class ParityCommand {
    private const string ContractPath = "tests/Puck.Parity/parity.contract.json";
    private const string ScratchPrefix = "puck-parity-";
    private const string SdfDocumentPath = "tests/Puck.Parity/parity.sdf.json";
    // The authored source of the parity world, and the name of the document the tree compile emits from it.
    private const string ShippedWorldName = "parity.world.json";
    private const string SourcePath = "tests/Puck.Parity/parity.puck";
    // The ticks a leg runs past the world's last scheduled capture, so the frame that serves it lands first.
    private const ulong WaitMarginTicks = 30;
    // The ticks the leg turns temporal reconstruction on before the world's first converging station, so every view's
    // temporal graph has installed before that station arms.
    private const ulong ReconstructionLeadTicks = 60;

    private static readonly TimeSpan SuiteBudget = TimeSpan.FromSeconds(value: 900);

    private static int Run(bool bakes, bool debugLayers) {
        if (!CliPaths.TryGetRepositoryRoot(repositoryRoot: out var repositoryRoot)) {
            return CliExit.Refused;
        }

        using var run = RunDirectory.Create(prefix: ScratchPrefix);

        Console.WriteLine(value: $"parity: artifacts {run.Path}");

        return run.Conclude(exitCode: Run(
            bakes: bakes,
            debugLayers: debugLayers,
            repositoryRoot: repositoryRoot,
            runDirectory: run.Path
        ));
    }
    // One parity run inside its run directory, which the caller concludes with the exit code this returns.
    private static int Run(bool bakes, bool debugLayers, string repositoryRoot, string runDirectory) {
        var suiteClock = Stopwatch.StartNew();

        if (!WorldOffscreenLeg.TryResolveWorld(
            artifact: out var world,
            repositoryRoot: repositoryRoot,
            runDirectory: runDirectory,
            timeout: CliProcess.RemainingBudget(
                budget: SuiteBudget,
                clock: suiteClock
            ),
            verb: "parity"
        )) {
            return CliExit.Refused;
        }

        using var lease = world;

        // The parity world ships its bakes as a released world does: the tree compile writes its documents, their
        // compiled worlds and the one bake pack into the run, beside a copy of every other file the tree holds (the
        // graph and its sources the documents name), and the legs boot that copy, whose BAKE chunk holds every bake
        // from the pack, so no capture depends on a bake made on the device.
        if (!TryShipWorld(
            artifact: world.Path,
            repositoryRoot: repositoryRoot,
            runDirectory: runDirectory,
            suiteClock: suiteClock,
            world: out var shippedWorld
        )) {
            return CliExit.Refused;
        }

        var validated = true;

        foreach (var backend in WorldOffscreenLeg.Backends) {
            var leg = RunBackend(
                artifact: world.Path,
                backend: backend,
                bakes: bakes,
                debugLayers: debugLayers,
                shippedWorld: shippedWorld,
                runDirectory: runDirectory,
                suiteClock: suiteClock,
                validation: out var validation
            );

            if (validation is { } verdict) {
                Console.WriteLine(value: ValidationLine(backend: backend, verdict: verdict));
                validated &= verdict.Passed;
            }
            if (leg != CliExit.Success) {
                return leg;
            }
        }

        return Fold(compared: ParityCompareCommand.Run(
            contractPath: Path.Combine(
                path1: repositoryRoot,
                path2: ContractPath
            ),
            leftDir: Path.Combine(
                path1: runDirectory,
                path2: "captures-vulkan"
            ),
            outDir: Path.Combine(
                path1: runDirectory,
                path2: "evidence"
            ),
            rightDir: Path.Combine(
                path1: runDirectory,
                path2: "captures-directx"
            )
        ), validated: validated);
    }

    /// <summary>Returns the verdict line a leg booted under its backend's validation layer prints: the backend, then
    /// <c>VALIDATION-OK</c> or <c>VALIDATION-FAIL</c>, then what the layer reported.</summary>
    /// <param name="backend">The leg's backend.</param>
    /// <param name="verdict">The leg's verdict (<see cref="DebugLayerOutput.Verdict"/>).</param>
    /// <returns>The line.</returns>
    internal static string ValidationLine(string backend, DebugLayerVerdict verdict) =>
        $"parity: {backend} {(verdict.Passed ? "VALIDATION-OK" : "VALIDATION-FAIL")} {verdict.Detail}";
    /// <summary>Folds the legs' validation verdicts into the comparison's exit code: a validation failure fails a
    /// comparison that held every verdict, and leaves a failed or refused comparison as it was.</summary>
    /// <param name="compared">The comparison's exit code.</param>
    /// <param name="validated">Whether every leg booted under its validation layer reported nothing.</param>
    /// <returns>The run's exit code.</returns>
    internal static int Fold(int compared, bool validated) => (((compared == CliExit.Success) && !validated)
        ? CliExit.Failed
        : compared);
    // The tick the leg waits to, past the last tick the world's captures rows schedule, and the tick it turns temporal
    // reconstruction on at, ReconstructionLeadTicks before the first station that converges, or null for a world with
    // none: both read from the document itself, so a station added there is captured without a second statement of its
    // schedule here. Every station that does not converge must lie before the reconstruction tick, since parity's
    // existing stations hold with reconstruction off. A world that cannot be read, whose last tick leaves no room for the
    // margin, whose first converging station leaves no room for the lead, or that captures a station that does not
    // converge once reconstruction is on, is refused by name.
    internal static bool TryReadSchedule(string worldPath, out ulong waitTick, out ulong? reconstructionTick, out string error) {
        waitTick = 0UL;
        reconstructionTick = null;
        error = string.Empty;

        IReadOnlyList<WorldCaptureRow> rows;

        try {
            rows = (WorldDefinitionSerialization.Deserialize(
                documentDirectory: Path.GetDirectoryName(path: worldPath),
                utf8Json: File.ReadAllBytes(path: worldPath)
            ).Captures?.Rows ?? []);
        } catch (Exception exception) when ((exception is InvalidDataException or JsonException or IOException or UnauthorizedAccessException or InvalidOperationException or FormatException or NotSupportedException)) {
            error = $"the parity world '{worldPath}' could not be read for its capture schedule: {exception.Message.ReplaceLineEndings(replacementText: " ")}";

            return false;
        }

        var lastTick = rows.SelectMany(selector: static row => row.Ticks).DefaultIfEmpty().Max();

        if (lastTick > (ulong.MaxValue - WaitMarginTicks)) {
            error = $"the parity world '{worldPath}' schedules a capture at tick {lastTick}, which leaves no room for the {WaitMarginTicks}-tick wait margin.";

            return false;
        }

        var converging = rows.Where(predicate: static row => (row.Converge > 0)).SelectMany(selector: static row => row.Ticks).ToArray();

        if (converging.Length > 0) {
            var first = converging.Min();

            if (first <= ReconstructionLeadTicks) {
                error = $"the parity world '{worldPath}' converges a station at tick {first}, which leaves no room for the {ReconstructionLeadTicks}-tick reconstruction lead.";

                return false;
            }

            var on = (first - ReconstructionLeadTicks);

            if (rows.FirstOrDefault(predicate: row => ((row.Converge == 0) && row.Ticks.Any(predicate: tick => (tick >= on)))) is { } late) {
                error = $"the parity world '{worldPath}' captures station '{late.Station}' at or after tick {on}, when reconstruction is on, without converging; a station that does not converge holds with reconstruction off.";

                return false;
            }

            reconstructionTick = on;
        }

        waitTick = checked((lastTick + WaitMarginTicks));

        return true;
    }

    // Compiles the parity tree into the run: every document and .puck source under it, with the per-user bake cache the
    // World keeps its bakes in, so a key baked once is never baked again; every other file of the tree is copied beside.
    // The compile runs through the CLI the World artifact's own build wrote beside it, since a compiled world holds only
    // for the engine build that derived it (CompiledWorld.EngineBuild) and the World artifact's build is the one that
    // boots it.
    private static bool TryShipWorld(string artifact, string repositoryRoot, string runDirectory, Stopwatch suiteClock, out string world) {
        world = string.Empty;

        try {
            return ShipWorld(artifact: artifact, repositoryRoot: repositoryRoot, runDirectory: runDirectory, suiteClock: suiteClock, world: out world);
        } catch (Exception exception) when ((exception is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or InvalidOperationException)) {
            Console.Error.WriteLine(value: $"ERROR: the parity tree could not ship: {exception.Message.ReplaceLineEndings(replacementText: " ")}");

            return false;
        }
    }
    private static bool ShipWorld(string artifact, string repositoryRoot, string runDirectory, Stopwatch suiteClock, out string world) {
        var tree = Path.Combine(
            path1: repositoryRoot,
            path2: Path.GetDirectoryName(path: SourcePath)!
        );
        var output = Path.Combine(
            path1: runDirectory,
            path2: "world"
        );
        var sources = new List<string>();

        _ = Directory.CreateDirectory(path: output);
        world = Path.Combine(
            path1: output,
            path2: ShippedWorldName
        );

        foreach (var file in Directory.EnumerateFiles(path: tree, searchOption: SearchOption.AllDirectories, searchPattern: "*")) {
            var name = Path.GetFileName(path: file);

            if (name.EndsWith(comparisonType: StringComparison.OrdinalIgnoreCase, value: WorldDocumentName.DocumentSuffix) || name.EndsWith(comparisonType: StringComparison.OrdinalIgnoreCase, value: ".puck")) {
                sources.Add(item: file);
            } else {
                var destination = Path.Combine(
                    path1: output,
                    path2: Path.GetRelativePath(path: file, relativeTo: tree)
                );

                _ = Directory.CreateDirectory(path: Path.GetDirectoryName(path: destination)!);
                File.Copy(
                    destFileName: destination,
                    overwrite: true,
                    sourceFileName: file
                );
            }
        }

        var timeout = CliProcess.RemainingBudget(budget: SuiteBudget, clock: suiteClock);

        if (timeout <= TimeSpan.Zero) {
            Console.Error.WriteLine(value: "ERROR: the parity time budget expired before the tree could compile.");

            return false;
        }

        var compile = CliProcess.RunCaptured(
            arguments: [
                Path.Combine(
                    path1: Path.GetDirectoryName(path: artifact)!,
                    path2: "Puck.Cli.dll"
                ),
                "compile",
                "--tree", tree,
                "--output", output,
                "--bake-cache", Puck.Abstractions.PuckUserDirectory.Resolve(name: "bakes"),
                .. sources,
            ],
            fileName: "dotnet",
            input: string.Empty,
            timeout: timeout
        );

        File.WriteAllText(
            contents: (compile.Stdout + compile.Stderr),
            path: Path.Combine(
                path1: runDirectory,
                path2: "compile.log"
            )
        );

        if (compile.TimedOut || (compile.ExitCode != 0)) {
            Console.Error.WriteLine(value: $"ERROR: the parity tree did not compile ({(compile.TimedOut ? "timed out" : $"exit {compile.ExitCode.ToString(provider: CultureInfo.InvariantCulture)}")}; see compile.log beside the run), so its bakes cannot ship.");

            return false;
        }
        if (
            !File.Exists(path: world) ||
            !File.Exists(path: CompiledWorld.Beside(documentPath: world)) ||
            !File.Exists(path: Path.Combine(path1: output, path2: WorldBakePack.FileName))
        ) {
            Console.Error.WriteLine(value: $"ERROR: the parity tree compiled but did not write {world}, its compiled world and its bake pack, so its bakes cannot ship.");

            return false;
        }

        Console.WriteLine(value: $"parity: shipped the parity world with its bake pack into {output}");

        return true;
    }
    // Refuses a leg that resolved any bake on the device or drew none: with the pack every bake is held when the world
    // loads, so the schedule resolves nothing (sdf.bakes.scheduled 0), and the bakes on, the parity world's creations
    // draw them (sdf.bakes.drawn above 0).
    private static string? BakeRefusal(string stdout, bool bakes) {
        long? Count(string kind) {
            foreach (var line in stdout.ReplaceLineEndings(replacementText: "\n").Split(separator: '\n')) {
                var fields = line.Split(options: StringSplitOptions.RemoveEmptyEntries, separator: ' ');

                if ((fields.Length == 2) && string.Equals(a: fields[0], b: kind, comparisonType: StringComparison.Ordinal) && long.TryParse(provider: CultureInfo.InvariantCulture, result: out var value, s: fields[1], style: NumberStyles.None)) {
                    return value;
                }
            }

            return null;
        }

        var scheduled = Count(kind: "sdf.bakes.scheduled");
        var drawn = Count(kind: "sdf.bakes.drawn");

        if ((scheduled is null) || (drawn is null)) {
            return "the leg printed no sdf.bakes counts";
        }
        if (scheduled != 0) {
            return $"the leg resolved {scheduled} bake(s) on the device (sdf.bakes.scheduled), so a capture could depend on a local bake";
        }
        if (bakes && (drawn == 0)) {
            return "the leg drew no bake (sdf.bakes.drawn 0), so the pack's bakes never reached a capture";
        }

        return null;
    }
    private static int RunBackend(string artifact, string backend, bool bakes, bool debugLayers, string shippedWorld, string runDirectory, Stopwatch suiteClock, out DebugLayerVerdict? validation) {
        validation = null;

        var captureDirectory = Path.Combine(
            path1: runDirectory,
            path2: $"captures-{backend}"
        );

        if (!TryReadSchedule(
            error: out var scheduleError,
            reconstructionTick: out var reconstructionTick,
            waitTick: out var waitTick,
            worldPath: shippedWorld
        )) {
            Console.Error.WriteLine(value: $"ERROR: {scheduleError}");

            return CliExit.Refused;
        }

        // The parity world drives no seats and reads no input, so no controller-clearing guard is needed; the script
        // only turns the bakes off when asked (a world carrying its bakes draws them), composes the world's companion SDF
        // document, turns temporal reconstruction on at the reconstruction tick when the world converges a station, and
        // waits past the last tick its captures rows schedule. A line after a wait releasing at tick R runs before tick
        // R + 1, so the reconstruction tick is exact.
        // It closes by reading the bake counts, which the leg is then held to (BakeRefusal).
        var script = $"{(bakes ? string.Empty : "world.bakes off\n")}world.sdf.load \"{Path.Combine(
            path1: Path.GetDirectoryName(path: shippedWorld)!,
            path2: Path.GetFileName(path: SdfDocumentPath)
        ).Replace(
            newChar: '/',
            oldChar: '\\'
        )}\"\n{((reconstructionTick is { } on) ? $"world.wait {on}\nworld.temporal on\nworld.wait {(waitTick - on)}\n" : $"world.wait {waitTick}\n")}world.counters sdf.bakes\n";
        var leg = WorldOffscreenLeg.Run(
            arguments: ["--capture-dir", captureDirectory, .. DebugLayerOutput.Arguments(debugLayers: debugLayers)],
            artifact: artifact,
            backend: backend,
            budget: SuiteBudget,
            // A SAFETY NET, not the leg length: the script closes with quit, so a healthy leg ends as soon as its
            // wait releases. The net outlasts a slow machine's whole leg: an offscreen leg paces one produced
            // frame per tick (about 45 s for the wait on an RTX 2060), and the host may hold its clock at a capture for
            // at most WorldCaptureScheduler.BuildHoldBudgetSeconds while the engine's pipeline set builds on a cold
            // driver cache plus WorldCaptureScheduler.HoldBudgetSeconds once it is ready.
            exitAfterSeconds: 300,
            process: out var process,
            runDirectory: runDirectory,
            script: script,
            suiteClock: suiteClock,
            verb: "parity",
            world: shippedWorld
        );

        return EvaluateBackend(
            backend: backend,
            bakes: bakes,
            captureDirectory: captureDirectory,
            debugLayers: debugLayers,
            leg: leg,
            process: process,
            validation: out validation
        );
    }

    // Judge stderr even when the process failed, before any refusal leaves the leg.
    internal static int EvaluateBackend(string backend, bool bakes, string captureDirectory, bool debugLayers, int leg, CliProcessResult? process, out DebugLayerVerdict? validation) {
        validation = DebugLayerOutput.Verdict(
            backend: backend,
            debugLayers: debugLayers,
            stderr: (process?.OutputLines ?? [])
                .Where(predicate: static line => (line.Stream == CliProcessOutputStream.Stderr))
                .Select(selector: static line => line.Line)
        );

        if (leg != CliExit.Success) {
            return leg;
        }

        if (BakeRefusal(bakes: bakes, stdout: (process?.Stdout ?? string.Empty)) is { } refusal) {
            Console.Error.WriteLine(value: $"ERROR: the {backend} leg: {refusal}.");

            return CliExit.Failed;
        }
        if (!File.Exists(path: Path.Combine(
            path1: captureDirectory,
            path2: "manifest.json"
        ))) {
            Console.Error.WriteLine(value: $"ERROR: the {backend} leg exited green but never wrote captures-{backend}/manifest.json; transcripts are beside the captures.");

            return CliExit.Refused;
        }

        return CliExit.Success;
    }

    public static Command Create() {
        var command = new Command(
            description: "Boot the authored parity world offscreen once per backend and compare the two runs.",
            name: "parity"
        );

        command.Detail(detail: """
            Boots the world authored in tests/Puck.Parity/parity.puck offscreen once per backend (vulkan, directx — no
            window is shown), runs each leg until 30 ticks past the last tick its captures rows schedule,
            collects each run's tick-scheduled captures and puck.parity.manifest.v1, and compares the pair
            under tests/Puck.Parity/parity.contract.json.

            Per capture, four independent verdicts, in order: the content gate (a capture its
            producer refused by name, one missing, or one below its station's census floor never reaches
            comparison — agreement between degenerate frames is vacuous), the state verdict
            (stateHash equality, exact, no envelope), the tick verdict (each frame refreshed its bound
            regions at the armed tick), and the pixel verdict (per-tile deltas
            against the station's contract thresholds — a localized defect cannot dilute itself
            across a whole-frame mean). Failures write both frames, a delta heatmap, and a
            per-verdict summary beside the run.

            The parity world ships its bakes: the run compiles the parity tree with the World artifact's own
            CLI and boots the compiled world, whose BAKE chunk holds every bake from its pack, and a leg that
            resolved a bake on the device (sdf.bakes.scheduled above zero) fails, so no capture depends on a
            local bake. The static creations draw their bakes, as a world that ships them does by default. With
            --bakes off every creation draws through its field, and `puck parity compare` of an on run
            against an off run holds each capture's stateHash, since bakes are presentation only.

            With --debug-layers each leg boots its World under its backend's validation layer, as
            `puck canary --debug-layers` does, and prints one validation verdict: VALIDATION-OK, or
            VALIDATION-FAIL naming the first validation message, or the statement that the layer never
            loaded. A leg's messages cannot be attributed to one capture, so a VALIDATION-FAIL fails the
            run (exit 1) after every capture's verdicts are printed.

            Requires both a Vulkan and a Direct3D 12 device on this machine; no display is taken over.

            Exit codes: 0 every capture held every verdict, and under --debug-layers every leg's validation
            verdict held, 1 a verdict failed, 2 a leg/build refusal or a malformed manifest or contract.
            """);

        var bakesOption = new Option<string>(name: "--bakes") { DefaultValueFactory = static _ => "on", Description = "Whether the parity world's static creations draw their bakes (on, the default) or their fields (off)." };

        bakesOption.AcceptOnlyFromAmong(values: ["on", "off"]);
        var debugLayersOption = new Option<bool>(name: DebugLayerOutput.Flag) { Description = "Boot each backend's leg with --debug-layers, the validation layer of that backend, and fail the run when a leg's stderr has a validation message or says the layer never loaded." };

        command.Options.Add(item: bakesOption);
        command.Options.Add(item: debugLayersOption);
        command.Subcommands.Add(item: ParityCompareCommand.Create());
        command.SetAction(action: result => Run(
            bakes: string.Equals(a: result.GetValue(option: bakesOption), b: "on", comparisonType: StringComparison.Ordinal),
            debugLayers: result.GetValue(option: debugLayersOption)
        ));
        return command;
    }
}
