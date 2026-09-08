using System.Diagnostics;

namespace Puck.AdvancedGamingBrick.Post;

/// <summary>
/// Tier-B stage: run a named group of reference conformance-suite ROMs and pass only when every one reports success
/// through its <c>r12</c> verdict register. These suites (CPU arm/thumb, memory timing, the save-backup state machines,
/// the nes exerciser) are the primary external correctness oracle for the CPU, bus, and cartridge. The group skips (never
/// fails) when the ROM corpus is absent, so the POST still runs anywhere; every ROM is its own case row, and the ROMs
/// run on every processor at once since each builds its own machine.
/// </summary>
internal sealed class ConformanceRomStage : IPostStage<PostContext> {
    private readonly string m_group;
    private readonly IReadOnlyList<(string RelativePath, string Name)> m_cases;

    /// <summary>Initializes a new instance of the <see cref="ConformanceRomStage"/> class.</summary>
    /// <param name="group">The group name (also the stage-name suffix).</param>
    /// <param name="cases">The (corpus-relative path, display name) pairs the group runs.</param>
    public ConformanceRomStage(string group, IReadOnlyList<(string RelativePath, string Name)> cases) {
        ArgumentException.ThrowIfNullOrEmpty(argument: group);
        ArgumentNullException.ThrowIfNull(argument: cases);

        m_group = group;
        m_cases = cases;
    }

    /// <inheritdoc/>
    public string Name =>
        $"conformance-{m_group}";
    /// <inheritdoc/>
    public PostTier Tier =>
        PostTier.B;

    /// <inheritdoc/>
    public PostStageOutcome Run(PostContext context) {
        var cases = RomCatalog.Resolve(
            root: context.TestRomRoot,
            group: m_group,
            cases: m_cases
        );

        if (cases.Count == 0) {
            return PostStageOutcome.Skip(detail: $"no conformance {m_group} ROMs (fetch the gba-tests corpus or pass --roms)");
        }

        return RomCaseRunner.Run(
            cases: cases,
            parallelism: context.Parallelism,
            probe: romCase => ConformanceRomProbe.Run(
                bios: context.BiosImage,
                romCase: romCase
            )
        );
    }
}
/// <summary>Runs a list of pass/fail ROM cases on every processor at once and folds them into a stage outcome with one
/// row per case, the shape every corpus stage of this battery shares.</summary>
internal static class RomCaseRunner {
    /// <summary>Runs the cases.</summary>
    /// <param name="cases">The cases, in report order.</param>
    /// <param name="probe">Measures one case: whether it passed and a one-line detail.</param>
    /// <param name="parallelism">How many cases to run at once.</param>
    /// <returns>The stage outcome.</returns>
    public static PostStageOutcome Run(IReadOnlyList<RomCase> cases, Func<RomCase, (bool Pass, string Detail)> probe, int parallelism) {
        var results = new PostCaseResult[cases.Count];

        _ = Parallel.For(
            body: index => {
                var romCase = cases[index];
                var start = Stopwatch.GetTimestamp();

                try {
                    var (pass, detail) = probe(romCase);

                    results[index] = new PostCaseResult(
                        Detail: detail,
                        Duration: Stopwatch.GetElapsedTime(startingTimestamp: start),
                        Name: romCase.Name,
                        Verdict: (pass
                            ? PostCaseVerdict.Pass
                            : PostCaseVerdict.Mismatch)
                    );
                } catch (Exception exception) when (exception is not OutOfMemoryException) {
                    results[index] = new PostCaseResult(
                        Detail: $"threw {exception.GetType().Name}: {exception.Message}",
                        Duration: Stopwatch.GetElapsedTime(startingTimestamp: start),
                        Name: romCase.Name,
                        Verdict: PostCaseVerdict.Error
                    );
                }
            },
            fromInclusive: 0,
            parallelOptions: new ParallelOptions {
                MaxDegreeOfParallelism = parallelism,
            },
            toExclusive: cases.Count
        );

        var passed = results.Count(predicate: static result => (result.Verdict == PostCaseVerdict.Pass));
        var failures = results
            .Where(predicate: static result => (result.Verdict != PostCaseVerdict.Pass))
            .Select(selector: static result => $"{result.Name} ({result.Detail})")
            .ToArray();

        return ((failures.Length == 0)
            ? PostStageOutcome.Pass(
                cases: results,
                detail: $"{passed}/{cases.Count} passed"
            )
            : PostStageOutcome.Fail(
                cases: results,
                detail: $"{passed}/{cases.Count} passed; failed: {string.Join(
                    separator: ", ",
                    values: failures
                )}"
            ));
    }
}
