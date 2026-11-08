using System.Numerics;
using System.Text.Json;
using Puck.Maths;
using Puck.Physics;
using Xunit;

namespace Puck.World.Server.Tests;

public sealed class WorldDiagnosticReportTests {
    [InlineData(1, 65536, true)]
    [InlineData(-1, 65536, false)]
    [InlineData(-1, 1, true)]
    [InlineData(-1, 0, true)]
    [Theory]
    public void ACertifiedFieldSignAnswersAllCandidateBallsWithoutPointWalks(int raw, int scaleRaw, bool open) {
        var field = new ConstantField(FixedQ4816.FromRawBits(value: raw), FixedQ4816.FromRawBits(value: scaleRaw));
        var collider = FixedStaticCollider.FieldSurface(FixedVector3.Zero, FixedQuaternion.Identity,
            FixedVector3.FromVector3(value: Vector3.One), new FixedColliderField(field, FixedColliderField.DefaultGradientStep) { Bounds = field });

        Assert.Equal(open, WorldDiagnosticGeometry.OpensOnto([collider], Vector3.Zero, Vector3.UnitY, 0.01, token: TestContext.Current.CancellationToken));
        Assert.Equal(1, field.BoundsReads);
        Assert.Equal(0, field.PointReads);
    }
    [Fact]
    public void AnUnboundedFieldRemainsANeighbourFarFromItsOrigin() {
        var field = new ConstantField(-FixedQ4816.One, FixedQ4816.One);
        var collider = FixedStaticCollider.FieldSurface(FixedVector3.Zero, FixedQuaternion.Identity, FixedVector3.Zero,
            new FixedColliderField(field, FixedColliderField.DefaultGradientStep, bounded: false) { Bounds = field });

        Assert.False(condition: WorldDiagnosticGeometry.OpensOnto([collider], new Vector3(x: 100, y: 0, z: 0), Vector3.UnitY, 0.01, token: TestContext.Current.CancellationToken));
        Assert.Equal(1, field.BoundsReads);
        Assert.Equal(0, field.PointReads);
    }

    private sealed class ConstantField(FixedQ4816 value, FixedQ4816 scale) : IFieldEvaluator, IFieldBounds {
        public int BoundsReads { get; private set; }
        public FieldEvaluatorCapabilities Capabilities => new(StepScale: scale, WarpFree: true);
        public int PointReads { get; private set; }
        public FixedQ4816 StepScale => scale;

        public bool TryDistance(FixedPosition position, out FixedQ4816 distance, out int material) {
            PointReads++;
            distance = value;
            material = 0;
            return true;
        }
        public bool TryDistanceBounds(FixedPosition lower, FixedPosition upper, out FixedInterval distance) {
            BoundsReads++;
            distance = FixedInterval.FromPoint(value: value);
            return true;
        }
        public bool TryFieldGradient(FixedPosition position, out FixedVector3 gradient) => throw new InvalidOperationException(message: "A sign test needs no gradient.");
        public bool TryFieldGradient(FixedPosition position, FixedQ4816 epsilon, out FixedVector3 gradient) => throw new InvalidOperationException(message: "A sign test needs no gradient.");
    }

