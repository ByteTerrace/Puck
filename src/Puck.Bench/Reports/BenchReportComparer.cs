using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Puck.Bench;

/// <summary>The exit code + one-line summary of a <see cref="BenchReportComparer"/> run — the value a console verb echoes
/// and a headless tool mode returns as the process exit code.</summary>
/// <param name="ExitCode">0 for a clean compare (the full table was written); 2 for a load/compatibility refusal
/// (nothing comparable was produced).</param>
/// <param name="Summary">A single human/agent-readable status line (the verb's echo; the table itself went to the
/// writer).</param>
public readonly record struct BenchCompareOutcome(int ExitCode, string Summary);

/// <summary>
/// Compares two <c>puck.bench.v1</c> reports. It refuses a <c>kind</c> or
/// <c>scoreFormula</c> mismatch (the versioning rule: two reports are comparable iff their formula strings match), prints
/// a loud CONTEXT-DIFFERS banner when the host/GPU/tier/present-mode/switch-state disagree (still compares — a
/// cross-condition diff is legitimate as long as the reader is told), aligns scenes by name (noting one-sided scenes),
/// and renders a per-scene diff with a NOISE-AWARE verdict: <c>SAME</c> inside measured run-to-run repeatability,
/// <c>LEAN</c> at the edge of it, <c>REAL</c> beyond it. The output is the same fixed-width, invariant-culture,
/// <c>[bench]</c>-prefixed shape as <see cref="BenchConsoleFormatter"/>, written to a supplied <see cref="TextWriter"/>
/// (the surfaces pass <see cref="Console.Out"/>). Read-only and side-effect-free apart from the writer — it never
/// touches a GPU, a host, or the run harness, so a headless tool mode can drive it before any window exists.
/// <para>
/// PACE-BOUND scenes (GPU frame time far under the display's present slot, so wall FPS is pinned by the pacer and says
/// nothing about the GPU) derive their verdict from GPU frame milliseconds instead of wall FPS, and the row says so —
/// otherwise a pace-bound scene would read <c>SAME</c> on a metric that could not have moved.
/// </para>
/// </summary>
public static class BenchReportComparer {
    private const int BannerWidth = 63;
    private const string Prefix = "[bench] ";

    // Noise-aware verdict bands (percent of the primary metric). Calibrated to the measured reference repeatability:
    // scored-scene run-to-run spreads land ≤8% (most ≤4%), so a move under 3% is indistinguishable from run-to-run
    // noise (SAME), a move inside 3–8% is a direction-only lean that lives INSIDE measured repeatability (LEAN), and a
    // move past 8% is beyond the noise floor — a real change (REAL).
    private const double SameBandPercent = 3.0;
    private const double RealBandPercent = 8.0;

    // Pace-bound detection: a scene whose median GPU frame is under this share of its median wall interval is pinned by
    // the pacer at the display cadence, so its wall FPS carries no GPU signal — the verdict must come from GPU frame ms.
    private const double PaceBoundGpuFrameShare = 0.5;

