using System.Collections.Concurrent;
using System.Diagnostics;
using Puck.Assets;

namespace Puck.HumbleGamingBrick.Post;

/// <summary>
/// Measures one suite's discovered <see cref="LedgerCase"/>s and compares each to <c>Expectations.json</c> — the
/// mechanical gate every ledger stage shares. The gate requires the recorded and actual verdicts to be equal for
/// every case: a recorded pass that now fails or turns inconclusive, a recorded fail that now passes or turns
/// inconclusive, and a recorded inconclusive that now resolves either way, are all reported (progress is a deliberate,
/// accepted act, never a silent gate loosening); a still-recorded-fail whose screenshot differing-pixel count changed
/// fails naming both counts; a case present on disk with no ledger entry fails as unrecorded; a ROM or expected-image
/// whose bytes no longer match its recorded hash fails as a hash mismatch; a ledger row for the suite with no matching
/// discovered case is skipped, or — under <see cref="PostContext.RequireAssets"/> — an infrastructure failure. Every
/// measured row also lands in <see cref="PostContext.Measurements"/>, from which the run's candidate ledger is built,
/// so a run never has to be repeated to record what it saw.
/// </summary>
internal static class LedgerEvaluator {
    private sealed record CaseMeasurement(LedgerEntry? Entry, ProbeOutcome? Outcome, string? Error, TimeSpan Duration);