    [Fact]
    public void TouchingBoxesHideTheirSharedFaceButKeepTheOuterEdge() {
        var left = FixedStaticCollider.Box(FixedVector3.Zero, FixedQuaternion.Identity, FixedVector3.FromVector3(value: Vector3.One));
        var right = FixedStaticCollider.Box(FixedVector3.FromVector3(value: new Vector3(x: 2, y: 0, z: 0)), FixedQuaternion.Identity, FixedVector3.FromVector3(value: Vector3.One));

        Assert.True(condition: WorldDiagnosticGeometry.SharedInteriorFace(left, right, Vector3.UnitX, 0.001));
        Assert.False(condition: WorldDiagnosticGeometry.SharedInteriorFace(left, right, new Vector3(x: 1, y: 1, z: 0), 0.001));
        Assert.False(condition: WorldDiagnosticGeometry.SharedInteriorFace(left, left, Vector3.UnitX, 0.001));
    }
    // Two stair treads against a wall, each a box rounded by its document: the edge the three share encloses a fillet
    // groove no bake cell draws, while the tread's corner at the wall opens onto the stairwell.
    [Fact]
    public void AGrooveThreeBoxesEncloseIsClosedWhereTheTreadMeetingTheWallIsOpen() {
        static FixedStaticCollider Box(Vector3 min, Vector3 max, float radius) => FixedStaticCollider.RoundedBox(
            FixedVector3.FromVector3(value: ((min + max) * 0.5f)), FixedQuaternion.Identity,
            FixedVector3.FromVector3(value: (((max - min) * 0.5f) - new Vector3(value: radius))), FixedQ4816.FromDouble(value: radius));
        var lower = Box(new Vector3(x: 10.3f, y: 9.05f, z: 34.9f), new Vector3(x: 12.1f, y: 9.55f, z: 35.3f), 0.008f);
        var upper = Box(new Vector3(x: 10.3f, y: 9.05f, z: 35.3f), new Vector3(x: 12.1f, y: 9.8f, z: 35.7f), 0.008f);
        var wall = Box(new Vector3(x: 12.1f, y: 9.05f, z: 34.5f), new Vector3(x: 12.3f, y: 13.55f, z: 37.7f), 0.004f);
        FixedStaticCollider[] stairs = [lower, upper, wall];
        const double Resolution = 0.0363;
        var groove = new Vector3(x: ((12.1f - 0.008f) + (0.008f / MathF.Sqrt(x: 2))), y: 9.3f, z: ((35.3f - 0.008f) + (0.008f / MathF.Sqrt(x: 2))));
        var corner = new Vector3(x: ((12.1f - 0.008f) + (0.008f / MathF.Sqrt(x: 2))), y: ((9.55f - 0.008f) + (0.008f / MathF.Sqrt(x: 2))), z: 35.1f);

        Assert.InRange(WorldDiagnosticGeometry.SignedDistance(collider: lower, point: groove), -0.0001, 0.0001);
        Assert.False(condition: WorldDiagnosticGeometry.OpensOnto(stairs, groove, Vector3.UnitX, Resolution, token: TestContext.Current.CancellationToken));
        Assert.True(condition: WorldDiagnosticGeometry.OpensOnto(stairs, corner, Vector3.UnitY, Resolution, token: TestContext.Current.CancellationToken));
        Assert.True(condition: WorldDiagnosticGeometry.OpensOnto(stairs, new Vector3(x: 11.2f, y: 9.55f, z: 35.1f), Vector3.UnitY, Resolution, token: TestContext.Current.CancellationToken));
        // A slab 5 cm over the tread closes the gap between them for a 3.6 cm cell; a bound's distance closes it only
        // where a ball's centre lies inside the bound, so the same slab as a superellipsoid leaves the tread measured.
        var tread = new Vector3(x: 11.2f, y: 9.55f, z: 35.1f);
        var slab = FixedStaticCollider.Box(FixedVector3.FromVector3(value: new Vector3(x: 11.2f, y: 10.1f, z: 35.1f)), FixedQuaternion.Identity,
            FixedVector3.FromVector3(value: new Vector3(x: 2, y: 0.5f, z: 2)));
        var bound = FixedStaticCollider.Superellipsoid(slab.Center, FixedQuaternion.Identity, slab.Extent, FixedQ4816.FromInteger(value: 8));

        Assert.False(condition: WorldDiagnosticGeometry.OpensOnto([lower, upper, wall, slab], tread, Vector3.UnitY, Resolution, token: TestContext.Current.CancellationToken));
        Assert.True(condition: WorldDiagnosticGeometry.OpensOnto([lower, upper, wall, bound], tread, Vector3.UnitY, Resolution, token: TestContext.Current.CancellationToken));
        Assert.False(condition: WorldDiagnosticGeometry.OpensOnto([lower, upper, bound with { Center = FixedVector3.FromVector3(value: new Vector3(x: 11.2f, y: 10.05f, z: 35.1f)) }],
            tread, Vector3.UnitY, Resolution, token: TestContext.Current.CancellationToken));
    }

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
        var covered = unknown with { Coverage = report.Coverage };

