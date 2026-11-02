using System.CommandLine;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Nodes;
using Puck.World.Server;
using Puck.World.Transpiler.Composition;

namespace Puck.Cli.Test;

public static partial class TestCommand {
    private const string DisclosureScratchPrefix = "puck-disclosure-";

    /// <summary>Creates <c>puck disclosure</c>: the noninterference harness over a world and its
    /// <c>&lt;world&gt;.disclosure.json</c> fixture.</summary>
    /// <returns>The verb.</returns>
    public static Command CreateDisclosure() {
        var pathArgument = new Argument<string>(name: "world") {
            Description = "A .puck world with a <stem>.disclosure.json fixture beside it, or the fixture itself.",
        };
        var keepOption = new Option<string?>(name: "--keep") {
            Description = "Run in this directory instead of a scratch one and keep it: the staged world, every leg's transcripts, exports and disclosure.json.",
        };
        var worldArtifactOption = new Option<string?>(name: "--world-artifact") {
            Description = "Boot this already-built Puck.World.dll instead of the build of this checkout's sources.",
        };
        var cacheOption = new Option<string?>(name: "--cache-dir") {
            Description = "Override the device-cache root the worker uses. Absent uses the ordinary per-user Puck caches.",
        };
        var seedOption = new Option<ulong?>(name: "--seed") {
            Description = "Draw the fuzzer's inputs from this seed instead of the fixture's.",
        };
        var linesOption = new Option<int?>(name: "--lines") {
            Description = "Draw this many console lines for the observed seat instead of the fixture's count.",
        };
        var actionsOption = new Option<int?>(name: "--actions") {
            Description = "Draw this many actions for each observed session instead of the fixture's count.",
        };
        var command = new Command(
            description: """
            Boots one headless world per variant of a disclosure fixture (and the first variant again, the control),
            identical but for the hidden state each variant's operator steps author, drives the same non-operator
            principals with the same inputs in each (the observed seat's console lines, the world's own schedule rows,
            and in-process sessions admitted from the fixture's authorities), and compares everything each principal
            received byte for byte: replies, refusals, typed outcomes, query answers, every delivery with its version,
            count and size per tick, and each stream's lifetime. A difference is a leak, named by its door (the verb,
            delivery kind, query or outcome), its first tick and the two answers. Exit codes are 0 when no principal
            can tell any variant from the first, 1 for a leak, and 2 for usage, a fixture or world that does not load,
            a leg that did not finish, or a control that differs from the first variant (the harness cannot judge).
            """,
            name: "disclosure"
        ) {
            pathArgument,
            keepOption,
            worldArtifactOption,
            cacheOption,
            seedOption,
            linesOption,
            actionsOption,
        };

        command.SetAction(action: parseResult => RunDisclosure(
            actions: parseResult.GetValue(option: actionsOption),
            cacheDirectory: parseResult.GetValue(option: cacheOption),
            keep: parseResult.GetValue(option: keepOption),
            lines: parseResult.GetValue(option: linesOption),
            path: (parseResult.GetValue(argument: pathArgument) ?? string.Empty),
            seed: parseResult.GetValue(option: seedOption),
            worldArtifact: parseResult.GetValue(option: worldArtifactOption)
        ));
        return command;
    }

