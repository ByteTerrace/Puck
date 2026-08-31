using Puck.HumbleGamingBrick.Post;

// Puck.HumbleGamingBrick.Post — the HumbleGamingBrick machine's power-on self-test and the primary way the
// machine is validated. It runs an ordered battery of self-checking stages and exits 0 (all passed), 1 (a check failed),
// or 2 (a stage could not run). There is no rich CLI: three hand-parsed knobs — where artifacts land, and an optional
// tier/name subset for iterating. Tier A runs anywhere on a synthetic ROM; Tier B needs the reference corpus, found via
// the PUCK_GB_TESTROMS environment variable and skipped when absent; Tier C (the cross-machine serial link) is
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
    sstRoot: sstRoot,
    testRomRoot: testRomRoot
);
var report = new PostBattery<PostContext>(
    banner: "Puck.HumbleGamingBrick.Post - HumbleGamingBrick machine power-on self-test",
    stages: stages
).Run(context: context);
report.Write(artifactsDirectory: artifactsDirectory);
return report.ExitCode;
