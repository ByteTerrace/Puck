using System.Numerics;
using System.Text.Json;
using Puck.Maths;
using Puck.Physics;
using Xunit;

namespace Puck.World.Server.Tests;

public sealed class WorldDiagnosticReportTests {
    private static WorldDiagnosticFinding Finding(double separation, ulong tick = 1) => new("", "colliders", "red",
        "analytic/box/node", "collider-outside-authored-solid", "boundary agrees", separation, new(0.01, 0, 0.001),
        new Vector3((float)separation, 0, 0), Vector3.UnitX,
        new(1, tick, null, "primary", "dais", 1, "dais", "round", "node", "union"));

    [Fact]
    public void ClustersKeepAStableAnchorButCurrentMeasurementsAndLifetime() {
        var tracker = new WorldDiagnosticTracker();
        var first = Assert.Single(tracker.Update("colliders", 10, [Finding(0.2), Finding(0.4)], false));
        Assert.Equal(0.4, first.Observed);
        var next = Assert.Single(tracker.Update("colliders", 20, [Finding(0.41, 20)], false));
        Assert.Equal(first.Id, next.Id);
        Assert.Equal(first.Witness, next.PinAnchor);
        Assert.Equal(0.41, next.Observed);
        Assert.Equal(11UL, next.ActiveTicks);
        Assert.Equal(20UL, next.Identity.Tick);
        var worse = Assert.Single(tracker.Update("colliders", 21, [Finding(0.8, 21)], false));
        Assert.Null(worse.PinAnchor);
        Assert.Equal(0.8, worse.Observed);
    }

    [Fact]
    public void AnEvaluatedCauseResolvesDespiteAnotherMissingProvider() {
        var tracker = new WorldDiagnosticTracker();
        _ = tracker.Update("colliders", 1, [Finding(0.4)], false);
        Assert.False(Assert.Single(tracker.Update("colliders", 2, [], false)).Resolved);
        Assert.True(Assert.Single(tracker.Update("colliders", 3, [], false, f => f.Source == "analytic/box/node")).Resolved);
        var returned = Assert.Single(tracker.Update("colliders", 5, [Finding(0.4, 5)], true));
        Assert.False(returned.Resolved);
        Assert.Equal(5UL, returned.FirstSeen);
        Assert.Equal(1UL, returned.ActiveTicks);
    }

    [Fact]
    public void MissingRepresentationsRemainUnknownInTheSharedRecord() {
        var unknown = Finding(0) with { Severity = "unknown", Observed = null };
        var report = new WorldDiagnosticReport(7, "colliders", 8, [unknown],
            new(0, 3, new Dictionary<string, int> { ["mesh pending"] = 3 }, false));
        using var json = JsonDocument.Parse(report.ToJson());
        Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("findings")[0].GetProperty("observed").ValueKind);
        Assert.False(json.RootElement.GetProperty("coverage").GetProperty("complete").GetBoolean());
        Assert.Contains("unknown=1", report.Summary());
        Assert.Contains("tested=0/3", report.Summary());
    }

    [Fact]
    public void ABoxAroundARoundDaisFiresWhileItsExactCylinderDoesNot() {
        var round = FixedStaticCollider.Cylinder(FixedVector3.Zero, FixedQ4816.One, FixedQ4816.One, FixedQuaternion.Identity);
        var box = FixedStaticCollider.Box(FixedVector3.Zero, FixedQuaternion.Identity, FixedVector3.FromVector3(Vector3.One));
        var budget = new WorldDiagnosticBudget(0, 0, WorldDiagnosticGeometry.Numerical(round));
        var mismatch = WorldDiagnosticGeometry.SurfaceSamples(box).Select(p => WorldDiagnosticGeometry.SignedDistance(round, p)).Max();
        Assert.True(mismatch > 0.4);
        Assert.Equal("collider-outside-authored-solid", WorldDiagnosticGeometry.ClassifyBoundary(mismatch, budget));
        Assert.All(WorldDiagnosticGeometry.SurfaceSamples(round), p =>
            Assert.Null(WorldDiagnosticGeometry.ClassifyBoundary(WorldDiagnosticGeometry.SignedDistance(round, p), budget)));
        Assert.Equal("authored-solid-outside-collider", WorldDiagnosticGeometry.ClassifyBoundary(-0.4, budget));
    }

    [Fact]
    public void APrecisionBudgetIncludesTheActualBakeCellAndNeverSnapReach() {
        var budget = new WorldDiagnosticBudget(0.02, 0.01, (double)FixedQ4816.Epsilon, new Vector3(0.02f).Length() * 0.5);
        Assert.Null(WorldDiagnosticGeometry.ClassifyBoundary(budget.Total, budget));
        Assert.NotNull(WorldDiagnosticGeometry.ClassifyBoundary(budget.Total + (double)FixedQ4816.Epsilon, budget));
    }
}
