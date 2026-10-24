using Puck.HumbleGamingBrick.Post;

// Puck.HumbleGamingBrick.Post — the HumbleGamingBrick machine's power-on self-test and the primary way the
// machine is validated. It runs an ordered battery of self-checking stages and exits 0 (all passed), 1 (a check
// failed, a ROM that does not pass its suite included), or 2 (a stage could not run). There is no rich CLI:
// hand-parsed knobs for where artifacts land, an optional tier/name subset for iterating, the corpus roots and
// --require-assets. No expectation file is read or written: each corpus ROM's own result
// is the run's result.

if (!CommandLineArguments.TryValidateValues(
    args: args,
    error: out var optionError,
    names: ["--corpus-cache"]
)) {
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
var parallelism = int.Parse(
    provider: System.Globalization.CultureInfo.InvariantCulture,
    s: (CommandLineArguments.Value(
        args: args,
        name: "--parallelism"
    ) ?? "0")
);
var corpora = CorpusManifest.Load(
    path: CorpusManifest.InRepository(projectName: "Puck.HumbleGamingBrick.Post"),
    cacheRoot: CommandLineArguments.Value(
        args: args,
        name: "--corpus-cache"
    )
);
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
var requireAssets = args.Contains(
    comparer: StringComparer.OrdinalIgnoreCase,
    value: "--require-assets"
);
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
    parallelism: parallelism,
    requireAssets: requireAssets,
    sstRoot: sstRoot,
    testRomRoot: testRomRoot
);
var report = new PostBattery<PostContext>(
    banner: "Puck.HumbleGamingBrick.Post - HumbleGamingBrick machine power-on self-test",
    stages: stages
).Run(context: context);
report.Write(artifactsDirectory: artifactsDirectory);
if (report.Abandoned) {
    // A hung stage cannot be cancelled. The written report names it, and ending the process here keeps the hung
    // thread from holding the run open.
    Environment.Exit(exitCode: report.ExitCode);
}

return report.ExitCode;
