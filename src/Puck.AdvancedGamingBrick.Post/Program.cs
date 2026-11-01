using Puck.AdvancedGamingBrick;
using Puck.AdvancedGamingBrick.Post;

var options = new PostCommandLine(args: args,
    flags: ["--fetch-corpora", "--no-prefetch", "--no-rtc", "--bus-trace"],
    requiredValues: ["--bios", "--ares", "--suite-focus", "--corpus-cache"],
    values: ["--bios", "--ares", "--suite-focus", "--corpus-cache", "--artifacts", "--tier", "--filter", "--parallelism", "--accuracy-suite"]);
if (!options.TryValidateValues(error: out var optionError)) {
    Console.Error.WriteLine(value: optionError);
    return 2;
}
ReadOnlyMemory<byte>? explicitBios;
try {
    explicitBios = LoadBios(path: options.Value(name: "--bios"));
} catch (Exception exception) when ((exception is IOException or UnauthorizedAccessException or ArgumentException)) {
    Console.Error.WriteLine(value: $"--bios: {exception.Message}");
    return 2;
}
var machineOptions = new AgbMachineOptions {
    DisablePrefetch = options.Flag(name: "--no-prefetch"),
    DisableRtc = options.Flag(name: "--no-rtc"),
    BusTrace = (options.Flag(name: "--bus-trace")
    ? Console.Error.WriteLine
    : null),
};
if (
    machineOptions.DisablePrefetch ||
    machineOptions.DisableRtc ||
    (machineOptions.BusTrace is not null)
) {
    string[] supported = ["--render", "--accuracy-suite", "--lockstep", "--pctrace", "--statetrace", "--trace-cycles", "--trace-crash", "--iodump"];

    if (!args.Any(predicate: arg => supported.Contains(
        value: arg,
        comparer: StringComparer.OrdinalIgnoreCase
    ))) {
        Console.Error.WriteLine(value: "--no-prefetch, --no-rtc and --bus-trace require a single-ROM inspection diagnostic; battery and benchmark runs use normal hardware behavior.");
        return 2;
    }
}
// A diagnostic boots what a World boots: the bundled Puck firmware unless --bios names another image. The battery keeps
// the zeroed stub, on which its BIOS-dependent stages skip.
var diagnostics = new Diagnostics(
    biosImage: (explicitBios ?? AgbFirmware.GetImage()),
    machineOptions: machineOptions
);
// A diagnostic flag short-circuits the battery: run that single investigative mode and return its exit code.
if (diagnostics.TryRun(
    args: args,
    exitCode: out var diagnosticExitCode
)) {
    return diagnosticExitCode;
}
var corpora = CorpusManifest.Load(
    path: CorpusManifest.InRepository(projectName: "Puck.AdvancedGamingBrick.Post"),
    cacheRoot: options.Value(name: "--corpus-cache")
);
if (options.Flag(name: "--fetch-corpora")) {
    Console.Out.WriteLine(value: $"{corpora.Fetch()} corpus archive(s) fetched into {corpora.EffectiveCacheRoot}");

    return 0;
}
var artifactsDirectory = (options.Value(name: "--artifacts") ?? Path.Combine(
    path1: "artifacts",
    path2: "agb-post"
));
var tierFilter = options.Value(name: "--tier");
var nameFilter = options.Value(name: "--filter");
var parallelism = int.Parse(
    provider: System.Globalization.CultureInfo.InvariantCulture,
    s: (options.Value(name: "--parallelism") ?? "0")
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
    accuracySuitePath: ExistingFile(path: options.Value(name: "--accuracy-suite")),
    artifactsDirectory: artifactsDirectory,
    biosImage: (explicitBios ?? new byte[ReplacementBios.ImageSize]),
    fuzzRoot: corpora.Resolve(
        args: args,
        flag: "--fuzz",
        name: "fuzzarm"
    ),
    parallelism: parallelism,
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
if (report.Abandoned) {
    // A hung stage cannot be cancelled. The written report names it, and ending the process here keeps the hung
    // thread from holding the run open.
    Environment.Exit(exitCode: report.ExitCode);
}
return report.ExitCode;
static string? ExistingFile(string? path) =>
    (((path is not null) && File.Exists(path: path))
        ? path
        : null
    );
// Loads the explicit --bios image, rejecting unreadable or wrong-sized files, or returns null when none is named. The
// banner reports the image's real classification (retail / replacement / unknown) via AgbBiosProfile.
static ReadOnlyMemory<byte>? LoadBios(string? path) {
    if (path is not null) {
        var bytes = File.ReadAllBytes(path: path);

        if (bytes.Length == ReplacementBios.ImageSize) {
            var identity = AgbBiosProfile.Identify(image: bytes);

            Console.WriteLine(value: $"== BIOS: loaded {identity.Description} (sha1 {identity.Sha1}) from {path} ==");

            return bytes;
        }

        throw new ArgumentException(message: $"Expected {ReplacementBios.ImageSize} bytes in '{path}', got {bytes.Length}.");
    }

    return null;
}
