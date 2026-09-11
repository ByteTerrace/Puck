using System.CommandLine;
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

    public static Command Create() {
        var command = new Command(description: """
            Boot the authored parity world offscreen once per backend and compare the two runs.

            Boots tests/Puck.Parity/parity.world.json offscreen once per backend (vulkan, directx — no
            window is shown), collects each run's tick-scheduled captures and puck.parity.manifest.v1, and
            compares the pair under tests/Puck.Parity/parity.contract.json.

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
            """, name: "parity");

        command.Subcommands.Add(item: ParityCompareCommand.Create());
        command.SetAction(action: _ => Run());
        return command;
    }

    private static int Run() {
        if (!CliPaths.TryGetRepositoryRoot(repositoryRoot: out var repositoryRoot)) {
            return 2;
        }

        var suiteClock = Stopwatch.StartNew();

        CliScratchDirectories.SweepScratch(scratchPrefix: ScratchPrefix);

        var runDirectory = CliScratchDirectories.CreateRunDirectory(scratchPrefix: ScratchPrefix);

        Console.WriteLine(value: $"parity: artifacts {runDirectory}");

        if (!TryBuildWorld(artifact: out var artifact, repositoryRoot: repositoryRoot, runDirectory: runDirectory, suiteClock: suiteClock)) {
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

        return ParityCompareCommand.Run(
            contractPath: Path.Combine(path1: repositoryRoot, path2: ContractPath),
            leftDir: Path.Combine(path1: runDirectory, path2: "captures-vulkan"),
            outDir: Path.Combine(path1: runDirectory, path2: "evidence"),
            rightDir: Path.Combine(path1: runDirectory, path2: "captures-directx")
        );
    }
    private static bool TryBuildWorld(string repositoryRoot, string runDirectory, Stopwatch suiteClock, out string artifact) {
        var worldProject = Path.Combine(path1: repositoryRoot, path2: "src", path3: "Puck.World", path4: "Puck.World.csproj");

        var buildDirectory = Path.Combine(runDirectory, "build");
        artifact = Path.Combine(buildDirectory, "Puck.World.dll");

        Console.WriteLine(value: "parity: building Puck.World once (Release).");

        CliProcessResult build;

        try {
            build = CliProcess.RunCaptured(
                fileName: "dotnet",
                arguments: ["build", worldProject, "-c", "Release", "--nologo", "--no-restore", "-p:NuGetAudit=false", "--output", buildDirectory],
                input: string.Empty,
                timeout: CliProcess.RemainingBudget(budget: SuiteBudget, clock: suiteClock)
            );
        } catch (Exception exception) when ((exception is InvalidOperationException or System.ComponentModel.Win32Exception)) {
            Console.Error.WriteLine(value: $"ERROR: could not start the Puck.World build: {exception.Message.ReplaceLineEndings(replacementText: " ")}");

            return false;
        }

        File.WriteAllText(Path.Combine(runDirectory, "build-stdout.log"), build.Stdout, new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(runDirectory, "build-stderr.log"), build.Stderr, new UTF8Encoding(false));
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
        script.AppendLine(value: "world.wait 1180");
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
                    // its 1180-tick wait releases. The net only has to outlast a slow machine — an offscreen leg on the
                    // RTX 2060 desktop paces one produced frame per tick and needs ~45 s for the wait alone, so 40 s
                    // (the pre-1180 value) cut legs off before wire.errors on every other run there.
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
}
