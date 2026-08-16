using System.Runtime.CompilerServices;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Diagnosers;
using Puck.Maths;
using QFixElem = Puck.Maths.QuadraticAlgebra<Puck.Maths.FixedQ4816>.Element;

namespace Puck.Cli.Bench;

// Gate scenario "3a. split mul narrow (latency)": FixedSplit{0,+1}. A unit squeeze alternated with its conjugate keeps
// the dependent chain bounded so the measured kernel is the closed fast path.
[MemoryDiagnoser]
[DisassemblyDiagnoser(maxDepth: 3)]
public class SplitMulNarrow {
    private const int Ops = Bench.LatencyOps;

    private FixedSplit m_s;
    private FixedSplit m_sConj;
    private QFixElem m_eConj;
    private QFixElem m_eStep;

    [GlobalSetup]
    public void Setup() {
        m_s = FixedSplit.FromRapidity(rapidity: FixedQ4816.FromDouble(value: 0.02));
        m_sConj = m_s.Conjugate();
        m_eStep = new QFixElem(U: m_s.U, V: m_s.V);
        m_eConj = new QFixElem(U: m_sConj.U, V: m_sConj.V);
    }
    [Benchmark(Baseline = true, OperationsPerInvoke = Ops)]
    public long Hand() {
        var accumulator = FixedSplit.MultiplicativeIdentity;
        var sink = 0L;

        for (var n = 0; (n < Ops); ++n) {
            accumulator = (accumulator * (((n & 1) == 0) ? m_s : m_sConj));
            sink ^= accumulator.U.Value;
        }

        return sink;
    }
    [Benchmark(OperationsPerInvoke = Ops)]
    public long GenericStatic() {
        var accumulator = new QFixElem(U: FixedQ4816.One, V: FixedQ4816.Zero);
        var sink = 0L;

        for (var n = 0; (n < Ops); ++n) {
            accumulator = Operands.SplitFused.Multiply(left: accumulator, right: (((n & 1) == 0) ? m_eStep : m_eConj));
            sink ^= accumulator.U.Value;
        }

        return sink;
    }
    [Benchmark(OperationsPerInvoke = Ops)]
    public long GenericLocal() =>
        Local(algebra: Operands.SplitFused, conj: m_eConj, step: m_eStep);

    [MethodImpl(methodImplOptions: MethodImplOptions.NoInlining)]
    private static long Local(QuadraticAlgebra<FixedQ4816> algebra, QFixElem step, QFixElem conj) {
        var accumulator = new QFixElem(U: FixedQ4816.One, V: FixedQ4816.Zero);
        var sink = 0L;

        for (var n = 0; (n < Ops); ++n) {
            accumulator = algebra.Multiply(left: accumulator, right: (((n & 1) == 0) ? step : conj));
            sink ^= accumulator.U.Value;
        }

        return sink;
    }
}
// Gate scenario "3b. split norm narrow (throughput)": throughput over an operand array bounded below 2^31, so both
// kernels take a narrow long tier — FixedSplit.Norm's magnitude gate and QuadraticAlgebra.NormFusedInteger's
// (P = 0, |Q| <= 1) tier — and neither reaches its Int128 fallback on these operands. This is the norm-quirk locus that
// motivated the microscope: the generic side's static-readonly callsite is where the widening-multiply recognizer
// failed before those narrow tiers existed. Attach the disassembler and read the asm.
[MemoryDiagnoser]
[DisassemblyDiagnoser(maxDepth: 3)]
public class SplitNormNarrow {
    private FixedSplit[] m_hand = [];
    private QFixElem[] m_gen = [];

    [GlobalSetup]
    public void Setup() {
        var rng = new Random(Seed: Operands.Seed);

        m_hand = new FixedSplit[Operands.WidePairCount];
        m_gen = new QFixElem[Operands.WidePairCount];

        for (var i = 0; (i < Operands.WidePairCount); ++i) {
            var value = new FixedSplit(U: FixedQ4816.FromRawBits(value: Operands.NarrowRaw(rng: rng)), V: FixedQ4816.FromRawBits(value: Operands.NarrowRaw(rng: rng)));

            m_hand[i] = value;
            m_gen[i] = new QFixElem(U: value.U, V: value.V);
        }
    }
    [Benchmark(Baseline = true, OperationsPerInvoke = Operands.WidePairCount)]
    public long Hand() {
        var sink = 0L;

        for (var i = 0; (i < m_hand.Length); ++i) {
            sink ^= m_hand[i].Norm.Value;
        }

        return sink;
    }
    [Benchmark(OperationsPerInvoke = Operands.WidePairCount)]
    public long GenericStatic() {
        var sink = 0L;

        for (var i = 0; (i < m_gen.Length); ++i) {
            sink ^= Operands.SplitFused.Norm(value: m_gen[i]).Value;
        }

        return sink;
    }
    [Benchmark(OperationsPerInvoke = Operands.WidePairCount)]
    public long GenericLocal() =>
        Local(algebra: Operands.SplitFused, values: m_gen);

    [MethodImpl(methodImplOptions: MethodImplOptions.NoInlining)]
    private static long Local(QuadraticAlgebra<FixedQ4816> algebra, QFixElem[] values) {
        var sink = 0L;

        for (var i = 0; (i < values.Length); ++i) {
            sink ^= algebra.Norm(value: values[i]).Value;
        }

        return sink;
    }
}
