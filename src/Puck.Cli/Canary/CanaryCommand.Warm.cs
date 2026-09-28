using System.Globalization;
using System.Text;

namespace Puck.Cli.Canary;

internal static partial class CanaryCommand {
    /// <summary>The whole timeout of one warm boot: long enough for a cold driver to build the engine's pipeline set
    /// (<c>WorldCaptureScheduler.BuildHoldBudgetSeconds</c>), which a leg's 60-second ceiling is not.</summary>
    internal const int WarmSeconds = 180;

    // The run-directory name of the warm boots, which no manifest id can take: ids are lowercase words and hyphens.
    private const string WarmRunId = "_warm";

    /// <summary>The world a run boots once per backend, before any leg, so every GPU leg starts from a pipeline cache
    /// that already holds the engine's pipelines instead of building them on a cold driver under its own timeout.</summary>
    /// <param name="Manifest">The first selected offscreen manifest whose positive leg is one plain World process; its
    /// positive world is the one booted.</param>
    /// <param name="Backends">The backends the selection's GPU proofs boot, in the order the run warms them.</param>
    internal sealed record CanaryWarm(CanaryManifest Manifest, IReadOnlyList<string> Backends);

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
    // ready and reading its pipeline-cache counts, then hands the cache it persisted to the seed. The first boot that
    // fails (WarmRefusal) ends the warm with its refusal, and the selection fails without starting a leg.
    private static string? WarmPipelineCache(CanaryWarm warm, string artifact, CanaryBudget budget) {
        var runDirectory = CreateRunDirectory(
            id: WarmRunId,
            leg: "pipeline-cache"
        );
        var stateDirectory = Path.Combine(
            path1: runDirectory,
            path2: "state"
        );
        var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

        foreach (var backend in warm.Backends) {
            budget.Cancellation.ThrowIfCancellationRequested();
            budget.Tally.WarmStarted();

            var process = CliProcess.RunCaptured(
                fileName: "dotnet",
                arguments: [
                    artifact,
                    "--world", warm.Manifest.Positive.WorldPath,
                    "--state-dir", stateDirectory,
                    "--exit-after-seconds", WarmSeconds.ToString(provider: CultureInfo.InvariantCulture),
                    .. BootShapeArguments(
                    backend: backend,
                    debugLayers: false,
                    manifest: warm.Manifest
                ),
                ],
                cancellationToken: budget.Cancellation,
                input: $"world.wait ready {(WarmSeconds - 20).ToString(provider: CultureInfo.InvariantCulture)}{Environment.NewLine}world.counters pipeline-cache.{backend}{Environment.NewLine}{RunnerQuit}",
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

            var stdout = SplitLines(text: process.Stdout);

            Console.WriteLine(value: $"canary: warmed the pipeline cache on {backend} from {warm.Manifest.Id}'s positive world: {CountOf(kind: "gpu.created.pipelines", lines: stdout)} pipeline(s) created, {CountOf(kind: "gpu.pipeline-cache.misses", lines: stdout)} built outside the cache.");
        }

        budget.Seed.Capture(directory: Path.Combine(
            path1: stateDirectory,
            path2: "pipeline-cache"
        ));

        return null;
    }

    /// <summary>Names how one warm boot failed, or returns <see langword="null"/> when it succeeded: it exited 0 before
    /// its timeout, after narrating the engine ready on standard error and printing its backend's pipeline-cache
    /// counts on standard output.</summary>
    /// <param name="backend">The backend the boot ran on.</param>
    /// <param name="process">The boot's captured process.</param>
    /// <param name="source">The id of the manifest whose positive world it booted.</param>
    /// <returns>The refusal, naming the backend, or <see langword="null"/>.</returns>
    internal static string? WarmRefusal(string backend, CliProcessResult process, string source) {
        var warm = $"the pipeline-cache warm on {backend} from {source}'s positive world";
        var stdout = SplitLines(text: process.Stdout);

        if (process.TimedOut) {
            return $"{warm} did not exit within its {WarmSeconds.ToString(provider: CultureInfo.InvariantCulture)}-second timeout";
        }
        if (process.ExitCode != 0) {
            return $"{warm} exited {process.ExitCode.ToString(provider: CultureInfo.InvariantCulture)}";
        }
        if (!SplitLines(text: process.Stderr).Any(predicate: static line => line.StartsWith(comparisonType: StringComparison.Ordinal, value: "[engine: ready at tick "))) {
            return $"{warm} never reported the engine ready";
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

    /// <summary>The pipeline cache the warm boots persisted, which every GPU leg's state directory starts from, and how
    /// many of those legs wrote nothing back to it. The files are captured once, before any leg starts; the counts are
    /// updated from the legs' own threads.</summary>
    internal sealed class CanaryPipelineCacheSeed {
        private IReadOnlyDictionary<string, byte[]> m_files = new Dictionary<string, byte[]>(comparer: StringComparer.Ordinal);
        private int m_seeded;
        private int m_unchanged;

        /// <summary>Gets the legs whose state directory started from the cache.</summary>
        public int Seeded => Volatile.Read(location: ref m_seeded);
        /// <summary>Gets the seeded legs whose cache held the same files, byte for byte, when their World exited: no
        /// pipeline was built outside it, since a pipeline the cache did not answer is written back to it.</summary>
        public int Unchanged => Volatile.Read(location: ref m_unchanged);

        /// <summary>Captures the cache files under <paramref name="directory"/>.</summary>
        /// <param name="directory">A state directory's <c>pipeline-cache</c> directory.</param>
        public void Capture(string directory) =>
            m_files = Read(directory: directory);
        /// <summary>Writes the captured files into <paramref name="stateDirectory"/>'s <c>pipeline-cache</c> directory.</summary>
        /// <param name="stateDirectory">A leg's state directory, before its World boots.</param>
        /// <returns>Whether there was a cache to write.</returns>
        public bool TrySeed(string stateDirectory) {
            if (m_files.Count == 0) {
                return false;
            }

            foreach (var (name, bytes) in m_files) {
                var path = Path.Combine(
                    path1: stateDirectory,
                    path2: "pipeline-cache",
                    path3: name
                );

                Directory.CreateDirectory(path: Path.GetDirectoryName(path: path)!);
                File.WriteAllBytes(
                    bytes: bytes,
                    path: path
                );
            }

            Interlocked.Increment(location: ref m_seeded);

            return true;
        }
        /// <summary>Counts a seeded leg whose cache still holds exactly the captured files.</summary>
        /// <param name="stateDirectory">The seeded leg's state directory, after its World exited.</param>
        public void Observe(string stateDirectory) {
            var files = Read(directory: Path.Combine(
                path1: stateDirectory,
                path2: "pipeline-cache"
            ));

            if (
                (files.Count == m_files.Count) &&
                files.All(predicate: file => (m_files.TryGetValue(key: file.Key, value: out var captured) && file.Value.AsSpan().SequenceEqual(other: captured)))
            ) {
                Interlocked.Increment(location: ref m_unchanged);
            }
        }

        // Every cache file under directory, by its path relative to it with forward slashes; a temporary another
        // writer is still renaming is not a cache file.
        private static Dictionary<string, byte[]> Read(string directory) => (Directory.Exists(path: directory)
            ? Directory.EnumerateFiles(
                path: directory,
                searchOption: SearchOption.AllDirectories,
                searchPattern: "*.bin"
            ).ToDictionary(
                comparer: StringComparer.Ordinal,
                elementSelector: File.ReadAllBytes,
                keySelector: path => Path.GetRelativePath(
                    path: path,
                    relativeTo: directory
                ).Replace(
                    newChar: '/',
                    oldChar: '\\'
                )
            )
            : new Dictionary<string, byte[]>(comparer: StringComparer.Ordinal));
    }
}
