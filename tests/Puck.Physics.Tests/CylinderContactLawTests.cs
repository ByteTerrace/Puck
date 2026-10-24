using Puck.Maths;

namespace Puck.Physics.Tests;

public sealed class CylinderContactLawTests {
    private static FixedQ4816 Q(double value) => FixedQ4816.FromDouble(value: value);
    private static FixedVector3 V(double x, double y, double z) => new(X: Q(value: x), Y: Q(value: y), Z: Q(value: z));

    private static readonly FixedStaticCollider Disc = FixedStaticCollider.Cylinder(
        center: FixedVector3.Zero, radius: Q(value: 1), halfHeight: Q(value: 0.1), rotation: FixedQuaternion.Identity);
    private static readonly FixedBodyColliderVolume Capsule = new(Kind: FixedBodyColliderKind.Capsule,
        Center: V(x: 0, y: 0.35, z: 0), Endpoint: V(x: 0, y: 1.7, z: 0), HalfExtents: FixedVector3.Zero,
        Rotation: FixedQuaternion.Identity, Radius: Q(value: 0.35));

    [Fact]
    public void ARoundDaisHasNoSquareCornerAndItsRimReturnsTheClosestPointNormal() {
        Assert.False(condition: Disc.TryGetPush(position: V(x: 1.1, y: 0, z: 1.1), orientation: FixedQuaternion.Identity,
            volume: Capsule, skin: FixedQ4816.Zero, push: out _));
        Assert.True(condition: Disc.TryGetPush(position: V(x: 0, y: 0.09, z: 0), orientation: FixedQuaternion.Identity,
            volume: Capsule, skin: FixedQ4816.Zero, push: out var cap));
        Assert.Equal(FixedVector3.UnitY, cap.Normal);
        Assert.InRange(((double)cap.Penetration), 0.0099, 0.0101);
        Assert.True(condition: Disc.TryGetPush(position: V(x: 1.1, y: 0, z: 0), orientation: FixedQuaternion.Identity,
            volume: Capsule, skin: FixedQ4816.Zero, push: out var rim));
        Assert.True(condition: ((rim.Normal.X > FixedQ4816.Zero) && (rim.Normal.Y > FixedQ4816.Zero)));
        Assert.InRange(((double)rim.Penetration), 0.0806, 0.0809);
    }
}