    private static int RunDisclosure(string path, string? keep, string? worldArtifact, string? cacheDirectory, ulong? seed, int? lines, int? actions) {
        if (!CliPaths.TryGetRepositoryRoot(repositoryRoot: out var repositoryRoot)) {
            Console.Error.WriteLine(value: "ERROR: puck disclosure must run inside the repository.");
            return 2;
        }
        var full = Path.GetFullPath(path: path);
        var (world, fixturePath) = (full.EndsWith(value: WorldDisclosureFixture.Suffix, comparisonType: StringComparison.OrdinalIgnoreCase)
            ? ((full[..^WorldDisclosureFixture.Suffix.Length] + ".puck"), full)
            : (full, Path.Combine(path1: (Path.GetDirectoryName(path: full) ?? "."), path2: (Path.GetFileNameWithoutExtension(path: full) + WorldDisclosureFixture.Suffix))));

        if (!WorldDisclosureFixture.TryRead(fixture: out var fixture, path: fixturePath, reason: out var fixtureReason)) {
            Console.Error.WriteLine(value: $"ERROR: {fixtureReason}");
            return 2;
        }
        var authored = (fixture!.Fuzz ?? new WorldDisclosureFuzz());
        var fuzz = authored with { Seed = (seed ?? authored.Seed), Lines = (lines ?? authored.Lines), Actions = (actions ?? authored.Actions) };

        if (keep is null) {
            using var run = RunDirectory.Create(prefix: DisclosureScratchPrefix);

            return run.Conclude(exitCode: RunDisclosureIn(cacheDirectory: cacheDirectory, fixture: fixture, fuzz: fuzz, repositoryRoot: repositoryRoot,
                runDirectory: run.Path, world: world, worldArtifact: worldArtifact));
        }
        var kept = Path.GetFullPath(path: keep);

        _ = Directory.CreateDirectory(path: kept);
        try {
            return RunDisclosureIn(cacheDirectory: cacheDirectory, fixture: fixture, fuzz: fuzz, repositoryRoot: repositoryRoot, runDirectory: kept, world: world, worldArtifact: worldArtifact);
        } finally {
            Console.WriteLine(value: $"disclosure: staged world, transcripts and exports kept under {kept}");
        }
    }
    private static int RunDisclosureIn(string world, WorldDisclosureFixture fixture, WorldDisclosureFuzz fuzz, string repositoryRoot, string runDirectory, string? worldArtifact, string? cacheDirectory) {
        var name = Path.GetFileNameWithoutExtension(path: world);

        _ = WorldCompileCache.Shared.TryCompile(compiled: out var compiled, failure: out var failure, includeTests: false, path: world);
        if ((failure is not null) || (compiled?.Document is not { } bytes) || (JsonNode.Parse(utf8Json: bytes) is not JsonObject document)) {
            Console.Error.WriteLine(value: $"ERROR: {world} does not compile to a world document{((failure is null) ? "." : $":{Environment.NewLine}{failure.Diagnostics.FormatReport(File.ReadAllText(path: world))}")}");
            return 2;
        }
        if (!TryStageTestWorld(foreign: [], generatedDirectory: Path.Combine(path1: runDirectory, path2: "generated"), name: name, path: out var staged,
            reason: out var stageReason, siblings: [], sourceDirectory: (Path.GetDirectoryName(path: world) ?? "."), world: document)) {
            Console.Error.WriteLine(value: $"ERROR: {world} does not compose: {stageReason}");
            return 2;
        }
        if (!TryReadSchedule(reason: out var scheduleReason, schedule: out var schedule, world: staged)) {
            Console.Error.WriteLine(value: $"ERROR: {scheduleReason} A disclosure fixture's world authors a schedule: its rows are the pair's authored script and its export tick ends the run.");
            return 2;
        }
        var from = (fuzz.From ?? (fixture.Variants.SelectMany(selector: static variant => variant.Steps).Select(selector: static step => step.At).DefaultIfEmpty(defaultValue: 0UL).Max() + 1UL));
        var until = (fuzz.Until ?? Math.Max(val1: from, val2: ((schedule!.ExportTick > 2UL) ? (schedule.ExportTick - 2UL) : 0UL)));

        if (until >= schedule!.ExportTick) {
            Console.Error.WriteLine(value: $"ERROR: the fuzz window ends at tick {until}, at or after the export tick {schedule.ExportTick}, so its last inputs would never answer.");
            return 2;
        }
        // The control is the first variant again: two worlds whose hidden state is equal, which must compare equal, or
        // the harness itself is reading something other than the hidden state.
        var variants = fixture.Variants.Prepend(element: fixture.Variants[0] with { Name = (fixture.Variants[0].Name + "~control") }).ToArray();
        var legs = variants.Select(selector: (variant, index) => (Variant: variant, Directory: Path.Combine(path1: runDirectory, path2: "legs",
            path3: string.Create(provider: CultureInfo.InvariantCulture, handler: $"{index:D2}-{variant.Name.Replace(oldChar: '~', newChar: '-')}")))).ToArray();
        var sessions = (fixture.Sessions ?? []);

        Console.WriteLine(value: string.Create(provider: CultureInfo.InvariantCulture,
            handler: $"disclosure: {name} observer={fixture.Observer} sessions=[{string.Join(separator: ", ", values: sessions)}] variants=[{string.Join(separator: ", ", values: fixture.Variants.Select(selector: static variant => variant.Name))}] fuzz seed={fuzz.Seed} lines={fuzz.Lines} actions={fuzz.Actions} window={from}..{until} export={schedule.ExportTick}"));
        if (!TryResolveArtifact(artifact: out var artifact, lease: out var lease, repositoryRoot: repositoryRoot, runDirectory: runDirectory, worldArtifact: worldArtifact)) {
            return 2;
        }
        using var held = lease;
        using var heavy = AcquireHeavy(path: world, repositoryRoot: repositoryRoot, worlds: legs.Length);
        var started = Stopwatch.GetTimestamp();

        foreach (var (_, directory) in legs) { Puck.Confinement.ConfinedDirectory.RemoveTree(path: directory); }
        var ran = RunWorker(artifact: artifact!, batch: Path.Combine(path1: runDirectory, path2: "batch-disclosure.json"), legs: [.. legs.Select(selector: leg => (object)new {
            World = staged,
            Directory = leg.Directory,
            ExitAfterSeconds = ((int)Math.Ceiling(a: LegCeiling(exportTick: schedule.ExportTick, rateHz: schedule.RateHz).TotalSeconds)),
            CacheDirectory = ((cacheDirectory is null) ? null : Path.GetFullPath(path: cacheDirectory)),
            Disclosure = new WorldDisclosureLeg(Fuzz: fuzz, From: from, Observer: fixture.Observer, Sessions: sessions, Script: (fixture.Script ?? []), Steps: leg.Variant.Steps, Until: until, Variant: leg.Variant.Name),
        })]);

        Console.WriteLine(value: string.Create(provider: CultureInfo.InvariantCulture, handler: $"disclosure: {legs.Length} world(s) in {Stopwatch.GetElapsedTime(startingTimestamp: started).TotalSeconds:F1} s"));
        var transcripts = new WorldDisclosureTranscript[legs.Length];

        for (var index = 0; (index < legs.Length); index++) {
            var (variant, directory) = legs[index];
            var exit = Path.Combine(path1: directory, path2: "exit-code");
            var file = Path.Combine(path1: directory, path2: "out", path3: WorldDisclosureTranscript.FileName);

            if (!File.Exists(path: exit) || (File.ReadAllText(path: exit) != "0") || !File.Exists(path: file)) {
                Console.Error.WriteLine(value: $"ERROR: the world of variant '{variant.Name}' did not finish{(ran ? string.Empty : " (the worker failed)")}; transcripts under {directory}.");
                var stderr = Path.Combine(path1: directory, path2: "stderr.log");

                if (File.Exists(path: stderr)) { Console.Error.WriteLine(value: Tail(text: File.ReadAllText(path: stderr))); }
                return 2;
            }
            transcripts[index] = WorldDisclosureTranscript.Parse(json: File.ReadAllBytes(path: file));
            Console.WriteLine(value: string.Create(provider: CultureInfo.InvariantCulture,
                handler: $"  {variant.Name}: {transcripts[index].Entries.Count} answer(s) recorded; {LegSeconds(directory: directory):F1} s"));
        }
        var noise = WorldDisclosureComparison.Compare(left: transcripts[1], right: transcripts[0]);

        if (noise.Count > 0) {
            Console.Error.WriteLine(value: $"REFUSED: the control '{variants[0].Name}' differs from '{variants[1].Name}', whose hidden state is the same; these doors answer differently without any hidden difference, so they judge nothing:");
            foreach (var leak in noise) { Console.Error.WriteLine(value: $"  {leak.Describe()}"); }
            return 2;
        }
        Console.WriteLine(value: $"  control: '{variants[0].Name}' agrees with '{variants[1].Name}' at every door");
        var leaks = 0;

        for (var index = 2; (index < legs.Length); index++) {
            var found = WorldDisclosureComparison.Compare(left: transcripts[1], right: transcripts[index]);

            leaks += found.Count;
            if (found.Count == 0) {
                Console.WriteLine(value: $"  pair {variants[1].Name} / {variants[index].Name}: no principal can tell them apart");
                continue;
            }
            Console.Error.WriteLine(value: $"  LEAK pair {variants[1].Name} / {variants[index].Name}: {found.Count} door(s)");
            foreach (var leak in found) { Console.Error.WriteLine(value: $"    {leak.Describe()}"); }
        }
        if (leaks > 0) {
            Console.Error.WriteLine(value: $"FAIL: {leaks} leaking door(s) across {(legs.Length - 2)} pair(s).");
            return 1;
        }
        Console.WriteLine(value: $"PASS: no observed principal can tell any variant of {name} from '{variants[1].Name}'.");
        return 0;
    }
    // A leg's own boot and authored time, from the timing its worker wrote.
    private static double LegSeconds(string directory) {
        var timing = Path.Combine(path1: directory, path2: "timing.json");

        if (!File.Exists(path: timing) || (JsonNode.Parse(json: File.ReadAllText(path: timing)) is not JsonObject root)) { return 0d; }
        return ((root["bootMilliseconds"]?.GetValue<double>() ?? 0d) + (root["armMilliseconds"]?.GetValue<double>() ?? 0d) + (root["authoredMilliseconds"]?.GetValue<double>() ?? 0d)) / 1000d;
    }
}
