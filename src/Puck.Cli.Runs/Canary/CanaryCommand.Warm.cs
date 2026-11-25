using System.Globalization;
using System.Text;

namespace Puck.Cli.Canary;

public static partial class CanaryCommand {
    /// <summary>The whole timeout of one warm boot: long enough for a cold driver to build the engine's pipeline set
    /// (<c>WorldCaptureScheduler.BuildHoldBudgetSeconds</c>), which a leg's 60-second ceiling is not.</summary>
    public const int WarmSeconds = 180;

    // The run-directory name of the warm boots, which no manifest id can take: ids are lowercase words and hyphens.
    private const string WarmRunId = "_warm";
    // The seconds a warm boot waits for its one capture to land once the engine is ready.
    private const int WarmCaptureSeconds = 20;

    /// <summary>Returns the file name a warm boot on <paramref name="backend"/> captures into its run directory: one
    /// frame, read back through the display encode, so the encode's pipeline is in the cache every leg starts from and
    /// no leg's first capture builds it cold.</summary>
    /// <param name="backend">The backend the boot runs on.</param>
    /// <returns>The capture's file name.</returns>
    internal static string WarmCaptureName(string backend) => $"{backend}-encode.png";

    /// <summary>The world a run boots once per backend, before any leg, so every GPU leg starts from a pipeline cache
    /// that already holds the engine's pipelines instead of building them on a cold driver under its own timeout.</summary>
    /// <param name="Manifest">The first selected offscreen manifest whose positive leg is one plain World process; its
    /// positive world is the one booted.</param>
    /// <param name="Backends">The backends the selection's GPU proofs boot, in the order the run warms them.</param>
    public sealed record CanaryWarm(CanaryManifest Manifest, IReadOnlyList<string> Backends);

    /// <summary>The warm a selection's proofs need, or <see langword="null"/> when none boots a GPU backend or no
    /// offscreen proof has a plain positive leg to boot.</summary>
    /// <param name="proofs">The selected proofs, in authored order.</param>
    /// <returns>The warm, or <see langword="null"/>.</returns>
    internal static CanaryWarm? WarmOf(IReadOnlyList<CanaryProof> proofs) {
        var source = proofs
            .Select(selector: static proof => proof.Manifest)
            .FirstOrDefault(predicate: static manifest => ((manifest.BootShape == CanaryBootShape.Offscreen) && IsPlainLeg(leg: manifest.Positive)));
        string[] backends = [.. proofs
            .Where(predicate: static proof => ((proof.Backend is not null) && (proof.Manifest.BootShape is CanaryBootShape.Offscreen or CanaryBootShape.Windowed)))
            .Select(selector: static proof => proof.Backend!)
            .Distinct(comparer: StringComparer.Ordinal)];

        return (((source is null) || (backends.Length == 0))
            ? null
            : new CanaryWarm(
                Backends: backends,
                Manifest: source
            ));
    }

