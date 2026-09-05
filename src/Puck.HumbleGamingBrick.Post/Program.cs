using Puck.HumbleGamingBrick.Post;

// Puck.HumbleGamingBrick.Post — the HumbleGamingBrick machine's power-on self-test and the primary way the
// machine is validated. It runs an ordered battery of self-checking stages and exits 0 (all passed), 1 (a check failed),
// or 2 (a stage could not run). There is no rich CLI: hand-parsed knobs for where artifacts land, an optional
// tier/name subset for iterating, and the ledger controls (--record, --require-assets, --record-accept-regressions,
// --record-allow-shrink). Tier A runs anywhere on a synthetic ROM; Tier B needs the reference corpus, found via the
// PUCK_GB_TESTROMS environment variable and skipped when absent; Tier C (the cross-machine serial link) is
// self-contained like Tier A and runs anywhere.

if (Diagnostics.TryRun(
    args: args,
    exitCode: out var diagnosticExitCode
)) {
    return diagnosticExitCode;
}
var artifactsDirectory = (CommandLineArguments.Value(
    args: args,
    name: "--artifacts"
) ?? Path.Combine(
    path1: "artifacts",
    path2: "gb-post"
));
var tierFilter = CommandLineArguments.Value(
    args: args,
    name: "--tier"
);
var nameFilter = CommandLineArguments.Value(
    args: args,
    name: "--filter"
);
// The reference-ROM corpus root: --roms wins, else PUCK_GB_TESTROMS, else the known corpus location on the development
// machine (so the POST finds it without configuration); Tier-B stages skip when it is absent.
var testRomRoot = CommandLineArguments.ResolveDirectoryRoot(
    args: args,
    fallback: @"D:\Source\ByteTerrace\Temp\GBC Test Suites",
    flag: "--roms",
    variable: "PUCK_GB_TESTROMS"
);
// The SingleStepTests/sm83 vector corpus root: --sst wins, else PUCK_GB_SST, else the known development-machine
// location (the established corpus-clone location pattern); the sst stage skips when it is absent.
var sstRoot = CommandLineArguments.ResolveDirectoryRoot(
    args: args,
    fallback: @"D:\Source\ByteTerrace\Temp\sm83-sst",
    flag: "--sst",
    variable: "PUCK_GB_SST"
);
// --record regenerates Expectations.json from measured outcomes instead of gating against it; --require-assets turns
// a ledger-recorded ROM that is absent from the resolved corpus into an infrastructure failure instead of a skip.
// --record-accept-regressions and --record-allow-shrink acknowledge the two dangerous shapes a recording pass can
// produce (see the write gate below) — omitting them is the safe default, not an oversight to work around.
var recordMode = args.Contains(
    comparer: StringComparer.OrdinalIgnoreCase,
    value: "--record"
);
var requireAssets = args.Contains(
    comparer: StringComparer.OrdinalIgnoreCase,
    value: "--require-assets"
);
var recordAcceptRegressions = args.Contains(
    comparer: StringComparer.OrdinalIgnoreCase,
    value: "--record-accept-regressions"
);
var recordAllowShrink = args.Contains(
    comparer: StringComparer.OrdinalIgnoreCase,
    value: "--record-allow-shrink"
);
var ledgerPath = ExpectationsLedger.ResolvePath();
var stages = PostStages.Create()
    .Where(predicate: stage => PostStageFilters.TierMatches(
    stage: stage,
    tierFilter: tierFilter
))
    .Where(predicate: stage => PostStageFilters.NameMatches(
    nameFilter: nameFilter,
    stage: stage
))
    .ToArray();
var context = new PostContext(
    artifactsDirectory: artifactsDirectory,
    ledger: (recordMode
        ? null
        : ExpectationsLedger.Load(path: ledgerPath)),
    recordMode: recordMode,
    requireAssets: requireAssets,
    sstRoot: sstRoot,
    testRomRoot: testRomRoot
);
var report = new PostBattery<PostContext>(
    banner: "Puck.HumbleGamingBrick.Post - HumbleGamingBrick machine power-on self-test",
    stages: stages
).Run(context: context);
report.Write(artifactsDirectory: artifactsDirectory);

