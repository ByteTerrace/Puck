namespace Puck.HumbleGamingBrick.Post;

/// <summary>
/// Evaluates one suite's discovered <see cref="LedgerCase"/>s against <c>Expectations.json</c> — the mechanical gate
/// every ledger stage shares. The gate requires the recorded and actual verdicts to be equal for every case: a
/// recorded pass that now fails or turns inconclusive, a recorded fail that now passes or turns inconclusive, and a
/// recorded inconclusive that now resolves either way, are all reported (progress is a deliberate, recorded act, never
/// a silent gate loosening); a still-recorded-fail whose screenshot differing-pixel count changed fails naming both
/// counts; a case present on disk with no ledger entry fails as unrecorded; a ROM or expected-image whose bytes no
/// longer match its recorded hash fails as a hash mismatch; a ledger row for the suite with no matching discovered
/// case is skipped, or — under <see cref="PostContext.RequireAssets"/> — an infrastructure failure. Under
/// <see cref="PostContext.RecordMode"/> the stage instead measures every case and appends its outcome to
/// <see cref="PostContext.RecordedEntries"/> without comparing to the existing ledger.
/// </summary>
internal static class LedgerEvaluator {
    /// <summary>Evaluates a suite's cases.</summary>
    /// <param name="context">The shared run context (ledger, record mode, require-assets).</param>
    /// <param name="cases">The suite's discovered cases.</param>
    /// <param name="suites">Every ledger <see cref="LedgerEntry.Suite"/> key this stage is responsible for — used to
    /// find a recorded row with no matching discovered case, independent of how many cases were actually found.</param>
    /// <returns>The stage outcome.</returns>
    public static PostStageOutcome Evaluate(PostContext context, IReadOnlyList<LedgerCase> cases, IReadOnlyList<string> suites) {
        if (context.RecordMode) {
            if (cases.Count == 0) {
                return PostStageOutcome.Skip(detail: "no cases discovered under the on-disk corpus root (set PUCK_GB_TESTROMS)");
            }

            foreach (var ledgerCase in cases) {
                context.RecordedEntries.Add(item: Measure(ledgerCase: ledgerCase));
            }

            return PostStageOutcome.Pass(detail: $"recorded {cases.Count} case(s)");
        }

        var discoveredKeys = new HashSet<(string Suite, string Path, string Model)>(
            collection: cases.Select(selector: static ledgerCase => (ledgerCase.Suite, ledgerCase.RelativePath, ModelKey(model: ledgerCase.Model)))
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
            return PostStageOutcome.Skip(detail: "no cases discovered under the on-disk corpus root (set PUCK_GB_TESTROMS)");
        }

        var pass = 0;
        var recordedFail = 0;
        var recordedInconclusive = 0;
        var unrunnable = 0;
        var problems = new List<string>();

        foreach (var ledgerCase in cases) {
            var modelKey = ModelKey(model: ledgerCase.Model);
            var key = (ledgerCase.Suite, ledgerCase.RelativePath, modelKey);

            if (!context.Ledger.TryGetValue(
                key: key,
                value: out var entry
            )) {
                problems.Add(item: $"{ledgerCase.RelativePath}[{modelKey}] unrecorded (run --record)");
                continue;
            }

            var actualHash = ExpectationsLedger.HashRom(romPath: ledgerCase.FullPath);

            if (!string.Equals(
                a: actualHash,
                b: entry.RomHash,
                comparisonType: StringComparison.OrdinalIgnoreCase
            )) {
                problems.Add(item: $"{ledgerCase.RelativePath}[{modelKey}] ROM hash mismatch (recorded {entry.RomHash}, actual {actualHash})");
                continue;
            }

            switch (ledgerCase.Disposition) {
                case CaseDisposition.Unrunnable:
                    if (entry.Outcome == LedgerOutcome.Unrunnable) {
                        ++unrunnable;
                    } else {
                        problems.Add(item: $"{ledgerCase.RelativePath}[{modelKey}] ledger disagrees: recorded '{entry.Outcome}', case is unrunnable");
                    }

                    continue;
            }

            if (entry.Outcome is not (LedgerOutcome.Pass or LedgerOutcome.Fail or LedgerOutcome.Inconclusive)) {
                problems.Add(item: $"{ledgerCase.RelativePath}[{modelKey}] ledger outcome '{entry.Outcome}' is not valid for a runnable case");
                continue;
            }

            if (ledgerCase.Probe == ProbeKind.Screenshot) {
                var imagePath = ScreenshotProbe.ResolveExpectedImage(ledgerCase: ledgerCase);
                var actualImageHash = ((imagePath is null)
                    ? null
                    : ExpectationsLedger.HashFile(path: imagePath));

                if (!string.Equals(
                    a: actualImageHash,
                    b: entry.ExpectedImageHash,
                    comparisonType: StringComparison.OrdinalIgnoreCase
                )) {
                    problems.Add(item: $"{ledgerCase.RelativePath}[{modelKey}] expected-image hash mismatch (recorded {(entry.ExpectedImageHash ?? "<none>")}, actual {(actualImageHash ?? "<none>")})");
                    continue;
                }
            }

            var outcome = ProbeRunner.Run(ledgerCase: ledgerCase);
            var actualOutcome = ToLedgerOutcome(verdict: outcome.Verdict);

            if (entry.Outcome != actualOutcome) {
                problems.Add(item: $"{ledgerCase.RelativePath}[{modelKey}] {MismatchLabel(
                    actual: actualOutcome,
                    recorded: entry.Outcome
                )}: recorded {entry.Outcome}, now {actualOutcome} ({outcome.Detail})");
                continue;
            }

            switch (actualOutcome) {
                case LedgerOutcome.Pass:
                    ++pass;

                    break;
                case LedgerOutcome.Inconclusive:
                    ++recordedInconclusive;

                    break;
                default:
                    if (
                        (ledgerCase.Probe == ProbeKind.Screenshot) &&
                        (entry.DiffPixels != outcome.DiffPixelCount)
                    ) {
                        problems.Add(item: $"{ledgerCase.RelativePath}[{modelKey}] screenshot diff-pixel count changed: recorded {entry.DiffPixels}, actual {outcome.DiffPixelCount}");
                        continue;
                    }

                    ++recordedFail;

                    break;
            }
        }

