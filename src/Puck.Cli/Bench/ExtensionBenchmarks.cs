using System.Runtime.CompilerServices;
using BenchmarkDotNet.Attributes;
using Puck.Maths;
using XElem = Puck.Maths.QuadraticExtensionField64.Element;
using ModPElem = Puck.Maths.QuadraticAlgebra<Puck.Cli.Bench.ModP>.Element;

namespace Puck.Cli.Bench;

// Gate scenario "6a. extension mul (latency)": QuadraticExtensionField64.Multiply vs QuadraticAlgebra<ModP>.Multiply
// over F_p. Modular-ulong arithmetic, not a fixed-point kernel, so MemoryDiagnoser only (no disassembler).
[MemoryDiagnoser]
public class ExtensionMul {
    private const int Ops = Bench.LatencyOps;

    private QuadraticExtensionField64 m_extension;
    private XElem m_xSeed;
    private XElem m_xStep;
    private ModPElem m_eSeed;
    private ModPElem m_eStep;

    [GlobalSetup]
    public void Setup() {
        var field = PrimeField64.Create(modulus: Operands.Modulus);

        m_extension = QuadraticExtensionField64.CreateCanonical(baseField: field);
        m_xSeed = new XElem(A: 123_456_789UL, B: 987_654_321UL);
        m_xStep = new XElem(A: 424_242_424UL, B: 111_111_113UL);
        m_eSeed = new ModPElem(U: new ModP(Value: m_xSeed.A, Modulus: Operands.Modulus), V: new ModP(Value: m_xSeed.B, Modulus: Operands.Modulus));
        m_eStep = new ModPElem(U: new ModP(Value: m_xStep.A, Modulus: Operands.Modulus), V: new ModP(Value: m_xStep.B, Modulus: Operands.Modulus));
    }
    [Benchmark(Baseline = true, OperationsPerInvoke = Ops)]
    public long Hand() {
        var accumulator = m_xSeed;
        var sink = 0L;

        for (var n = 0; (n < Ops); ++n) {
            accumulator = m_extension.Multiply(left: accumulator, right: m_xStep);
            sink ^= unchecked((long)accumulator.B);
        }

        return sink;
    }
    [Benchmark(OperationsPerInvoke = Ops)]
    public long GenericStatic() {
        var accumulator = m_eSeed;
        var sink = 0L;

        for (var n = 0; (n < Ops); ++n) {
            accumulator = Operands.ModAlg.Multiply(left: accumulator, right: m_eStep);
            sink ^= unchecked((long)accumulator.V.Value);
        }

        return sink;
    }
    [Benchmark(OperationsPerInvoke = Ops)]
    public long GenericLocal() =>
        Local(algebra: Operands.ModAlg, seed: m_eSeed, step: m_eStep);

    [MethodImpl(methodImplOptions: MethodImplOptions.NoInlining)]
    private static long Local(QuadraticAlgebra<ModP> algebra, ModPElem seed, ModPElem step) {
        var accumulator = seed;
        var sink = 0L;

        for (var n = 0; (n < Ops); ++n) {
            accumulator = algebra.Multiply(left: accumulator, right: step);
            sink ^= unchecked((long)accumulator.V.Value);
        }

        return sink;
    }
}
// Gate scenario "6b. extension-only operations": Frobenius and BatchInverse have NO QuadraticAlgebra<T> counterpart —
// a structural retention gap, not a perf gap. Measured hand-only so the microscope covers the gate 1:1.
[MemoryDiagnoser]
public class ExtensionOnly {
    private const int Ops = Bench.LatencyOps;

    private QuadraticExtensionField64 m_extension;
    private XElem m_seed;
    private XElem[] m_batch = [];

    [GlobalSetup]
    public void Setup() {
        var rng = new Random(Seed: Operands.Seed);

        m_extension = QuadraticExtensionField64.CreateCanonical(baseField: PrimeField64.Create(modulus: Operands.Modulus));
        m_seed = new XElem(A: 123_456_789UL, B: 987_654_321UL);
        m_batch = new XElem[Operands.WidePairCount];

        for (var i = 0; (i < Operands.WidePairCount); ++i) {
            // Non-zero base-field parts guarantee non-zero norm for every element (required by BatchInverse).
            m_batch[i] = new XElem(A: Operands.RandomResidue(rng: rng) | 1UL, B: Operands.RandomResidue(rng: rng));
        }
    }
    [Benchmark(OperationsPerInvoke = Ops)]
    public long Frobenius() {
        var accumulator = m_seed;
        var sink = 0L;

        for (var n = 0; (n < Ops); ++n) {
            accumulator = m_extension.Frobenius(value: accumulator);
            sink ^= unchecked((long)accumulator.B);
        }

        return sink;
    }
    [Benchmark(OperationsPerInvoke = Operands.WidePairCount)]
    public long BatchInverse() {
        // Inverting the inverses returns the original set, so the span stays non-zero across invocations with no reseed.
        m_extension.BatchInverse(values: m_batch.AsSpan());

        return unchecked((long)m_batch[0].A);
    }
}
