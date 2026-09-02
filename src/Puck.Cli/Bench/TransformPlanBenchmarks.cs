using BenchmarkDotNet.Attributes;
using Puck.Maths;

namespace Puck.Cli.Bench;

// Plan construction is intentionally separate from transform latency: it allocates and builds every reusable
// twiddle table, while Forward/Inverse are allocation-free calls over a plan built in GlobalSetup.
[MemoryDiagnoser]
public class TransformPlanCreation {
    [Params(256, 16384)]
    public int Length { get; set; }

    [Benchmark]
    public NumberTheoreticTransformPlan Ntt() => NumberTheoreticTransformPlan.Create(length: Length);
    [Benchmark]
    public FixedFourierTransformPlan Fft() => FixedFourierTransformPlan.Create(length: Length);
    [Benchmark]
    public FixedCosineTransformPlan Dct() => FixedCosineTransformPlan.Create(length: Length);
}