    /// <summary>Loads, compares, and writes the full diff table for two <c>puck.bench.v1</c> reports.</summary>
    /// <param name="pathA">The FIRST (baseline) report path, or the alias <c>prev</c>/<c>latest</c> (resolved by
    /// filename sort under <c>bench-reports/</c> in the current directory).</param>
    /// <param name="pathB">The SECOND (candidate) report path, or the alias <c>prev</c>/<c>latest</c>.</param>
    /// <param name="writer">The sink the fixed-width table is written to (the surfaces pass <see cref="Console.Out"/>).</param>
    /// <returns>The exit code (0 clean, 2 refusal) and a one-line summary.</returns>
    public static BenchCompareOutcome Run(string pathA, string pathB, TextWriter writer) {
        ArgumentNullException.ThrowIfNull(argument: pathA);
        ArgumentNullException.ThrowIfNull(argument: pathB);
        ArgumentNullException.ThrowIfNull(argument: writer);

        if (ResolveArgument(argument: pathA, resolved: out var resolvedA, error: out var resolveErrorA) is false) {
            return Refuse(writer: writer, reason: resolveErrorA);
        }

        if (ResolveArgument(argument: pathB, resolved: out var resolvedB, error: out var resolveErrorB) is false) {
            return Refuse(writer: writer, reason: resolveErrorB);
        }

        if (Load(path: resolvedA, document: out var a, error: out var loadErrorA) is false) {
            return Refuse(writer: writer, reason: loadErrorA);
        }

        if (Load(path: resolvedB, document: out var b, error: out var loadErrorB) is false) {
            return Refuse(writer: writer, reason: loadErrorB);
        }

        if (!string.Equals(a: a.ScoreFormula, b: b.ScoreFormula, comparisonType: StringComparison.Ordinal)) {
            return Refuse(writer: writer, reason: $"scoreFormula mismatch — '{a.ScoreFormula}' (A) vs '{b.ScoreFormula}' (B); reports are comparable only within one formula version");
        }

        return WriteComparison(a: a, b: b, pathA: resolvedA, pathB: resolvedB, writer: writer);
    }

    // ---- alias + load -------------------------------------------------------------------------------------------

    // Resolves a path argument: the aliases 'latest'/'prev' map to the newest / second-newest bench-reports/*.json by
    // filename sort (the timestamped names sort chronologically); anything else is taken verbatim. Returns false with a
    // reason when an alias cannot resolve.
    private static bool ResolveArgument(string argument, out string resolved, out string error) {
        resolved = argument;
        error = "";

        var isLatest = string.Equals(a: argument, b: "latest", comparisonType: StringComparison.OrdinalIgnoreCase);
        var isPrev = string.Equals(a: argument, b: "prev", comparisonType: StringComparison.OrdinalIgnoreCase);

        if (!isLatest && !isPrev) {
            return true;
        }

        var directory = BenchReportWriter.ReportDirectoryName;

        if (!Directory.Exists(path: directory)) {
            error = $"cannot resolve '{argument}' — no '{directory}/' directory under the current path";

            return false;
        }

        var files = Directory.GetFiles(path: directory, searchPattern: "*.json");

        Array.Sort(array: files, comparer: StringComparer.Ordinal);

        var wanted = (isLatest ? (files.Length - 1) : (files.Length - 2));

        if (wanted < 0) {
            error = $"cannot resolve '{argument}' — '{directory}/' has {files.Length} report(s), need {(isLatest ? 1 : 2)}";

            return false;
        }

        resolved = files[wanted];

        return true;
    }

    // Reads and deserializes one report through the source-gen context; returns false with a reason on any failure or a
    // non-puck.bench.v1 document (each side must be a v1 report before a compat check is even meaningful).
    private static bool Load(string path, out BenchReportDocument document, out string error) {
        document = null!;
        error = "";

        if (!File.Exists(path: path)) {
            error = $"report not found: '{path}'";

            return false;
        }

        BenchReportDocument? parsed;

        try {
            var json = File.ReadAllText(path: path);

            parsed = JsonSerializer.Deserialize(json: json, jsonTypeInfo: BenchReportJsonContext.Default.BenchReportDocument);
        } catch (JsonException exception) {
            error = $"'{path}' is not a valid JSON report: {exception.Message}";

            return false;
        } catch (IOException exception) {
            error = $"could not read '{path}': {exception.Message}";

            return false;
        } catch (UnauthorizedAccessException exception) {
            error = $"could not read '{path}': {exception.Message}";

            return false;
        }

        if (parsed is null) {
            error = $"'{path}' deserialized to nothing";

            return false;
        }

        if (!string.Equals(a: parsed.Kind, b: BenchReportDocument.DocumentKind, comparisonType: StringComparison.Ordinal)) {
            error = $"'{path}' is not a {BenchReportDocument.DocumentKind} report (kind='{parsed.Kind}')";

            return false;
        }

        // Collections omitted from JSON deserialize to null. Normalize them before walking the report so a
        // hand-trimmed report produces a useful comparison or refusal rather than a null-reference failure.
        var normalized = Normalize(document: parsed);

        // A report with no scenes cannot be meaningfully compared (the whole table is per-scene); refuse loudly through
        // the same exit-2 path a kind/formula mismatch takes rather than printing an empty diff.
        if (normalized.Scenes.Count == 0) {
            error = $"'{path}' has no scenes to compare";

            return false;
        }

        document = normalized;

        return true;
    }

