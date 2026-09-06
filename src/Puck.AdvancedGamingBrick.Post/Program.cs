using Puck.AdvancedGamingBrick;
using Puck.AdvancedGamingBrick.Post;

// Puck.AdvancedGamingBrick.Post — the AdvancedGamingBrick machine's power-on self-test and the primary way the machine is
// validated. It runs an ordered battery of self-checking stages and exits 0 (all passed), 1 (a check failed), or 2 (a
// stage could not run). There is no rich CLI for the battery: a few hand-parsed knobs — where artifacts land, the corpus
// roots and per-machine cartridges, and an optional tier/name subset for iterating. Tier A runs anywhere on
// hand-assembled vectors and a synthetic cartridge; Tier B needs the corpora declared in corpora.json (--fetch-corpora
// fills the cache), a real BIOS (--bios), or user ROMs (--games), and its stages skip when those are absent. The
// accuracy-frontier diagnostic modes (the cosim oracles, single-ROM inspectors) live in Diagnostics and run before the
// battery when their flag is present; see the README.

if (!CommandLineArguments.TryValidateValues(args: args, names: ["--bios", "--ares", "--suite-focus", "--corpus-cache"], error: out var optionError)) {
    Console.Error.WriteLine(value: optionError);
    return 2;
}
ReadOnlyMemory<byte> biosImage;
try {
    biosImage = LoadBios(path: CommandLineArguments.Value(args: args, name: "--bios"));
} catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException) {
    Console.Error.WriteLine(value: $"--bios: {exception.Message}");
    return 2;
}
var machineOptions = new AgbMachineOptions {
    DisablePrefetch = args.Contains(value: "--no-prefetch", comparer: StringComparer.OrdinalIgnoreCase),
    DisableRtc = args.Contains(value: "--no-rtc", comparer: StringComparer.OrdinalIgnoreCase),
    BusTrace = args.Contains(value: "--bus-trace", comparer: StringComparer.OrdinalIgnoreCase) ? Console.Error.WriteLine : null,
};
if (machineOptions.DisablePrefetch || machineOptions.DisableRtc || machineOptions.BusTrace is not null) {
    string[] supported = ["--render", "--probe", "--ags", "--accuracy-suite", "--lockstep", "--pctrace", "--statetrace", "--trace-cycles", "--trace-crash", "--iodump", "--link-init-trace"];
    if (!args.Any(predicate: arg => supported.Contains(value: arg, comparer: StringComparer.OrdinalIgnoreCase))) {
        Console.Error.WriteLine(value: "--no-prefetch, --no-rtc and --bus-trace require a single-ROM inspection diagnostic; battery and benchmark runs use normal hardware behavior.");
        return 2;
    }
}
var diagnostics = new Diagnostics(biosImage: biosImage, machineOptions: machineOptions);
// A diagnostic flag short-circuits the battery: run that single investigative mode and return its exit code.
if (diagnostics.TryRun(
    args: args,
    exitCode: out var diagnosticExitCode
)) {
    return diagnosticExitCode;
}
var corpora = CorpusManifest.Load(path: CorpusManifest.BesideSource(), cacheRoot: CommandLineArguments.Value(args: args, name: "--corpus-cache"));

if (args.Contains(
    comparer: StringComparer.OrdinalIgnoreCase,
    value: "--fetch-corpora"
)) {
    Console.Out.WriteLine(value: $"{corpora.Fetch()} corpus archive(s) fetched into {corpora.EffectiveCacheRoot}");

    return 0;
}

var artifactsDirectory = (CommandLineArguments.Value(
    args: args,
    name: "--artifacts"
) ?? Path.Combine(
    path1: "artifacts",
    path2: "agb-post"
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
    accuracySuitePath: ExistingFile(path: CommandLineArguments.Value(
        args: args,
        name: "--accuracy-suite"
    )),
    agsPath: ExistingFile(path: CommandLineArguments.Value(
        args: args,
        name: "--ags"
    )),
    artifactsDirectory: artifactsDirectory,
    biosImage: biosImage,
    fuzzRoot: corpora.Resolve(
        args: args,
        flag: "--fuzz",
        name: "fuzzarm"
    ),
    gamesRoot: ExistingDirectory(path: CommandLineArguments.Value(
        args: args,
        name: "--games"
    )),
    linkGamePath: ExistingFile(path: CommandLineArguments.Value(
        args: args,
        name: "--link-game"
    )),
    parallelism: parallelism,
    solarRomPath: ExistingFile(path: CommandLineArguments.Value(
        args: args,
        name: "--solar-rom"
    )),
    testRomRoot: corpora.Resolve(
        args: args,
        flag: "--roms",
        name: "gba-tests"
    )
);
var report = new PostBattery<PostContext>(
    banner: "Puck.AdvancedGamingBrick.Post - AdvancedGamingBrick machine power-on self-test",
    stages: stages
).Run(context: context);

report.Write(artifactsDirectory: artifactsDirectory);

return report.ExitCode;

static string? ExistingDirectory(string? path) =>
    (((path is not null) && Directory.Exists(path: path))
        ? path
        : null);
static string? ExistingFile(string? path) =>
    (((path is not null) && File.Exists(path: path))
        ? path
        : null);
// Loads the explicit --bios image, rejecting unreadable or wrong-sized files. Only omission selects the zeroed
// 16 KiB stub, on which BIOS-dependent stages skip cleanly. The banner reports the image's
// real classification (retail / replacement / unknown) via AgbBiosProfile rather than assuming a replacement.
static ReadOnlyMemory<byte> LoadBios(string? path) {
    if (path is not null) {
        var bytes = File.ReadAllBytes(path: path);

        if (bytes.Length == ReplacementBios.ImageSize) {
            var identity = AgbBiosProfile.Identify(image: bytes);

            Console.WriteLine(value: $"== BIOS: loaded {identity.Description} (sha1 {identity.Sha1}) from {path} ==");

            return bytes;
        }

        throw new ArgumentException(message: $"Expected {ReplacementBios.ImageSize} bytes in '{path}', got {bytes.Length}.");
    }

    return new byte[ReplacementBios.ImageSize];
}
