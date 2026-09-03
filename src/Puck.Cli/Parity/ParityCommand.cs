using System.Diagnostics;
using System.Text;

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

    public static int Run(string[] args) {
        // Dispatched before the -h/--help check below: 'compare' owns its own usage text and exit code (3, not
        // this verb's 2), so its help must never fall through to this verb's Usage().
        if ((args.Length != 0) && (args[0] == "compare")) {
            return ParityCompareCommand.Run(args: args[1..]);
        }
        if ((args.Length != 0) || (Array.IndexOf(array: args, value: "-h") >= 0) || (Array.IndexOf(array: args, value: "--help") >= 0)) {
            return Usage();
        }
        if (!CliPaths.TryGetRepositoryRoot(repositoryRoot: out var repositoryRoot)) {
            return 2;
        }

        var suiteClock = Stopwatch.StartNew();

        CliScratchDirectories.SweepScratch(scratchPrefix: ScratchPrefix);

        var runDirectory = CliScratchDirectories.CreateRunDirectory(scratchPrefix: ScratchPrefix);

        Console.WriteLine(value: $"parity: artifacts {runDirectory}");

        if (!TryBuildWorld(artifact: out var artifact, repositoryRoot: repositoryRoot, suiteClock: suiteClock)) {
            return 2;
        }

        foreach (var backend in ((string[])["vulkan", "directx"])) {
            var leg = RunBackend(
                artifact: artifact,
                backend: backend,
                repositoryRoot: repositoryRoot,
                runDirectory: runDirectory,
                suiteClock: suiteClock
            );

            if (leg != 0) {
                return leg;
            }
        }

        return ParityCompareCommand.Run(args: [
            Path.Combine(path1: runDirectory, path2: "captures-vulkan"),
            Path.Combine(path1: runDirectory, path2: "captures-directx"),
            "--contract", Path.Combine(path1: repositoryRoot, path2: ContractPath),
            "--out", Path.Combine(path1: runDirectory, path2: "evidence"),
        ]);
    }

    private static bool TryBuildWorld(string repositoryRoot, Stopwatch suiteClock, out string artifact) {
        var worldProject = Path.Combine(path1: repositoryRoot, path2: "src", path3: "Puck.World", path4: "Puck.World.csproj");

        artifact = Path.Combine(paths: [repositoryRoot, "src", "Puck.World", "bin", "Release", "net10.0", "Puck.World.dll"]);

        Console.WriteLine(value: "parity: building Puck.World once (Release).");

        CliProcessResult build;

        try {
            build = CliProcess.RunCaptured(
                fileName: "dotnet",
                arguments: ["build", worldProject, "-c", "Release", "--nologo", "--no-restore", "-p:NuGetAudit=false"],
                input: string.Empty,
                timeout: CliProcess.RemainingBudget(budget: SuiteBudget, clock: suiteClock)
            );
        } catch (Exception exception) when ((exception is InvalidOperationException or System.ComponentModel.Win32Exception)) {
            Console.Error.WriteLine(value: $"ERROR: could not start the Puck.World build: {exception.Message.ReplaceLineEndings(replacementText: " ")}");

            return false;
        }

        if (build.TimedOut || (build.ExitCode != 0)) {
            Console.Error.WriteLine(value: (build.TimedOut
                ? $"ERROR: the Puck.World build exceeded the {SuiteBudget.TotalSeconds:0}-second whole-suite budget."
                : $"ERROR: the Puck.World build exited {build.ExitCode}."));

            return false;
        }
        if (!File.Exists(path: artifact)) {
            Console.Error.WriteLine(value: $"ERROR: the Puck.World build exited 0 but did not produce the exact artifact {artifact}.");

            return false;
        }

        return true;
    }
    // Boots one offscreen leg on the named backend; the parity world's own captures rows land every scheduled
    // frame and write the manifest. Returns 0 with the manifest written, or 2 with the refusal already reported.
    private static int RunBackend(string artifact, string backend, string repositoryRoot, string runDirectory, Stopwatch suiteClock) {
        var captureDirectory = Path.Combine(path1: runDirectory, path2: $"captures-{backend}");
        var script = new StringBuilder();

        // The parity world drives no seats and reads no input, so no controller-clearing guard is needed; the
        // script only composes the world's companion SDF document, waits past the last scheduled capture tick,
        // and closes with the runner-owned exact-count check.
        script.AppendLine(value: $"world.sdf.load {Path.Combine(path1: repositoryRoot, path2: SdfDocumentPath).Replace(newChar: '\\', oldChar: '/')}");
        script.AppendLine(value: "world.wait 1050");
        script.AppendLine(value: "wire.errors");
        // quit ends the leg the moment the script has run instead of idling out the exit budget below.
        script.AppendLine(value: "quit");

        var remaining = CliProcess.RemainingBudget(budget: SuiteBudget, clock: suiteClock);

        if (remaining <= TimeSpan.FromSeconds(value: 1)) {
            Console.Error.WriteLine(value: $"ERROR: the {SuiteBudget.TotalSeconds:0}-second whole-suite budget was exhausted before the {backend} leg started.");

            return 2;
        }

        Console.WriteLine(value: $"parity: running the parity world offscreen on {backend}.");

        CliProcessResult process;

        try {
            process = CliProcess.RunCaptured(
                fileName: "dotnet",
                arguments: [
                    artifact,
                    "--world", Path.Combine(path1: repositoryRoot, path2: WorldPath),
                    "--backend", backend,
                    // A SAFETY NET, not the leg length: the script closes with quit, so a healthy leg ends as soon as
                    // its 1050-tick wait releases. The net only has to outlast a slow machine — an offscreen leg on the
                    // RTX 2060 desktop paces one produced frame per tick and needs ~40 s for the wait alone, so 40 s
                    // (the old value) cut legs off before wire.errors on every other run there.
                    "--exit-after-seconds", "150",
                    "--state-dir", Path.Combine(path1: runDirectory, path2: $"state-{backend}"),
                    "--capture-dir", captureDirectory,
                ],
                input: script.ToString(),
                timeout: remaining
            );
        } catch (Exception exception) when ((exception is InvalidOperationException or System.ComponentModel.Win32Exception)) {
            Console.Error.WriteLine(value: $"ERROR: could not start the {backend} leg: {exception.Message.ReplaceLineEndings(replacementText: " ")}");

            return 2;
        }

        File.WriteAllText(
            path: Path.Combine(path1: runDirectory, path2: $"{backend}-stdout.log"),
            contents: process.Stdout,
            encoding: new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)
        );
        File.WriteAllText(
            path: Path.Combine(path1: runDirectory, path2: $"{backend}-stderr.log"),
            contents: process.Stderr,
            encoding: new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)
        );

        if (process.TimedOut || (process.ExitCode != 0)) {
            Console.Error.WriteLine(value: (process.TimedOut
                ? $"ERROR: the {backend} leg exceeded the remaining suite budget."
                : $"ERROR: the {backend} leg exited {process.ExitCode}; transcripts are beside the captures."));

            return 2;
        }

        var terminal = process.OutputLines.LastOrDefault(predicate: static line => line.Line.StartsWith(comparisonType: StringComparison.Ordinal, value: "[wire.errors:"));

        if ((terminal is null) || (terminal.Stream != CliProcessOutputStream.Stdout) || !string.Equals(a: terminal.Line, b: "[wire.errors: 0 rejected]", comparisonType: StringComparison.Ordinal)) {
            Console.Error.WriteLine(value: $"ERROR: the {backend} leg did not accept every scripted command (expected exactly '[wire.errors: 0 rejected]' on stdout); transcripts are beside the captures.");

            return 2;
        }
        if (!File.Exists(path: Path.Combine(path1: captureDirectory, path2: "manifest.json"))) {
            Console.Error.WriteLine(value: $"ERROR: the {backend} leg exited green but never wrote captures-{backend}/manifest.json; transcripts are beside the captures.");

            return 2;
        }

        return 0;
    }
    private static int Usage() {
        Console.Error.WriteLine(
            value:
                """
                parity
                parity compare <leftDir> <rightDir> --contract <file> [--out <dir>]

                  no arguments  boot tests/Puck.Parity/parity.world.json offscreen once per backend
                                (vulkan, directx — no window is shown), collect each run's
                                tick-scheduled captures and puck.parity.manifest.v1, and compare the
                                pair under tests/Puck.Parity/parity.contract.json
                  compare       gate/state/pixel-verdict comparison of two already-captured manifest
                                runs (run 'parity compare -h' for its own usage)

                Per capture, three independent verdicts, in order: the content gate (a capture refused
                as camera-inside-geometry, missing, or below its station's census floor never reaches
                comparison — agreement between degenerate frames is vacuous), the state verdict
                (stateHash equality, exact, no envelope), and the pixel verdict (per-tile deltas
                against the station's contract thresholds — a localized defect cannot dilute itself
                across a whole-frame mean). Failures write both frames, a delta heatmap, and a
                per-verdict summary beside the run.

                Requires both a Vulkan and a Direct3D 12 device on this machine; no display is taken over.

                Exit codes: 0 every capture held all three verdicts, 2 a verdict failed or a leg/build
                refused, 3 malformed manifest or contract.
                """);

        return 2;
    }
}