    // Rebuilds the document with every nullable collection replaced by an empty one (top-level Scenes/FeatureSwitches,
    // each scene's per-pass map, and the host resolution array), so a deserialized report is safe to walk. Value-type
    // sections (score, host, engine) are already non-null; a missing "score" object deserializes to the zero struct,
    // which the compare reports as an overall of 0 rather than throwing.
    private static BenchReportDocument Normalize(BenchReportDocument document) {
        var scenes = (document.Scenes ?? []);
        var normalizedScenes = new BenchReportScene[scenes.Count];

        for (var index = 0; (index < scenes.Count); index++) {
            var scene = scenes[index];

            normalizedScenes[index] = scene with {
                Gpu = scene.Gpu with { Passes = (scene.Gpu.Passes ?? EmptyStats) },
            };
        }

        return document with {
            FeatureSwitches = (document.FeatureSwitches ?? EmptySwitches),
            Host = document.Host with { Resolution = (document.Host.Resolution ?? []) },
            Scenes = normalizedScenes,
        };
    }

    private static readonly IReadOnlyDictionary<string, string> EmptySwitches = new Dictionary<string, string>(comparer: StringComparer.Ordinal);
    private static readonly IReadOnlyDictionary<string, BenchReportStats> EmptyStats = new Dictionary<string, BenchReportStats>(comparer: StringComparer.Ordinal);

    private static BenchCompareOutcome Refuse(TextWriter writer, string reason) {
        writer.WriteLine(value: $"{Prefix}COMPARE REFUSED — {reason}");

        return new BenchCompareOutcome(ExitCode: 2, Summary: $"compare refused: {reason}");
    }

    // ---- the comparison table -----------------------------------------------------------------------------------

