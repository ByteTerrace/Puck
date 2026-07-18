using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace Puck.Post;

/// <summary>
/// The fuzz HUNT orchestrator — the differential fuzzer flipped from GATE to MAXIMIZER. Where <see cref="FuzzStage"/>
/// asks "does this fixed seed sample stay benign?", the hunt SEARCHES a seed range for the candidates that drift the
/// MOST, keeps a ranked leaderboard, and saves each champion as a runnable repro plus an amplified diff heatmap. It is
/// a separate entry point (<c>--hunt-drift</c>) so the <c>fuzz</c> stage's gate role is untouched.
/// <para>Every candidate renders in its OWN child process (<c>--hunt-render --hunt-seed N</c>) — the MANDATORY
/// process-pair isolation the fuzzer already relies on (a malformed program can TDR the GPU; a shared process re-uses
/// one DXC context and corrupts the comparison) — exactly as the <c>tools fuzz</c> sweep spawns one child per gate
/// seed. This orchestrator itself needs no GPU: it spawns children, parses each child's structured
/// <c>HUNT seed=… score=…</c> line, ranks by the composite <see cref="DriftScore"/>, then re-runs the top-N
/// deterministically with artifact writing on (the seed reproduces the identical program, so a champion re-run is
/// byte-for-byte the scoring run). Progress and a final leaderboard table print to stdout; the table and the champion
/// artifacts land under <c>artifacts/drift-hunt/</c> (gitignored).</para>
/// </summary>
internal static class DriftHunt {
    /// <summary>The default per-candidate child wall-clock budget: generous against a cold shader/pipeline warm-up,
    /// small against a wedged/TDR'd child (which is then killed and classified, not left to hang the sweep).</summary>
    public const int DefaultTimeoutSeconds = 120;

    // One scored (or classified) candidate. Score is negative for a child that did not produce a scored line (timeout,
    // crash, skip) — those are dropped from the leaderboard.
    private readonly record struct Candidate(int Seed, double Score, double Diff, int MaxDelta, double Mean, double Isolated, double Unit, double Cluster, string Classification) {
        public bool Scored => (Score >= 0.0);
    }

