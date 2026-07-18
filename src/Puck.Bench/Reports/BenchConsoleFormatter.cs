using System.Globalization;
using System.Text;

namespace Puck.Bench;

/// <summary>
/// The stdout half of the everything-as-data output contract (plan §8) — fixed-width, invariant-culture, every line
/// prefixed <c>[bench]</c>, written via <see cref="Console.Out"/> (the <c>room.bench</c> precedent). Always-on:
/// <see cref="BenchRuntime"/> wires <see cref="WriteScene"/> to <see cref="BenchRuntime.SceneCompleted"/>,
/// <see cref="WriteRun"/> to <see cref="BenchRuntime.RunCompleted"/>, and <see cref="WriteSweep"/> to
/// <see cref="BenchRuntime.SweepCompleted"/> at construction — the tables are the contract, not an opt-in.
/// </summary>
public static class BenchConsoleFormatter {
    private const string Prefix = "[bench] ";

    /// <summary>Prints one scene's block immediately as it completes — throughput, the GPU per-pass breakdown, the
    /// CPU buckets, and the score line.</summary>
    /// <param name="scene">The completed scene's result.</param>
    public static void WriteScene(BenchSceneResult scene) {
        ArgumentNullException.ThrowIfNull(argument: scene);

        Console.Out.WriteLine(value: BuildSceneBlock(scene: scene));
    }

    /// <summary>Prints the final score block after a completed run (nothing for a refused/aborted outcome beyond a
    /// one-line reason — there is nothing scored to report).</summary>
    /// <param name="outcome">The completed run/leg outcome.</param>
    /// <param name="host">The host facts the summary line stamps.</param>
    /// <param name="reportPath">The JSON report's path, or <see langword="null"/> when nothing was written.</param>
    public static void WriteRun(BenchRunOutcome outcome, BenchHostInfo host, string? reportPath) {
        ArgumentNullException.ThrowIfNull(argument: outcome);
        ArgumentNullException.ThrowIfNull(argument: host);

        Console.Out.WriteLine(value: BuildRunBlock(outcome: outcome, host: host, reportPath: reportPath));
    }

    /// <summary>Prints the combined sweep summary table after the last leg completes.</summary>
    /// <param name="sweep">The collected sweep legs.</param>
    public static void WriteSweep(BenchSweepOutcome sweep) {
        ArgumentNullException.ThrowIfNull(argument: sweep);

        Console.Out.WriteLine(value: BuildSweepBlock(sweep: sweep));
    }

    // ---- per-scene block ----------------------------------------------------------------------------------------

