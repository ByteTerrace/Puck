using BenchmarkDotNet.Attributes;
using Puck.Maths;

namespace Puck.Cli.Bench;

[MemoryDiagnoser]
public class NthPrime64Requests {
    [Params(203_280_222UL, 1_000_000_000UL, 10_000_000_000UL, 425_656_284_035_217_743UL)]
    public ulong Ordinal { get; set; }

    [GlobalSetup]
    public void Setup() {
        var expected = Ordinal switch {
            203_280_222 => 4_294_967_311UL,
            1_000_000_000 => 22_801_763_489UL,
            10_000_000_000 => 252_097_800_623UL,
            425_656_284_035_217_743 => 18_446_744_073_709_551_557UL,
            _ => throw new InvalidOperationException(),
        };

        if (Select() != expected) { throw new InvalidOperationException(message: "The ulong rank request differs from its independent reference."); }
    }
    [Benchmark]
    public ulong Select() => (Ordinal - 1UL).NthPrime();
}