    // A leg the runner boots as one World on its authored world alone: no authorities, companion, connection, entry,
    // package or hidden compiler.
    private static bool IsPlainLeg(CanaryLeg leg) =>
        ((leg.Authorities.Count == 0) && (leg.AuthorityWorldPath is null) && !leg.Connect && (leg.Entry is null) && (leg.Package is null) && !leg.HideShaderCompiler);
    // Boots the warm world once per backend into one shared state directory, each boot waiting for the engine to be
    // ready, capturing one frame through the display encode and reading its pipeline-cache counts. The boots fill the
    // device cache root's pipeline caches, which every leg's World reads whatever its state directory. The first boot
    // that fails (WarmRefusal) ends the warm with its refusal, and the selection fails without starting a leg.
    private static string? WarmPipelineCache(CanaryWarm warm, string artifact, CanaryBudget budget) {
        // A warm that refuses keeps its transcripts, named; one that succeeds is deleted.
        using var run = RunDirectory.Create(prefix: $"{ScratchPrefix}{WarmRunId}-pipeline-cache-");
        var runDirectory = run.Path;
        var stateDirectory = Path.Combine(
            path1: runDirectory,
            path2: "state"
        );
        var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

        foreach (var backend in warm.Backends) {
            budget.Cancellation.ThrowIfCancellationRequested();
            using var launch = WorldLaunch.Admit(cancellationToken: budget.Cancellation, request: LegRequest(artifact: artifact, backend: backend, debugLayers: false, manifest: warm.Manifest) with {
                ExitAfterSeconds = WarmSeconds,
                StateDirectory = stateDirectory,
                World = warm.Manifest.Positive.WorldPath,
            });

            budget.Tally.WarmStarted();

            var process = launch.Run(
                cancellationToken: budget.Cancellation,
                input: string.Join(
                    separator: Environment.NewLine,
                    values: [
                        $"world.wait ready {((WarmSeconds - 20) - WarmCaptureSeconds).ToString(provider: CultureInfo.InvariantCulture)}",
                        $"world.screenshot {Puck.Abstractions.PuckPaths.Slashed(path: Path.Combine(path1: runDirectory, path2: WarmCaptureName(backend: backend)))}",
                        $"world.wait captures {WarmCaptureSeconds.ToString(provider: CultureInfo.InvariantCulture)}",
                        $"world.counters pipeline-cache.{backend}",
                        RunnerQuit,
                    ]
                ),
                timeout: TimeSpan.FromSeconds(value: WarmSeconds)
            );

            File.WriteAllText(
                contents: process.Stdout,
                encoding: utf8,
                path: Path.Combine(
                    path1: runDirectory,
                    path2: $"{backend}-stdout.log"
                )
            );
            File.WriteAllText(
                contents: process.Stderr,
                encoding: utf8,
                path: Path.Combine(
                    path1: runDirectory,
                    path2: $"{backend}-stderr.log"
                )
            );

            if (WarmRefusal(
                backend: backend,
                process: process,
                source: warm.Manifest.Id
            ) is { } refusal) {
                return $"{refusal}; see {runDirectory}";
            }

            var stdout = CliText.Lines(text: process.Stdout);

            Console.WriteLine(value: $"canary: warmed the pipeline cache on {backend} from {warm.Manifest.Id}'s positive world: {CountOf(kind: "gpu.created.pipelines", lines: stdout)} pipeline(s) created, {CountOf(kind: "gpu.pipeline-cache.misses", lines: stdout)} built outside the cache.");
        }

        run.Conclude(passed: true);

        return null;
    }

    /// <summary>Names how one warm boot failed, or returns <see langword="null"/> when it succeeded: it exited 0 before
    /// its timeout, after narrating the engine ready and its capture landing (<see cref="WarmCaptureName"/>, which the
    /// display encode served, so the encode's pipeline is built) on standard error, and printing its backend's
    /// pipeline-cache counts on standard output.</summary>
    /// <param name="backend">The backend the boot ran on.</param>
    /// <param name="process">The boot's captured process.</param>
    /// <param name="source">The id of the manifest whose positive world it booted.</param>
    /// <returns>The refusal, naming the backend, or <see langword="null"/>.</returns>
    public static string? WarmRefusal(string backend, CliProcessResult process, string source) {
        var warm = $"the pipeline-cache warm on {backend} from {source}'s positive world";
        var stdout = CliText.Lines(text: process.Stdout);

        if (process.TimedOut) {
            return $"{warm} did not exit within its {WarmSeconds.ToString(provider: CultureInfo.InvariantCulture)}-second timeout";
        }
        if (process.ExitCode != 0) {
            return $"{warm} exited {process.ExitCode.ToString(provider: CultureInfo.InvariantCulture)}";
        }
        if (!CliText.Lines(text: process.Stderr).Any(predicate: static line => line.StartsWith(comparisonType: StringComparison.Ordinal, value: "[engine: ready at tick "))) {
            return $"{warm} never reported the engine ready";
        }
        if (!CliText.Lines(text: process.Stderr).Any(predicate: line => (line.StartsWith(comparisonType: StringComparison.Ordinal, value: "[capture] ") && line.Contains(comparisonType: StringComparison.Ordinal, value: " -> ") && line.EndsWith(comparisonType: StringComparison.Ordinal, value: WarmCaptureName(backend: backend))))) {
            return $"{warm} never landed its capture, so the display encode's pipeline was not built";
        }
        if ((CountOf(kind: "gpu.created.pipelines", lines: stdout) is null) || (CountOf(kind: "gpu.pipeline-cache.misses", lines: stdout) is null)) {
            return $"{warm} printed no pipeline-cache.{backend} counts";
        }

        return null;
    }

    // The value of the last `<kind> <value>` line a counters readout printed for kind, or null.
    private static long? CountOf(IReadOnlyList<string> lines, string kind) {
        for (var index = (lines.Count - 1); (index >= 0); index--) {
            var fields = lines[index].Split(
                options: StringSplitOptions.RemoveEmptyEntries,
                separator: ' '
            );

            if (
                (fields.Length >= 2) &&
                string.Equals(a: fields[^2], b: kind, comparisonType: StringComparison.Ordinal) &&
                long.TryParse(result: out var value, s: fields[^1], style: NumberStyles.None, provider: CultureInfo.InvariantCulture)
            ) {
                return value;
            }
        }

        return null;
    }
}
