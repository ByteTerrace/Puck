using System.Globalization;
using System.Text.Json.Nodes;

namespace Puck.Cli.Bench;

/// <summary>What a comparison of one chapter is.</summary>
internal enum BenchmarkOutcome {
    /// <summary>The same program, both ended and arrived, neither disturbed: its numbers are compared.</summary>
    Comparable,
    /// <summary>The chapter's program differs between the runs: both are described and no difference is attributed to execution.</summary>
    ChangedWorkload,
    /// <summary>A chapter is off course, disturbed, unfinished or missing in either run: nothing is compared and the raw evidence stays.</summary>
    Unavailable,
}

/// <summary>A comparison the comparer will not make, by the name of what stops it.</summary>
/// <param name="Code">The refusal's name: <see cref="BenchmarkComparison.AnotherRoute"/>, <see cref="BenchmarkComparison.UnlikeConditions"/>,
/// <see cref="BenchmarkComparison.BeganInLockstep"/>, <see cref="BenchmarkComparison.DisturbedRun"/> or <see cref="BenchmarkComparison.MissingMetric"/>.</param>
/// <param name="Message">What differs, naming both sides.</param>
internal sealed record BenchmarkRefusal(string Code, string Message);

/// <summary>One metric of a compared chapter.</summary>
/// <param name="Name">The metric's name.</param>
/// <param name="Baseline">The baseline's value.</param>
/// <param name="Candidate">The candidate's value.</param>
/// <param name="Unit">The unit.</param>
/// <param name="Kind">How the reading answers load and cadence.</param>
internal sealed record BenchmarkDelta(string Name, double Baseline, double Candidate, string Unit, BenchmarkReadingKind Kind) {
    /// <summary>Gets the candidate less the baseline.</summary>
    public double Difference => (Candidate - Baseline);
    /// <summary>Gets the difference as a percentage of the baseline, or <see langword="null"/> when the baseline is zero.</summary>
    public double? Percent => ((Baseline == 0.0) ? null : ((Difference / Math.Abs(value: Baseline)) *100.0));
}

/// <summary>One chapter's comparison.</summary>
/// <param name="Loop">The loop's kind: cold, warm or steady.</param>
/// <param name="Name">The chapter's name.</param>
/// <param name="Outcome">What the comparison is.</param>
/// <param name="Reason">Why the chapter is unavailable or a changed workload, or <see langword="null"/>.</param>
/// <param name="Hash">Whether the chapter's end state hash was compared and what it showed.</param>
/// <param name="Deltas">The metrics of a comparable chapter.</param>
internal sealed record BenchmarkChapterComparison(string Loop, string Name, BenchmarkOutcome Outcome, string? Reason, string Hash, IReadOnlyList<BenchmarkDelta> Deltas);

/// <summary>One backend's comparison: the refusals that stopped it, or its chapters.</summary>
/// <param name="Backend">The backend, or <c>headless</c>.</param>
/// <param name="Refusals">What stopped the comparison; empty when it was made.</param>
/// <param name="Chapters">Each chapter's comparison.</param>
internal sealed record BenchmarkRunComparison(string Backend, IReadOnlyList<BenchmarkRefusal> Refusals, IReadOnlyList<BenchmarkChapterComparison> Chapters);

/// <summary>
/// Compares two benchmark reports chapter by chapter. A comparison is refused, by name, when it would set unlike things
/// side by side: another route, unlike conditions, a run that began in lockstep, or a metric one report lacks. A chapter
/// is comparable, a changed workload, or unavailable; the numbers of a comparable chapter are set side by side with the
/// difference, and no threshold judges them. No frame of one run is compared with a frame of another.
/// </summary>
internal sealed class BenchmarkComparison {
    private BenchmarkComparison(IReadOnlyList<BenchmarkRunComparison> runs) => Runs = runs;

    /// <summary>Gets each backend's comparison.</summary>
    public IReadOnlyList<BenchmarkRunComparison> Runs { get; }

    /// <summary>Gets every refusal of every backend.</summary>
    public IEnumerable<BenchmarkRefusal> Refusals => Runs.SelectMany(selector: static run => run.Refusals);

