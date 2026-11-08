using System.CommandLine;

using BenchmarkDotNet.Configs;
using BenchmarkDotNet.ConsoleArguments;
using BenchmarkDotNet.ConsoleArguments.ListBenchmarks;
using BenchmarkDotNet.Diagnosers;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Loggers;
using BenchmarkDotNet.Running;
using BenchmarkDotNet.Toolchains.InProcess.Emit;

namespace Puck.Cli.Bench;

// The `puck bench` verb: `kernels`, the BenchmarkDotNet microscope over the Maths, SDF and state kernels, beside the
// `world`, `startup`, `frames` and `state-evidence` lanes, which run outside BenchmarkDotNet entirely.
//
// BenchmarkDotNet owns the whole option grammar of `kernels`, so it arrives as one unparsed string[] the switcher reads
// itself: puck rewrites no token of it, and reads BenchmarkDotNet's own parse only to judge the run's outcome.
public static class BenchRunner {
    private const string KernelsVerb = "bench kernels";

    // MemoryDiagnoser rides on every scenario: these kernels are meant to be zero-alloc, and its byte columns are
    // the only thing that reports whether they are. No class carries DisassemblyDiagnoser: its ClrMD cannot read the
    // DAC of the runtime this CLI targets and fails the whole case; the JIT's own DOTNET_JitDisasm reaches the same
    // code because the scenarios run in this process. The in-process toolchain rides as a mutator, so it applies to whichever job the command line picks
    // (a plain job here would run beside a command-line --job and double the output): every scenario runs in this
    // process against the CLI's own loaded assemblies, never a rebuild of whatever source the working tree holds.
    private static IConfig KernelsConfig() =>
        ManualConfig
            .Create(config: DefaultConfig.Instance)
            .AddDiagnoser(newDiagnosers: MemoryDiagnoser.Default)
            .AddJob(newJobs: new Job(id: "InProcess").WithToolchain(toolchain: InProcessEmitToolchain.Instance).AsMutator());
    // The exit code of a kernels run: 0 when every selected scenario ran, or the run only listed, printed information
    // or help; 1 when a scenario failed validation, failed to run or measured nothing; 2 when the arguments did not
    // parse or selected nothing.
    private static int Run(string[] benchmarkArguments) {
        var config = KernelsConfig();
        var (parsed, _, options) = ConfigParser.Parse(
            args: benchmarkArguments,
            globalConfig: config,
            logger: NullLogger.Instance
        );
        var summaries = BenchmarkSwitcher
            .FromAssembly(assembly: typeof(BenchRunner).Assembly)
            .Run(
            args: benchmarkArguments,
            config: config
        ).ToArray();

        if (!parsed) {
            // The switcher has printed its parser's own message. Help and version requests also fail its parse.
            return (benchmarkArguments.Any(predicate: static token => (token is "--help" or "--version"))
                ? CliExit.Success
                : CliExit.Refuse(
                verb: KernelsVerb,
                what: string.Join(separator: ' ', values: benchmarkArguments),
                why: "BenchmarkDotNet could not parse these arguments (its message is above)"
            ));
        }

        if ((options.ListBenchmarkCaseMode != ListBenchmarkCaseMode.Disabled) || options.PrintInformation) {
            return CliExit.Success;
        }

        if (summaries.Sum(selector: static summary => summary.BenchmarksCases.Length) == 0) {
            return CliExit.Refuse(
                verb: KernelsVerb,
                what: string.Join(separator: ' ', values: benchmarkArguments),
                why: "no scenario matched, so nothing was measured"
            );
        }

        var failures = 0;

        foreach (var summary in summaries) {
            foreach (var error in summary.ValidationErrors.Where(predicate: static error => error.IsCritical)) {
                Console.Error.WriteLine(value: $"puck {KernelsVerb}: validation: {error.Message}");

                failures++;
            }

            foreach (var report in summary.Reports.Where(predicate: static report => (!report.Success || (report.ResultStatistics is null)))) {
                Console.Error.WriteLine(value: $"puck {KernelsVerb}: {report.BenchmarkCase.DisplayInfo}: did not run to a measurement");

                failures++;
            }

            // A case refused before it ran (a critical validation error) leaves no report at all.
            foreach (var benchmarkCase in summary.BenchmarksCases.Where(predicate: benchmarkCase => !summary.Reports.Any(predicate: report => ReferenceEquals(objA: report.BenchmarkCase, objB: benchmarkCase)))) {
                Console.Error.WriteLine(value: $"puck {KernelsVerb}: {benchmarkCase.DisplayInfo}: never ran");

                failures++;
            }
        }

        return ((failures == 0)
            ? CliExit.Success
            : CliExit.Failed);
    }

