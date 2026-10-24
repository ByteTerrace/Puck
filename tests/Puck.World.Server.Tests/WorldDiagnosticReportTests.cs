using System.Numerics;
using System.Text.Json;
using Puck.Maths;
using Puck.Physics;
using Xunit;

namespace Puck.World.Server.Tests;

public sealed class WorldDiagnosticReportTests {
    private static WorldDiagnosticFinding Finding(double separation, ulong tick = 1) => new("", "colliders", "red",
        "analytic/box/node", "collider-outside-authored-solid", "boundary agrees", separation, new(0.01, 0, 0.001),
        new Vector3(x: ((float)separation), y: 0, z: 0), Vector3.UnitX,
        new(Creation: "round", Frame: null, Instance: "primary", Node: "node", Operation: "union", Revision: 1, Snapshot: 1, Stamp: "dais", Subject: "dais", Tick: tick));

    [Fact]
    public void ClustersKeepAStableAnchorButCurrentMeasurementsAndLifetime() {
        var tracker = new WorldDiagnosticTracker();
        var first = Assert.Single(collection: tracker.Update("colliders", 10, [Finding(0.2), Finding(0.4)], false));

        Assert.Equal(0.4, first.Observed);
        var next = Assert.Single(collection: tracker.Update("colliders", 20, [Finding(separation: 0.41, tick: 20)], false));

        Assert.Equal(first.Id, next.Id);
        Assert.Equal(first.Witness, next.PinAnchor);
        Assert.Equal(0.41, next.Observed);
        Assert.Equal(11UL, next.ActiveTicks);
        Assert.Equal(20UL, next.Identity.Tick);
        var worse = Assert.Single(collection: tracker.Update("colliders", 21, [Finding(separation: 0.8, tick: 21)], false));

        Assert.Null(value: worse.PinAnchor);
        Assert.Equal(0.8, worse.Observed);
    }
    [Fact]
    public void AnEvaluatedCauseResolvesDespiteAnotherMissingProvider() {
        var tracker = new WorldDiagnosticTracker();

        _ = tracker.Update("colliders", 1, [Finding(0.4)], false);
        Assert.False(condition: Assert.Single(collection: tracker.Update("colliders", 2, [], false)).Resolved);
        Assert.True(condition: Assert.Single(collection: tracker.Update("colliders", 3, [], false, f => (f.Source == "analytic/box/node"))).Resolved);
        var returned = Assert.Single(collection: tracker.Update("colliders", 5, [Finding(separation: 0.4, tick: 5)], true));

        Assert.False(condition: returned.Resolved);
        Assert.Equal(5UL, returned.FirstSeen);
        Assert.Equal(1UL, returned.ActiveTicks);
    }
    [Fact]
    public void MissingRepresentationsRemainUnknownInTheSharedRecord() {
        var unknown = Finding(0) with { Severity = "unknown", Observed = null };
        var report = new WorldDiagnosticReport(7, "colliders", 8, [unknown],
            new(0, 3, new Dictionary<string, int> { ["mesh pending"] = 3 }, false) {
                Comparisons = new Dictionary<string, WorldDiagnosticSampleCounts> {
                    ["collision-versus-baked-mesh"] = new(Clean: 0, Red: 0, Unknown: 3),
                },
            });
        using var json = JsonDocument.Parse(report.ToJson());

        Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty(propertyName: "findings")[0].GetProperty(propertyName: "observed").ValueKind);
        Assert.False(condition: json.RootElement.GetProperty(propertyName: "coverage").GetProperty(propertyName: "complete").GetBoolean());
        var samples = json.RootElement.GetProperty(propertyName: "coverage").GetProperty(propertyName: "comparisons").GetProperty(propertyName: "collision-versus-baked-mesh");