    /// <summary>Gets the chapters that were compared or described as a changed workload.</summary>
    public int Compared => Runs.Sum(selector: static run => run.Chapters.Count(predicate: static chapter => (chapter.Outcome != BenchmarkOutcome.Unavailable)));

    /// <summary>Gets the chapters that could not be compared.</summary>
    public int Unavailable => Runs.Sum(selector: static run => run.Chapters.Count(predicate: static chapter => (chapter.Outcome == BenchmarkOutcome.Unavailable)));

    /// <summary>Gets the exit status: <see cref="CliExit.Refused"/> when any comparison was refused,
    /// <see cref="CliExit.Failed"/> when no chapter could be compared or described (a run whose every chapter is
    /// unavailable is a failure, never a completed comparison), else <see cref="CliExit.Success"/>.</summary>
    public int ExitCode => (Refusals.Any() ? CliExit.Refused : ((Compared == 0) ? CliExit.Failed : CliExit.Success));

    /// <summary>The refusal for a baseline and a candidate that are not one route.</summary>
    public const string AnotherRoute = "another-route";
    /// <summary>The refusal for runs made under unlike conditions.</summary>
    public const string UnlikeConditions = "unlike-conditions";
    /// <summary>The refusal for a run that began with presentation locked to ticks.</summary>
    public const string BeganInLockstep = "began-in-lockstep";
    /// <summary>The refusal for a metric one report holds and the other lacks.</summary>
    public const string MissingMetric = "missing-metric";
    /// <summary>The refusal for a run whose conditions (competing load, another World, a held GPU clock) make its timings untrustworthy.</summary>
    public const string DisturbedRun = "disturbed-run";

    /// <summary>Compares two benchmark reports, backend by backend.</summary>
    /// <param name="baseline">The baseline's <c>benchmark</c> section.</param>
    /// <param name="candidate">The candidate's <c>benchmark</c> section.</param>
    /// <returns>The comparison.</returns>
    public static BenchmarkComparison Of(JsonObject baseline, JsonObject candidate) {
        var runs = new List<BenchmarkRunComparison>();
        var baseRoute = (baseline["route"]?.GetValue<string>() ?? string.Empty);
        var candidateRoute = (candidate["route"]?.GetValue<string>() ?? string.Empty);
        var candidates = (candidate["runs"] as JsonArray ?? []).OfType<JsonObject>().ToArray();
        var matched = 0;

        foreach (var run in candidates) {
            var backend = (run["backend"]?.GetValue<string>() ?? string.Empty);
            var other = (baseline["runs"] as JsonArray ?? []).OfType<JsonObject>().FirstOrDefault(predicate: row => (row["backend"]?.GetValue<string>() == backend));

            if (other is null) {
                continue;
            }

            matched++;
            runs.Add(item: CompareRun(backend: backend, baseline: other, candidate: run, baseRoute: baseRoute, candidateRoute: candidateRoute));
        }
        if (matched == 0) {
            var held = string.Join(separator: ", ", values: (baseline["runs"] as JsonArray ?? []).OfType<JsonObject>().Select(selector: static row => (row["backend"]?.GetValue<string>() ?? "?")));
            var named = string.Join(separator: ", ", values: candidates.Select(selector: static row => (row["backend"]?.GetValue<string>() ?? "?")));

            runs.Add(item: new BenchmarkRunComparison(Backend: "-", Chapters: [], Refusals: [new(Code: UnlikeConditions, Message: $"the baseline holds runs on [{held}] and the candidate on [{named}]: no backend is in both")]));
        }

        return new BenchmarkComparison(runs: runs);
    }

