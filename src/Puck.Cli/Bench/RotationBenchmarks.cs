using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Diagnosers;
using Puck.Maths;

namespace Puck.Cli.Bench;

// The quaternion members whose kernels carry the most rounding structure: the scale-free FromTo, the great-circle
// Slerp, and the fused logarithm — over unit and near-unit operands of gameplay scale.
[MemoryDiagnoser]
[DisassemblyDiagnoser(maxDepth: 3)]
public class QuaternionKernels {
    private const int Count = 512;

    private FixedVector3[] m_from = [];
    private FixedVector3[] m_to = [];
    private FixedQuaternion[] m_left = [];
    private FixedQuaternion[] m_right = [];
    private FixedQ4816[] m_amounts = [];

    [GlobalSetup]
    public void Setup() {
        var rng = new Random(Seed: Operands.Seed);

        m_from = new FixedVector3[Count];
        m_to = new FixedVector3[Count];
        m_left = new FixedQuaternion[Count];
        m_right = new FixedQuaternion[Count];
        m_amounts = new FixedQ4816[Count];

        for (var i = 0; (i < Count); ++i) {
            m_from[i] = new(X: FixedQ4816.FromRawBits(value: Operands.NarrowRaw(rng: rng)), Y: FixedQ4816.FromRawBits(value: Operands.NarrowRaw(rng: rng)), Z: FixedQ4816.FromRawBits(value: Operands.NarrowRaw(rng: rng)));
            m_to[i] = new(X: FixedQ4816.FromRawBits(value: Operands.NarrowRaw(rng: rng)), Y: FixedQ4816.FromRawBits(value: Operands.NarrowRaw(rng: rng)), Z: FixedQ4816.FromRawBits(value: Operands.NarrowRaw(rng: rng)));
            m_left[i] = FixedQuaternion.FromTo(from: m_from[i], to: m_to[i]);
            m_right[i] = FixedQuaternion.FromTo(from: m_to[i], to: m_from[(Count - 1) - i]);
            m_amounts[i] = FixedQ4816.FromRawBits(value: rng.NextInt64(maxValue: (1L << 16), minValue: 0L));
        }
    }
    [Benchmark]
    public long FromTo() {
        var sink = 0L;

        for (var i = 0; (i < Count); ++i) { sink ^= FixedQuaternion.FromTo(from: m_from[i], to: m_to[i]).W.Value; }

        return sink;
    }
    [Benchmark]
    public long Slerp() {
        var sink = 0L;

        for (var i = 0; (i < Count); ++i) { sink ^= FixedQuaternion.Slerp(amount: m_amounts[i], from: m_left[i], to: m_right[i]).W.Value; }

        return sink;
    }
    [Benchmark]
    public long Log() {
        var sink = 0L;

        for (var i = 0; (i < Count); ++i) { sink ^= m_left[i].Log().X.Value; }

        return sink;
    }
    [Benchmark]
    public long Normalize() {
        var sink = 0L;

        for (var i = 0; (i < Count); ++i) { sink ^= m_from[i].Normalize().X.Value; }

        return sink;
    }
}
