using System.Numerics;

using Puck.Maths;
using Puck.Physics;
using Puck.SignedDistance;
using Puck.World.Authoring;

using Xunit;

namespace Puck.World.Server.Tests;

/// <summary>A carved creation's contact reads one exact union piece only where its partition proves the composed solid is
/// that piece's: each law poses a body where a shape's reach once missed its drawn solid (a revolved prism's radial
/// offset, a capsule baked wider than tall, a gauge-scaled carve under a contact margin) and holds the narrowed push to
/// the push the composed field gives at the same pose.</summary>
public sealed class CarvedFieldPartitionLawTests {
    private static readonly FixedQ4816 Skin = FixedQ4816.FromDouble(value: 0.05d);

    private static FixedVector3 V(double x, double y, double z) => new(X: FixedQ4816.FromDouble(value: x), Y: FixedQ4816.FromDouble(value: y), Z: FixedQ4816.FromDouble(value: z));
    private static ShapeDocument Shape(int id, SdfSolidPrimitive type, Vector3 position, Vector3 scale, SdfBlendOp blend = SdfBlendOp.Union, SdfLift? lift = null) =>
        new(Id: id, Name: $"shape-{id}", Type: type, Position: position, Rotation: Quaternion.Identity, Scale: scale, Material: 0,
            Blend: blend, Smooth: 0f, Group: 0) { Lift = lift };
    // The ground every law stands on: a 20 m square slab whose top is at y = 0.
    private static ShapeDocument Ground => Shape(id: 0, position: new Vector3(x: 0f, y: -1f, z: 0f), scale: new Vector3(x: 10f, y: 1f, z: 10f), type: SdfSolidPrimitive.Box);
    // A carve far from every pose, so the creation is carved (and partitioned) without the carve reaching a law's body.
    private static ShapeDocument FarCarve(int id) =>
        Shape(blend: SdfBlendOp.Subtraction, id: id, position: new Vector3(x: 8f, y: 0f, z: 8f), scale: new Vector3(x: 0.5f, y: 0.5f, z: 0.5f), type: SdfSolidPrimitive.Box);
    private static FixedStaticCollider Collider(float margin, params ShapeDocument[] shapes) {
        var canonical = CreationCanonicalizer.Canonicalize(document: new CreationDocument(
            Schema: CreationDocument.CurrentSchema, Name: "carved", Palette: null, Shapes: shapes, Frames: null, Noise: null), source: "carved");
        var definition = Fixtures.BuildGradientUpDocument(gradientUp: false) with {
            CreationsRaw = [new WorldPrototype(Id: "carved", Document: canonical.Document, HashRaw: canonical.Hash)],
            PlacementRowsRaw = [new WorldPlacement(Id: "carved", PrototypeId: "carved", Position: Vector3.Zero, YawDegrees: 0f, Scale: 1f,
                Solid: new WorldSolid(Margin: margin))],
        };
        var collider = Assert.Single(collection: WorldColliderSet.Build(definition: definition).DiagnosticColliders).Collider;

        Assert.NotNull(@object: collider.Field?.Partition);
        return collider;
    }
    private static FixedBodyColliderVolume Ball(double radius) => new(Center: FixedVector3.Zero, Endpoint: FixedVector3.Zero,
        HalfExtents: FixedVector3.Zero, Kind: FixedBodyColliderKind.Sphere, Radius: FixedQ4816.FromDouble(value: radius), Rotation: FixedQuaternion.Identity);
    // The contact a body reads (the collider's push, narrowed where its partition proves a piece) and the composed field's
    // own certified solve at the same pose: the same verdict, the same certainty (a narrowed piece is always certain, so
    // a certain answer where the field is not is a narrowing that skipped what the field reads), and the same push
    // direction within the field's bracket.
    private static (bool Pushes, FixedContactPush Push) Agree(FixedStaticCollider collider, FixedVector3 centre, double radius) {
        var volume = Ball(radius: radius);
        var pushes = collider.TryGetPush(orientation: FixedQuaternion.Identity, position: centre, push: out var push, skin: Skin, uncertain: out var uncertain, volume: in volume);
        var field = FixedFieldColliderContact.TryPush(collider: in collider, orientation: FixedQuaternion.Identity, position: centre, push: out var fieldPush,
            skin: Skin, uncertain: out var fieldUncertain, volume: in volume);

        Assert.Equal(expected: fieldUncertain, actual: uncertain);
        Assert.Equal(expected: field, actual: pushes);

        if (pushes) {
            Assert.True(condition: (FixedVector3.Dot(left: push.Normal, right: fieldPush.Normal) > FixedQ4816.FromDouble(value: 0.99d)),
                userMessage: $"normal {push.Normal} against the field's {fieldPush.Normal}");
            Assert.True(condition: (FixedQ4816.Abs(value: (push.Penetration - fieldPush.Penetration)) < FixedQ4816.FromDouble(value: 0.002d)),
                userMessage: $"penetration {(double)push.Penetration} against the field's {(double)fieldPush.Penetration}");
        }

        return (pushes, push);
    }