    private static BenchmarkRunComparison CompareRun(string backend, JsonObject baseline, JsonObject candidate, string baseRoute, string candidateRoute) {
        var refusals = new List<BenchmarkRefusal>();

        if (!string.Equals(a: baseRoute, b: candidateRoute, comparisonType: StringComparison.Ordinal)) {
            refusals.Add(item: new(Code: AnotherRoute, Message: $"the baseline played '{baseRoute}' and the candidate '{candidateRoute}'"));
        } else if (ChapterNames(run: baseline) is var names && !names.SequenceEqual(second: ChapterNames(run: candidate), comparer: StringComparer.Ordinal)) {
            refusals.Add(item: new(Code: AnotherRoute, Message: $"the route '{baseRoute}' has chapters [{string.Join(separator: " | ", values: names)}] in the baseline and [{string.Join(separator: " | ", values: ChapterNames(run: candidate))}] in the candidate"));
        }

        var differences = ConditionDifferences(baseline: baseline, candidate: candidate).ToArray();

        if (differences.Length > 0) {
            refusals.Add(item: new(Code: UnlikeConditions, Message: string.Join(separator: "; ", values: differences)));
        }
        foreach (var (side, run) in new[] { ("baseline", baseline), ("candidate", candidate) }) {
            if (run["lockstepAtEntry"]?.GetValue<bool>() == true) {
                refusals.Add(item: new(Code: BeganInLockstep, Message: $"the {side} run began with presentation locked to ticks, which makes every frame of it a tick-paced frame"));
            }
        }
        foreach (var (side, run) in new[] { ("baseline", baseline), ("candidate", candidate) }) {
            if (BenchmarkConditions.ReasonsOf(run: run) is { Count: > 0 } reasons) {
                refusals.Add(item: new(Code: DisturbedRun, Message: $"the {side} run's conditions make its timings untrustworthy: {string.Join(separator: "; ", values: reasons)}"));
            }
        }
        if (refusals.Count > 0) {
            return new BenchmarkRunComparison(Backend: backend, Chapters: [], Refusals: refusals);
        }

        var chapters = new List<BenchmarkChapterComparison>();
        var fresh = ((baseline["start"]?.GetValue<string>() == "fresh") && (candidate["start"]?.GetValue<string>() == "fresh"));
        var loops = (baseline["loops"] as JsonArray ?? []).OfType<JsonObject>().ToArray();
        var others = (candidate["loops"] as JsonArray ?? []).OfType<JsonObject>().ToArray();

        for (var index = 0; (index < Math.Min(val1: loops.Length, val2: others.Length)); index++) {
            var kind = (loops[index]["kind"]?.GetValue<string>() ?? $"loop {index}");
            var left = (loops[index]["chapters"] as JsonArray ?? []).OfType<JsonObject>().ToArray();
            var right = (others[index]["chapters"] as JsonArray ?? []).OfType<JsonObject>().ToArray();

            for (var chapter = 0; (chapter < Math.Min(val1: left.Length, val2: right.Length)); chapter++) {
                chapters.Add(item: CompareChapter(kind: kind, baseline: left[chapter], candidate: right[chapter], fresh: fresh, refusals: refusals));
            }
        }

        return new BenchmarkRunComparison(Backend: backend, Chapters: chapters, Refusals: refusals);
    }

    private static string[] ChapterNames(JsonObject run) => (run["loops"] as JsonArray ?? []).OfType<JsonObject>().FirstOrDefault()?["chapters"] is JsonArray chapters
        ? [.. chapters.OfType<JsonObject>().Select(selector: static chapter => (chapter["name"]?.GetValue<string>() ?? string.Empty))]
        : [];

    // Every condition one run states that the other states differently or not at all, each by key with both values.
    private static IEnumerable<string> ConditionDifferences(JsonObject baseline, JsonObject candidate) {
        var left = (baseline["conditions"] as JsonObject ?? []);
        var right = (candidate["conditions"] as JsonObject ?? []);

        foreach (var key in left.Select(selector: static pair => pair.Key).Union(second: right.Select(selector: static pair => pair.Key)).Order(comparer: StringComparer.Ordinal)) {
            var a = left[key]?.ToJsonString();
            var b = right[key]?.ToJsonString();

            if (!string.Equals(a: a, b: b, comparisonType: StringComparison.Ordinal)) {
                yield return $"{key} was {(a ?? "unstated")} in the baseline and {(b ?? "unstated")} in the candidate";
            }
        }
    }