    private static string BuildSceneBlock(BenchSceneResult scene) {
        var builder = new StringBuilder();

        builder.Append(value: Prefix)
            .Append(value: scene.Name.PadRight(totalWidth: 22))
            .Append(value: scene.Category.PadRight(totalWidth: 8))
            .Append(value: "samples=").Append(value: Int(value: scene.SampleFrames))
            .Append(value: " spikes=").Append(value: Int(value: scene.Wall.SpikeFrames))
            .Append(value: "   bound=").Append(value: BenchVerdictText.Format(verdict: scene.Verdict))
            .Append(value: '\n');

        builder.Append(value: Prefix).Append(value: "  wall  ms med/p95/p99  ")
            .Append(value: Ms(value: scene.Wall.Binned.MedianMs)).Append(value: '/')
            .Append(value: Ms(value: scene.Wall.Binned.P95Ms)).Append(value: '/')
            .Append(value: Ms(value: scene.Wall.Binned.P99Ms))
            .Append(value: "   fps mean ").Append(value: Fps(value: scene.Wall.ThroughputFps))
            .Append(value: "  1%low ").Append(value: Fps(value: scene.Wall.OnePercentLowFps))
            .Append(value: '\n');

        builder.Append(value: Prefix).Append(value: "  gpu   ms med  frame ").Append(value: Ms(value: scene.GpuFrame.MedianMs));

        foreach (var pass in scene.Passes) {
            builder.Append(value: " | ").Append(value: pass.Label).Append(value: ' ').Append(value: Ms(value: pass.Stats.MedianMs));
        }

        builder.Append(value: '\n');

        builder.Append(value: Prefix).Append(value: "  cpu   ms med  pump ").Append(value: Ms(value: scene.Pump.MedianMs))
            .Append(value: " | gpu-drain ").Append(value: Ms(value: scene.GpuDrain.MedianMs))
            .Append(value: " | produce ").Append(value: Ms(value: scene.Produce.MedianMs))
            .Append(value: " | present ").Append(value: Ms(value: scene.Present.MedianMs))
            .Append(value: " | pacer ").Append(value: Ms(value: scene.Pacer.MedianMs))
            .Append(value: '\n');

        builder.Append(value: Prefix).Append(value: "  score ").Append(value: scene.Score.ToString(provider: CultureInfo.InvariantCulture));

        if (BenchScoreModel.TryGetReferenceFps(sceneName: scene.Name, referenceFps: out var referenceFps)) {
            builder.Append(value: "   (ref ").Append(value: Fps(value: referenceFps)).Append(value: " fps, weight ")
                .Append(value: scene.Weight.ToString(format: "0.00", provider: CultureInfo.InvariantCulture)).Append(value: ')');
        } else {
            builder.Append(value: "   (unscored)");
        }

        builder.Append(value: "   canary beam ").Append(value: Ms(value: scene.BeamMedianMs));
        builder.Append(value: FlagSuffix(scene: scene));

        return builder.ToString();
    }

    // Every variance flag prints here too (§6 rule: a report never launders a dirty run into clean-looking numbers).
    private static string FlagSuffix(BenchSceneResult scene) {
        var builder = new StringBuilder();

        if (scene.Noisy) {
            builder.Append(value: " NOISY");
        }

        if (scene.CanaryDrift) {
            builder.Append(value: " CANARY-DRIFT");
        }

        if (scene.Paced) {
            builder.Append(value: " PACED-PRESENT");
        }

        return builder.ToString();
    }

    // ---- final run block ------------------------------------------------------------------------------------------