        var summary = $"{pass} pass, {recordedFail} recorded-fail, {recordedInconclusive} recorded-inconclusive, {unrunnable} unrunnable";

        if (
            (missingFromDisk.Length > 0) &&
            !context.RequireAssets
        ) {
            summary += $", {missingFromDisk.Length} recorded but absent from disk (skipped)";
        }

        return ((problems.Count == 0)
            ? PostStageOutcome.Pass(detail: summary)
            : PostStageOutcome.Fail(detail: $"{summary}; {JoinCapped(lines: problems)}"));
    }

    // A suite with a large corpus (gambatte, SameSuite) can produce thousands of problem lines; a report is read by a
    // person, so it names a bounded sample rather than dumping every one.
    private const int MaxJoinedLines = 20;

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

    // Names the transition a mismatch represents: a recorded pass regressing is the dangerous direction (silently
    // blessed by a careless --record), while a recorded fail resolving to a pass is a ratchet that needs a deliberate
    // re-record either way — both, and every other transition, are surfaced as a gate failure, never accepted quietly.
    private static string MismatchLabel(LedgerOutcome recorded, LedgerOutcome actual) =>
        (recorded switch {
            LedgerOutcome.Pass => "regression",
            LedgerOutcome.Fail when (actual == LedgerOutcome.Pass) => "ratchet: now passes; re-record",
            _ => "ledger disagrees",
        });
    private static LedgerOutcome ToLedgerOutcome(ProbeVerdict verdict) =>
        verdict switch {
            ProbeVerdict.Pass => LedgerOutcome.Pass,
            ProbeVerdict.Fail => LedgerOutcome.Fail,
            ProbeVerdict.Inconclusive => LedgerOutcome.Inconclusive,
            _ => throw new NotSupportedException(message: $"Unhandled probe verdict '{verdict}'."),
        };
    private static string ModelKey(ConsoleModel model) =>
        model.ToString();
    private static LedgerEntry Measure(LedgerCase ledgerCase) {
        var modelKey = ModelKey(model: ledgerCase.Model);
        // Every discovered case's ROM exists on disk by construction (SuiteCatalog/RomCatalog only ever build a case
        // from a file Directory.EnumerateFiles or File.Exists already found); a hash that cannot be computed here is a
        // discovery-time race, not a normal outcome, so it throws rather than recording an empty placeholder hash a
        // future run could accidentally satisfy.
        var hash = ExpectationsLedger.HashRom(romPath: ledgerCase.FullPath);
        var imageHash = ((ledgerCase.Probe == ProbeKind.Screenshot)
            ? ImageHash(ledgerCase: ledgerCase)
            : null);

        if (ledgerCase.Disposition == CaseDisposition.Unrunnable) {
            return new LedgerEntry(
                DiffPixels: null,
                ExpectedImageHash: imageHash,
                Model: modelKey,
                Outcome: LedgerOutcome.Unrunnable,
                Path: ledgerCase.RelativePath,
                Probe: ExpectationsLedger.ProbeName(probe: ledgerCase.Probe),
                Reason: ledgerCase.UnrunnableReason,
                RomHash: hash,
                Suite: ledgerCase.Suite
            );
        }

        var outcome = ProbeRunner.Run(ledgerCase: ledgerCase);
        var recordedOutcome = ToLedgerOutcome(verdict: outcome.Verdict);

        return new LedgerEntry(
            DiffPixels: outcome.DiffPixelCount,
            ExpectedImageHash: imageHash,
            Model: modelKey,
            Outcome: recordedOutcome,
            Path: ledgerCase.RelativePath,
            Probe: ExpectationsLedger.ProbeName(probe: ledgerCase.Probe),
            Reason: ((recordedOutcome == LedgerOutcome.Pass)
                ? null
                : outcome.Detail),
            RomHash: hash,
            Suite: ledgerCase.Suite
        );
    }
    private static string? ImageHash(LedgerCase ledgerCase) {
        var imagePath = ScreenshotProbe.ResolveExpectedImage(ledgerCase: ledgerCase);

        return ((imagePath is null)
            ? null
            : ExpectationsLedger.HashFile(path: imagePath));
    }
}