    private static BenchCompareOutcome WriteComparison(BenchReportDocument a, BenchReportDocument b, string pathA, string pathB, TextWriter writer) {
        var builder = new StringBuilder();

        AppendHeader(builder: builder, a: a, b: b, pathA: pathA, pathB: pathB);
        AppendContextBanner(builder: builder, a: a, b: b);

        var sameCount = 0;
        var movedCount = 0;
        var oneSided = 0;
        var byNameB = new Dictionary<string, BenchReportScene>(comparer: StringComparer.Ordinal);

        foreach (var scene in b.Scenes) {
            byNameB[scene.Name] = scene;
        }

        var seenInB = new HashSet<string>(comparer: StringComparer.Ordinal);

        foreach (var sceneA in a.Scenes) {
            if (byNameB.TryGetValue(key: sceneA.Name, value: out var sceneB)) {
                _ = seenInB.Add(item: sceneA.Name);

                var same = AppendScene(builder: builder, sceneA: sceneA, sceneB: sceneB);

                if (same) {
                    sameCount++;
                } else {
                    movedCount++;
                }
            } else {
                AppendOneSided(builder: builder, scene: sceneA, side: "A", counterpart: "B");
                oneSided++;
            }
        }

        foreach (var sceneB in b.Scenes) {
            if (!seenInB.Contains(item: sceneB.Name)) {
                AppendOneSided(builder: builder, scene: sceneB, side: "B", counterpart: "A");
                oneSided++;
            }
        }

        builder.Append(value: Prefix).Append(value: new string(c: '=', count: BannerWidth));

        writer.WriteLine(value: builder.ToString());

        var comparedCount = (sameCount + movedCount);
        var oneSidedText = ((oneSided > 0) ? $"; {oneSided} one-sided" : string.Empty);
        var summary = $"compare: {comparedCount} scene(s) — {sameCount} SAME, {movedCount} moved{oneSidedText}; overall {IntText(value: a.Score.Overall)} -> {IntText(value: b.Score.Overall)} ({SignedInt(value: (b.Score.Overall - a.Score.Overall))})";

        return new BenchCompareOutcome(ExitCode: 0, Summary: summary);
    }
    private static void AppendHeader(StringBuilder builder, BenchReportDocument a, BenchReportDocument b, string pathA, string pathB) {
        builder.Append(value: Prefix).Append(value: Banner(title: " BENCH COMPARE ")).Append(value: '\n');
        builder.Append(value: Prefix).Append(value: "A  ").Append(value: pathA)
            .Append(value: "   git ").Append(value: a.Engine.GitCommit)
            .Append(value: "   score ").Append(value: IntText(value: a.Score.Overall)).Append(value: '\n');
        builder.Append(value: Prefix).Append(value: "B  ").Append(value: pathB)
            .Append(value: "   git ").Append(value: b.Engine.GitCommit)
            .Append(value: "   score ").Append(value: IntText(value: b.Score.Overall)).Append(value: '\n');

        var overallDelta = (b.Score.Overall - a.Score.Overall);
        var overallPercent = ((a.Score.Overall != 0) ? ((100.0 * overallDelta) / a.Score.Overall) : 0.0);

        builder.Append(value: Prefix).Append(value: "overall  score ")
            .Append(value: IntText(value: a.Score.Overall)).Append(value: " -> ").Append(value: IntText(value: b.Score.Overall))
            .Append(value: "  (").Append(value: SignedInt(value: overallDelta)).Append(value: ", ").Append(value: Percent(value: overallPercent)).Append(value: ')');

        if (FindBiggestPassMove(a: a, b: b) is { } move) {
            builder.Append(value: "   biggest per-pass move: ").Append(value: move.Scene).Append(value: '/').Append(value: move.Pass)
                .Append(value: ' ').Append(value: SignedMs(value: move.DeltaMs)).Append(value: " ms (").Append(value: ((move.DeltaMs > 0.0) ? "slower" : "faster")).Append(value: ')');
        } else {
            builder.Append(value: "   biggest per-pass move: none (no common passes)");
        }

        builder.Append(value: '\n');
    }

    // Prints a loud banner listing every host/switch condition that differs — the compare still runs (a cross-condition
    // diff is legitimate), but the reader is told exactly what changed so a move is never mis-attributed.
    private static void AppendContextBanner(StringBuilder builder, BenchReportDocument a, BenchReportDocument b) {
        var differences = new List<string>();

        AddDiff(differences: differences, label: "gpuName", left: a.Host.GpuName, right: b.Host.GpuName);
        AddDiff(differences: differences, label: "backend", left: a.Host.Backend, right: b.Host.Backend);
        AddDiff(differences: differences, label: "resolution", left: ResolutionText(host: a.Host), right: ResolutionText(host: b.Host));
        AddDiff(differences: differences, label: "presentMode", left: a.Host.PresentMode, right: b.Host.PresentMode);
        AddDiff(differences: differences, label: "presentRateTier", left: a.Host.PresentRateTier, right: b.Host.PresentRateTier);
        AddDiff(differences: differences, label: "renderScaleTier", left: a.Host.RenderScaleTier, right: b.Host.RenderScaleTier);
        AddSwitchDiffs(differences: differences, a: a.FeatureSwitches, b: b.FeatureSwitches);

        if (differences.Count == 0) {
            return;
        }

        builder.Append(value: Prefix).Append(value: "!! CONTEXT DIFFERS — comparing across conditions (A vs B):").Append(value: '\n');

        foreach (var difference in differences) {
            builder.Append(value: Prefix).Append(value: "!!   ").Append(value: difference).Append(value: '\n');
        }
    }
    private static void AddDiff(List<string> differences, string label, string left, string right) {
        if (!string.Equals(a: left, b: right, comparisonType: StringComparison.Ordinal)) {
            differences.Add(item: $"{label}: '{left}' vs '{right}'");
        }
    }
    private static void AddSwitchDiffs(List<string> differences, IReadOnlyDictionary<string, string> a, IReadOnlyDictionary<string, string> b) {
        foreach (var pair in a) {
            var right = (b.TryGetValue(key: pair.Key, value: out var value) ? value : "(absent)");

            if (!string.Equals(a: pair.Value, b: right, comparisonType: StringComparison.Ordinal)) {
                differences.Add(item: $"switch {pair.Key}: '{pair.Value}' vs '{right}'");
            }
        }

        foreach (var pair in b) {
            if (!a.ContainsKey(key: pair.Key)) {
                differences.Add(item: $"switch {pair.Key}: '(absent)' vs '{pair.Value}'");
            }
        }
    }

