using System.Numerics;

using Puck.Maths;
using Puck.Physics;
using Puck.SignedDistance;
using Puck.World.Authoring;

using Xunit;

namespace Puck.World.Server.Tests;

/// <summary>A relieved creation collides as the surface its relief draws: one field collider per copy whose surface is
/// the zero set of the same relieved field the field provider and the bake read.</summary>
public sealed class ReliefColliderLawTests {
    private static FixedQ4816 Q(double value) => FixedQ4816.FromDouble(value: value);
    private static FixedVector3 V(double x, double y, double z) => new(X: Q(value: x), Y: Q(value: y), Z: Q(value: z));
    private static ShapeDocument Shape(int id, SdfSolidPrimitive type, Vector3 position, Vector3 scale, float? exponent = null) =>
        new(Id: id, Name: $"shape-{id}", Type: type, Position: position, Rotation: Quaternion.Identity, Scale: scale, Material: 0,
            Blend: SdfBlendOp.Union, Smooth: 0f, Group: 0, Exponent: exponent);
    private static WorldDefinition Document(IReadOnlyList<ShapeDocument> shapes, float yawDegrees = 30f, bool relieved = true) {
        var canonical = CreationCanonicalizer.Canonicalize(document: new CreationDocument(
            Schema: CreationDocument.CurrentSchema, Name: "rock", Palette: null, Shapes: shapes, Frames: null,
            Noise: (relieved ? new CreationNoiseDocument(Frequency: 0.5f, Amplitude: 0.4f, Octaves: 3, Seed: 7u) : null)), source: "rock");
        var source = Fixtures.BuildGradientUpDocument(gradientUp: false);

        return source with {
            CreationsRaw = [new WorldPrototype(Id: "rock", Document: canonical.Document, HashRaw: canonical.Hash)],
            PlacementRowsRaw = [new WorldPlacement(Id: "rock", PrototypeId: "rock", Position: new Vector3(x: 3f, y: 0f, z: -2f),
                YawDegrees: yawDegrees, Scale: 1f, Solid: new WorldSolid(Margin: 0f))],
        };
    }

    private static readonly ShapeDocument[] Masses = [
        Shape(id: 0, position: Vector3.Zero, scale: new Vector3(x: 6f, y: 3f, z: 4f), type: SdfSolidPrimitive.Superellipsoid, exponent: 2f),
        Shape(id: 1, position: new Vector3(x: 5f, y: 0f, z: 0f), scale: new Vector3(value: 2f), type: SdfSolidPrimitive.Box),
        Shape(id: 2, position: new Vector3(x: -5f, y: 1f, z: 0f), scale: new Vector3(value: 1.5f), type: SdfSolidPrimitive.Sphere),
    ];