    /// <summary>Evaluates a suite's cases.</summary>
    /// <param name="context">The shared run context.</param>
    /// <param name="cases">The suite's discovered cases.</param>
    /// <param name="suites">Every ledger <see cref="LedgerEntry.Suite"/> key this stage is responsible for — used to
    /// find a recorded row with no matching discovered case, independent of how many cases were actually found.</param>
    /// <returns>The stage outcome, carrying one row per measured case.</returns>
    public static PostStageOutcome Evaluate(PostContext context, IReadOnlyList<LedgerCase> cases, IReadOnlyList<string> suites) {
        var discoveredKeys = new HashSet<(string Suite, string Path, string Model)>(
            collection: cases.Select(selector: static ledgerCase => ledgerCase.Key)
        );

        context.Measurements.NoteDiscovery(
            keys: discoveredKeys,
            suites: suites
        );

        var missingFromDisk = context.Ledger.Values
            .Where(predicate: entry => (suites.Contains(value: entry.Suite) && !discoveredKeys.Contains(item: entry.Key)))
            .OrderBy(
            keySelector: static entry => entry.Path,
            comparer: StringComparer.Ordinal
        )
            .ThenBy(
            keySelector: static entry => entry.Model,
            comparer: StringComparer.Ordinal
        )
            .ToArray();

        // The corpus is external and never committed, so a recorded ROM this machine does not have on disk is the
        // expected shape of an incomplete checkout, not a defect — skipped rather than failed unless the caller opted
        // into treating that as infrastructure trouble.
        if (
            (missingFromDisk.Length > 0) &&
            context.RequireAssets
        ) {
            return PostStageOutcome.Infra(detail: $"{missingFromDisk.Length} recorded case(s) absent from disk (--require-assets): {JoinCapped(lines: missingFromDisk.Select(selector: static entry => $"{entry.Path}[{entry.Model}]"))}");
        }

        if (cases.Count == 0) {
            return PostStageOutcome.Skip(detail: "no cases discovered under the corpus root (pass --roms)");
        }

        var selected = cases
            .Where(predicate: ledgerCase => InLane(
                context: context,
                ledgerCase: ledgerCase
            ))
            .ToArray();
        var measurements = Measure(
            cases: selected,
            parallelism: context.Parallelism
        );

        context.Measurements.NoteMeasured(entries: measurements
            .Select(selector: static measurement => measurement.Entry)
            .Where(predicate: static entry => (entry is not null))!);

        var results = new List<PostCaseResult>(capacity: selected.Length);
        var pass = 0;
        var expectedFail = 0;
        var unrunnable = 0;
        var problems = new List<string>();
        var stageArtifacts = Path.Combine(
            path1: context.ArtifactsDirectory,
            path2: suites[0]
        );

        for (var index = 0; (index < selected.Length); ++index) {
            var ledgerCase = selected[index];
            var measurement = measurements[index];
            var name = $"{ledgerCase.RelativePath}[{ledgerCase.ModelKey}]";
            var (verdict, detail) = Classify(
                ledgerCase: ledgerCase,
                measurement: measurement,
                recorded: (context.Ledger.TryGetValue(
                    key: ledgerCase.Key,
                    value: out var recorded
                )
                    ? recorded
                    : null)
            );

            switch (verdict) {
                case PostCaseVerdict.Pass:
                    ++pass;

                    break;
                case PostCaseVerdict.ExpectedFail:
                    ++expectedFail;

                    break;
                case PostCaseVerdict.Skip:
                    ++unrunnable;

                    break;
                default:
                    problems.Add(item: $"{name} {detail}");

                    if (measurement.Outcome?.ActualImage is not null) {
                        WriteMismatchImages(
                            directory: stageArtifacts,
                            name: name,
                            outcome: measurement.Outcome
                        );
                    }

                    break;
            }

            results.Add(item: new PostCaseResult(
                Detail: detail,
                Duration: measurement.Duration,
                Name: name,
                Verdict: verdict
            ));
        }

        var laneNote = ((context.Lane == PostLane.All)
            ? string.Empty
            : $" [{context.Lane.ToString().ToLowerInvariant()} lane: {selected.Length} of {cases.Count}]");
        var summary = $"{pass} pass, {expectedFail} recorded-fail, {unrunnable} unrunnable{laneNote}";

        if (missingFromDisk.Length > 0) {
            summary += $", {missingFromDisk.Length} recorded but absent from disk (skipped)";
        }

        return ((problems.Count == 0)
            ? PostStageOutcome.Pass(
                cases: results,
                detail: summary
            )
            : PostStageOutcome.Fail(
                cases: results,
                detail: $"{summary}; {JoinCapped(lines: problems)}"
            ));
    }
    /// <summary>Measures every case and returns its ledger row, in case order.</summary>
    /// <param name="cases">The cases to measure.</param>
    /// <param name="parallelism">How many cases to measure at once.</param>
    /// <returns>One row per case.</returns>
    /// <exception cref="InvalidOperationException">A case could not be measured.</exception>
    public static LedgerEntry[] MeasureEntries(IReadOnlyList<LedgerCase> cases, int parallelism) {
        var measurements = Measure(
            cases: cases,
            parallelism: parallelism
        );
        var entries = new LedgerEntry[measurements.Length];

        for (var index = 0; (index < measurements.Length); ++index) {
            entries[index] = (measurements[index].Entry ?? throw new InvalidOperationException(message: $"{cases[index].RelativePath}[{cases[index].ModelKey}] {measurements[index].Error}"));
        }

        return entries;
    }

    // A suite with a large corpus (gambatte, SameSuite) can produce thousands of problem lines; a report is read by a
    // person, so it names a bounded sample rather than dumping every one.
    private const int MaxJoinedLines = 20;