    // Renders one aligned scene's diff block; returns whether the scene's primary-metric verdict read SAME.
    private static bool AppendScene(StringBuilder builder, BenchReportScene sceneA, BenchReportScene sceneB) {
        var paceBound = (IsPaceBound(scene: sceneA) || IsPaceBound(scene: sceneB));

        // Primary metric: wall FPS for an honestly GPU/CPU-bound scene; GPU frame ms for a pace-bound scene (whose wall
        // FPS is pinned by the pacer and cannot move). higherIsFaster follows the metric — more FPS is faster, fewer ms
        // is faster.
        var (metricPercent, verdict) = (paceBound
            ? Classify(oldValue: sceneA.Gpu.Frame.MedianMs, newValue: sceneB.Gpu.Frame.MedianMs, higherIsFaster: false)
            : Classify(oldValue: sceneA.Wall.MeanFps, newValue: sceneB.Wall.MeanFps, higherIsFaster: true));

        var scoreDelta = (sceneB.Score - sceneA.Score);

        builder.Append(value: Prefix)
            .Append(value: sceneA.Name.PadRight(totalWidth: 20))
            .Append(value: sceneA.Category.PadRight(totalWidth: 8))
            .Append(value: verdict.PadRight(totalWidth: 12));

        if (paceBound) {
            builder.Append(value: "gpu ms ")
                .Append(value: Ms(value: sceneA.Gpu.Frame.MedianMs)).Append(value: " -> ").Append(value: Ms(value: sceneB.Gpu.Frame.MedianMs))
                .Append(value: "  (").Append(value: Percent(value: metricPercent)).Append(value: ')');
        } else {
            builder.Append(value: "wall fps ")
                .Append(value: Fps(value: sceneA.Wall.MeanFps)).Append(value: " -> ").Append(value: Fps(value: sceneB.Wall.MeanFps))
                .Append(value: "  (").Append(value: Percent(value: metricPercent)).Append(value: ')');
        }

        builder.Append(value: "   score ").Append(value: IntText(value: sceneA.Score)).Append(value: " -> ").Append(value: IntText(value: sceneB.Score))
            .Append(value: " (").Append(value: SignedInt(value: scoreDelta)).Append(value: ')');

        if (paceBound) {
            builder.Append(value: "   [pace-bound: verdict from GPU frame ms; wall pinned by pacer]");
        }

        builder.Append(value: '\n');

        // GPU frame + per-pass median deltas (% AND absolute ms).
        builder.Append(value: Prefix).Append(value: "  gpu  ms  frame ").Append(value: DeltaCell(oldMs: sceneA.Gpu.Frame.MedianMs, newMs: sceneB.Gpu.Frame.MedianMs));
        AppendPassDeltas(builder: builder, a: sceneA.Gpu.Passes, b: sceneB.Gpu.Passes);
        builder.Append(value: '\n');

        // CPU-bucket median deltas.
        builder.Append(value: Prefix).Append(value: "  cpu  ms  ")
            .Append(value: "pump ").Append(value: DeltaCell(oldMs: sceneA.Cpu.Pump.MedianMs, newMs: sceneB.Cpu.Pump.MedianMs))
            .Append(value: " | gpu-drain ").Append(value: DeltaCell(oldMs: sceneA.Cpu.GpuDrain.MedianMs, newMs: sceneB.Cpu.GpuDrain.MedianMs))
            .Append(value: " | produce ").Append(value: DeltaCell(oldMs: sceneA.Cpu.Produce.MedianMs, newMs: sceneB.Cpu.Produce.MedianMs))
            .Append(value: " | present ").Append(value: DeltaCell(oldMs: sceneA.Cpu.Present.MedianMs, newMs: sceneB.Cpu.Present.MedianMs))
            .Append(value: " | pacer ").Append(value: DeltaCell(oldMs: sceneA.Cpu.Pacer.MedianMs, newMs: sceneB.Cpu.Pacer.MedianMs))
            .Append(value: '\n');

        // Flags side by side (noisy / canary-drift / paced), so a change in variance posture is visible even when the
        // numbers read SAME.
        builder.Append(value: Prefix).Append(value: "  flags  A[").Append(value: FlagText(scene: sceneA)).Append(value: "]  B[").Append(value: FlagText(scene: sceneB)).Append(value: ']')
            .Append(value: '\n');

        return string.Equals(a: verdict, b: "SAME", comparisonType: StringComparison.Ordinal);
    }
    private static void AppendPassDeltas(StringBuilder builder, IReadOnlyDictionary<string, BenchReportStats> a, IReadOnlyDictionary<string, BenchReportStats> b) {
        foreach (var pair in a) {
            builder.Append(value: " | ").Append(value: pair.Key).Append(value: ' ');

            if (b.TryGetValue(key: pair.Key, value: out var right)) {
                builder.Append(value: DeltaCell(oldMs: pair.Value.MedianMs, newMs: right.MedianMs));
            } else {
                builder.Append(value: "(A only)");
            }
        }

        foreach (var pair in b) {
            if (!a.ContainsKey(key: pair.Key)) {
                builder.Append(value: " | ").Append(value: pair.Key).Append(value: " (B only)");
            }
        }
    }
    private static void AppendOneSided(StringBuilder builder, BenchReportScene scene, string side, string counterpart) {
        builder.Append(value: Prefix)
            .Append(value: scene.Name.PadRight(totalWidth: 20))
            .Append(value: scene.Category.PadRight(totalWidth: 8))
            .Append(value: $"ONLY-IN-{side}".PadRight(totalWidth: 12))
            .Append(value: "(no counterpart in ").Append(value: counterpart).Append(value: ')')
            .Append(value: '\n');
    }

