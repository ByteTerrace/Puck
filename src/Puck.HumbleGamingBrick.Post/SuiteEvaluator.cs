using System.Collections.Concurrent;
using System.Diagnostics;
using Puck.Assets;

namespace Puck.HumbleGamingBrick.Post;

/// <summary>
/// Runs one suite's discovered <see cref="SuiteCase"/>s and reports what each did, the one measurement every corpus
/// stage shares. A case passes when its probe reports success, fails when the probe reports failure or no result
/// within its frame budget, and is skipped when the corpus's own howto makes it impossible to check mechanically; a
/// case that throws is an error. The stage fails when any case failed or errored, and its report names a bounded
/// sample of them, so a ROM that does not pass is visible on every run and nothing records that it once did not. A
/// suite with no cases on disk skips, or is an infrastructure failure under <see cref="PostContext.RequireAssets"/>.
/// </summary>
internal static class SuiteEvaluator {
    private sealed record CaseMeasurement(ProbeOutcome? Outcome, string? Error, TimeSpan Duration);

    private static (PostCaseVerdict Verdict, string Detail) Classify(SuiteCase suiteCase, CaseMeasurement measurement) {
        if (suiteCase.Disposition == CaseDisposition.Unrunnable) {
            return (PostCaseVerdict.Skip, (suiteCase.UnrunnableReason ?? "unrunnable"));
        }

        var outcome = measurement.Outcome;

        if (outcome is null) {
            return (PostCaseVerdict.Error, (measurement.Error ?? "not measured"));
        }

        return (outcome.Verdict switch {
            ProbeVerdict.Pass => (PostCaseVerdict.Pass, outcome.Detail),
            ProbeVerdict.Fail => (PostCaseVerdict.Mismatch, $"fail: {outcome.Detail}"),
            _ => (PostCaseVerdict.Mismatch, $"inconclusive: {outcome.Detail}"),
        });
    }
    private static string JoinCapped(IEnumerable<string> lines) {
        var all = lines.ToArray();
        var shown = string.Join(
            separator: "; ",
            values: all.Take(count: MaxJoinedLines)
        );

        return ((all.Length > MaxJoinedLines)
            ? $"{shown} (+{(all.Length - MaxJoinedLines)} more)"
            : shown
        );
    }
    // Cases are independent (each builds its own machine), so they run on every processor at once; the long ones go
    // first so the run does not end on a tail of single-threaded work. A case's own ordering in the report is its
    // discovery order, restored by the index.
    private static CaseMeasurement[] Measure(IReadOnlyList<SuiteCase> cases, int parallelism) {
        var measurements = new CaseMeasurement[cases.Count];
        var order = Enumerable
            .Range(
            count: cases.Count,
            start: 0
        )
            .OrderByDescending(keySelector: index => cases[index].FrameCap)
            .ThenBy(keySelector: static index => index)
            .ToArray();

        _ = Parallel.ForEach(
            body: index => measurements[index] = MeasureOne(suiteCase: cases[index]),
            parallelOptions: new ParallelOptions {
                MaxDegreeOfParallelism = parallelism,
            },
            source: Partitioner.Create(
                array: order,
                loadBalance: true
            )
        );

        return measurements;
    }
    private static CaseMeasurement MeasureOne(SuiteCase suiteCase) {
        if (suiteCase.Disposition == CaseDisposition.Unrunnable) {
            return new CaseMeasurement(
                Duration: TimeSpan.Zero,
                Error: null,
                Outcome: null
            );
        }

        var start = Stopwatch.GetTimestamp();

        try {
            var outcome = ProbeRunner.Run(suiteCase: suiteCase);

            return new CaseMeasurement(
                Duration: Stopwatch.GetElapsedTime(startingTimestamp: start),
                Error: null,
                Outcome: outcome
            );
        } catch (Exception exception) when ((exception is not OutOfMemoryException)) {
            return new CaseMeasurement(
                Duration: Stopwatch.GetElapsedTime(startingTimestamp: start),
                Error: $"threw {exception.GetType().Name}: {exception.Message}",
                Outcome: null
            );
        }
    }
    // A failing screenshot leaves the frame it produced and a mask of the differing pixels beside the report, so a red
    // run is diagnosable from its artifacts.
    private static void WriteMismatchImages(string directory, string name, ProbeOutcome outcome) {
        var actual = outcome.ActualImage!;
        var stem = Path.Combine(
            path1: directory,
            path2: name
                .Replace(
                newChar: '_',
                oldChar: '/'
            )
                .Replace(
                newChar: '_',
                oldChar: '\\'
            )
        );

        _ = Directory.CreateDirectory(path: directory);
        PngEncoder.Write(
            height: actual.Height,
            path: (stem + ".actual.png"),
            rgba: actual.Rgba,
            width: actual.Width
        );

        var expected = outcome.ExpectedImage;

        if (
            (expected is null) ||
            (expected.Rgba.Length != actual.Rgba.Length)
        ) {
            return;
        }

        var mask = new byte[actual.Rgba.Length];

        for (var offset = 0; (offset < mask.Length); offset += 4) {
            var same = (
                (actual.Rgba[offset] == expected.Rgba[offset]) &&
                (actual.Rgba[(offset + 1)] == expected.Rgba[(offset + 1)]) &&
                (actual.Rgba[(offset + 2)] == expected.Rgba[(offset + 2)])
            );

            mask[offset] = (same
                ? actual.Rgba[offset]
                : ((byte)0xFF)
            );
            mask[(offset + 1)] = (same
                ? actual.Rgba[(offset + 1)]
                : ((byte)0x00)
            );
            mask[(offset + 2)] = (same
                ? actual.Rgba[(offset + 2)]
                : ((byte)0x00)
            );
            mask[(offset + 3)] = 0xFF;
        }

        PngEncoder.Write(
            height: actual.Height,
            path: (stem + ".diff.png"),
            rgba: mask,
            width: actual.Width
        );
    }

