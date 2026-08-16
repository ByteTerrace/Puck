using System.Runtime.CompilerServices;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Diagnosers;
using Puck.Maths;
using QFixElem = Puck.Maths.QuadraticAlgebra<Puck.Maths.FixedQ4816>.Element;
using QQuatElem = Puck.Maths.QuadraticAlgebra<Puck.Maths.FixedQuaternion>.Element;

namespace Puck.Cli.Bench;

// Gate scenario "4. dual<FixedQ4816> mul (latency)": the dual relation {0,0} over FixedQ4816, so the generic takes
// QuadraticAlgebra's integer lane. The chain holds every raw far below 2^31, so the narrow long tier runs; its gate
// requires an integer P of zero, which drops the P*root term outright, while the Q*root term survives as v1*v2 scaled
// by the zero coefficient. Hand (FixedDual's fused scalar kernel) never forms v1*v2 at all. The row therefore contrasts
// a kernel specialized on the relation against one carrying the relation as data, and the Static/Local pair separates
// the static readonly descriptor, whose coefficients the JIT may fold, from the parameter form, where it cannot.
[MemoryDiagnoser]
[DisassemblyDiagnoser(maxDepth: 3)]
public class DualFixMul {
    private const int Ops = Bench.LatencyOps;

    private FixedDual<FixedQ4816> m_seed;
    private FixedDual<FixedQ4816> m_step;
    private QFixElem m_eSeed;
    private QFixElem m_eStep;

    [GlobalSetup]
    public void Setup() {
        m_seed = new FixedDual<FixedQ4816>(Real: FixedQ4816.One, Dual: FixedQ4816.FromDouble(value: 0.5));
        m_step = new FixedDual<FixedQ4816>(Real: FixedQ4816.One, Dual: FixedQ4816.FromRawBits(value: 1L));
        m_eSeed = new QFixElem(U: m_seed.Real, V: m_seed.Dual);
        m_eStep = new QFixElem(U: m_step.Real, V: m_step.Dual);
    }
    [Benchmark(Baseline = true, OperationsPerInvoke = Ops)]
    public long Hand() {
        var accumulator = m_seed;
        var sink = 0L;

        for (var n = 0; (n < Ops); ++n) {
            accumulator = (accumulator * m_step);
            sink ^= accumulator.Dual.Value;
        }

        return sink;
    }
    [Benchmark(OperationsPerInvoke = Ops)]
    public long GenericStatic() {
        var accumulator = m_eSeed;
        var sink = 0L;

        for (var n = 0; (n < Ops); ++n) {
            accumulator = Operands.DualFix.Multiply(left: accumulator, right: m_eStep);
            sink ^= accumulator.V.Value;
        }

        return sink;
    }
    [Benchmark(OperationsPerInvoke = Ops)]
    public long GenericLocal() =>
        Local(algebra: Operands.DualFix, seed: m_eSeed, step: m_eStep);

    [MethodImpl(methodImplOptions: MethodImplOptions.NoInlining)]
    private static long Local(QuadraticAlgebra<FixedQ4816> algebra, QFixElem seed, QFixElem step) {
        var accumulator = seed;
        var sink = 0L;

        for (var n = 0; (n < Ops); ++n) {
            accumulator = algebra.Multiply(left: accumulator, right: step);
            sink ^= accumulator.V.Value;
        }

        return sink;
    }
}
// Gate scenario "5. dual quaternion mul (latency)": the decisive shape, at equal multiply count. The generic's {0,0}
// coefficients hit the degeneracy short-circuit, so it returns U1*U2 and U1*V2 + V1*U2 — three carrier Hamilton
// products — without forming the root product or either coefficient term; Hand is FixedDual's fused quaternion kernel,
// one Hamilton product for the real part plus the eight-leaf seam accumulation standing in for the other two. The row
// therefore measures structure rather than multiply count: three carrier operator invocations and two carrier additions,
// every component rounded once per product, against a raw-level accumulation that rounds each dual component once for
// the whole sum. Step's dual part is zero, so the chain stays in-regime while every Hamilton product still executes.
[MemoryDiagnoser]
[DisassemblyDiagnoser(maxDepth: 3)]
public class DualQuaternionMul {
    private const int Ops = Bench.LatencyOps;

    private FixedDual<FixedQuaternion> m_seed;
    private FixedDual<FixedQuaternion> m_step;
    private QQuatElem m_eSeed;
    private QQuatElem m_eStep;

    [GlobalSetup]
    public void Setup() {
        var rotSeed = FixedQuaternion.FromAxisAngle(axis: new FixedVector3(X: FixedQ4816.Zero, Y: FixedQ4816.One, Z: FixedQ4816.Zero), angle: FixedQ4816.FromDouble(value: 0.6));
        var dualSeed = FixedQuaternion.FromAxisAngle(axis: new FixedVector3(X: FixedQ4816.One, Y: FixedQ4816.Zero, Z: FixedQ4816.Zero), angle: FixedQ4816.FromDouble(value: 0.3));
        var rotStep = FixedQuaternion.FromAxisAngle(axis: new FixedVector3(X: FixedQ4816.Zero, Y: FixedQ4816.Zero, Z: FixedQ4816.One), angle: FixedQ4816.FromDouble(value: 0.02));

        m_seed = new FixedDual<FixedQuaternion>(Dual: dualSeed, Real: rotSeed);
        m_step = new FixedDual<FixedQuaternion>(Real: rotStep, Dual: FixedQuaternion.AdditiveIdentity);
        m_eSeed = new QQuatElem(U: m_seed.Real, V: m_seed.Dual);
        m_eStep = new QQuatElem(U: m_step.Real, V: m_step.Dual);
    }
    [Benchmark(Baseline = true, OperationsPerInvoke = Ops)]
    public long Hand() {
        var accumulator = m_seed;
        var sink = 0L;

        for (var n = 0; (n < Ops); ++n) {
            accumulator = (accumulator * m_step);
            sink ^= accumulator.Dual.W.Value;
        }

        return sink;
    }
    [Benchmark(OperationsPerInvoke = Ops)]
    public long GenericStatic() {
        var accumulator = m_eSeed;
        var sink = 0L;

        for (var n = 0; (n < Ops); ++n) {
            accumulator = Operands.DualQuat.Multiply(left: accumulator, right: m_eStep);
            sink ^= accumulator.V.W.Value;
        }

        return sink;
    }
    [Benchmark(OperationsPerInvoke = Ops)]
    public long GenericLocal() =>
        Local(algebra: Operands.DualQuat, seed: m_eSeed, step: m_eStep);

    [MethodImpl(methodImplOptions: MethodImplOptions.NoInlining)]
    private static long Local(QuadraticAlgebra<FixedQuaternion> algebra, QQuatElem seed, QQuatElem step) {
        var accumulator = seed;
        var sink = 0L;

        for (var n = 0; (n < Ops); ++n) {
            accumulator = algebra.Multiply(left: accumulator, right: step);
            sink ^= accumulator.V.W.Value;
        }

        return sink;
    }
}
