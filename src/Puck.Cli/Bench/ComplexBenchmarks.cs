using System.Runtime.CompilerServices;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Diagnosers;
using Puck.Maths;
using QFixElem = Puck.Maths.QuadraticAlgebra<Puck.Maths.FixedQ4816>.Element;

namespace Puck.Cli.Bench;

// Gate scenario "1. complex mul narrow (latency)": a unit-rotation dependent chain (raw ~2^16, the fast path).
// FixedComplex.operator* vs QuadraticAlgebra<FixedQ4816>{0,-1}.Multiply, generic held statically and locally.
[MemoryDiagnoser]
[DisassemblyDiagnoser(maxDepth: 3)]
public class ComplexMulNarrow {
    private const int Ops = Bench.LatencyOps;

    private FixedComplex m_rot;
    private FixedComplex m_seed;
    private QFixElem m_eSeed;
    private QFixElem m_eStep;

    [GlobalSetup]
    public void Setup() {
        m_seed = FixedComplex.FromAngle(angle: FixedQ4816.FromDouble(value: 0.3));
        m_rot = FixedComplex.FromAngle(angle: FixedQ4816.FromDouble(value: 0.017));
        m_eSeed = new QFixElem(U: m_seed.Real, V: m_seed.Imaginary);
        m_eStep = new QFixElem(U: m_rot.Real, V: m_rot.Imaginary);
    }
    [Benchmark(Baseline = true, OperationsPerInvoke = Ops)]
    public long Hand() {
        var accumulator = m_seed;
        var sink = 0L;

        for (var n = 0; (n < Ops); ++n) {
            accumulator = (accumulator * m_rot);
            sink ^= accumulator.Real.Value;
        }

        return sink;
    }
    [Benchmark(OperationsPerInvoke = Ops)]
    public long GenericStatic() {
        var accumulator = m_eSeed;
        var sink = 0L;

        for (var n = 0; (n < Ops); ++n) {
            accumulator = Operands.ComplexFused.Multiply(left: accumulator, right: m_eStep);
            sink ^= accumulator.U.Value;
        }

        return sink;
    }
    [Benchmark(OperationsPerInvoke = Ops)]
    public long GenericLocal() =>
        Local(algebra: Operands.ComplexFused, seed: m_eSeed, step: m_eStep);

    [MethodImpl(methodImplOptions: MethodImplOptions.NoInlining)]
    private static long Local(QuadraticAlgebra<FixedQ4816> algebra, QFixElem seed, QFixElem step) {
        var accumulator = seed;
        var sink = 0L;

        for (var n = 0; (n < Ops); ++n) {
            accumulator = algebra.Multiply(left: accumulator, right: step);
            sink ^= accumulator.U.Value;
        }

        return sink;
    }
}
// Gate scenario "2. complex mul wide (throughput)": raw >= 2^31 forces the Int128 path; throughput over operand pairs.
[MemoryDiagnoser]
[DisassemblyDiagnoser(maxDepth: 3)]
public class ComplexMulWide {
    private FixedComplex[] m_handA = [];
    private FixedComplex[] m_handB = [];
    private QFixElem[] m_genA = [];
    private QFixElem[] m_genB = [];

    [GlobalSetup]
    public void Setup() {
        var rng = new Random(Seed: Operands.Seed);

        m_handA = new FixedComplex[Operands.WidePairCount];
        m_handB = new FixedComplex[Operands.WidePairCount];
        m_genA = new QFixElem[Operands.WidePairCount];
        m_genB = new QFixElem[Operands.WidePairCount];

        for (var i = 0; (i < Operands.WidePairCount); ++i) {
            var a = new FixedComplex(Real: FixedQ4816.FromRawBits(value: Operands.WideRaw(rng: rng)), Imaginary: FixedQ4816.FromRawBits(value: Operands.WideRaw(rng: rng)));
            var b = new FixedComplex(Real: FixedQ4816.FromRawBits(value: Operands.WideRaw(rng: rng)), Imaginary: FixedQ4816.FromRawBits(value: Operands.WideRaw(rng: rng)));

            m_handA[i] = a;
            m_handB[i] = b;
            m_genA[i] = new QFixElem(U: a.Real, V: a.Imaginary);
            m_genB[i] = new QFixElem(U: b.Real, V: b.Imaginary);
        }
    }
    [Benchmark(Baseline = true, OperationsPerInvoke = Operands.WidePairCount)]
    public long Hand() {
        var sink = 0L;

        for (var i = 0; (i < m_handA.Length); ++i) {
            sink ^= (m_handA[i] * m_handB[i]).Real.Value;
        }

        return sink;
    }
    [Benchmark(OperationsPerInvoke = Operands.WidePairCount)]
    public long GenericStatic() {
        var sink = 0L;

        for (var i = 0; (i < m_genA.Length); ++i) {
            sink ^= Operands.ComplexFused.Multiply(left: m_genA[i], right: m_genB[i]).U.Value;
        }

        return sink;
    }
    [Benchmark(OperationsPerInvoke = Operands.WidePairCount)]
    public long GenericLocal() =>
        Local(a: m_genA, algebra: Operands.ComplexFused, b: m_genB);

    [MethodImpl(methodImplOptions: MethodImplOptions.NoInlining)]
    private static long Local(QuadraticAlgebra<FixedQ4816> algebra, QFixElem[] a, QFixElem[] b) {
        var sink = 0L;

        for (var i = 0; (i < a.Length); ++i) {
            sink ^= algebra.Multiply(left: a[i], right: b[i]).U.Value;
        }

        return sink;
    }
}