    // F1: a revolved prism sits at its |scale.z| radial offset. A ring trench of radius 2.5 to 3.5 is cut through the
    // ground's top; a ball over the trench has no ground under it within its reach, so nothing pushes it.
    [Fact]
    public void ABallOverARevolvedTrenchFindsNoGround() {
        var collider = Collider(margin: 0f, Ground, Shape(blend: SdfBlendOp.Subtraction, id: 1, lift: SdfLift.Revolve, position: Vector3.Zero,
            scale: new Vector3(x: 0.5f, y: 0.5f, z: 3f), type: SdfSolidPrimitive.Prism));

        Assert.False(condition: Agree(centre: V(x: 3d, y: 0.3d, z: 0d), collider: collider, radius: 0.35d).Pushes);
        Assert.False(condition: Agree(centre: V(x: 0d, y: 0.3d, z: -3d), collider: collider, radius: 0.35d).Pushes);

        // Away from the trench the ground answers analytically and holds a ball pressed into it.
        var (pushes, push) = Agree(centre: V(x: 6d, y: 0.33d, z: 0d), collider: collider, radius: 0.35d);

        Assert.True(condition: pushes);
        Assert.True(condition: (push.Normal.Y > FixedQ4816.FromDouble(value: 0.99d)));
    }

    // F1: a revolved prism with no radial offset is a frustum about its axis, radius 0.4 at its base and 0.2 at its top
    // (the default taper), 0.3 at its middle; a ball pressed against its side is pushed off that side.
    [Fact]
    public void ABallAgainstARevolvedFrustumIsPushedOffItsSide() {
        var collider = Collider(margin: 0f, Ground, FarCarve(id: 1), Shape(id: 2, lift: SdfLift.Revolve, position: new Vector3(x: 0f, y: 1f, z: 0f),
            scale: new Vector3(x: 0.4f, y: 0.4f, z: 0f), type: SdfSolidPrimitive.Prism));
        var (pushes, push) = Agree(centre: V(x: 0d, y: 1d, z: 0.63d), collider: collider, radius: 0.35d);

        Assert.True(condition: pushes);
        Assert.True(condition: (push.Normal.Z > FixedQ4816.FromDouble(value: 0.9d)), userMessage: $"normal {push.Normal}");
    }

    // F2: a capsule scaled alike in X and Z bakes its segment from its origin up 0.5·scale.y with radius scale.x, so a
    // [2, 1, 2] pill reaches from y = -2 to y = 2.5; a ball resting on its top is held up there.
    [Fact]
    public void ABallOnAWideCapsulesTopIsHeldUp() {
        var collider = Collider(margin: 0f, Ground, FarCarve(id: 1), Shape(id: 2, position: Vector3.Zero, scale: new Vector3(x: 2f, y: 1f, z: 2f),
            type: SdfSolidPrimitive.Capsule));
        var (pushes, push) = Agree(centre: V(x: 0d, y: 2.83d, z: 0d), collider: collider, radius: 0.35d);

        Assert.True(condition: pushes);
        Assert.True(condition: (push.Normal.Y > FixedQ4816.FromDouble(value: 0.99d)), userMessage: $"normal {push.Normal}");
    }

    // F3: under a 0.12 m contact margin an ellipsoid carve of radii [4, 1, 4] dilates by its gauge, to x = 4.48 at its
    // equator, not 4.12; a small ball over x = 4.3, whose reach misses the undilated carve, has the ground cut from under it.
    [Fact]
    public void AGaugeScaledCarveUnderAMarginReachesItsDilatedSurface() {
        var collider = Collider(margin: 0.12f, Ground, Shape(blend: SdfBlendOp.Subtraction, id: 1, position: Vector3.Zero,
            scale: new Vector3(x: 4f, y: 1f, z: 4f), type: SdfSolidPrimitive.Ellipsoid));

        Assert.False(condition: Agree(centre: V(x: 4.3d, y: 0.2d, z: 0d), collider: collider, radius: 0.05d).Pushes);
    }
}
