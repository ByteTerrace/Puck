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
// `world`, `startup` and `state-evidence` lanes, which run outside BenchmarkDotNet entirely.
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
    /// <returns>The verb.</returns>
    public static Command Create(TimeProvider clock) {
        var benchmarkArguments = CliOptions.Forwarded(
            description: "Forwarded to BenchmarkDotNet's switcher verbatim: --filter, --job, --list, --runtimes, --hide, and the rest of its grammar.",
            name: "benchmark-arguments"
        );
        var kernelsCommand = new Command(
            description: "Run the BenchmarkDotNet micro-benchmarks over the Maths, SDF, and state kernels.",
            name: "kernels"
        ) { benchmarkArguments };
        var worldArgument = new Argument<string?>(name: "world") { Arity = ArgumentArity.ZeroOrOne, Description = $"The world's asset package folder, .puck source or .world.json document. Default: the debug world, {WorldBenchmarks.DefaultWorldRelativePath}." };
        var worldCommand = new Command(
            description: "Time the Puck.World.Server tick path with a plain stopwatch harness.",
            name: "world"
        ) { worldArgument };
        var command = new Command(
            description: "Measure kernels, the server tick path, World startup, and the cost schedule's evidence.",
            name: "bench"
        ) { kernelsCommand, StartupBenchmarks.Create(), StateEvidenceCommand.Create(clock: clock), worldCommand };

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
            integer slot with its allocation and the arena versions and installed rows it moved, the tick paced at
            the simulation rate with the count of
            submissions that waited on a screen machine's worker, and each screen machine's own tick of emulation
            timed alone. A server construction can cost hundreds of milliseconds, past what an iteration-based job
            can amortize honestly, which is why this lane sits beside BenchmarkDotNet rather than inside it.
            """);
        kernelsCommand.SetAction(action: parseResult => Run(benchmarkArguments: (parseResult.GetValue(argument: benchmarkArguments) ?? [])));
        worldCommand.SetAction(action: parseResult => WorldBenchmarks.Run(world: parseResult.GetValue(argument: worldArgument)));

        return command;
    }
}