    // ---- verdict math -------------------------------------------------------------------------------------------

    // The noise-aware classifier: the signed metric delta percent plus its verdict token. SAME under the noise floor,
    // LEAN inside measured repeatability (direction only), REAL beyond it.
    private static (double percent, string verdict) Classify(double oldValue, double newValue, bool higherIsFaster) {
        if (oldValue <= 0.0) {
            // A zero (or missing) baseline metric has no ratio to move against — a percent change is undefined, so this
            // is NOT "SAME" (which would falsely imply a measured no-change). Report a distinct baseline-zero verdict.
            return (0.0, "n/a(A=0)");
        }

        var percent = ((100.0 * (newValue - oldValue)) / oldValue);
        var magnitude = Math.Abs(value: percent);

        if (magnitude < SameBandPercent) {
            return (percent, "SAME");
        }

        var faster = (higherIsFaster ? (percent > 0.0) : (percent < 0.0));
        var band = ((magnitude <= RealBandPercent) ? "LEAN" : "REAL");

        return (percent, $"{band}-{(faster ? "FASTER" : "SLOWER")}");
    }

    // A scene is pace-bound when its median GPU frame is far under its median wall interval — the pacer holds the wall
    // cadence at the display ceiling, so wall FPS carries no GPU signal.
    private static bool IsPaceBound(BenchReportScene scene) {
        var wallMedian = scene.Wall.Stats.MedianMs;
        var gpuMedian = scene.Gpu.Frame.MedianMs;

        return ((wallMedian > 0.0) && (gpuMedian > 0.0) && (gpuMedian < (PaceBoundGpuFrameShare * wallMedian)));
    }

