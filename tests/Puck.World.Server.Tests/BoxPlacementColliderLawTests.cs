using System.Numerics;

using Puck.Maths;
using Puck.SignedDistance;
using Puck.World.Authoring;

using Xunit;

namespace Puck.World.Server.Tests;

public sealed class BoxPlacementColliderLawTests {
    private static FixedQ4816 Q(double value) => FixedQ4816.FromDouble(value: value);
    private static FixedVector3 V(double x, double y, double z) => new(X: Q(value: x), Y: Q(value: y), Z: Q(value: z));
    private static WorldDefinition Document(float scale = 1f, float chamfer = 0f, SdfSolidPrimitive type = SdfSolidPrimitive.Box, Vector3? halfExtents = null,
        float? exponent = null, Quaternion? rotation = null, float? rounding = null) {
        var shape = new ShapeDocument(Id: 0, Name: "solid", Type: type, Position: Vector3.Zero,
            Rotation: (rotation ?? Quaternion.Identity), Scale: (halfExtents ?? new Vector3(value: 2f)), Material: 0, Blend: SdfBlendOp.Union,
            Smooth: 0f, Group: 0, Chamfer: ((chamfer > 0) ? chamfer : null), Exponent: exponent, Rounding: rounding);
        var canonical = CreationCanonicalizer.Canonicalize(document: new CreationDocument(
            Schema: CreationDocument.CurrentSchema, Name: "solid", Palette: null, Shapes: [shape], Frames: null), source: "solid");
        var source = Fixtures.BuildGradientUpDocument(gradientUp: false);

        return source with {
            CreationsRaw = [new WorldPrototype(Id: "solid", Document: canonical.Document, HashRaw: canonical.Hash)],
            PlacementRowsRaw = [new WorldPlacement(Id: "solid", PrototypeId: "solid", Position: Vector3.Zero,
                YawDegrees: 0f, Scale: scale, Solid: new WorldSolid(Margin: 0f))],
        };
    }

