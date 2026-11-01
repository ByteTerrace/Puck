using Puck.HumbleGamingBrick.Post;

var options = new PostCommandLine(args: args,
    flags: ["--fetch-corpora", "--require-assets"],
    requiredValues: ["--corpus-cache"],
    values: ["--artifacts", "--tier", "--filter", "--parallelism", "--corpus-cache"]);
if (!options.TryValidateValues(error: out var optionError)) {
    Console.Error.WriteLine(value: optionError);
    return 2;
}
if (Diagnostics.TryRun(
    args: args,
    exitCode: out var diagnosticExitCode
)) {
    return diagnosticExitCode;
}
var artifactsDirectory = (options.Value(name: "--artifacts") ?? Path.Combine(
    path1: "artifacts",
    path2: "gb-post"
));
var tierFilter = options.Value(name: "--tier");
var nameFilter = options.Value(name: "--filter");
var parallelism = int.Parse(
    provider: System.Globalization.CultureInfo.InvariantCulture,
    s: (options.Value(name: "--parallelism") ?? "0")
);
var corpora = CorpusManifest.Load(
    path: CorpusManifest.InRepository(projectName: "Puck.HumbleGamingBrick.Post"),
    cacheRoot: options.Value(name: "--corpus-cache")
);
// --fetch-corpora fills the local cache from the manifest's pinned archives and exits; a build agent runs it once per
// cache key, a developer once per version bump.
if (options.Flag(name: "--fetch-corpora")) {
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
var requireAssets = options.Flag(name: "--require-assets");
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