    // Scans every scene present in both reports for the per-pass median move with the largest absolute milliseconds.
    private static (string Scene, string Pass, double DeltaMs)? FindBiggestPassMove(BenchReportDocument a, BenchReportDocument b) {
        var byNameB = new Dictionary<string, BenchReportScene>(comparer: StringComparer.Ordinal);

        foreach (var scene in b.Scenes) {
            byNameB[scene.Name] = scene;
        }

        (string Scene, string Pass, double DeltaMs)? best = null;

        foreach (var sceneA in a.Scenes) {
            if (!byNameB.TryGetValue(key: sceneA.Name, value: out var sceneB)) {
                continue;
            }

            foreach (var pair in sceneA.Gpu.Passes) {
                if (!sceneB.Gpu.Passes.TryGetValue(key: pair.Key, value: out var right)) {
                    continue;
                }

                var delta = (right.MedianMs - pair.Value.MedianMs);

                if ((best is not { } current) || (Math.Abs(value: delta) > Math.Abs(value: current.DeltaMs))) {
                    best = (sceneA.Name, pair.Key, delta);
                }
            }
        }

        return best;
    }

    // ---- shared formatting --------------------------------------------------------------------------------------

    // "old -> new (pct, +absms)" for one millisecond channel.
    private static string DeltaCell(double oldMs, double newMs) {
        var percent = ((oldMs > 0.0) ? ((100.0 * (newMs - oldMs)) / oldMs) : 0.0);

        return $"{Ms(value: oldMs)}->{Ms(value: newMs)} ({Percent(value: percent)}, {SignedMs(value: (newMs - oldMs))})";
    }
    private static string FlagText(BenchReportScene scene) {
        var builder = new StringBuilder();

        if (scene.Flags.Noisy) {
            _ = builder.Append(value: "noisy,");
        }

        if (scene.Canary.CanaryDrift) {
            _ = builder.Append(value: "canary-drift,");
        }

        if (scene.Flags.Paced) {
            _ = builder.Append(value: "paced,");
        }

        return ((builder.Length > 0) ? builder.ToString(startIndex: 0, length: (builder.Length - 1)) : "-");
    }
    private static string ResolutionText(BenchReportHost host) {
        var width = ((host.Resolution.Length > 0) ? host.Resolution[0] : 0);
        var height = ((host.Resolution.Length > 1) ? host.Resolution[1] : 0);

        return $"{IntText(value: width)}x{IntText(value: height)}";
    }
    private static string Banner(string title) {
        if (title.Length >= BannerWidth) {
            return title;
        }

        var totalPad = (BannerWidth - title.Length);
        var left = (totalPad / 2);
        var right = (totalPad - left);

        return ((new string(c: '=', count: left) + title) + new string(c: '=', count: right));
    }
    private static string Ms(double value) => value.ToString(format: "F2", provider: CultureInfo.InvariantCulture);
    private static string Fps(double value) => value.ToString(format: "F1", provider: CultureInfo.InvariantCulture);
    private static string IntText(int value) => value.ToString(provider: CultureInfo.InvariantCulture);

    // Signed percent, always carrying its sign ("+0.2%", "-1.3%").
    private static string Percent(double value) {
        var sign = ((value >= 0.0) ? "+" : string.Empty);

        return $"{sign}{value.ToString(format: "F1", provider: CultureInfo.InvariantCulture)}%";
    }

    // Signed milliseconds, always carrying its sign ("+0.04", "-0.12").
    private static string SignedMs(double value) {
        var sign = ((value >= 0.0) ? "+" : string.Empty);

        return $"{sign}{value.ToString(format: "F2", provider: CultureInfo.InvariantCulture)}";
    }
    private static string SignedInt(int value) {
        var sign = ((value >= 0) ? "+" : string.Empty);

        return $"{sign}{value.ToString(provider: CultureInfo.InvariantCulture)}";
    }
}