    // A smooth join collides as drawn without relief: two coincident unit crowns smooth-unioned at 0.25 draw their top a
    // quarter radius, 0.0625, above either primitive's, and the copy is one field collider whose surface is there.
    [Fact]
    public void ASmoothJoinWithoutReliefCollidesAsItsComposedField() {
        ShapeDocument[] crowns = [
            Shape(id: 0, position: new Vector3(x: 0f, y: 3f, z: 0f), scale: Vector3.One, type: SdfSolidPrimitive.Superellipsoid, exponent: 2f),
            (Shape(id: 1, position: new Vector3(x: 0f, y: 3f, z: 0f), scale: Vector3.One, type: SdfSolidPrimitive.Superellipsoid, exponent: 2f) with {
                Blend = SdfBlendOp.SmoothUnion, Smooth = 0.25f }),
        ];
        var set = WorldColliderSet.Build(definition: Document(shapes: crowns, relieved: false));

        Assert.Equal(expected: 1, actual: set.FieldCount);
        Assert.True(condition: set.TryNearestSurfaceAlongDirection(origin: V(x: 3, y: 6, z: -2), direction: -FixedVector3.UnitY,
            maxDistance: Q(value: 3), assistHalfAngle: Q(value: 0.01), candidate: out var top));
        Assert.InRange(actual: ((double)top.Point.Y), low: 4.06, high: 4.065);
    }
    [Fact]
    public void ARelievedCopyCompilesOneFieldColliderOnTheProvidersRelievedField() {
        var definition = Document(shapes: Masses);
        var set = WorldColliderSet.Build(definition: definition);
        var row = Assert.Single(collection: set.DiagnosticColliders);
        var collider = row.Collider;

        Assert.Equal(expected: FixedStaticColliderKind.Field, actual: collider.Kind);
        Assert.Equal(expected: 1, actual: set.FieldCount);
        Assert.Equal(expected: 1, actual: set.SolidCount);
        Assert.Equal(expected: 3, actual: collider.Field!.Bases.Count);
        Assert.NotNull(@object: collider.WorldHalfExtent());
        Assert.True(condition: WorldSolidField.TryBuild(definition: definition, built: out var field, reason: out var reason), userMessage: reason);
        Assert.Equal(expected: 1L, actual: field!.Census.PlacementFieldCount);

        // Every witness the diagnostic projects onto the collider lies on the field provider's own relieved field.
        var witnesses = WorldDiagnosticGeometry.SurfaceSamples(collider: collider, token: TestContext.Current.CancellationToken).ToArray();

        Assert.NotEmpty(collection: witnesses);
        foreach (var witness in witnesses) {
            Assert.True(condition: field!.Probe(position: FixedVector3.FromVector3(value: witness), distance: out var distance, gradient: out _, material: out _));
            Assert.InRange(actual: ((double)distance), high: 0.002, low: -0.002);
            Assert.InRange(actual: WorldDiagnosticGeometry.SignedDistance(collider: collider, point: witness), high: 0.002, low: -0.002);
        }
    }
    [Fact]
    public void ABodyStandsTheSkinAboveTheReliefAndStaysWhereItStands() {
        var definition = Document(shapes: Masses);
        var set = WorldColliderSet.Build(definition: definition);
        var collider = Assert.Single(collection: set.DiagnosticColliders).Collider;
        FixedBodyColliderVolume[] volumes = [new(Center: V(x: 0d, y: 0.35d, z: 0d), Endpoint: V(x: 0d, y: 1.7d, z: 0d), HalfExtents: FixedVector3.Zero,
            Kind: FixedBodyColliderKind.Capsule, Radius: Q(value: 0.35d), Rotation: FixedQuaternion.Identity)];
        // Above the superellipsoid's shoulder, off its crown, where the relief and the curve both slope.
        var position = V(x: 4.5d, y: 3.6d, z: -0.5d);
        var rest = FixedVector3.Zero;

        for (var tick = 0; (tick < 90); tick++) {
            var velocity = V(x: 0d, y: -0.3d, z: 0d);
            var start = position;

            position += V(x: 0d, y: -0.05d, z: 0d);
            var resolution = set.ResolveSweep(orientation: FixedQuaternion.Identity, position: ref position, previousPosition: in start,
                up: FixedVector3.UnitY, velocity: ref velocity, volumes: volumes);

            if (tick == 60) {
                Assert.True(condition: resolution.Grounded);
                rest = position;
            }
        }

        // At rest a body does not creep: the walkable ground lifts it along up, not along its tilted normal.
        Assert.InRange(actual: ((double)(position - rest).Length), high: 0.0005, low: 0d);
        Assert.True(condition: FixedFieldColliderContact.TryMeasure(collider: in collider, distance: out var foot, normal: out _,
            point: (position + volumes[0].Center)));
        var reach = (0.35d + definition.Collision.ContactSkin);

        Assert.InRange(actual: ((double)foot), high: (reach + 0.01d), low: (reach - 0.002d));
    }
    [Fact]
    public void ARelievedPlaneHasNoBounds() {
        var set = WorldColliderSet.Build(definition: Document(shapes: [
            Shape(id: 0, position: Vector3.Zero, scale: Vector3.One, type: SdfSolidPrimitive.Plane),
        ]));
        var collider = Assert.Single(collection: set.DiagnosticColliders).Collider;

        Assert.Equal(expected: FixedStaticColliderKind.Field, actual: collider.Kind);
        Assert.Null(@object: collider.WorldHalfExtent());
    }
}
