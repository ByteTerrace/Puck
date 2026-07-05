using Puck.HumbleGamingBrick.Post;

// Puck.HumbleGamingBrick.Post — the Game Boy / Game Boy Color machine's power-on self-test and the primary way the
// machine is validated. It runs an ordered battery of self-checking stages and exits 0 (all passed), 1 (a check failed),
// or 2 (a stage could not run). There is no rich CLI: three hand-parsed knobs — where artifacts land, and an optional
// tier/name subset for iterating. Tier A runs anywhere on a synthetic ROM; Tier B (later) needs the reference corpus,
// found via the PUCK_GB_TESTROMS environment variable and skipped when absent.

if (Diagnostics.TryRun(args: args, exitCode: out var diagnosticExitCode)) {
    return diagnosticExitCode;
}

var artifactsDirectory = ArgValue(args: args, name: "--artifacts") ?? Path.Combine(path1: "artifacts", path2: "gb-post");
var tierFilter = ArgValue(args: args, name: "--tier");
var nameFilter = ArgValue(args: args, name: "--filter");
var testRomRoot = ResolveTestRomRoot(args: args);

var stages = PostStages.Create()
    .Where(predicate: stage => TierMatches(stage: stage, tierFilter: tierFilter))
    .Where(predicate: stage => NameMatches(stage: stage, nameFilter: nameFilter))
    .ToArray();

var context = new PostContext(artifactsDirectory: artifactsDirectory, testRomRoot: testRomRoot);
var report = new PostBattery(stages: stages).Run(context: context);

report.Write(artifactsDirectory: artifactsDirectory);

return report.ExitCode;

static string? ArgValue(string[] args, string name) {
    for (var index = 0; (index < (args.Length - 1)); ++index) {
        if (string.Equals(a: args[index], b: name, comparisonType: StringComparison.OrdinalIgnoreCase)) {
            return args[index + 1];
        }
    }

    return null;
}

// The reference-ROM corpus root: --roms wins, else PUCK_GB_TESTROMS, else null (Tier-B stages skip when it is absent).
static string? ResolveTestRomRoot(string[] args) {
    var explicitRoot = ArgValue(args: args, name: "--roms");

    if (!string.IsNullOrEmpty(value: explicitRoot)) {
        return explicitRoot;
    }

    var fromEnvironment = Environment.GetEnvironmentVariable(variable: "PUCK_GB_TESTROMS");

    if (!string.IsNullOrEmpty(value: fromEnvironment) && Directory.Exists(path: fromEnvironment)) {
        return fromEnvironment;
    }

    // The known corpus location on the development machine, so the POST finds it without configuration; absent it,
    // the Tier-B stages skip.
    const string fallback = @"D:\Source\ByteTerrace\Temp\GBC Test Suites";

    return Directory.Exists(path: fallback) ? fallback : null;
}

static bool TierMatches(IPostStage stage, string? tierFilter) =>
    (string.IsNullOrEmpty(value: tierFilter) || string.Equals(a: stage.Tier.ToString(), b: tierFilter, comparisonType: StringComparison.OrdinalIgnoreCase));

static bool NameMatches(IPostStage stage, string? nameFilter) =>
    (string.IsNullOrEmpty(value: nameFilter) || stage.Name.Contains(value: nameFilter, comparisonType: StringComparison.OrdinalIgnoreCase));
