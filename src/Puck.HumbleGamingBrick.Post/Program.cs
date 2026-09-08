using Puck.HumbleGamingBrick.Post;

// Puck.HumbleGamingBrick.Post — the HumbleGamingBrick machine's power-on self-test and the primary way the
// machine is validated. It runs an ordered battery of self-checking stages and exits 0 (all passed), 1 (a check failed),
// or 2 (a stage could not run, or an accept was refused). There is no rich CLI: hand-parsed knobs for where artifacts
// land, an optional tier/name/lane subset for iterating, the corpus roots and commercial cartridges, and the ledger
// controls (--accept,
// --accept-regressions, --accept-shrink, --accept-candidate, --require-assets). Every run writes its candidate ledger
// beside its report; accepting it is a file copy under the refusal rules, never a second run.

if (!CommandLineArguments.TryValidateValues(args: args, names: ["--corpus-cache"], error: out var optionError)) {
    Console.Error.WriteLine(value: optionError);
    return 2;
}
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
var lane = (CommandLineArguments.Value(
    args: args,
    name: "--lane"
)?.ToLowerInvariant() switch {
    null or "all" => PostLane.All,
    "gate" => PostLane.Gate,
    "frontier" => PostLane.Frontier,
    var other => throw new ArgumentException(message: $"--lane must be gate, frontier, or all; got '{other}'."),
});
var parallelism = int.Parse(
    provider: System.Globalization.CultureInfo.InvariantCulture,
    s: (CommandLineArguments.Value(
        args: args,
        name: "--parallelism"
    ) ?? "0")
);
var corpora = CorpusManifest.Load(path: CorpusManifest.InRepository(projectName: "Puck.HumbleGamingBrick.Post"), cacheRoot: CommandLineArguments.Value(args: args, name: "--corpus-cache"));

// --fetch-corpora fills the local cache from the manifest's pinned archives and exits; a build agent runs it once per
// cache key, a developer once per version bump.
if (args.Contains(
    comparer: StringComparer.OrdinalIgnoreCase,
    value: "--fetch-corpora"
)) {
    Console.Out.WriteLine(value: $"{corpora.Fetch()} corpus archive(s) fetched into {corpora.EffectiveCacheRoot}");

    return 0;
}

var testRomRoot = corpora.Resolve(
    args: args,
    flag: "--roms",
    name: "game-boy-test-roms"
);
var sstRoot = corpora.Resolve(
    args: args,
    flag: "--sst",
    name: "sm83-sst"
);
// The two commercial cartridges are per-machine assets named on the command line; their stages skip without them.
var linkRomPath = ExistingFile(path: CommandLineArguments.Value(
    args: args,
    name: "--link-rom"
));
var tradeRomPath = ExistingFile(path: CommandLineArguments.Value(
    args: args,
    name: "--trade-rom"
));
var requireAssets = args.Contains(
    comparer: StringComparer.OrdinalIgnoreCase,
    value: "--require-assets"
);
var accept = args.Contains(
    comparer: StringComparer.OrdinalIgnoreCase,
    value: "--accept"
);
var acceptRegressions = args.Contains(
    comparer: StringComparer.OrdinalIgnoreCase,
    value: "--accept-regressions"
);
var acceptShrink = args.Contains(
    comparer: StringComparer.OrdinalIgnoreCase,
    value: "--accept-shrink"
);
var candidatePath = CommandLineArguments.Value(
    args: args,
    name: "--accept-candidate"
);
var ledgerPath = ExpectationsLedger.ResolvePath();
var existing = ExpectationsLedger.Load(path: ledgerPath);

// --accept-candidate takes a candidate a previous run (this machine's or a build agent's) wrote, and applies the same
// refusal rules an in-run --accept does, with no battery run at all.
if (candidatePath is not null) {
    var loaded = ExpectationsLedger.Load(path: candidatePath);
    var candidateDelta = LedgerAcceptance.Compare(
        candidate: loaded,
        existing: existing
    );

    candidateDelta.Print();

    return Accept(
        candidate: loaded,
        delta: candidateDelta,
        blockers: []
    );
}

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
    lane: lane,
    ledger: existing,
    linkRomPath: linkRomPath,
    parallelism: parallelism,
    requireAssets: requireAssets,
    sstRoot: sstRoot,
    testRomRoot: testRomRoot,
    tradeRomPath: tradeRomPath
);
var report = new PostBattery<PostContext>(
    banner: "Puck.HumbleGamingBrick.Post - HumbleGamingBrick machine power-on self-test",
    stages: stages
).Run(context: context);

report.Write(artifactsDirectory: artifactsDirectory);

var candidate = LedgerAcceptance.BuildCandidate(
    existing: existing,
    measurements: context.Measurements
);
var delta = LedgerAcceptance.Compare(
    candidate: candidate,
    existing: existing
);
var candidateFile = Path.Combine(
    path1: artifactsDirectory,
    path2: "Expectations.candidate.json"
);

ExpectationsLedger.Save(
    entries: candidate.Values,
    path: candidateFile
);
delta.Print();
Console.Out.WriteLine(value: (delta.IsEmpty
    ? $"Candidate ledger matches {ledgerPath} ({candidate.Count} rows); written to {candidateFile}"
    : $"Candidate ledger written to {candidateFile}: {delta.Ratcheted.Count} ratcheted, {delta.Regressed.Count} regressed, {delta.Dropped.Count} dropped, {delta.Added.Count} added"));

if (!accept) {
    return report.ExitCode;
}

var blockers = new List<string>();
var infraStages = report.Results
    .Where(predicate: static result => (result.Outcome.Verdict == PostVerdict.Infra))
    .Select(selector: static result => result.Name)
    .ToArray();

if (infraStages.Length > 0) {
    blockers.Add(item: $"{infraStages.Length} stage(s) ended in infrastructure failure ({string.Join(separator: ", ", values: infraStages)})");
}

var erroredCases = report.Results
    .Where(predicate: static result => (result.Outcome.Cases is not null))
    .Sum(selector: static result => result.Outcome.Cases!.Count(predicate: static item => (item.Verdict == PostCaseVerdict.Error)));

if (erroredCases > 0) {
    blockers.Add(item: $"{erroredCases} case(s) could not be measured");
}

var acceptExit = Accept(
    candidate: candidate,
    delta: delta,
    blockers: blockers
);

return ((acceptExit == 0)
    ? 0
    : acceptExit);

static string? ExistingFile(string? path) =>
    (((path is not null) && File.Exists(path: path))
        ? path
        : null);

int Accept(IReadOnlyDictionary<(string Suite, string Path, string Model), LedgerEntry> candidate, LedgerAcceptance.Delta delta, IReadOnlyList<string> blockers) {
    var refusals = LedgerAcceptance.Refusals(
        acceptRegressions: acceptRegressions,
        acceptShrink: acceptShrink,
        blockers: blockers,
        delta: delta
    );

    if (refusals.Count > 0) {
        Console.Error.WriteLine(value: $"accept refused: {string.Join(separator: "; ", values: refusals)}.");

        return 2;
    }

    ExpectationsLedger.Save(
        entries: candidate.Values,
        path: ledgerPath
    );
    Console.Out.WriteLine(value: $"Accepted {candidate.Count} ledger rows into {ledgerPath}");

    return 0;
}
