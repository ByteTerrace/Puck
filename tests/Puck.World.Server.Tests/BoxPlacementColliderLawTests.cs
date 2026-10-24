using System.Numerics;

using Puck.Maths;
using Puck.SignedDistance;
using Puck.World.Authoring;

using Xunit;

namespace Puck.World.Server.Tests;

public sealed class BoxPlacementColliderLawTests {
    private static FixedQ4816 Q(double value) => FixedQ4816.FromDouble(value: value);
    private static FixedVector3 V(double x, double y, double z) => new(X: Q(value: x), Y: Q(value: y), Z: Q(value: z));
    private static WorldDefinition Document(float scale = 1f, float chamfer = 0f, SdfSolidPrimitive type = SdfSolidPrimitive.Box) {
        var shape = new ShapeDocument(Id: 0, Name: "solid", Type: type, Position: Vector3.Zero,
            Rotation: Quaternion.Identity, Scale: new Vector3(value: 2f), Material: 0, Blend: SdfBlendOp.Union,
            Smooth: 0f, Group: 0, Chamfer: chamfer);
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
                        maxDistance: Q(value: 6), assistHalfAngle: Q(value: 0.01), candidate: out var surface));
                    Assert.True(condition: field!.Probe(position: surface.Point, distance: out var distance, gradient: out _, material: out _));
                    Assert.True(condition: (distance <= Q(value: 0.0002)), userMessage: $"chamfer {chamfer}, {surface.Point}: outside by {distance}");
                }
            }
        }
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
}