        Assert.Equal(0, samples.GetProperty(propertyName: "red").GetInt32());
        Assert.Equal(0, samples.GetProperty(propertyName: "clean").GetInt32());
        Assert.Equal(3, samples.GetProperty(propertyName: "unknown").GetInt32());
        Assert.Contains("unknown=1", report.Summary());
        Assert.Contains("tested=0/3", report.Summary());
    }
    [Fact]
    public void ABoxAroundARoundDaisFiresWhileItsExactCylinderDoesNot() {
        var round = FixedStaticCollider.Cylinder(FixedVector3.Zero, FixedQ4816.One, FixedQ4816.One, FixedQuaternion.Identity);
        var box = FixedStaticCollider.Box(FixedVector3.Zero, FixedQuaternion.Identity, FixedVector3.FromVector3(value: Vector3.One));
        var budget = new WorldDiagnosticBudget(0, 0, WorldDiagnosticGeometry.Numerical(collider: round));
        var mismatch = WorldDiagnosticGeometry.SurfaceSamples(collider: box).Select(selector: p => WorldDiagnosticGeometry.SignedDistance(collider: round, point: p)).Max();

        Assert.True(condition: (mismatch > 0.4));
        Assert.Equal("collider-outside-authored-solid", WorldDiagnosticGeometry.ClassifyBoundary(budget: budget, separation: mismatch));
        Assert.All(WorldDiagnosticGeometry.SurfaceSamples(collider: round), p =>
            Assert.Null(@object: WorldDiagnosticGeometry.ClassifyBoundary(WorldDiagnosticGeometry.SignedDistance(collider: round, point: p), budget)));
        Assert.Equal("authored-solid-outside-collider", WorldDiagnosticGeometry.ClassifyBoundary(budget: budget, separation: -0.4));
    }
    [Fact]
    public void APrecisionBudgetIncludesTheActualBakeCellAndNeverSnapReach() {
        var budget = new WorldDiagnosticBudget(0.02, 0.01, ((double)FixedQ4816.Epsilon), (new Vector3(value: 0.02f).Length() * 0.5));

        Assert.Null(@object: WorldDiagnosticGeometry.ClassifyBoundary(budget.Total, budget));
        Assert.NotNull(@object: WorldDiagnosticGeometry.ClassifyBoundary((budget.Total + ((double)FixedQ4816.Epsilon)), budget));
    }
    [Fact]
    public void AComputedInsetBoundExplainsOnlyInwardApproximation() {
        var contraction = new Vector2(x: 0.02f, y: 0.01f);
        var budget = new WorldDiagnosticBudget(0, 0, 0.001) { ConservativeInset = contraction.Length() };

        Assert.Null(@object: WorldDiagnosticGeometry.ClassifyBoundary(budget: budget, separation: -0.0005));
        Assert.Equal("known-approximation", WorldDiagnosticGeometry.ClassifyBoundary(budget: budget, separation: -0.02));
        Assert.Equal("authored-solid-outside-collider", WorldDiagnosticGeometry.ClassifyBoundary(budget: budget, separation: -0.024));
        Assert.Equal("collider-outside-authored-solid", WorldDiagnosticGeometry.ClassifyBoundary(budget: budget, separation: 0.002));
    }
    [InlineData(0, 0.08, false)]
    [InlineData((-Math.PI / 4), 0.08, false)]
    [InlineData(0.6, 0.4, true)]
    [InlineData((-Math.PI / 4), 0, false)]
    [Theory]
    public void RoundedBoxWitnessesAgreeWithPhysicsAtFacesEdgesAndCorners(double angle, double radius, bool rod) {
        var collider = FixedStaticCollider.RoundedBox(
            FixedVector3.FromVector3(value: new Vector3(x: 31, y: 2, z: -17)),
            FixedQuaternion.FromAxisAngle(FixedVector3.UnitY, FixedQ4816.FromDouble(value: angle)),
            FixedVector3.FromVector3(value: (rod ? new Vector3(x: 2, y: 0, z: 0) : new Vector3(x: 2, y: 1, z: 0.25f))),
            FixedQ4816.FromDouble(value: radius));
        var budget = new WorldDiagnosticBudget(0, 0, WorldDiagnosticGeometry.Numerical(collider: collider));
        var samples = WorldDiagnosticGeometry.SurfaceSamples(collider: collider).ToArray();

        Assert.Equal(26, samples.Length);
        var sphere = new FixedBodyColliderVolume(Kind: FixedBodyColliderKind.Sphere,
            Center: FixedVector3.Zero, Endpoint: FixedVector3.Zero, HalfExtents: FixedVector3.Zero,
            Rotation: FixedQuaternion.Identity, Radius: FixedQ4816.FromDouble(value: 0.02));

        foreach (var point in samples) {
            // The production hold query and narrowphase independently certify each diagnostic witness.
            var probe = FixedVector3.FromVector3(value: point);

            Assert.True(condition: FixedSurfaceQuery.TryNearest([collider], [], probe, FixedQ4816.One, out var nearest));
            Assert.InRange(((double)nearest.Distance), 0, budget.Total);
            Assert.True(condition: collider.TryGetPush(probe, FixedQuaternion.Identity, sphere, FixedQ4816.Zero, out var push));
            Assert.InRange(((double)push.Penetration), (0.02 - budget.Total), (0.02 + budget.Total));
            Assert.Null(@object: WorldDiagnosticGeometry.ClassifyBoundary(WorldDiagnosticGeometry.SignedDistance(collider: collider, point: point), budget));
            foreach (var offset in new[] { -0.01, 0.01 }) {
                var displaced = (point + (nearest.Normal.ToVector3() * ((float)offset)));

                Assert.True(condition: collider.TryGetPush(FixedVector3.FromVector3(value: displaced), FixedQuaternion.Identity,
                    sphere, FixedQ4816.Zero, out var displacedPush));
                var signed = ((double)(sphere.Radius - displacedPush.Penetration));

                Assert.InRange(WorldDiagnosticGeometry.SignedDistance(collider: collider, point: displaced), (signed - budget.Total), (signed + budget.Total));
                // At radius zero, a sharp edge has several normals; moving along one may stay on another face.
                if (radius > 0) { Assert.InRange(signed, (offset - budget.Total), (offset + budget.Total)); }
            }
        }
        // Signed interior distance includes the offset, rather than just the inner half-extents.
        Assert.InRange(WorldDiagnosticGeometry.SignedDistance(collider: collider, point: collider.Center.ToVector3()),
            ((-(rod ? 0 : 0.25) - radius) - budget.Total), ((-(rod ? 0 : 0.25) - radius) + budget.Total));
    }
}
