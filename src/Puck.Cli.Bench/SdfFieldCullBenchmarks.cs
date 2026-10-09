using System.Numerics;

using BenchmarkDotNet.Attributes;

using Puck.Maths;
using Puck.SignedDistance;
using Puck.SignedDistance.Queries;

namespace Puck.Cli.Bench;

// One distance query at the origin over a field of one near sphere and 4000 far instances of eight union spheres each,
// every instance bounded honestly. Each far instance should cost one bound test; SdfFieldEvaluatorCullLawTests holds
// the cull to running none of the far bodies, and this is where the query's latency is read, on a quiet machine.
[MemoryDiagnoser]
public class SdfFieldCull {
    private const int InstanceCount = 4000;
    private const int ShapesPerInstance = 8;

    private SdfFieldEvaluator? m_evaluator;
    private FixedPosition m_origin;

    /// <summary>Builds the field and its evaluator.</summary>
    [GlobalSetup]
    public void Setup() {
        var builder = new SdfProgramBuilder();
        var material = builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One));

        _ = builder.ResetPoint();
        _ = builder.Translate(offset: new Vector3(
            x: 0f,
            y: 0f,
            z: 3f
        ));
        _ = builder.Sphere(
            blend: SdfBlendOp.Union,
            material: material,
            radius: 0.5f
        );

        for (var index = 0; (index < InstanceCount); index++) {
            var center = new Vector3(
                x: (1000f + (index * 25f)),
                y: 0f,
                z: 0f
            );

            _ = builder.Instance(
                boundCenter: center,
                boundRadius: ((ShapesPerInstance * 1.0f) + 1.0f),
                emit: instance => {
                    for (var shape = 0; (shape < ShapesPerInstance); shape++) {
                        _ = instance.ResetPoint();
                        _ = instance.Translate(offset: (center + new Vector3(
                            x: shape,
                            y: 0f,
                            z: 0f
                        )));
                        _ = instance.Sphere(
                            blend: SdfBlendOp.Union,
                            material: material,
                            radius: 0.4f
                        );
                    }
                }
            );
        }

        m_evaluator = new SdfFieldEvaluator(program: builder.Build());
        m_origin = FixedPosition.FromLocal(local: new FixedVector3(
            X: FixedQ4816.Zero,
            Y: FixedQ4816.Zero,
            Z: FixedQ4816.Zero
        ));
    }
    /// <summary>Answers one distance query at the origin.</summary>
    /// <returns>Whether the field answered.</returns>
    [Benchmark]
    public bool FarInstances4000x8() => m_evaluator!.TryDistance(
        distance: out _,
        material: out _,
        position: m_origin
    );
}
