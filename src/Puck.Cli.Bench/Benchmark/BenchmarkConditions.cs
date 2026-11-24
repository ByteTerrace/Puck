using System.Text.Json.Nodes;
using Puck.Cli.Host;

namespace Puck.Cli.Bench;

/// <summary>
/// The machine's conditions a benchmark run is written with, and the verdict on them: a run is disturbed when the rest of the
/// machine used enough CPU, another World was live, or the adapter's clock was held, and a disturbed run's timings are not a
/// verdict on the build. The World records the adapter (<c>adapter</c> of its run, and of each chapter); the verb records the
/// competition and joins both into a run's <c>machine</c> object, which the comparer reads.
/// </summary>
internal static class BenchmarkConditions {
    /// <summary>The verdict of a run whose conditions are quiet.</summary>
    public const string Clean = "clean";
    /// <summary>The verdict of a run whose conditions make its timings untrustworthy.</summary>
    public const string Disturbed = "disturbed";
    /// <summary>The verdict of a run no verb watched (one typed into a World by hand and folded): only the adapter's clock
    /// was judged, and what the rest of the machine did is unknown.</summary>
    public const string Unwatched = "unwatched";

    /// <summary>Joins the competition the verb read and the adapter the World read into one run's <c>machine</c> object.</summary>
    /// <param name="run">The World's own report of the run, whose <c>adapter</c> holds the adapter's state.</param>
    /// <param name="competition">What the rest of the machine did while the run played, or <see langword="null"/> when none was recorded.</param>
    /// <returns>The object: the verdict, the reasons for it, the competition and the thresholds both judgements used.</returns>
    public static JsonObject Machine(JsonObject run, HostCompetitionReport? competition) {
        var reasons = new JsonArray();

        foreach (var reason in (competition?.Disturbed ?? [])) {
            reasons.Add(item: reason);
        }
        if (run["adapter"]?["heldReason"]?.GetValue<string>() is { } held) {
            reasons.Add(item: held);
        }

        return new JsonObject {
            ["verdict"] = ((reasons.Count > 0) ? Disturbed : ((competition is null) ? Unwatched : Clean)),
            ["disturbed"] = reasons,
            ["competition"] = ((competition is null) ? new JsonObject { ["recorded"] = false } : Competition(report: competition)),
        };
    }

    private static JsonObject Competition(HostCompetitionReport report) => new() {
        ["recorded"] = true,
        ["wallSeconds"] = report.WallSeconds,
        ["logicalProcessors"] = report.LogicalProcessors,
        ["othersCpuSeconds"] = report.OthersCpuSeconds,
        ["othersShare"] = report.OthersShare,
        ["othersCores"] = report.OthersCores,
        ["ownCpuSeconds"] = report.OwnCpuSeconds,
        ["attributedSeconds"] = report.AttributedSeconds,
        ["disturbedAboveShare"] = HostCompetitionRecorder.DisturbedShare,
        ["disturbedAboveCores"] = HostCompetitionRecorder.DisturbedCores,
        ["top"] = new JsonArray([.. report.Top.Select(selector: static competitor => (JsonNode)new JsonObject { ["name"] = competitor.Name, ["processes"] = competitor.Processes, ["cpuSeconds"] = competitor.CpuSeconds })]),
        ["worlds"] = new JsonArray([.. report.Worlds.Select(selector: static world => (JsonNode)world)]),
        ["leaseHolders"] = new JsonArray([.. report.LeaseHolders.Select(selector: static holder => (JsonNode)holder)]),
        ["readings"] = report.Readings,
    };

    /// <summary>Returns the reasons a run is disturbed.</summary>
    /// <param name="run">One run of a report.</param>
    /// <returns>The reasons; empty for a quiet run or one that recorded no conditions.</returns>
    public static IReadOnlyList<string> ReasonsOf(JsonObject run) => (run["machine"]?["disturbed"] as JsonArray ?? []).Select(selector: static reason => (reason?.GetValue<string>() ?? string.Empty)).ToArray();

    /// <summary>Writes the verdict over every run of a report as one line, or <see langword="null"/> when they are all quiet.</summary>
    /// <param name="runs">The report's runs.</param>
    /// <returns>The line, <c>DISTURBED: &lt;backend&gt;: &lt;reasons&gt; | ...</c>, <c>UNWATCHED: ...</c> when a run no
    /// verb watched is among them and none is disturbed, or <see langword="null"/>.</returns>
    public static string? Describe(IEnumerable<JsonObject> runs) {
        var all = runs.ToArray();
        var disturbed = all.Select(selector: static run => (Backend: (run["backend"]?.GetValue<string>() ?? "?"), Reasons: ReasonsOf(run: run))).Where(predicate: static run => (run.Reasons.Count > 0)).ToArray();

        if (disturbed.Length > 0) {
            return $"DISTURBED, its timings are not a verdict: {string.Join(separator: " | ", values: disturbed.Select(selector: static run => $"{run.Backend}: {string.Join(separator: "; ", values: run.Reasons)}"))}";
        }

        return all.Any(predicate: static run => (run["machine"]?["verdict"]?.GetValue<string>() == Unwatched))
            ? "UNWATCHED: no verb read what the rest of the machine did while a run played; only the adapter's clock was judged"
            : null;
    }
}
