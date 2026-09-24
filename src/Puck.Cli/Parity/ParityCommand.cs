using System.CommandLine;
using System.Diagnostics;

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
    private const string WorldPath = "tests/Puck.Parity/parity.world.json";

    private static readonly TimeSpan SuiteBudget = TimeSpan.FromSeconds(value: 600);

    private static int Run() {
        if (!CliPaths.TryGetRepositoryRoot(repositoryRoot: out var repositoryRoot)) {
            return CliExit.Refused;
        }

        var suiteClock = Stopwatch.StartNew();

        CliScratchDirectories.SweepScratch(scratchPrefix: ScratchPrefix);

        var runDirectory = Directory.CreateTempSubdirectory(prefix: ScratchPrefix).FullName;

        Console.WriteLine(value: $"parity: artifacts {runDirectory}");

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

        foreach (var backend in WorldOffscreenLeg.Backends) {
            var leg = RunBackend(
                artifact: world.Path,
                backend: backend,
                repositoryRoot: repositoryRoot,
                runDirectory: runDirectory,
                suiteClock: suiteClock
            );

            if (leg != CliExit.Success) {
                return leg;
            }
        }

        return ParityCompareCommand.Run(
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
        );
    }
    // Boots one offscreen leg on the named backend; the parity world's own captures rows land every scheduled
    // frame and write the manifest. Returns CliExit.Success with the manifest written, or CliExit.Refused with the
    // refusal already reported.
    private static int RunBackend(string artifact, string backend, string repositoryRoot, string runDirectory, Stopwatch suiteClock) {
        var captureDirectory = Path.Combine(
            path1: runDirectory,
            path2: $"captures-{backend}"
        );
        // The parity world drives no seats and reads no input, so no controller-clearing guard is needed; the script
        // only composes the world's companion SDF document and waits past the last scheduled capture tick.
        var script = $"world.sdf.load {Path.Combine(
            path1: repositoryRoot,
            path2: SdfDocumentPath
        ).Replace(
            newChar: '\\',
            oldChar: '/'
        )}\nworld.wait 1180\n";
        var leg = WorldOffscreenLeg.Run(
            arguments: ["--capture-dir", captureDirectory],
            artifact: artifact,
            backend: backend,
            budget: SuiteBudget,
            // A SAFETY NET, not the leg length: the script closes with quit, so a healthy leg ends as soon as its
            // 1180-tick wait releases. The net only has to outlast a slow machine — an offscreen leg on the RTX 2060
            // desktop paces one produced frame per tick and needs ~45 s for the wait alone, so 40 s (the pre-1180
            // value) cut legs off before wire.errors on every other run there. A cold driver shader cache adds at most
            // the capture hold budget (WorldCaptureScheduler.HoldBudgetSeconds, 60 s) while the host holds its clock
            // at the first capture, which still ends inside this net.
            exitAfterSeconds: 150,
            process: out _,
            runDirectory: runDirectory,
            script: script,
            suiteClock: suiteClock,
            verb: "parity",
            world: Path.Combine(
                path1: repositoryRoot,
                path2: WorldPath
            )
        );

        if (leg != CliExit.Success) {
            return leg;
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
            Boots tests/Puck.Parity/parity.world.json offscreen once per backend (vulkan, directx — no
            window is shown), collects each run's tick-scheduled captures and puck.parity.manifest.v1, and
            compares the pair under tests/Puck.Parity/parity.contract.json.

            Per capture, three independent verdicts, in order: the content gate (a capture its
            producer refused by name, one missing, or one below its station's census floor never reaches
            comparison — agreement between degenerate frames is vacuous), the state verdict
            (stateHash equality, exact, no envelope), and the pixel verdict (per-tile deltas
            against the station's contract thresholds — a localized defect cannot dilute itself
            across a whole-frame mean). Failures write both frames, a delta heatmap, and a
            per-verdict summary beside the run.

            Requires both a Vulkan and a Direct3D 12 device on this machine; no display is taken over.

            Exit codes: 0 every capture held all three verdicts, 1 a verdict failed, 2 a leg/build
            refusal or a malformed manifest or contract.
            """);

        command.Subcommands.Add(item: ParityCompareCommand.Create());
        command.SetAction(action: _ => Run());
        return command;
    }
}