    private static BenchmarkChapterComparison CompareChapter(string kind, JsonObject baseline, JsonObject candidate, bool fresh, List<BenchmarkRefusal> refusals) {
        var name = (candidate["name"]?.GetValue<string>() ?? string.Empty);

        if ((WhyUnavailable(side: "baseline", chapter: baseline) ?? WhyUnavailable(side: "candidate", chapter: candidate)) is { } why) {
            return new BenchmarkChapterComparison(Loop: kind, Name: name, Outcome: BenchmarkOutcome.Unavailable, Reason: why, Hash: "not compared", Deltas: []);
        }

        var identity = baseline["identity"]?.GetValue<string>();
        var other = candidate["identity"]?.GetValue<string>();

        if (!string.Equals(a: identity, b: other, comparisonType: StringComparison.Ordinal)) {
            return new BenchmarkChapterComparison(Loop: kind, Name: name, Outcome: BenchmarkOutcome.ChangedWorkload, Reason: $"the chapter's program is {identity} in the baseline and {other} in the candidate, so no difference is attributed to execution", Hash: "not compared", Deltas: []);
        }

        var left = BenchmarkMetrics.Of(chapter: baseline);
        var right = BenchmarkMetrics.Of(chapter: candidate);
        var missing = new List<string>();

        foreach (var metric in left.Keys.Union(second: right.Keys).Where(predicate: BenchmarkMetrics.IsRequired)) {
            if (!left.ContainsKey(key: metric)) {
                missing.Add(item: $"{metric} (absent from the baseline)");
            } else if (!right.ContainsKey(key: metric)) {
                missing.Add(item: $"{metric} (absent from the candidate)");
            }
        }
        if (missing.Count > 0) {
            refusals.Add(item: new(Code: BenchmarkComparison.MissingMetric, Message: $"{kind} chapter \"{name}\": {string.Join(separator: ", ", values: missing)}"));

            return new BenchmarkChapterComparison(Loop: kind, Name: name, Outcome: BenchmarkOutcome.Unavailable, Reason: "a metric is missing", Hash: "not compared", Deltas: []);
        }

        var deltas = left.Where(predicate: pair => right.ContainsKey(key: pair.Key)).Select(selector: pair => new BenchmarkDelta(Name: pair.Key, Baseline: pair.Value.Value, Candidate: right[pair.Key].Value, Unit: pair.Value.Unit, Kind: pair.Value.Kind)).ToArray();
        var hash = (!fresh ? "not compared (a run that was not fresh)" : (string.Equals(a: baseline["stateHash"]?.GetValue<string>(), b: candidate["stateHash"]?.GetValue<string>(), comparisonType: StringComparison.Ordinal) ? "same" : "different"));

        return new BenchmarkChapterComparison(Loop: kind, Name: name, Outcome: BenchmarkOutcome.Comparable, Reason: null, Hash: hash, Deltas: deltas);
    }

    /// <summary>Returns whether a chapter of one run cannot stand in a comparison: it did not end, is off course or has no
    /// arrival read, or is disturbed.</summary>
    /// <param name="chapter">The chapter's report.</param>
    /// <returns><see langword="true"/> for an unavailable chapter.</returns>
    public static bool IsUnavailable(JsonObject chapter) => (WhyUnavailable(side: "run", chapter: chapter) is not null);

    // Why a chapter of one side cannot be compared, or null when it ended, arrived and was not disturbed.
    private static string? WhyUnavailable(string side, JsonObject chapter) {
        var status = chapter["status"]?.GetValue<string>();

        if (status != "ended") {
            return $"the {side} chapter is {status ?? "missing"}";
        }

        var arrival = chapter["arrival"]?.GetValue<string>();

        if (arrival != "arrived") {
            return ((arrival == "off course") ? $"the {side} chapter is off course: {string.Join(separator: "; ", values: (chapter["arrivalFailed"] as JsonArray ?? []).Select(selector: static gate => gate?.GetValue<string>()))}" : $"the {side} chapter's arrival is unmeasured");
        }
        if (chapter["disturbed"] is JsonArray { Count: > 0 } disturbed) {
            return $"the {side} chapter is disturbed: {string.Join(separator: "; ", values: disturbed.Select(selector: static reason => reason?.GetValue<string>()))}";
        }

        return null;
    }

