using System.CommandLine;

using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Diagnosers;
using BenchmarkDotNet.Running;

namespace Puck.Cli.Bench;

// The `puck bench` verb: the on-demand Puck.Maths microscope, plus the `world` and `startup` lanes, which run
// outside BenchmarkDotNet entirely.
//
// BenchmarkDotNet owns the whole option grammar apart from those sub-verbs, so it arrives as one unparsed
// string[] the switcher reads itself: puck neither validates nor rewrites a token of it. A token equal to a sub-verb
// anywhere on the line selects the lane, so it can never reach the switcher as an option value.
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

    public static Command Create() {
        var benchmarkArguments = new Argument<string[]>(name: "benchmark-arguments") {
            Arity = ArgumentArity.ZeroOrMore,
            Description = "Forwarded to BenchmarkDotNet's switcher verbatim: --filter, --job, --list, --runtimes, --hide, and the rest of its grammar.",
        };
        var worldCommand = new Command(
            description: """
            The Puck.World.Server tick-path lane: a plain stopwatch harness, not a BenchmarkDotNet job.

            Boots the shipped puck.world.json and the checked-in Klondike fixture document
            (Bench/klondike.fixture.world.json) and prints one row per number — shipped-world server construction
            time, idle-tick time and quiet-tick allocation (median over a sampled window, after a warmup), and a
            scripted Klondike deal's per-tick time and per-mutation allocation. A server construction against the
            shipped world costs hundreds of milliseconds, past what an iteration-based job can amortize honestly, which is
            why this lane sits beside the switcher rather than inside it.
            """,
            name: "world"
        );
        var command = new Command(
            description: """
            The Puck.Maths micro-benchmark microscope (BenchmarkDotNet).

              puck bench --filter '*Norm*'   the scenarios matching a glob
              puck bench --list flat         the scenarios the switcher sees
              puck bench world               the Puck.World.Server tick-path stopwatch lane
              puck bench startup             fresh-process world readiness and rendered capture timing
              puck bench state-evidence      replay the cost schedule's pinned llvm-mca instruction evidence

            Every argument outside the `world` and `startup` sub-verbs reaches BenchmarkDotNet's switcher unchanged. Job rigor is
            chosen there and layered over the base config puck supplies:

              (no --job)    BenchmarkDotNet's adaptive default — the balanced everyday setting
              --job short   fast survey (fewer warmup/target iterations), a shape-of-the-numbers pass
              --job long    thorough verdict (many iterations, tight error bars) before a retention decision

            puck's own help answers -h and --help here, so BenchmarkDotNet's (-h is its `hide` option) are reached
            past a `--` separator: `puck bench -- --help`.
            """,
            name: "bench"
        ) { benchmarkArguments, worldCommand, StartupBenchmarks.Create(), StateEvidenceCommand.Create() };

        // Option-shaped tokens are the switcher's, not misspellings.
        command.TreatUnmatchedTokensAsErrors = false;
        worldCommand.SetAction(action: _ => WorldBenchmarks.Run());
        command.SetAction(action: parseResult => Run(benchmarkArguments: (parseResult.GetValue(argument: benchmarkArguments) ?? [])));

        return command;
    }
}