    private static string BuildRunBlock(BenchRunOutcome outcome, BenchHostInfo host, string? reportPath) {
        if (!outcome.Succeeded) {
            return $"{Prefix}{ReasonBanner(outcome: outcome)}";
        }

        var builder = new StringBuilder();
        const int BannerWidth = 55;

        builder.Append(value: Prefix).Append(value: Banner(title: $" PUCK BENCH — {outcome.Suite} ", width: BannerWidth)).Append(value: '\n');
        builder.Append(value: Prefix).Append(value: "scene").Append(value: new string(c: ' ', count: 23)).Append(value: "score   weight   fps     flags").Append(value: '\n');

        foreach (var scene in outcome.Scenes) {
            builder.Append(value: Prefix)
                .Append(value: scene.Name.PadRight(totalWidth: 24))
                .Append(value: scene.Score.ToString(provider: CultureInfo.InvariantCulture).PadLeft(totalWidth: 8))
                .Append(value: "   ")
                .Append(value: scene.Weight.ToString(format: "0.00", provider: CultureInfo.InvariantCulture).PadLeft(totalWidth: 4))
                .Append(value: "   ")
                .Append(value: Fps(value: scene.SceneFps).PadLeft(totalWidth: 6))
                .Append(value: "   ")
                .Append(value: ((SceneFlagTokens(scene: scene) is { Length: > 0 } tokens) ? tokens : "-"))
                .Append(value: '\n');
        }

        builder.Append(value: Prefix).Append(value: "OVERALL").Append(value: new string(c: ' ', count: 21))
            .Append(value: outcome.Score.Overall.ToString(provider: CultureInfo.InvariantCulture).PadLeft(totalWidth: 8))
            .Append(value: "    ").Append(value: outcome.Score.ScoreFormula)
            .Append(value: (outcome.Score.Capped ? "   CAPPED BY PRESENT MODE" : string.Empty))
            .Append(value: (outcome.Score.Partial ? "   PARTIAL (a scored scene was dropped from the composite)" : string.Empty))
            .Append(value: '\n');

        builder.Append(value: Prefix).Append(value: "host ").Append(value: host.GpuName).Append(value: ' ')
            .Append(value: host.Backend).Append(value: ' ').Append(value: host.RenderScaleTier).Append(value: ' ')
            .Append(value: host.ResolutionWidth).Append(value: 'x').Append(value: host.ResolutionHeight).Append(value: ' ')
            .Append(value: host.PresentMode).Append(value: '/').Append(value: host.PresentRateTier)
            .Append(value: "   git ").Append(value: host.GitCommit).Append(value: '\n');

        if (reportPath is { } path) {
            builder.Append(value: Prefix).Append(value: "report ").Append(value: path).Append(value: '\n');
        }

        builder.Append(value: Prefix).Append(value: new string(c: '=', count: BannerWidth));

        return builder.ToString();
    }
    private static string ReasonBanner(BenchRunOutcome outcome) =>
        ((outcome.Failure == BenchFailure.Aborted)
            ? $"ABORTED {outcome.Suite} — no scores reported (environment restored)"
            : $"REFUSED {outcome.Suite} — reason: {outcome.Failure}");
    private static string SceneFlagTokens(BenchSceneResult scene) {
        var builder = new StringBuilder();

        if (scene.Noisy) {
            builder.Append(value: "noisy,");
        }

        if (scene.CanaryDrift) {
            builder.Append(value: "canary-drift,");
        }

        if (scene.Paced) {
            builder.Append(value: "paced,");
        }

        return ((builder.Length > 0) ? builder.ToString(startIndex: 0, length: (builder.Length - 1)) : string.Empty);
    }

    // ---- sweep block ---------------------------------------------------------------------------------------------

    private static string BuildSweepBlock(BenchSweepOutcome sweep) {
        var builder = new StringBuilder();
        var width = Math.Max(val1: 55, val2: ((sweep.SwitchName.Length + sweep.Suite.Length) + 24));

        builder.Append(value: Prefix).Append(value: Banner(title: $" BENCH SWEEP — {sweep.SwitchName} over {sweep.Suite} ", width: width)).Append(value: '\n');
        builder.Append(value: Prefix).Append(value: "value                overall   capped").Append(value: '\n');

        foreach (var leg in sweep.Legs) {
            var value = (leg.SwitchValue ?? "?");

            builder.Append(value: Prefix)
                .Append(value: value.PadRight(totalWidth: 20))
                .Append(value: (leg.Succeeded ? leg.Score.Overall.ToString(provider: CultureInfo.InvariantCulture) : "-").PadLeft(totalWidth: 8))
                .Append(value: "   ")
                .Append(value: ((leg.Succeeded && leg.Score.Capped) ? "yes" : "no"))
                .Append(value: '\n');
        }

        builder.Append(value: Prefix).Append(value: new string(c: '=', count: width));

        return builder.ToString();
    }

    // ---- shared number formatting ----------------------------------------------------------------------------------

    // Centers a title inside a fixed-width run of '=' characters (never narrower than the title itself).
    private static string Banner(string title, int width) {
        if (title.Length >= width) {
            return title;
        }

        var totalPad = (width - title.Length);
        var left = (totalPad / 2);
        var right = (totalPad - left);

        return ((new string(c: '=', count: left) + title) + new string(c: '=', count: right));
    }
    private static string Ms(double value) => value.ToString(format: "F2", provider: CultureInfo.InvariantCulture);
    private static string Fps(double value) => value.ToString(format: "F1", provider: CultureInfo.InvariantCulture);
    private static string Int(int value) => value.ToString(provider: CultureInfo.InvariantCulture);
}