    /// <summary>Runs the hunt.</summary>
    /// <param name="rounds">The number of candidate seeds to search.</param>
    /// <param name="seedBase">The first candidate seed; the sweep spans <c>[seedBase, seedBase + rounds)</c>.</param>
    /// <param name="topN">How many champions to keep and save artifacts for.</param>
    /// <param name="timeoutSeconds">The per-candidate child wall-clock budget.</param>
    /// <returns>The process exit code (0 — a hunt is exploratory and never fails the build).</returns>
    public static int Run(int rounds, int seedBase, int topN, int timeoutSeconds) {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 10240)) {
            Console.WriteLine(value: "DRIFT-HUNT skip | the cross-backend tier (Direct3D 12) requires Windows 10.0.10240+");

            return 0;
        }

        var executable = (Environment.ProcessPath ?? throw new InvalidOperationException(message: "Environment.ProcessPath is unavailable; the drift hunt cannot spawn its isolated child processes."));
        var huntDirectory = Path.Combine(path1: "artifacts", path2: "drift-hunt");
        var effectiveRounds = Math.Max(val1: 1, val2: rounds);
        var effectiveTopN = Math.Clamp(value: topN, min: 1, max: effectiveRounds);
        var timeoutMilliseconds = (Math.Max(val1: 1, val2: timeoutSeconds) * 1000);
        var candidates = new List<Candidate>(capacity: effectiveRounds);

        Console.WriteLine(value: $"DRIFT-HUNT start | {effectiveRounds} candidates from seed {seedBase} | top {effectiveTopN} | one isolated child process per candidate | timeout {(timeoutMilliseconds / 1000)}s/candidate | Vulkan (SPIR-V) vs Direct3D 12 (DXIL)");

        for (var index = 0; (index < effectiveRounds); index++) {
            var seed = (seedBase + index);

            var (exitCode, timedOut, output) = Spawn(executable: executable, timeoutMilliseconds: timeoutMilliseconds, arguments: ["--hunt-render", "--hunt-seed", seed.ToString(provider: CultureInfo.InvariantCulture)]);
            var candidate = Parse(seed: seed, exitCode: exitCode, timedOut: timedOut, output: output);

            candidates.Add(item: candidate);

            Console.WriteLine(value: (candidate.Scored
                ? $"  [{(index + 1),4}/{effectiveRounds}] seed {seed,8}: score {candidate.Score,8:0.###} | diff {candidate.Diff,7:0.###}% maxΔ{candidate.MaxDelta,-3} mean {candidate.Mean:0.#####} cluster {candidate.Cluster:0.###}%"
                : $"  [{(index + 1),4}/{effectiveRounds}] seed {seed,8}: {candidate.Classification}"));
        }

        var champions = candidates
            .Where(predicate: static candidate => candidate.Scored)
            .OrderByDescending(keySelector: static candidate => candidate.Score)
            .Take(count: effectiveTopN)
            .ToList();

        // Champion artifacts: re-run each top-N seed with writing on. Deterministic from the seed, so the re-run
        // reproduces the identical program and metrics — the saved renders/heatmap match the scored ones exactly.
        if (champions.Count != 0) {
            _ = Directory.CreateDirectory(path: huntDirectory);

            for (var rank = 0; (rank < champions.Count); rank++) {
                var seedText = champions[rank].Seed.ToString(provider: CultureInfo.InvariantCulture);

                _ = Spawn(executable: executable, timeoutMilliseconds: timeoutMilliseconds, arguments: ["--hunt-render", "--hunt-seed", seedText, "--hunt-artifacts", huntDirectory, "--hunt-rank", rank.ToString(provider: CultureInfo.InvariantCulture)]);
            }
        }

        var table = BuildLeaderboard(champions: champions, rounds: effectiveRounds, seedBase: seedBase, scoredCount: candidates.Count(predicate: static candidate => candidate.Scored), huntDirectory: huntDirectory);

        Console.WriteLine(value: table);

        if (champions.Count != 0) {
            File.WriteAllText(path: Path.Combine(path1: huntDirectory, path2: "leaderboard.txt"), contents: table);
        }

        return 0;
    }

    // Parses a child's structured "HUNT seed=… …" line into a Candidate. A timeout/crash/skip/error line yields a
    // negative-score classification that the leaderboard drops.
    private static Candidate Parse(int seed, int exitCode, bool timedOut, string output) {
        if (timedOut) {
            return Unscored(seed: seed, classification: "TIMEOUT (hang/TDR, killed)");
        }

        var line = FindHuntLine(output: output);

        if (line is null) {
            return Unscored(seed: seed, classification: $"no HUNT line (exit {exitCode})");
        }

        var fields = ParseFields(line: line);

        if (fields.TryGetValue(key: "error", value: out var error)) {
            return Unscored(seed: seed, classification: $"error: {error}");
        }

        if (fields.ContainsKey(key: "skip")) {
            return Unscored(seed: seed, classification: "skip");
        }

        if (!fields.TryGetValue(key: "score", value: out var scoreText) || !double.TryParse(s: scoreText, style: NumberStyles.Float, provider: CultureInfo.InvariantCulture, result: out var score)) {
            return Unscored(seed: seed, classification: $"unparseable HUNT line (exit {exitCode})");
        }

        return new Candidate(
            Seed: seed,
            Score: score,
            Diff: Field(fields: fields, key: "diff"),
            MaxDelta: (int)Field(fields: fields, key: "maxDelta"),
            Mean: Field(fields: fields, key: "mean"),
            Isolated: Field(fields: fields, key: "isolated"),
            Unit: Field(fields: fields, key: "unit"),
            Cluster: Field(fields: fields, key: "cluster"),
            Classification: "scored"
        );
    }
    private static Candidate Unscored(int seed, string classification) =>
        new(Seed: seed, Score: -1.0, Diff: 0.0, MaxDelta: 0, Mean: 0.0, Isolated: 0.0, Unit: 0.0, Cluster: 0.0, Classification: classification);
    private static string? FindHuntLine(string output) {
        foreach (var line in output.Split(separator: '\n')) {
            if (line.Contains(value: "HUNT seed=", comparisonType: StringComparison.Ordinal)) {
                return line.Trim();
            }
        }

        return null;
    }
    private static Dictionary<string, string> ParseFields(string line) {
        var fields = new Dictionary<string, string>(comparer: StringComparer.Ordinal);

        foreach (var token in line.Split(separator: ' ', options: StringSplitOptions.RemoveEmptyEntries)) {
            var equals = token.IndexOf(value: '=');

            if (equals > 0) {
                fields[token[..equals]] = token[(equals + 1)..];
            }
        }

        return fields;
    }
    private static double Field(Dictionary<string, string> fields, string key) =>
        ((fields.TryGetValue(key: key, value: out var text) && double.TryParse(s: text, style: NumberStyles.Float, provider: CultureInfo.InvariantCulture, result: out var value)) ? value : 0.0);
    private static string BuildLeaderboard(List<Candidate> champions, int rounds, int seedBase, int scoredCount, string huntDirectory) {
        var builder = new StringBuilder();

        _ = builder.AppendLine(value: $"DRIFT-HUNT leaderboard | top {champions.Count} of {scoredCount} scored / {rounds} candidates from seed {seedBase}");
        _ = builder.AppendLine(value: "  rank      seed     score    diff%   maxΔ      mean  cluster%  isolated   unitΔ");

        for (var rank = 0; (rank < champions.Count); rank++) {
            var champion = champions[rank];

            _ = builder.AppendLine(value: $"  {rank,4}  {champion.Seed,8}  {champion.Score,8:0.###}  {champion.Diff,7:0.###}  {champion.MaxDelta,5}  {champion.Mean,8:0.#####}  {champion.Cluster,8:0.###}  {champion.Isolated,8:0.###}  {champion.Unit,6:0.###}");
        }

        if (champions.Count == 0) {
            _ = builder.AppendLine(value: "  (no candidate produced a scored render — every child timed out, crashed, or skipped)");
        } else {
            _ = builder.AppendLine(value: $"artifacts: {huntDirectory}{Path.DirectorySeparatorChar}champion-<rank>-seed<seed>-{{vulkan,directx,diff-amp64x}}.png + -repro.json");
        }

        return builder.ToString().TrimEnd();
    }

    // Spawns one isolated child, captures stdout+stderr, and enforces a hard wall-clock timeout — the same watchdog
    // pattern the tools fuzz sweep uses (the engine has no GPU-wait timeout, so a hung/TDR'd child is killed here).
    private static (int ExitCode, bool TimedOut, string Output) Spawn(string executable, int timeoutMilliseconds, string[] arguments) {
        var startInfo = new ProcessStartInfo {
            CreateNoWindow = true,
            FileName = executable,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };

        foreach (var argument in arguments) {
            startInfo.ArgumentList.Add(item: argument);
        }

        using var process = (Process.Start(startInfo: startInfo) ?? throw new InvalidOperationException(message: $"The drift-hunt child failed to start ({executable})."));
        var output = new StringBuilder();

        process.OutputDataReceived += (_, eventArguments) => { if (eventArguments.Data is not null) { lock (output) { _ = output.AppendLine(value: eventArguments.Data); } } };
        process.ErrorDataReceived += (_, eventArguments) => { if (eventArguments.Data is not null) { lock (output) { _ = output.AppendLine(value: eventArguments.Data); } } };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        if (!process.WaitForExit(milliseconds: timeoutMilliseconds)) {
            try {
                process.Kill(entireProcessTree: true);
            } catch {
                // The process may have exited between the timeout check and the kill; ignore.
            }

            _ = process.WaitForExit(milliseconds: 5000);

            lock (output) {
                return (-1, true, output.ToString());
            }
        }

        process.WaitForExit();

        lock (output) {
            return (process.ExitCode, false, output.ToString());
        }
    }
}
