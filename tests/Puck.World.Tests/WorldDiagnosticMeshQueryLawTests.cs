using System.Numerics;
using Puck.SdfVm;
using Xunit;

namespace Puck.World.Tests;

/// <summary>A ray hitting another surface never certifies a discrepancy at the queried boundary.</summary>
public sealed class WorldDiagnosticMeshQueryLawTests {
    [Fact]
    public void ADistantSurfaceRayHitIsUnknownNeverRedOrClean() {
        var scene = Scene(gap: 17.7f);

        Assert.True(condition: scene.Intersect(Vector3.Zero, -Vector3.UnitY, 20, out _, out _, out _));
        Assert.Null(@object: WorldDiagnosticMeshQuery.Measure(scene, Vector3.Zero, new(0, 0, 0.001), 0.01));
    }
    [Fact]
    public void ANearbyEdgeCertifiesCleanEvenWhenTheNormalRayMissesIt() {
        var mesh = new SdfMesh(new Vector3[] { new(x: 0.001f, y: 0, z: -1), new(x: 1, y: 0, z: -1), new(x: 0.001f, y: 0, z: 1) }, new uint[] { 0, 2, 1 }) {
            BakeCell = new Vector3(value: 0.02f),
        };
        var scene = SdfMeshIrradianceScene.Build([new(mesh, Matrix4x4.Identity, 0, new object())])!;

        Assert.False(condition: scene.Intersect(Vector3.Zero, -Vector3.UnitY, 20, out _, out _, out _));
        var sample = WorldDiagnosticMeshQuery.Measure(scene, Vector3.Zero, new(0, 0, 0.0001), 0.02);

        Assert.NotNull(@object: sample);
        Assert.False(condition: sample.IsRed);
        Assert.Equal(0.001, sample.Distance, 6);
    }
    [Fact]
    public void ACertifiedLocalDiscrepancyIsRedWithoutAddingSearchReachToItsBudget() {
        var sample = WorldDiagnosticMeshQuery.Measure(Scene(gap: 0.025f), Vector3.Zero, new(0, 0, 0.001), 0.01);

        Assert.NotNull(@object: sample);
        Assert.True(condition: sample.IsRed);
        Assert.Equal(0.025, sample.Distance, 6);
        Assert.Equal(0.011, sample.Budget.Total, 6);
    }
    [Fact]
    public void AZeroDistanceSurfaceIsEvaluatedClean() {
        var sample = WorldDiagnosticMeshQuery.Measure(Scene(gap: 0), Vector3.Zero, new(0, 0, 0.001), 0.01);

        Assert.NotNull(@object: sample);
        Assert.False(condition: sample.IsRed);
        Assert.Equal(0, sample.Distance);
    }
    [Fact]
    public void AShapeBoundExplainsOnlyAnInwardMeshSeparation() {
        var budget = new Server.WorldDiagnosticBudget(0, 0, 0.001) { ConservativeInset = 0.03 };
        var inside = WorldDiagnosticMeshQuery.Measure(Scene(gap: -0.025f), Vector3.Zero, budget, 0.01);

        Assert.NotNull(@object: inside);
        Assert.True(condition: inside.Inside);
        Assert.False(condition: inside.IsRed);
        var outside = WorldDiagnosticMeshQuery.Measure(Scene(gap: 0.025f), Vector3.Zero, budget, 0.01);

        Assert.NotNull(@object: outside);
        Assert.False(condition: outside.Inside);
        Assert.True(condition: outside.IsRed);
        var beyond = WorldDiagnosticMeshQuery.Measure(Scene(gap: -0.05f), Vector3.Zero, budget, 0.01);

        Assert.NotNull(@object: beyond);
        Assert.True(condition: beyond.IsRed);
    }

    private static SdfMeshIrradianceScene Scene(float gap) {
        var mesh = new SdfMesh(new Vector3[] { new(x: -1, y: -gap, z: -1), new(x: 1, y: -gap, z: -1), new(x: 0, y: -gap, z: 1) }, new uint[] { 0, 2, 1 }) {
            BakeCell = new Vector3(x: 0.02f, y: 0, z: 0),
        };

        return SdfMeshIrradianceScene.Build([new(mesh, Matrix4x4.Identity, 0, new object())])!;
    }
}