    private static (PostCaseVerdict Verdict, string Detail) Classify(LedgerCase ledgerCase, CaseMeasurement measurement, LedgerEntry? recorded) {
        if (measurement.Entry is null) {
            return (PostCaseVerdict.Error, (measurement.Error ?? "not measured"));
        }

        var actual = measurement.Entry;
        var actualDetail = (measurement.Outcome?.Detail ?? actual.Reason ?? string.Empty);

        if (recorded is null) {
            return (PostCaseVerdict.Mismatch, $"unrecorded ({actual.Outcome}: {actualDetail}); accept the candidate ledger to record it");
        }

        if (!string.Equals(
            a: actual.RomHash,
            b: recorded.RomHash,
            comparisonType: StringComparison.OrdinalIgnoreCase
        )) {
            return (PostCaseVerdict.Mismatch, $"ROM hash mismatch (recorded {recorded.RomHash}, actual {actual.RomHash})");
        }

        if (
            (ledgerCase.Probe == ProbeKind.Screenshot) &&
            !string.Equals(
                a: actual.ExpectedImageHash,
                b: recorded.ExpectedImageHash,
                comparisonType: StringComparison.OrdinalIgnoreCase
            )
        ) {
            return (PostCaseVerdict.Mismatch, $"expected-image hash mismatch (recorded {(recorded.ExpectedImageHash ?? "<none>")}, actual {(actual.ExpectedImageHash ?? "<none>")})");
        }

        if (actual.Outcome != recorded.Outcome) {
            return (PostCaseVerdict.Mismatch, $"{MismatchLabel(
                actual: actual.Outcome,
                recorded: recorded.Outcome
            )}: recorded {recorded.Outcome}, now {actual.Outcome} ({actualDetail})");
        }

