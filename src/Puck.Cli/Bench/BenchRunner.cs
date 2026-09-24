using System.CommandLine;

using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Diagnosers;
using BenchmarkDotNet.Running;

namespace Puck.Cli.Bench;

// The `puck bench` verb: `kernels`, the BenchmarkDotNet microscope over the Maths, SDF and state kernels, beside the
// `world`, `startup` and `state-evidence` lanes, which run outside BenchmarkDotNet entirely.
//
// BenchmarkDotNet owns the whole option grammar of `kernels`, so it arrives as one unparsed string[] the switcher reads
// itself: puck neither validates nor rewrites a token of it.
internal static class BenchRunner {
    // MemoryDiagnoser rides on every scenario: these kernels are meant to be zero-alloc, and its byte columns are
    // the only thing that reports whether they are. DisassemblyDiagnoser is attached per-class, only on the
    // fixed-point kernels, via attribute — the extension scenario is modular-ulong arithmetic, not a fixed-point
    // kernel. No explicit job is added here so a command-line --job is the single job that runs (adding one here
    // would run alongside it and double the output).
    private static int Run(string[] benchmarkArguments) {
        var config = ManualConfig
            .Create(config: DefaultConfig.Instance)
            .AddDiagnoser(newDiagnosers: MemoryDiagnoser.Default);

        BenchmarkSwitcher
            .FromAssembly(assembly: typeof(BenchRunner).Assembly)
            .Run(
            args: benchmarkArguments,
            config: config
        );

        return 0;
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
        var worldCommand = new Command(
            description: "Time the Puck.World.Server tick path with a plain stopwatch harness.",
            name: "world"
        );
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
            """);
        worldCommand.Detail(detail: """
            Boots the shipped puck.world.json and the checked-in Klondike fixture document
            (Bench/klondike.fixture.world.json) and prints one row per number: shipped-world server construction time,
            idle-tick time and quiet-tick allocation (median over a sampled window, after a warmup), and a scripted
            Klondike deal's per-tick time and per-mutation allocation. A server construction against the shipped world
            costs hundreds of milliseconds, past what an iteration-based job can amortize honestly, which is why this
            lane sits beside BenchmarkDotNet rather than inside it.
            """);
        kernelsCommand.SetAction(action: parseResult => Run(benchmarkArguments: (parseResult.GetValue(argument: benchmarkArguments) ?? [])));
        worldCommand.SetAction(action: _ => WorldBenchmarks.Run());

        return command;
    }
}
