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

    private FixedComplex _rot;
    private FixedComplex _seed;
    private QFixElem _eSeed;
    private QFixElem _eStep;

    [GlobalSetup]
    public void Setup() {
        _seed = FixedComplex.FromAngle(angle: FixedQ4816.FromDouble(value: 0.3));
        _rot = FixedComplex.FromAngle(angle: FixedQ4816.FromDouble(value: 0.017));
        _eSeed = new QFixElem(U: _seed.Real, V: _seed.Imaginary);
        _eStep = new QFixElem(U: _rot.Real, V: _rot.Imaginary);
    }
    [Benchmark(Baseline = true, OperationsPerInvoke = Ops)]
    public long Hand() {
        var accumulator = _seed;
        var sink = 0L;

        for (var n = 0; (n < Ops); ++n) {
            accumulator = (accumulator * _rot);
            sink ^= accumulator.Real.Value;
        }

        return sink;
    }
    [Benchmark(OperationsPerInvoke = Ops)]
    public long GenericStatic() {
        var accumulator = _eSeed;
        var sink = 0L;

        for (var n = 0; (n < Ops); ++n) {
            accumulator = Operands.ComplexFused.Multiply(left: accumulator, right: _eStep);
            sink ^= accumulator.U.Value;
        }

        return sink;
    }
    [Benchmark(OperationsPerInvoke = Ops)]
    public long GenericLocal() =>
        Local(algebra: Operands.ComplexFused, seed: _eSeed, step: _eStep);

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
    private FixedComplex[] _handA = [];
    private FixedComplex[] _handB = [];
    private QFixElem[] _genA = [];
    private QFixElem[] _genB = [];

    [GlobalSetup]
    public void Setup() {
        var rng = new Random(Seed: Operands.Seed);

        _handA = new FixedComplex[Operands.WidePairCount];
        _handB = new FixedComplex[Operands.WidePairCount];
        _genA = new QFixElem[Operands.WidePairCount];
        _genB = new QFixElem[Operands.WidePairCount];

        for (var i = 0; (i < Operands.WidePairCount); ++i) {
            var a = new FixedComplex(Real: FixedQ4816.FromRawBits(value: Operands.WideRaw(rng: rng)), Imaginary: FixedQ4816.FromRawBits(value: Operands.WideRaw(rng: rng)));
            var b = new FixedComplex(Real: FixedQ4816.FromRawBits(value: Operands.WideRaw(rng: rng)), Imaginary: FixedQ4816.FromRawBits(value: Operands.WideRaw(rng: rng)));

            _handA[i] = a;
            _handB[i] = b;
            _genA[i] = new QFixElem(U: a.Real, V: a.Imaginary);
            _genB[i] = new QFixElem(U: b.Real, V: b.Imaginary);
        }
    }
    [Benchmark(Baseline = true, OperationsPerInvoke = Operands.WidePairCount)]
    public long Hand() {
        var sink = 0L;

        for (var i = 0; (i < _handA.Length); ++i) {
            sink ^= (_handA[i] * _handB[i]).Real.Value;
        }

        return sink;
    }
    [Benchmark(OperationsPerInvoke = Operands.WidePairCount)]
    public long GenericStatic() {
        var sink = 0L;

        for (var i = 0; (i < _genA.Length); ++i) {
            sink ^= Operands.ComplexFused.Multiply(left: _genA[i], right: _genB[i]).U.Value;
        }

        return sink;
    }
    [Benchmark(OperationsPerInvoke = Operands.WidePairCount)]
    public long GenericLocal() =>
        Local(algebra: Operands.ComplexFused, a: _genA, b: _genB);

    [MethodImpl(methodImplOptions: MethodImplOptions.NoInlining)]
    private static long Local(QuadraticAlgebra<FixedQ4816> algebra, QFixElem[] a, QFixElem[] b) {
        var sink = 0L;

        for (var i = 0; (i < a.Length); ++i) {
            sink ^= algebra.Multiply(left: a[i], right: b[i]).U.Value;
        }

        return sink;
    }
}