        report = report with { Findings = [covered, covered] };
        using var json = JsonDocument.Parse(report.ToJson());

        Assert.False(condition: json.RootElement.GetProperty(propertyName: "findings")[0].TryGetProperty(propertyName: "coverage", value: out _));
        using var picked = JsonDocument.Parse(covered.ToJson());

        Assert.True(condition: picked.RootElement.TryGetProperty(propertyName: "coverage", value: out _));

        Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty(propertyName: "findings")[0].GetProperty(propertyName: "observed").ValueKind);
        Assert.False(condition: json.RootElement.GetProperty(propertyName: "coverage").GetProperty(propertyName: "complete").GetBoolean());
        var samples = json.RootElement.GetProperty(propertyName: "coverage").GetProperty(propertyName: "comparisons").GetProperty(propertyName: "collision-versus-baked-mesh");

        Assert.Equal(0, samples.GetProperty(propertyName: "red").GetInt32());
        Assert.Equal(0, samples.GetProperty(propertyName: "clean").GetInt32());
        Assert.Equal(3, samples.GetProperty(propertyName: "unknown").GetInt32());
        Assert.Contains("unknown=2", report.Summary());
        Assert.Contains("tested=0/3", report.Summary());
    }
    [Fact]
    public void ABoxAroundARoundDaisFiresWhileItsExactCylinderDoesNot() {
        var round = FixedStaticCollider.Cylinder(FixedVector3.Zero, FixedQ4816.One, FixedQ4816.One, FixedQuaternion.Identity);
        var box = FixedStaticCollider.Box(FixedVector3.Zero, FixedQuaternion.Identity, FixedVector3.FromVector3(value: Vector3.One));
        var budget = new WorldDiagnosticBudget(0, 0, WorldDiagnosticGeometry.Numerical(collider: round));
        var mismatch = WorldDiagnosticGeometry.SurfaceSamples(collider: box, token: TestContext.Current.CancellationToken).Select(selector: p => WorldDiagnosticGeometry.SignedDistance(collider: round, point: p)).Max();

        Assert.True(condition: (mismatch > 0.4));
        Assert.Equal("collider-outside-authored-solid", WorldDiagnosticGeometry.ClassifyBoundary(budget: budget, separation: mismatch));
        Assert.All(WorldDiagnosticGeometry.SurfaceSamples(collider: round, token: TestContext.Current.CancellationToken), p =>
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
        var samples = WorldDiagnosticGeometry.SurfaceSamples(collider: collider, token: TestContext.Current.CancellationToken).ToArray();

        Assert.Equal(26, samples.Length);
        var sphere = new FixedBodyColliderVolume(Kind: FixedBodyColliderKind.Sphere,
            Center: FixedVector3.Zero, Endpoint: FixedVector3.Zero, HalfExtents: FixedVector3.Zero,
            Rotation: FixedQuaternion.Identity, Radius: FixedQ4816.FromDouble(value: 0.02));

        foreach (var point in samples) {
            // The production hold query and narrowphase independently certify each diagnostic witness.
            var probe = FixedVector3.FromVector3(value: point);

            Assert.True(condition: FixedSurfaceQuery.TryNearest([collider], [], probe, FixedQ4816.One, out var nearest));
            Assert.InRange(((double)nearest.Distance), 0, budget.Total);
            Assert.True(condition: collider.TryGetPush(probe, FixedQuaternion.Identity, sphere, FixedQ4816.Zero, out var push, out _));
            Assert.InRange(((double)push.Penetration), (0.02 - budget.Total), (0.02 + budget.Total));
            Assert.Null(@object: WorldDiagnosticGeometry.ClassifyBoundary(WorldDiagnosticGeometry.SignedDistance(collider: collider, point: point), budget));
            foreach (var offset in new[] { -0.01, 0.01 }) {
                var displaced = (point + (nearest.Normal.ToVector3() * ((float)offset)));

                Assert.True(condition: collider.TryGetPush(FixedVector3.FromVector3(value: displaced), FixedQuaternion.Identity,
                    sphere, FixedQ4816.Zero, out var displacedPush, out _));
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
