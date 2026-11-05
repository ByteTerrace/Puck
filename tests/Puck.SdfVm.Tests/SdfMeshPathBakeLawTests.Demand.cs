using System.Numerics;
using Puck.Abstractions.Cameras;
using Puck.Abstractions.Presentation;
using Xunit;

namespace Puck.SdfVm.Tests;

public sealed partial class SdfMeshPathBakeLawTests {
    // A view whose camera stands at a point, looking down -Z.
    private static SdfViewSnapshot ViewAt(Vector3 eye, bool elsewhere = false) => new(
        Camera: new CameraSnapshot(AspectRatio: (16f / 9f), Forward: -Vector3.UnitZ, Position: eye, Right: Vector3.UnitX, TanHalfFieldOfView: 0.5f, Up: Vector3.UnitY),
        Region: new NormalizedRect(Height: 1f, Width: 1f, X: 0f, Y: 0f)
    ) { Elsewhere = elsewhere };

    // The class's shapes plan two groups: the plate-and-block union about the origin first, as a union, then the lone block
    // 8 m along X. A frame with no demand point takes the plan's order; a camera standing at the lone block takes its
    // lattice first; a view another residency renders demands nothing. The meshes are the same whichever order baked them.
    [Fact]
    public void AFrameTakesTheLatticesOfTheGroupsItsViewsSeeLargestFirst() {
        var (program, materials) = Program(shapes: Shapes);
        var union = -1;
        var lone = -3;

        int First(IReadOnlyList<SdfViewSnapshot> views) {
            using var bakeRun = new MeshBakeTestRun(new SdfMeshPathBake { Residency = "demand" });
            var bake = bakeRun.Bake;
            var dresser = bakeRun.Dresser;
            var frame = new SdfFrame(Program: program, ProgramChanged: true, Time: 0f, Views: views) { FarDistance = 100f };

            bake.Demand.Observe(frame: frame, height: 1080);
            _ = dresser.Dress(frame: frame);
            Assert.True(condition: bake.TryTake(batch: out var batch, field: bake.Program));
            Assert.Equal(expected: 2, actual: batch.Lattices.Length);

            return batch.Lattices[0].Selector;
        }

        Assert.Equal(expected: union, actual: First(views: []));
        Assert.Equal(expected: lone, actual: First(views: [ViewAt(eye: new Vector3(x: 8f, y: 1.2f, z: 1f))]));
        Assert.Equal(expected: union, actual: First(views: [ViewAt(eye: new Vector3(x: -1f, y: 1.5f, z: 2f)), ViewAt(eye: new Vector3(x: 30f, y: 1.5f, z: 2f))]));
        Assert.Equal(expected: union, actual: First(views: [ViewAt(elsewhere: true, eye: new Vector3(x: 8f, y: 1.2f, z: 1f))]));

        using var nearRun = new MeshBakeTestRun(new SdfMeshPathBake { Residency = "near" });

        var near = nearRun.Bake;

        var nearDresser = nearRun.Dresser;
        var frame = new SdfFrame(Program: program, ProgramChanged: true, Time: 0f, Views: [ViewAt(eye: new Vector3(x: 8f, y: 1.2f, z: 1f))]) { FarDistance = 100f };

        near.Demand.Observe(frame: frame, height: 1080);
        Drive(bake: near, dresser: nearDresser, frame: frame, materials: materials, shapes: Shapes);

        var expected = m_baked.Value.Run.Dresser.Dress(frame: m_baked.Value.Frame).MeshDraws;
        var actual = nearDresser.Dress(frame: frame).MeshDraws;

        Assert.Equal(expected: expected.Count, actual: actual.Count);
        Assert.All(collection: expected, action: draw => Assert.Contains(collection: actual, filter: other => other.Mesh.Indices.Span.SequenceEqual(other: draw.Mesh.Indices.Span) && other.Mesh.Positions.Span.SequenceEqual(other: draw.Mesh.Positions.Span)));
        // The lone block's box lies within 3 m of the camera, the union's beyond.
        Assert.Contains(expectedSubstring: "demand-points=1 groups=2", actualString: near.DescribeResidency(radius: 3f));
        Assert.Contains(expectedSubstring: "near=1 ", actualString: near.DescribeResidency(radius: 3f));
        Assert.Contains(expectedSubstring: "near=2 ", actualString: near.DescribeResidency(radius: 100f));
    }
    // A box whose diagonal projects past a pixel ranks by the pixels a metre covers at its distance, no nearer than the
    // near distance, whatever its size; one under a pixel ranks below every such box by the share it covers; the point
    // that ranks a box highest wins, and a hidden view demands nothing.
    [Fact]
    public void ADemandPointRanksABoxByThePixelsAMetreCoversThere() {
        var demand = new SdfMeshDemand();
        var frame = new SdfFrame(Program: new Puck.SignedDistance.SdfProgramBuilder().Build(), ProgramChanged: false, Time: 0f, Views: [
            ViewAt(eye: Vector3.Zero),
            ViewAt(eye: new Vector3(x: 100f, y: 0f, z: 0f)) with { Region = new NormalizedRect(Height: 0f, Width: 0f, X: 0f, Y: 0f) },
        ]);

        Assert.Equal(expected: 0f, actual: demand.Priority(center: Vector3.Zero, half: Vector3.UnitZ));
        Assert.True(condition: float.IsPositiveInfinity(f: demand.Distance(center: Vector3.Zero, half: Vector3.UnitZ)));
        demand.Observe(frame: frame, height: 1000);
        Assert.Equal(expected: 1, actual: demand.Count);
        Assert.Equal(expected: Vector3.Zero, actual: demand.Point(index: 0));

        // 500 pixels from the axis to the frame's top over a half-angle tangent of one half: 1000 pixels a metre at 1 m,
        // so a box one metre off ranks 1001 whether its diagonal is two metres or two centimetres.
        Assert.Equal(expected: 1001f, actual: demand.Priority(center: new Vector3(x: 0f, y: 0f, z: -2f), half: Vector3.UnitZ), tolerance: 0.01f);
        Assert.Equal(expected: 1001f, actual: demand.Priority(center: new Vector3(x: 0f, y: 0f, z: -1.01f), half: new Vector3(x: 0f, y: 0f, z: 0.01f)), tolerance: 0.01f);
        Assert.Equal(expected: (1f + (1000f / SdfMeshDemand.NearDistance)), actual: demand.Priority(center: Vector3.Zero, half: Vector3.UnitZ), tolerance: 0.01f);
        // A tenth of a millimetre 100 m off covers a fiftieth of a pixel.
        Assert.Equal(expected: 0.002f, actual: demand.Priority(center: new Vector3(x: 0f, y: 0f, z: -100f), half: new Vector3(x: 0.0001f, y: 0f, z: 0f)), tolerance: 0.0001f);
        Assert.Equal(expected: 99f, actual: demand.Distance(center: new Vector3(x: 100f, y: 0f, z: 0f), half: Vector3.UnitX), tolerance: 0.001f);

        // A draw's box is its host box's: a unit triangle's, moved and doubled by its matrix.
        var triangle = new SdfMesh(indices: new uint[] { 0, 1, 2 }, positions: new Vector3[] { Vector3.Zero, Vector3.UnitX, Vector3.UnitY });
        var draw = new SdfMeshDraw(Identity: "triangle", Material: 0, Mesh: triangle, ObjectToWorld: (Matrix4x4.CreateScale(scale: 2f) * Matrix4x4.CreateTranslation(position: new Vector3(x: 4f, y: 0f, z: 0f))));

        Assert.True(condition: SdfMeshDemand.BoxOf(center: out var center, draw: in draw, half: out var half));
        Assert.Equal(expected: new Vector3(x: 5f, y: 1f, z: 0f), actual: center);
        Assert.Equal(expected: new Vector3(x: 1f, y: 1f, z: 0f), actual: half);
    }
}