    /// <summary>Evaluates a suite's cases.</summary>
    /// <param name="context">The shared run context.</param>
    /// <param name="cases">The suite's discovered cases.</param>
    /// <param name="name">The stage's name, which is the directory under the artifacts directory its mismatch images land in.</param>
    /// <returns>The stage outcome, carrying one row per case.</returns>
    public static PostStageOutcome Evaluate(PostContext context, IReadOnlyList<SuiteCase> cases, string name) {
        if (cases.Count == 0) {
            return (context.RequireAssets
                ? PostStageOutcome.Infra(detail: "no cases discovered under the corpus root (--require-assets)")
                : PostStageOutcome.Skip(detail: "no cases discovered under the corpus root (pass --roms)")
            );
        }

        var measurements = Measure(
            cases: cases,
            parallelism: context.Parallelism
        );
        var results = new List<PostCaseResult>(capacity: cases.Count);
        var pass = 0;
        var skipped = 0;
        var failed = 0;
        var problems = new List<string>();
        var stageArtifacts = Path.Combine(
            path1: context.ArtifactsDirectory,
            path2: name
        );

        for (var index = 0; (index < cases.Count); ++index) {
            var suiteCase = cases[index];
            var measurement = measurements[index];
            var caseName = $"{suiteCase.RelativePath}[{suiteCase.ModelKey}]";
            var (verdict, detail) = Classify(
                measurement: measurement,
                suiteCase: suiteCase
            );

            switch (verdict) {
                case PostCaseVerdict.Pass:
                    ++pass;

                    break;
                case PostCaseVerdict.Skip:
                    ++skipped;

                    break;
                default:
                    ++failed;
                    problems.Add(item: $"{caseName} {detail}");

                    // A suite can fail thousands of cases, so only a bounded sample leaves images behind.
                    if (
                        (failed <= MaxJoinedLines) &&
                        (measurement.Outcome?.ActualImage is not null)
                    ) {
                        WriteMismatchImages(
                            directory: stageArtifacts,
                            name: caseName,
                            outcome: measurement.Outcome
                        );
                    }

                    break;
            }

            results.Add(item: new PostCaseResult(
                Detail: detail,
                Duration: measurement.Duration,
                Name: caseName,
                Verdict: verdict
            ));
        }

        var summary = $"{pass} pass, {failed} fail, {skipped} unrunnable of {cases.Count}";

        return ((failed == 0)
            ? PostStageOutcome.Pass(
                cases: results,
                detail: summary
            )
            : PostStageOutcome.Fail(
                cases: results,
                detail: $"{summary}; {JoinCapped(lines: problems)}"
            )
        );
    }
    /// <summary>Runs every case and returns its outcome, in case order.</summary>
    /// <param name="cases">The cases to run; none may be <see cref="CaseDisposition.Unrunnable"/>.</param>
    /// <param name="parallelism">How many cases to run at once.</param>
    /// <returns>One outcome per case.</returns>
    /// <exception cref="InvalidOperationException">A case could not be run.</exception>
    public static ProbeOutcome[] MeasureOutcomes(IReadOnlyList<SuiteCase> cases, int parallelism) {
        var measurements = Measure(
            cases: cases,
            parallelism: parallelism
        );
        var outcomes = new ProbeOutcome[measurements.Length];

        for (var index = 0; (index < measurements.Length); ++index) {
            outcomes[index] = (measurements[index].Outcome ?? throw new InvalidOperationException(message: $"{cases[index].RelativePath}[{cases[index].ModelKey}] {measurements[index].Error}"));
        }

        return outcomes;
    }

    // A suite with a large corpus (gambatte, SameSuite) can produce thousands of problem lines; a report is read by a
    // person, so it names a bounded sample rather than dumping every one.
    private const int MaxJoinedLines = 20;
}
