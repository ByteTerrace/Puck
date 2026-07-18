using Puck.AdvancedGamingBrick;
using Puck.AdvancedGamingBrick.Post;

// Puck.AdvancedGamingBrick.Post — the AdvancedGamingBrick machine's power-on self-test and the primary way the machine is
// validated. It runs an ordered battery of self-checking stages and exits 0 (all passed), 1 (a check failed), or 2 (a
// stage could not run). There is no rich CLI for the battery: a few hand-parsed knobs — where artifacts land, the corpus
// and commercial-ROM roots, and an optional tier/name subset for iterating. Tier A runs anywhere on hand-assembled
// vectors and a synthetic cartridge; Tier B needs the reference corpus (PUCK_AGB_TESTROMS), user ROMs (PUCK_AGB_GAMES),
// and/or a real replacement BIOS (PUCK_AGB_BIOS), and its stages skip when those are absent. The accuracy-frontier
// diagnostic modes (the cosim oracles, single-ROM inspectors) live in Diagnostics and run before the battery when their
// flag is present; see the README.

var biosImage = LoadBios();
Diagnostics.BiosImage = biosImage;

// A diagnostic flag short-circuits the battery: run that single investigative mode and return its exit code.
if (Diagnostics.TryRun(args: args, exitCode: out var diagnosticExitCode)) {
    return diagnosticExitCode;
}
var artifactsDirectory = (ArgValue(args: args, name: "--artifacts") ?? Path.Combine(path1: "artifacts", path2: "agb-post"));
var tierFilter = ArgValue(args: args, name: "--tier");
var nameFilter = ArgValue(args: args, name: "--filter");
var testRomRoot = ResolveRoot(args: args, flag: "--roms", variable: "PUCK_AGB_TESTROMS");
var gamesRoot = ResolveRoot(args: args, flag: "--games", variable: "PUCK_AGB_GAMES");
var stages = PostStages.Create()
    .Where(predicate: stage => TierMatches(stage: stage, tierFilter: tierFilter))
    .Where(predicate: stage => NameMatches(stage: stage, nameFilter: nameFilter))
    .ToArray();
var context = new PostContext(artifactsDirectory: artifactsDirectory, testRomRoot: testRomRoot, gamesRoot: gamesRoot, biosImage: biosImage);
var report = new PostBattery(stages: stages).Run(context: context);
report.Write(artifactsDirectory: artifactsDirectory);
return report.ExitCode;

// Loads a BIOS image from PUCK_AGB_BIOS when present and correctly sized, so the BIOS-dependent stages (BIOS IRQ
// dispatch) run; otherwise a zeroed 16 KiB stub, on which those stages skip cleanly. The banner reports the image's
// real classification (retail / replacement / unknown) via AgbBiosProfile rather than assuming a replacement — a
// retail image once loaded here was mislabelled "replacement", masking whether cycle-parity work was even eligible.
static ReadOnlyMemory<byte> LoadBios() {
    var biosPath = Environment.GetEnvironmentVariable(variable: "PUCK_AGB_BIOS");

    if (!string.IsNullOrEmpty(value: biosPath) && File.Exists(path: biosPath)) {
        var bytes = File.ReadAllBytes(path: biosPath);

        if (bytes.Length == ReplacementBios.ImageSize) {
            var identity = AgbBiosProfile.Identify(image: bytes);

            Console.WriteLine(value: $"== BIOS: loaded {identity.Description} (sha1 {identity.Sha1}) from {biosPath} ==");

            return bytes;
        }

        Console.WriteLine(value: $"== BIOS: ignoring {biosPath} (expected {ReplacementBios.ImageSize} bytes, got {bytes.Length}) ==");
    }

    return new byte[ReplacementBios.ImageSize];
}
static string? ArgValue(string[] args, string name) {
    for (var index = 0; (index < (args.Length - 1)); ++index) {
        if (string.Equals(a: args[index], b: name, comparisonType: StringComparison.OrdinalIgnoreCase)) {
            return args[(index + 1)];
        }
    }

    return null;
}

// A directory root: the CLI flag wins, else the environment variable (when it names an existing directory), else null
// (the stages that need it skip when it is absent).
static string? ResolveRoot(string[] args, string flag, string variable) {
    var explicitRoot = ArgValue(args: args, name: flag);

    if (!string.IsNullOrEmpty(value: explicitRoot)) {
        return explicitRoot;
    }

    var fromEnvironment = Environment.GetEnvironmentVariable(variable: variable);

    return ((!string.IsNullOrEmpty(value: fromEnvironment) && Directory.Exists(path: fromEnvironment)) ? fromEnvironment : null);
}
static bool TierMatches(IPostStage stage, string? tierFilter) =>
    (string.IsNullOrEmpty(value: tierFilter) || string.Equals(a: stage.Tier.ToString(), b: tierFilter, comparisonType: StringComparison.OrdinalIgnoreCase));
static bool NameMatches(IPostStage stage, string? nameFilter) =>
    (string.IsNullOrEmpty(value: nameFilter) || stage.Name.Contains(value: nameFilter, comparisonType: StringComparison.OrdinalIgnoreCase));