    [InlineData(0.5f)]
    [InlineData(1f)]
    [InlineData(2f)]
    [Theory]
    public void AScaledBoxSurfaceAgreesWithTheDrawnFilletAndKeepsTheFace(float scale) {
        var definition = Document(scale: scale);
        var colliders = WorldColliderSet.Build(definition: definition);

        Assert.True(condition: WorldSolidField.TryBuild(definition: definition, built: out var field, reason: out var reason), userMessage: reason);
        var s = Q(value: scale);

        Assert.True(condition: colliders.TryNearestSurfaceAlongDirection(origin: (V(x: 2.2, y: 2.2, z: 2.2) * s), direction: V(x: -1, y: -1, z: -1),
            maxDistance: Q(value: 2), assistHalfAngle: Q(value: 0.01), candidate: out var corner));
        Assert.True(condition: field!.Probe(position: corner.Point, distance: out var distance, gradient: out _, material: out _));
        Assert.InRange(actual: ((double)distance), high: 0.0002, low: -0.0002);
        Assert.InRange(((double)(corner.Point.X / s)), 1.966, 1.967);
        Assert.True(condition: colliders.TryNearestSurfaceAlongDirection(origin: (V(x: 0, y: 2.2, z: 0) * s), direction: -FixedVector3.UnitY,
            maxDistance: Q(value: 2), assistHalfAngle: Q(value: 0.01), candidate: out var face));
        Assert.Equal((Q(value: 2) * s), face.Point.Y);
    }
    [InlineData(0.001f)]
    [InlineData(0.04f)]
    [InlineData(0.1f)]
    [InlineData(1f)]
    [InlineData(2f)]
    [Theory]
    public void AChamferColliderStaysInsideTheDrawnSolid(float chamfer) {
        var definition = Document(chamfer: chamfer);
        var colliders = WorldColliderSet.Build(definition: definition);

        Assert.True(condition: WorldSolidField.TryBuild(definition: definition, built: out var field, reason: out var reason), userMessage: reason);

        for (var x = -1; (x <= 1); x++) {
            for (var y = -1; (y <= 1); y++) {
                for (var z = -1; (z <= 1); z++) {
                    if ((x == 0) && (y == 0) && (z == 0)) { continue; }
                    var probe = V(x: (x * 3), y: (y * 3), z: (z * 3));

                    Assert.True(condition: colliders.TryNearestSurfaceAlongDirection(origin: probe, direction: -probe,
                        maxDistance: Q(value: 6), assistHalfAngle: FixedQ4816.Pi, candidate: out var surface));
                    Assert.True(condition: field!.Probe(position: surface.Point, distance: out var distance, gradient: out _, material: out _));
                    Assert.InRange(actual: ((double)distance), high: 0.0002, low: -0.0002);
                }
            }
        }
    }
    [InlineData(0.1f, 2f)]
    [InlineData(1f, 2f)]
    [InlineData(2f, 10f)]
    [Theory]
    public void AChamferKeepsItsSurvivingFlatFacePlanes(float chamfer, float width) {
        var definition = Document(chamfer: chamfer, halfExtents: new Vector3(x: width, y: 2f, z: width));
        var colliders = WorldColliderSet.Build(definition: definition);
        var normal = FixedVector3.UnitY;
        var origin = V(x: 0, y: 3, z: 0);

        Assert.True(condition: colliders.TryNearestSurfaceAlongDirection(origin: origin, direction: -normal,
            maxDistance: Q(value: 2), assistHalfAngle: Q(value: 0.01), candidate: out var face));
        Assert.Equal(Q(value: 2), face.Point.Y);
        var position = V(x: 0, y: 1.99, z: 0);
        var velocity = -FixedVector3.UnitY;
        var volume = new Puck.Physics.FixedBodyColliderVolume(Kind: Puck.Physics.FixedBodyColliderKind.Sphere,
            Center: V(x: 0, y: 0.1, z: 0), Endpoint: FixedVector3.Zero, HalfExtents: FixedVector3.Zero,
            Radius: Q(value: 0.1), Rotation: FixedQuaternion.Identity);
        var result = colliders.Resolve(position: ref position, velocity: ref velocity, orientation: FixedQuaternion.Identity,
            volumes: [volume], up: normal);

        Assert.True(condition: result.Grounded);
        Assert.InRange(actual: ((double)position.Y), low: 2, high: 2.021);
    }
    [Fact]
    public void TheCircularCylinderStillHasItsExactSurface() {
        var colliders = WorldColliderSet.Build(definition: Document(type: SdfSolidPrimitive.Cylinder));

        Assert.Equal(1, colliders.CylinderCount);
        Assert.True(condition: colliders.TryNearestSurfaceAlongDirection(origin: V(x: 3, y: 0, z: 3), direction: V(x: -1, y: 0, z: -1),
            maxDistance: Q(value: 3), assistHalfAngle: Q(value: 0.01), candidate: out var rim));
        Assert.InRange(((double)rim.Point.Length), 1.9999, 2.0001);
        Assert.InRange(((double)rim.Point.X), 1.4141, 1.4143);
    }
    [InlineData(0.25f, 0.045f, 0.25f, 0f)]
    [InlineData(0.035f, 0.25f, 0.23f, 8f)]
    [Theory]
    public void TheChairSeatAndBackFollowTheirExponentFourSolid(float x, float y, float z, float angle) {
        var definition = Document(type: SdfSolidPrimitive.Superellipsoid, halfExtents: new Vector3(x: x, y: y, z: z), exponent: 4,
            rotation: Quaternion.CreateFromAxisAngle(Vector3.UnitZ, ((angle * MathF.PI) / 180)));
        var set = WorldColliderSet.Build(definition: definition);
        var collider = Assert.Single(collection: set.DiagnosticColliders).Collider;

        Assert.Equal(Puck.Physics.FixedStaticColliderKind.Superellipsoid, collider.Kind);
        Assert.True(condition: WorldSolidField.TryBuild(definition, out var field, out var reason), userMessage: reason);
        foreach (var point in WorldDiagnosticGeometry.SurfaceSamples(collider: collider)) {
            Assert.True(condition: field!.Probe(FixedVector3.FromVector3(value: point), out var distance, out _, out _));
            Assert.InRange(actual: ((double)distance), high: 0.0002, low: -0.0002);
        }
    }
    [InlineData(0.1f, 0f)]
    [InlineData(0f, 0.1f)]
    [Theory]
    public void AConservativeCylinderRimReportsItsComputedContraction(float chamfer, float rounding) {
        var definition = Document(type: SdfSolidPrimitive.Cylinder, chamfer: chamfer, rounding: rounding);
        var row = Assert.Single(collection: WorldColliderSet.Build(definition: definition).DiagnosticColliders);
        var bound = new Vector2(x: ((chamfer > 0) ? chamfer : rounding), y: ((chamfer > 0) ? 0 : rounding)).Length();

        Assert.InRange(row.ConservativeInset, (bound - 0.0001), (bound + 0.0001));
        Assert.True(condition: WorldSolidField.TryBuild(definition, out var field, out var reason), userMessage: reason);
        foreach (var point in WorldDiagnosticGeometry.SurfaceSamples(collider: row.Collider)) {
            Assert.True(condition: field!.Probe(FixedVector3.FromVector3(value: point), out var distance, out _, out _));
            var budget = new WorldDiagnosticBudget(0, 0, WorldDiagnosticGeometry.Numerical(collider: row.Collider)) { ConservativeInset = row.ConservativeInset };

            Assert.Contains(WorldDiagnosticGeometry.ClassifyBoundary(budget: budget, separation: ((double)distance)), new string?[] { null, "known-approximation" });
        }
    }
}
