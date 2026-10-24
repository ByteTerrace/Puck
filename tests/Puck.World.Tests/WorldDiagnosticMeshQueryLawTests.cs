using System.Numerics;
using Puck.SdfVm;
using Xunit;

namespace Puck.World.Tests;

/// <summary>A ray hitting another surface never certifies a discrepancy at the queried boundary.</summary>
public sealed class WorldDiagnosticMeshQueryLawTests {
    [Fact]
    public void ADistantSurfaceRayHitIsUnknownNeverRedOrClean() {
        var scene = Scene(17.7f);
        Assert.True(scene.Intersect(Vector3.Zero, -Vector3.UnitY, 20, out _, out _, out _));
        Assert.Null(WorldDiagnosticMeshQuery.Measure(scene, Vector3.Zero, new(0, 0, 0.001), 0.01));
    }

    [Fact]
    public void ANearbyEdgeCertifiesCleanEvenWhenTheNormalRayMissesIt() {
        var mesh = new SdfMesh(new Vector3[] { new(0.001f, 0, -1), new(1, 0, -1), new(0.001f, 0, 1) }, new uint[] { 0, 2, 1 }) {
            BakeCell = new Vector3(0.02f),
        };
        var scene = SdfMeshIrradianceScene.Build([new(mesh, Matrix4x4.Identity, 0, new object())])!;
        Assert.False(scene.Intersect(Vector3.Zero, -Vector3.UnitY, 20, out _, out _, out _));
        var sample = WorldDiagnosticMeshQuery.Measure(scene, Vector3.Zero, new(0, 0, 0.0001), 0.02);
        Assert.NotNull(sample);
        Assert.False(sample.IsRed);
        Assert.Equal(0.001, sample.Distance, 6);
    }

    [Fact]
    public void ACertifiedLocalDiscrepancyIsRedWithoutAddingSearchReachToItsBudget() {
        var sample = WorldDiagnosticMeshQuery.Measure(Scene(0.025f), Vector3.Zero, new(0, 0, 0.001), 0.01);
        Assert.NotNull(sample);
        Assert.True(sample.IsRed);
        Assert.Equal(0.025, sample.Distance, 6);
        Assert.Equal(0.011, sample.Budget.Total, 6);
    }

    [Fact]
    public void AZeroDistanceSurfaceIsEvaluatedClean() {
        var sample = WorldDiagnosticMeshQuery.Measure(Scene(0), Vector3.Zero, new(0, 0, 0.001), 0.01);
        Assert.NotNull(sample);
        Assert.False(sample.IsRed);
        Assert.Equal(0, sample.Distance);
    }

    private static SdfMeshIrradianceScene Scene(float gap) {
        var mesh = new SdfMesh(new Vector3[] { new(-1, -gap, -1), new(1, -gap, -1), new(0, -gap, 1) }, new uint[] { 0, 2, 1 }) {
            BakeCell = new Vector3(0.02f, 0, 0),
        };
        return SdfMeshIrradianceScene.Build([new(mesh, Matrix4x4.Identity, 0, new object())])!;
    }
}