    /// <summary>Creates the <c>bench</c> verb; <paramref name="clock"/> bounds its tool runs.</summary>
    /// <param name="clock">The CLI host's clock.</param>
    /// <param name="sampleHost">The host sampler the <c>world</c>, <c>startup</c> and <c>frames</c> lanes record their load with, or
    /// <see langword="null"/> to record none.</param>
    /// <returns>The verb.</returns>
    public static Command Create(TimeProvider clock, BenchHostSample? sampleHost = null) {
        var benchmarkArguments = CliOptions.Forwarded(
            description: "Forwarded to BenchmarkDotNet's switcher verbatim: --filter, --job, --list, --runtimes, --hide, and the rest of its grammar.",
            name: "benchmark-arguments"
        );
        var kernelsCommand = new Command(
            description: "Run the BenchmarkDotNet micro-benchmarks over the Maths, SDF, and state kernels.",
            name: "kernels"
        ) { benchmarkArguments };
        var worldArgument = new Argument<string?>(name: "world") { Arity = ArgumentArity.ZeroOrOne, Description = $"The world's asset package folder, .puck source or .world.json document. Default: the debug world, {WorldBenchmarks.DefaultWorldRelativePath}." };
        var repeatsOption = CliOptions.Count(defaultValue: 1, description: "How many times the whole lane runs, 1 to 100; each metric's median, p10 and p90 are taken over them.", maximum: 100, minimum: 1, name: "--repeats");
        var artifactOption = new Option<string?>(name: "--world-artifact") { Description = "A World build's Puck.World.dll whose engine is measured in place of this CLI's own; the bench code stays this CLI's." };
        var outputOption = new Option<string?>(name: "--output") { Description = "Parent of a unique run directory that receives report.json (puck.bench.v1)." };
        var jsonOption = new Option<bool>(name: "--json") { Description = "Print the report (puck.bench.v1) on standard output, with the table on standard error." };
        var worldCommand = new Command(
            description: "Time the Puck.World.Server tick path with a plain stopwatch harness.",
            name: "world"
        ) { worldArgument, repeatsOption, artifactOption, outputOption, jsonOption };
        var command = new Command(
            description: "Measure kernels, the server tick path, World startup, windowed frames, and the cost schedule's evidence.",
            name: "bench"
        ) { kernelsCommand, StartupBenchmarks.Create(sampleHost: sampleHost), FrameBenchmarks.Create(sampleHost), StateEvidenceCommand.Create(clock: clock), worldCommand };

        kernelsCommand.Detail(detail: """
            Every argument reaches BenchmarkDotNet's switcher unchanged. Job rigor is chosen there and layered over the
            base config puck supplies:

              (no --job)    BenchmarkDotNet's adaptive default, the balanced everyday setting
              --job short   fast survey (fewer warmup/target iterations), a shape-of-the-numbers pass
              --job long    thorough verdict (many iterations, tight error bars) before a retention decision

            Examples:

              puck bench kernels --filter '*Norm*'   the scenarios matching a glob
              puck bench kernels --list flat         the scenarios the switcher sees

            puck's own help answers -h and --help here, so BenchmarkDotNet's (-h is its `hide` option) are reached
            past a `--` separator: `puck bench kernels -- --help`.

            Every scenario runs in this process, against the assemblies this CLI was built with; nothing is rebuilt
            from the working tree, so the numbers describe this build. Exit 0 when every selected scenario measured
            (or the run only listed or printed help), 1 when one failed validation or did not run to a measurement,
            and 2 when the arguments did not parse or matched no scenario.
            """);
        worldCommand.Detail(detail: """
            Loads one world, the debug world unless another is named, builds its server without a device, and
            prints one row per number: server construction time, idle-tick time and quiet-tick allocation (median
            over a sampled window, after a warmup), the contact work per sampled tick as deterministic counts
            (sweeps, sweep bounds queries, interval walks with their instructions and rotation expansions, point
            evaluations with their instructions), a paced tick that drains a whole-row value write to one plain
            integer slot with its allocation and the arena versions and installed rows it moved, the tick that
            drains a live creation edit changing shading alone (a palette slot's roughness on the creation with the
            most shapes) with its allocation (shading-edit-tick, shading-edit-allocation), the tick paced at
            the simulation rate with the count of
            submissions that waited on a screen machine's worker, and each screen machine's own tick of emulation
            timed alone. A server construction can cost hundreds of milliseconds, past what an iteration-based job
            can amortize honestly, which is why this lane sits beside BenchmarkDotNet rather than inside it.

            The run is also a puck.bench.v1 report (--json prints it, --output writes report.json): the measured
            build's identity (version with its commit, SHA-256 of its engine assemblies), the host load sampled
            during the run, and each metric (idle-tick, quiet-tick-allocation, server-construction, paced-tick,
            the work counts and the rest) as median, p10 and p90 over --repeats runs. `puck measure` reads the idle
            tick as /metrics/idle-tick/median. With --world-artifact the engine assemblies come from that World
            build, loaded in their own context beside this CLI's bench code, so a before/after pair measures two
            builds; a build whose engine this bench code cannot call is refused by name.
            """);
        kernelsCommand.SetAction(action: parseResult => Run(benchmarkArguments: (parseResult.GetValue(argument: benchmarkArguments) ?? [])));
        worldCommand.SetAction(action: parseResult => WorldBenchmarks.Run(
            artifact: parseResult.GetValue(option: artifactOption),
            json: parseResult.GetValue(option: jsonOption),
            output: parseResult.GetValue(option: outputOption),
            repeats: parseResult.GetValue(option: repeatsOption),
            sampleHost: sampleHost,
            world: parseResult.GetValue(argument: worldArgument)
        ));

        return command;
    }
}
