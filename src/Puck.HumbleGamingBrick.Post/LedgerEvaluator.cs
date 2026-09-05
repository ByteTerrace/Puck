namespace Puck.HumbleGamingBrick.Post;

/// <summary>
/// Evaluates one suite's discovered <see cref="LedgerCase"/>s against <c>Expectations.json</c> — the mechanical gate
/// every ledger stage shares. Ratchet semantics: a recorded pass that now fails, or a recorded fail that now passes,
/// both fail the stage (progress is a deliberate, recorded act, not a silent gate loosening); a recorded fail whose
/// screenshot differing-pixel count changed fails naming both counts; a case present on disk with no ledger entry
/// fails as unrecorded; a ROM whose bytes no longer match its recorded hash fails as a hash mismatch; a recorded case
/// absent from disk is skipped, or — under <c>--require-assets</c> — an infrastructure failure. Under
/// <see cref="PostContext.RecordMode"/> the stage instead measures every case and appends its outcome to
/// <see cref="PostContext.RecordedEntries"/> without comparing to the existing ledger.
/// </summary>
internal static class LedgerEvaluator {
    /// <summary>Evaluates a suite's cases.</summary>
    /// <param name="context">The shared run context (ledger, record mode, require-assets).</param>
    /// <param name="cases">The suite's discovered cases.</param>
    /// <returns>The stage outcome.</returns>
    public static PostStageOutcome Evaluate(PostContext context, IReadOnlyList<LedgerCase> cases) {
        if (cases.Count == 0) {
            return PostStageOutcome.Skip(detail: "no cases discovered under the on-disk corpus root (set PUCK_GB_TESTROMS)");
        }

        if (context.RecordMode) {
            foreach (var ledgerCase in cases) {
                context.RecordedEntries.Add(item: Measure(ledgerCase: ledgerCase));
            }

            return PostStageOutcome.Pass(detail: $"recorded {cases.Count} case(s)");
        }

        var pass = 0;
        var recordedFail = 0;
        var unrunnable = 0;
        var infra = false;
        var problems = new List<string>();

        foreach (var ledgerCase in cases) {
            var modelKey = ModelKey(model: ledgerCase.Model);
            var key = (ledgerCase.Suite, ledgerCase.RelativePath, modelKey);

            if (!File.Exists(path: ledgerCase.FullPath)) {
                if (
                    context.Ledger.ContainsKey(key: key) &&
                    context.RequireAssets
                ) {
                    infra = true;
                    problems.Add(item: $"{ledgerCase.RelativePath}[{modelKey}] recorded but absent from disk (--require-assets)");
                }

                continue;
            }

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

            if (entry.Outcome is not (LedgerOutcome.Pass or LedgerOutcome.Fail)) {
                problems.Add(item: $"{ledgerCase.RelativePath}[{modelKey}] ledger outcome '{entry.Outcome}' is not valid for a runnable case");
                continue;
            }

            var outcome = ProbeRunner.Run(ledgerCase: ledgerCase);
            var actualPassed = (outcome.Verdict == ProbeVerdict.Pass);

            if (entry.Outcome == LedgerOutcome.Pass) {
                if (actualPassed) {
                    ++pass;
                } else {
                    problems.Add(item: $"{ledgerCase.RelativePath}[{modelKey}] regression: recorded pass, now {outcome.Verdict} ({outcome.Detail})");
                }

                continue;
            }

            if (actualPassed) {
                problems.Add(item: $"{ledgerCase.RelativePath}[{modelKey}] ratchet: now passes; re-record");
                continue;
            }

            if (
                (ledgerCase.Probe == ProbeKind.Screenshot) &&
                (entry.DiffPixels != outcome.DiffPixelCount)
            ) {
                problems.Add(item: $"{ledgerCase.RelativePath}[{modelKey}] screenshot diff-pixel count changed: recorded {entry.DiffPixels}, actual {outcome.DiffPixelCount}");
                continue;
            }

            ++recordedFail;
        }

        var summary = $"{pass} pass, {recordedFail} recorded-fail, {unrunnable} unrunnable";

        if (infra) {
            return PostStageOutcome.Infra(detail: string.Join(
                separator: "; ",
                values: problems
            ));
        }

        return ((problems.Count == 0)
            ? PostStageOutcome.Pass(detail: summary)
            : PostStageOutcome.Fail(detail: $"{summary}; {string.Join(
            separator: "; ",
            values: problems
        )}"));
    }

    private static string ModelKey(ConsoleModel model) =>
        model.ToString();
    private static LedgerEntry Measure(LedgerCase ledgerCase) {
        var modelKey = ModelKey(model: ledgerCase.Model);
        var hash = (File.Exists(path: ledgerCase.FullPath)
            ? ExpectationsLedger.HashRom(romPath: ledgerCase.FullPath)
            : string.Empty);

        if (ledgerCase.Disposition == CaseDisposition.Unrunnable) {
            return new LedgerEntry(
                DiffPixels: null,
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
        var passed = (outcome.Verdict == ProbeVerdict.Pass);

        return new LedgerEntry(
            DiffPixels: outcome.DiffPixelCount,
            Model: modelKey,
            Outcome: (passed
                ? LedgerOutcome.Pass
                : LedgerOutcome.Fail),
            Path: ledgerCase.RelativePath,
            Probe: ExpectationsLedger.ProbeName(probe: ledgerCase.Probe),
            Reason: (passed
                ? null
                : outcome.Detail),
            RomHash: hash,
            Suite: ledgerCase.Suite
        );
    }
}
