using Puck.HumbleGamingBrick.Post;

// Puck.HumbleGamingBrick.Post — the HumbleGamingBrick machine's power-on self-test and the primary way the
// machine is validated. It runs an ordered battery of self-checking stages and exits 0 (all passed), 1 (a check failed),
// or 2 (a stage could not run). There is no rich CLI: hand-parsed knobs for where artifacts land, an optional
// tier/name subset for iterating, and the ledger controls (--record, --require-assets). Tier A runs anywhere on a
// synthetic ROM; Tier B needs the reference corpus, found via the PUCK_GB_TESTROMS environment variable and skipped
// when absent; Tier C (the cross-machine serial link) is self-contained like Tier A and runs anywhere.

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
var recordMode = args.Contains(
    comparer: StringComparer.OrdinalIgnoreCase,
    value: "--record"
);
var requireAssets = args.Contains(
    comparer: StringComparer.OrdinalIgnoreCase,
    value: "--require-assets"
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
    // A recording run rewrites the ledger from what it measured, so a run that only selected part of the battery
    // must merge over the entries already on file — replacing wholesale would delete every suite the filter did not
    // select, silently discarding the ratchet for the rest of the corpus. An unfiltered run replaces wholesale, which
    // is what lets it drop entries for ROMs the corpus no longer carries.
    var isFiltered = ((tierFilter is not null) || (nameFilter is not null));
    var recorded = context.RecordedEntries;
    var entries = ((IEnumerable<LedgerEntry>)recorded);

    if (isFiltered) {
        // A suite this run actually measured is replaced wholesale (an entry the new discovery no longer produces
        // must not survive as a stale, never-checked row); a suite this run did not touch keeps its existing rows.
        var measuredSuites = new HashSet<string>(
            collection: recorded.Select(selector: static entry => entry.Suite),
            comparer: StringComparer.Ordinal
        );
        var merged = ExpectationsLedger
            .Load(path: ledgerPath)
            .Where(predicate: pair => !measuredSuites.Contains(item: pair.Value.Suite))
            .ToDictionary(
            elementSelector: static pair => pair.Value,
            keySelector: static pair => pair.Key
        );

        foreach (var entry in recorded) {
            merged[entry.Key] = entry;
        }

        entries = merged.Values;
    }

    var written = entries.ToArray();

    ExpectationsLedger.Save(
        entries: written,
        path: ledgerPath
    );
    Console.Out.WriteLine(value: (isFiltered
        ? $"Merged {recorded.Count} measured ledger entries into the {written.Length} in {ledgerPath}; the run was filtered, so unselected suites were kept"
        : $"Recorded {written.Length} ledger entries to {ledgerPath}"));
}

return report.ExitCode;
