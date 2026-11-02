using System.Globalization;

namespace Puck.Cli.Evals;

/// <summary>A run left out of a comparable score, and why.</summary>
/// <param name="Case">The case name.</param>
/// <param name="Arm">The arm.</param>
/// <param name="Run">The run's one-based index in its arm.</param>
/// <param name="Reason">Why it is infrastructure evidence rather than a verdict on the teaching.</param>
public sealed record EvalExclusion(string Case, string Arm, int Run, string Reason);

/// <summary>One case's comparable score in one arm.</summary>
/// <param name="Vector">Each included run's score, in run order.</param>
/// <param name="Perfect">How many included runs passed every comparable grader.</param>
/// <param name="GraderShare">The largest share of a run's score one comparable grader carries; a difference in means
/// above it takes more than one grader moving.</param>
/// <param name="Excluded">The runs left out, with their reasons.</param>
public sealed record EvalArmScore(IReadOnlyList<double> Vector, int Perfect, double GraderShare, IReadOnlyList<EvalExclusion> Excluded) {
    /// <summary>Gets the mean of <see cref="Vector"/>, or <see langword="null"/> when no run was included.</summary>
    public double? Mean => ((Vector.Count == 0) ? null : Vector.Average());
    /// <summary>Gets whether every included run was perfect (pass^k at k = the included runs), or
    /// <see langword="null"/> when no run was included.</summary>
    public bool? PassAll => ((Vector.Count == 0) ? null : (Perfect == Vector.Count));
    /// <summary>Formats the vector as <c>[1.00 0.67 1.00]</c>.</summary>
    /// <returns>The vector's text.</returns>
    public string FormatVector() => $"[{string.Join(separator: ' ', values: Vector.Select(selector: static score => score.ToString(format: "0.00", provider: CultureInfo.InvariantCulture)))}]";
}

/// <summary>The comparable recompute: one score per run that two result documents can be compared on whatever ablation
/// mode each ran in. The runner scores activation graders under <c>--ablation none</c> and excludes them under
/// <c>with-without</c>, so its own scores differ between the modes by those graders alone; this score leaves them out
/// of every run. It counts only graders that executed, and leaves out a run whose turns reached the case's cap, that
/// ended on an error, that a mock aborted, or whose judge graders the cost ceiling skipped, listing each.</summary>
public static class EvalScoring {
    /// <summary>Indicates whether a verdict counts toward the comparable score.</summary>
    /// <param name="evalCase">The case the verdict belongs to.</param>
    /// <param name="verdict">The verdict.</param>
    /// <returns><see langword="true"/> for an executed grader that is not an activation grader.</returns>
    public static bool Counts(EvalCase evalCase, EvalGraderVerdict verdict) =>
        ((verdict.Passed is not null) && !verdict.WithOnly && !(evalCase.Definition(name: verdict.Name)?.IsActivation ?? false));
    /// <summary>Returns why a run is left out of the comparable score, or <see langword="null"/> when it counts.</summary>
    /// <param name="evalCase">The case.</param>
    /// <param name="run">The run.</param>
    /// <returns>The reason, or <see langword="null"/>.</returns>
    public static string? ExclusionReason(EvalCase evalCase, EvalRun run) {
        if (run.Error is { } error) { return $"error: {error}"; }
        if (run.Turns >= evalCase.MaxTurns) { return $"reached max_turns ({run.Turns} of {evalCase.MaxTurns})"; }
        if (run.Aborted) { return "a mock aborted it"; }
        if (run.SkippedPaidGraders) { return "the cost ceiling skipped its judge graders"; }
        return (run.Graders.Any(predicate: verdict => Counts(evalCase: evalCase, verdict: verdict)) ? null : "no comparable grader executed");
    }
    /// <summary>Scores one case's arm.</summary>
    /// <param name="evalCase">The case.</param>
    /// <param name="arm">The arm, <c>with</c> or <c>without</c>.</param>
    /// <returns>The score, with an empty vector when the arm has no included run or did not run.</returns>
    public static EvalArmScore Score(EvalCase evalCase, string arm) {
        var vector = new List<double>();
        var excluded = new List<EvalExclusion>();
        var perfect = 0;
        var share = 0.0;

        foreach (var run in (evalCase.Arms.TryGetValue(key: arm, value: out var runs) ? runs : [])) {
            if (ExclusionReason(evalCase: evalCase, run: run) is { } reason) {
                excluded.Add(item: new EvalExclusion(Arm: arm, Case: evalCase.Name, Reason: reason, Run: run.Index));
                continue;
            }
            var counted = run.Graders.Where(predicate: verdict => Counts(evalCase: evalCase, verdict: verdict)).ToList();
            var total = counted.Sum(selector: static verdict => verdict.Weight);
            var passed = counted.Where(predicate: static verdict => (verdict.Passed == true)).Sum(selector: static verdict => verdict.Weight);

            vector.Add(item: (passed / total));
            share = Math.Max(val1: share, val2: (counted.Max(selector: static verdict => verdict.Weight) / total));
            if (counted.All(predicate: static verdict => (verdict.Passed == true))) { perfect++; }
        }
        return new EvalArmScore(Excluded: excluded, GraderShare: share, Perfect: perfect, Vector: vector);
    }
    /// <summary>Judges whether the difference between two arms' scores is real: the means differ by more than one
    /// grader's share of a run's score, and the run vectors do not overlap.</summary>
    /// <param name="a">The first score.</param>
    /// <param name="b">The second score.</param>
    /// <returns><see langword="true"/> for a real difference; <see langword="false"/> when either side has no included
    /// run.</returns>
    public static bool IsReal(EvalArmScore a, EvalArmScore b) {
        if ((a.Mean is not { } meanA) || (b.Mean is not { } meanB)) { return false; }
        var separated = ((a.Vector.Max() < b.Vector.Min()) || (b.Vector.Max() < a.Vector.Min()));

        return (separated && (Math.Abs(value: (meanB - meanA)) > Math.Max(val1: a.GraderShare, val2: b.GraderShare)));
    }
}
