using BenchmarkDotNet.Attributes;
using Puck.Maths;

namespace Puck.Cli.Bench;

// The spline's two costs: the exact compile (Sturm isolation, guard-scale roots, the reduced-rational arc table) once
// per track, and the per-frame Evaluate (segment bisection, arc-table inversion, tangent and curvature) many times per
// tick. A closed sixteen-knot loop of gentle curvature keeps every segment inside the compile's admitted envelope.
[MemoryDiagnoser]
public class CurvatureSplineKernels {
    private const int KnotCount = 16;
    private const int SampleCount = 1024;

    private CurvatureSplineKnot[] m_knots = [];
    private CompiledCurvatureSpline m_spline = null!;
    private FixedQ4816[] m_stations = [];

    [GlobalSetup]
    public void Setup() {
        var rng = new Random(Seed: Operands.Seed);

        m_knots = new CurvatureSplineKnot[KnotCount];

        for (var i = 0; (i < KnotCount); ++i) {
            var angle = ((2.0 * Math.PI * i) / KnotCount);
            var radius = (40.0 + rng.NextDouble());

            m_knots[i] = new(
                Curvature: FixedQ4816.FromDouble(value: (1.0 / radius)),
                Elevation: FixedQ4816.FromDouble(value: rng.NextDouble()),
                TangentYaw: FixedQ4816.FromDouble(value: (angle + (Math.PI / 2.0))),
                X: FixedQ4816.FromDouble(value: (radius * Math.Cos(angle))),
                Z: FixedQ4816.FromDouble(value: (radius * Math.Sin(angle)))
            );
        }

        m_spline = CurvatureSpline.Compile(closed: true, knots: m_knots);
        m_stations = new FixedQ4816[SampleCount];

        var total = ((double)m_spline.TotalLength);

        for (var i = 0; (i < SampleCount); ++i) { m_stations[i] = FixedQ4816.FromDouble(value: (rng.NextDouble() * total)); }
    }
    [Benchmark]
    public int Compile() =>
        CurvatureSpline.Compile(closed: true, knots: m_knots).SegmentCount;
    [Benchmark]
    public long Evaluate() {
        var sink = 0L;

        for (var i = 0; (i < SampleCount); ++i) {
            var sample = m_spline.Evaluate(arcLength: m_stations[i]);

            sink ^= (sample.Position.X.Value + sample.Curvature.Value);
        }

        return sink;
    }
}