    /// <summary>Writes the comparison as text: the refusals by name, then each chapter's outcome and the headline of
    /// its numbers.</summary>
    /// <param name="writer">The destination.</param>
    /// <param name="all">Whether to write every metric of a comparable chapter and not its headline.</param>
    public void Write(TextWriter writer, bool all) {
        foreach (var run in Runs) {
            writer.WriteLine(value: $"[{run.Backend}]");
            foreach (var refusal in run.Refusals) {
                writer.WriteLine(value: $"  REFUSED {refusal.Code}: {refusal.Message}");
            }
            foreach (var chapter in run.Chapters) {
                writer.WriteLine(value: $"  {chapter.Loop} \"{chapter.Name}\": {Describe(outcome: chapter.Outcome)}{((chapter.Reason is null) ? string.Empty : $": {chapter.Reason}")}");
                if (chapter.Outcome != BenchmarkOutcome.Comparable) {
                    continue;
                }

                writer.WriteLine(value: $"    end state hash: {chapter.Hash}");
                foreach (var delta in chapter.Deltas.Where(predicate: delta => (all || Headline(name: delta.Name)))) {
                    var percent = delta.Percent;

                    writer.WriteLine(value: string.Create(provider: CultureInfo.InvariantCulture, handler: $"    {delta.Name}: {delta.Baseline:0.###} -> {delta.Candidate:0.###} {delta.Unit} ({delta.Difference:+0.###;-0.###;0}{((percent is { } share) ? $", {share:+0.#;-0.#;0}%" : string.Empty)}) [{Reading(kind: delta.Kind)}]"));
                }
            }
        }
        writer.WriteLine(value: $"compared {Compared} chapter(s); {Unavailable} unavailable; {Refusals.Count()} refusal(s). Timings are read on one run each: a difference in them is a lead, which puck measure confirms in a quiet window.");
    }

    private static string Describe(BenchmarkOutcome outcome) => outcome switch {
        BenchmarkOutcome.Comparable => "comparable",
        BenchmarkOutcome.ChangedWorkload => "changed workload",
        _ => "unavailable",
    };

    private static string Reading(BenchmarkReadingKind kind) => kind switch {
        BenchmarkReadingKind.Deterministic => "exact",
        BenchmarkReadingKind.Cadence => "per frame",
        _ => "load-dependent",
    };

    private static bool Headline(string name) => (name is "frames.iterations" or "frames.averageRate" or "frames.interval.mean" or "frames.interval.p50" or "frames.interval.p99" or "frames.interval.p999" or "frames.interval.max" or "frames.work.p99" or "frames.over" or "frames.overTwice" or "hitches.count" or "frames.pauseLongestMs")
        || name.StartsWith(value: "gpu.", comparisonType: StringComparison.Ordinal);

    /// <summary>Writes the comparison as JSON.</summary>
    /// <returns>The comparison.</returns>
    public JsonObject ToJson() => new() {
        ["compared"] = Compared,
        ["unavailable"] = Unavailable,
        ["exit"] = ExitCode,
        ["runs"] = new JsonArray([.. Runs.Select(selector: static run => (JsonNode)new JsonObject {
            ["backend"] = run.Backend,
            ["refusals"] = new JsonArray([.. run.Refusals.Select(selector: static refusal => (JsonNode)new JsonObject { ["code"] = refusal.Code, ["message"] = refusal.Message })]),
            ["chapters"] = new JsonArray([.. run.Chapters.Select(selector: static chapter => (JsonNode)new JsonObject {
                ["loop"] = chapter.Loop,
                ["name"] = chapter.Name,
                ["outcome"] = Describe(outcome: chapter.Outcome),
                ["reason"] = chapter.Reason,
                ["hash"] = chapter.Hash,
                ["metrics"] = new JsonArray([.. chapter.Deltas.Select(selector: static delta => (JsonNode)new JsonObject {
                    ["name"] = delta.Name, ["baseline"] = delta.Baseline, ["candidate"] = delta.Candidate, ["difference"] = delta.Difference, ["percent"] = delta.Percent, ["unit"] = delta.Unit, ["kind"] = Reading(kind: delta.Kind),
                })]),
            })]),
        })]),
    };
}
