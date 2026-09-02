using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Diagnosers;
using BenchmarkDotNet.Running;

namespace Puck.Cli.Bench;

// The `puck bench` verb: the on-demand Puck.Maths microscope. The original algebra classes remain named 1:1 after
// scenarios of the standalone quadratic-algebra bench, which no longer builds; workload-specific classes cover
// transforms and other Maths kernels. Of the retired grid only the complex-multiply ratio is still measured
// automatically, by BenchTests in tests/Puck.Maths.Tests.
//
//   puck bench --filter '*Norm*'
//
// Job rigor is chosen on the command line and layered over the base config below:
//   (default, no --job) : BenchmarkDotNet's adaptive default — the sane balanced setting for everyday runs.
//   --job short         : fast survey (fewer warmup/target iterations) for a quick shape-of-the-numbers pass.
//   --job long          : thorough verdict (many iterations, tight error bars) before a retention decision.
internal static class BenchRunner {
    public static int Run(string[] args) {
        // MemoryDiagnoser rides on every scenario: these kernels are meant to be zero-alloc, and its byte columns are
        // the only thing that reports whether they are. DisassemblyDiagnoser is attached per-class, only on the
        // fixed-point kernels, via attribute — the extension scenario is modular-ulong arithmetic, not a fixed-point
        // kernel. No explicit job is added here so a command-line --job is the single job that runs (adding one here
        // would run alongside it and double the output).
        var config = ManualConfig
            .Create(config: DefaultConfig.Instance)
            .AddDiagnoser(newDiagnosers: MemoryDiagnoser.Default);

        BenchmarkSwitcher
            .FromAssembly(assembly: typeof(BenchRunner).Assembly)
            .Run(args: args, config: config);

        return 0;
    }
}