        switch (actual.Outcome) {
            case LedgerOutcome.Pass:
                return (PostCaseVerdict.Pass, actualDetail);
            case LedgerOutcome.Unrunnable:
                return (PostCaseVerdict.Skip, (actual.Reason ?? "unrunnable"));
            case LedgerOutcome.Fail when (
                (ledgerCase.Probe == ProbeKind.Screenshot) &&
                (recorded.DiffPixels != actual.DiffPixels)
            ):
                return (PostCaseVerdict.Mismatch, $"screenshot diff-pixel count changed: recorded {recorded.DiffPixels}, actual {actual.DiffPixels}");
            default:
                return (PostCaseVerdict.ExpectedFail, $"recorded {actual.Outcome}: {actualDetail}");
        }
    }
    private static string? ImageHash(LedgerCase ledgerCase) {
        var imagePath = ScreenshotProbe.ResolveExpectedImage(ledgerCase: ledgerCase);

        return ((imagePath is null)
            ? null
            : ExpectationsLedger.HashFile(path: imagePath));
    }
    private static bool InLane(PostContext context, LedgerCase ledgerCase) {
        if (
            (context.Lane == PostLane.All) ||
            (ledgerCase.Disposition == CaseDisposition.Unrunnable)
        ) {
            return true;
        }

        if (!context.Ledger.TryGetValue(
            key: ledgerCase.Key,
            value: out var recorded
        )) {
            return (context.Lane == PostLane.Gate);
        }

        return (context.Lane switch {
            PostLane.Gate => (recorded.Outcome is (LedgerOutcome.Pass or LedgerOutcome.Unrunnable)),
            _ => (recorded.Outcome is (LedgerOutcome.Fail or LedgerOutcome.Inconclusive)),
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
            : shown);
    }
    // Cases are independent (each builds its own machine), so they run on every processor at once; the long ones go
    // first so the run does not end on a tail of single-threaded work. A case's own ordering in the report is its
    // discovery order, restored by the index.
    private static CaseMeasurement[] Measure(IReadOnlyList<LedgerCase> cases, int parallelism) {
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
            body: index => measurements[index] = MeasureOne(ledgerCase: cases[index]),
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
    private static CaseMeasurement MeasureOne(LedgerCase ledgerCase) {
        var start = Stopwatch.GetTimestamp();

        try {
            var hash = ExpectationsLedger.HashRom(romPath: ledgerCase.FullPath);
            var imageHash = ((ledgerCase.Probe == ProbeKind.Screenshot)
                ? ImageHash(ledgerCase: ledgerCase)
                : null);

            if (ledgerCase.Disposition == CaseDisposition.Unrunnable) {
                return new CaseMeasurement(
                    Duration: Stopwatch.GetElapsedTime(startingTimestamp: start),
                    Entry: new LedgerEntry(
                        DiffPixels: null,
                        ExpectedImageHash: imageHash,
                        Model: ledgerCase.ModelKey,
                        Outcome: LedgerOutcome.Unrunnable,
                        Path: ledgerCase.RelativePath,
                        Probe: ExpectationsLedger.ProbeName(probe: ledgerCase.Probe),
                        Reason: ledgerCase.UnrunnableReason,
                        RomHash: hash,
                        Suite: ledgerCase.Suite
                    ),
                    Error: null,
                    Outcome: null
                );
            }

            var outcome = ProbeRunner.Run(
                budget: CaseBudget.ForFrames(frames: ledgerCase.FrameCap),
                ledgerCase: ledgerCase
            );
            var recordedOutcome = ToLedgerOutcome(verdict: outcome.Verdict);

            return new CaseMeasurement(
                Duration: Stopwatch.GetElapsedTime(startingTimestamp: start),
                Entry: new LedgerEntry(
                    DiffPixels: outcome.DiffPixelCount,
                    ExpectedImageHash: imageHash,
                    Model: ledgerCase.ModelKey,
                    Outcome: recordedOutcome,
                    Path: ledgerCase.RelativePath,
                    Probe: ExpectationsLedger.ProbeName(probe: ledgerCase.Probe),
                    Reason: ((recordedOutcome == LedgerOutcome.Pass)
                        ? null
                        : outcome.Detail),
                    RomHash: hash,
                    Suite: ledgerCase.Suite
                ),
                Error: null,
                Outcome: outcome
            );
        } catch (CaseBudgetExceededException exception) {
            return new CaseMeasurement(
                Duration: Stopwatch.GetElapsedTime(startingTimestamp: start),
                Entry: null,
                Error: exception.Message,
                Outcome: null
            );
        } catch (Exception exception) when (exception is not OutOfMemoryException) {
            return new CaseMeasurement(
                Duration: Stopwatch.GetElapsedTime(startingTimestamp: start),
                Entry: null,
                Error: $"threw {exception.GetType().Name}: {exception.Message}",
                Outcome: null
            );
        }
    }
    // Names the transition a mismatch represents: a recorded pass regressing is the dangerous direction, while a
    // recorded fail resolving to a pass is a ratchet that needs a deliberate accept either way — both, and every other
    // transition, are surfaced as a gate failure, never accepted quietly.
    private static string MismatchLabel(LedgerOutcome recorded, LedgerOutcome actual) =>
        (recorded switch {
            LedgerOutcome.Pass => "regression",
            LedgerOutcome.Fail when (actual == LedgerOutcome.Pass) => "ratchet: now passes; accept the candidate ledger",
            _ => "ledger disagrees",
        });
    private static LedgerOutcome ToLedgerOutcome(ProbeVerdict verdict) =>
        verdict switch {
            ProbeVerdict.Pass => LedgerOutcome.Pass,
            ProbeVerdict.Fail => LedgerOutcome.Fail,
            ProbeVerdict.Inconclusive => LedgerOutcome.Inconclusive,
            _ => throw new NotSupportedException(message: $"Unhandled probe verdict '{verdict}'."),
        };
    // A mismatching screenshot leaves the frame it produced and a mask of the differing pixels beside the report, so
    // a red run is diagnosable from its artifacts.
    private static void WriteMismatchImages(string directory, string name, ProbeOutcome outcome) {
        var actual = outcome.ActualImage!;
        var stem = Path.Combine(
            path1: directory,
            path2: name
                .Replace(
                oldChar: '/',
                newChar: '_'
            )
                .Replace(
                oldChar: '\\',
                newChar: '_'
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

            mask[offset] = (same ? actual.Rgba[offset] : ((byte)0xFF));
            mask[(offset + 1)] = (same ? actual.Rgba[(offset + 1)] : ((byte)0x00));
            mask[(offset + 2)] = (same ? actual.Rgba[(offset + 2)] : ((byte)0x00));
            mask[(offset + 3)] = 0xFF;
        }

        PngEncoder.Write(
            height: actual.Height,
            path: (stem + ".diff.png"),
            rgba: mask,
            width: actual.Width
        );
    }
}