if (recordMode) {
    var infraStages = report.Results
        .Where(predicate: static result => (result.Outcome.Verdict == PostVerdict.Infra))
        .Select(selector: static result => result.Name)
        .ToArray();

    if (infraStages.Length > 0) {
        Console.Error.WriteLine(value: $"--record refused: {infraStages.Length} stage(s) ended in infrastructure failure ({string.Join(separator: ", ", values: infraStages)}); a ledger built from an incomplete run is worse than no ledger at all.");

        return 2;
    }

    var recorded = context.RecordedEntries;
    var duplicateGroup = recorded
        .GroupBy(keySelector: static entry => entry.Key)
        .FirstOrDefault(predicate: static group => (group.Count() > 1));

    if (duplicateGroup is not null) {
        Console.Error.WriteLine(value: $"--record refused: {duplicateGroup.Count()} measured entries share suite '{duplicateGroup.Key.Suite}', path '{duplicateGroup.Key.Path}', model '{duplicateGroup.Key.Model}' — two stages are tagging the same case.");

        return 2;
    }

    // A suite this run actually measured is replaced wholesale (an entry the new discovery no longer produces must
    // not survive as a stale, never-checked row); a suite this run did not touch (an unselected --tier/--filter, or —
    // legitimately — a suite this corpus checkout does not carry) keeps its existing rows untouched and out of the
    // diff below.
    var isFiltered = ((tierFilter is not null) || (nameFilter is not null));
    var measuredSuites = new HashSet<string>(
        collection: recorded.Select(selector: static entry => entry.Suite),
        comparer: StringComparer.Ordinal
    );
    var existing = ExpectationsLedger.Load(path: ledgerPath);
    var merged = existing
        .Where(predicate: pair => !measuredSuites.Contains(item: pair.Value.Suite))
        .ToDictionary(
        elementSelector: static pair => pair.Value,
        keySelector: static pair => pair.Key
    );

    foreach (var entry in recorded) {
        merged[entry.Key] = entry;
    }

    // A suite this run never measured is carried into merged unchanged (same key, same entry), so diffing every
    // existing row against merged only ever surfaces a real difference for a suite this run actually touched.
    // Fail -> Pass is the one direction a recording pass is trusted to apply on its own: it is always a deliberate
    // fix landing in the same change. Every other change of outcome — including a recorded Fail losing its signature
    // entirely into Inconclusive, which the corroboration/liveness gates above can produce on a case whose old
    // register-dump/pixel/audio verdict was never actually corroborated — is a regression: a resolved verdict either
    // moved to a different resolved verdict, or was lost to "we no longer know," and both need the same
    // acknowledgment a Pass regressing does.
    var regressed = new List<string>();
    var ratcheted = new List<string>();
    var dropped = new List<string>();

    foreach (var old in existing.Values) {
        if (!merged.TryGetValue(
            key: old.Key,
            value: out var current
        )) {
            dropped.Add(item: $"{old.Suite}/{old.Path}[{old.Model}] recorded {old.Outcome}, no longer discovered");

            continue;
        }

        if (old.Outcome == current.Outcome) {
            continue;
        }

        if (
            (old.Outcome == LedgerOutcome.Fail) &&
            (current.Outcome == LedgerOutcome.Pass)
        ) {
            ratcheted.Add(item: $"{old.Suite}/{old.Path}[{old.Model}] recorded fail -> now pass");
        } else {
            regressed.Add(item: $"{old.Suite}/{old.Path}[{old.Model}] recorded {old.Outcome} -> now {current.Outcome} ({current.Reason})");
        }
    }

    foreach (var line in ratcheted) {
        Console.Out.WriteLine(value: $"ratchet: {line}");
    }

    foreach (var line in regressed) {
        Console.Out.WriteLine(value: $"regression: {line}");
    }

    foreach (var line in dropped) {
        Console.Out.WriteLine(value: $"dropped: {line}");
    }

    var refusals = new List<string>();

    if (
        (regressed.Count > 0) &&
        !recordAcceptRegressions
    ) {
        refusals.Add(item: $"{regressed.Count} case(s) regressed from a recorded verdict (pass --record-accept-regressions to acknowledge)");
    }

    if (
        (dropped.Count > 0) &&
        !recordAllowShrink
    ) {
        refusals.Add(item: $"{dropped.Count} recorded case(s) are no longer discovered (pass --record-allow-shrink to acknowledge)");
    }

    if (refusals.Count > 0) {
        Console.Error.WriteLine(value: $"--record refused: {string.Join(separator: "; ", values: refusals)}.");

        return 2;
    }

    var written = merged.Values.ToArray();

    ExpectationsLedger.Save(
        entries: written,
        path: ledgerPath
    );
    Console.Out.WriteLine(value: (isFiltered
        ? $"Merged {recorded.Count} measured ledger entries into the {written.Length} in {ledgerPath}; the run was filtered, so unselected suites were kept"
        : $"Recorded {written.Length} ledger entries to {ledgerPath}"));
}

return report.ExitCode;
