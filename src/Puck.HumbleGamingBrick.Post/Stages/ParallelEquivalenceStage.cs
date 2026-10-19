namespace Puck.HumbleGamingBrick.Post;

/// <summary>
/// Tier-B stage: what a run measures is independent of how many cases ran at once. Runs one small suite
/// (GBMicrotest: hundreds of two-frame cases) once on a single thread and once on every processor, and requires the
/// two outcome sequences to be equal, case for case — verdict, detail, and pixel counts included. Cases share
/// nothing by construction (each builds its own machine), and this is the stage that keeps that true.
/// </summary>
internal sealed class ParallelEquivalenceStage : IPostStage<PostContext> {
    /// <inheritdoc/>
    public bool IsConcurrent =>
        true;
    /// <inheritdoc/>
    public string Name =>
        "parallel-equivalence";
    /// <inheritdoc/>
    public PostTier Tier =>
        PostTier.B;

    /// <inheritdoc/>
    public PostStageOutcome Run(PostContext context) {
        var cases = SuiteCatalog.GbMicrotestRoms(root: context.TestRomRoot)
            .Where(predicate: static suiteCase => (suiteCase.Disposition == CaseDisposition.Runnable))
            .ToArray();

        if (cases.Length == 0) {
            return PostStageOutcome.Skip(detail: "no GBMicrotest cases discovered under the corpus root (pass --roms)");
        }

        var serial = SuiteEvaluator.MeasureOutcomes(
            cases: cases,
            parallelism: 1
        );
        var parallel = SuiteEvaluator.MeasureOutcomes(
            cases: cases,
            parallelism: context.Parallelism
        );
        var differences = new List<string>();

        for (var index = 0; (index < cases.Length); ++index) {
            if (serial[index] != parallel[index]) {
                differences.Add(item: $"{cases[index].RelativePath}[{cases[index].ModelKey}]: serial {serial[index].Verdict} ({serial[index].Detail}) vs parallel {parallel[index].Verdict} ({parallel[index].Detail})");
            }
        }

        return ((differences.Count == 0)
            ? PostStageOutcome.Pass(detail: $"{cases.Length} cases measured identically on 1 and {context.Parallelism} threads")
            : PostStageOutcome.Fail(detail: $"{differences.Count} of {cases.Length} cases differ between 1 and {context.Parallelism} threads: {string.Join(
                separator: "; ",
                values: differences.Take(count: 10)
            )}")
        );
    }
}
