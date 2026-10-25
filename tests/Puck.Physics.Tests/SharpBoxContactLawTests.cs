using Puck.Maths;

namespace Puck.Physics.Tests;

public sealed class SharpBoxContactLawTests {
    private static FixedQ4816 Q(double value) => FixedQ4816.FromDouble(value: value);
    private static FixedVector3 V(double x, double y, double z) => new(X: Q(value: x), Y: Q(value: y), Z: Q(value: z));

    [InlineData(false)]
    [InlineData(true)]
    [Theory]
    public void AThinBoxMeetsTheCornerWithItsOwnFaceAndLeavesEmptyBoundsClear(bool turned) {
        var rotation = (turned ? FixedQuaternion.FromAxisAngle(FixedVector3.UnitY, Q(value: 0.4)) : FixedQuaternion.Identity);
        var wall = FixedStaticCollider.Box(FixedVector3.Zero, rotation, V(x: 1, y: 1, z: 1));
        var body = new FixedBodyColliderVolume(FixedBodyColliderKind.Box, FixedVector3.Zero, FixedVector3.Zero,
            V(x: 1, y: 0.1, z: 0.1), FixedQuaternion.FromAxisAngle(FixedVector3.UnitY, FixedQ4816.FromDegrees(degrees: 45)), FixedQ4816.Zero);

        Assert.False(condition: wall.TryGetPush(rotation.Rotate(vector: V(x: 1.5, y: 0, z: 1.5)), rotation, body, FixedQ4816.Zero, out _));
        var position = rotation.Rotate(vector: V(x: 1.04, y: 0, z: 1.04));

        Assert.True(condition: wall.TryGetPush(position, rotation, body, FixedQ4816.Zero, out var push));
        var expectedNormal = rotation.Rotate(vector: V(x: 1, y: 0, z: 1).Normalize());

        Assert.InRange((push.Normal - expectedNormal).Length, FixedQ4816.Zero, Q(value: 0.0002));
        Assert.InRange(push.Penetration, Q(value: 0.0432), Q(value: 0.0437));
        Assert.False(condition: wall.TryGetPush((position + (push.Normal * (push.Penetration + Q(value: 0.001)))), rotation,
            body, FixedQ4816.Zero, out _));
    }
    [InlineData(false)]
    [InlineData(true)]
    [Theory]
    public void DynamicCapsuleBoxContactSharesTheStaticCornerAndReversesWithBodyOrder(bool turned) {
        var rotation = (turned ? FixedQuaternion.FromAxisAngle(FixedVector3.UnitY, Q(value: 0.4)) : FixedQuaternion.Identity);
        var capsule = new FixedBodyColliderVolume(FixedBodyColliderKind.Capsule, V(x: 0, y: -0.5, z: 0), V(x: 0, y: 0.5, z: 0),
            FixedVector3.Zero, FixedQuaternion.Identity, Q(value: 0.35));
        var box = new FixedBodyColliderVolume(FixedBodyColliderKind.Box, V(x: 0.1, y: 0, z: -0.2), FixedVector3.Zero,
            V(x: 1, y: 1, z: 1), FixedQuaternion.Identity, FixedQ4816.Zero);
        var boxPosition = -rotation.Rotate(vector: box.Center);
        var wall = FixedStaticCollider.Box(FixedVector3.Zero, rotation, box.HalfExtents);
        var position = rotation.Rotate(vector: V(x: 1.2, y: 0, z: 1.2));

        Assert.True(condition: FixedDynamicBodyContacts.TryCorrection(correction: out var correction, leftOrientation: rotation, leftPosition: position, leftVolumes: [capsule], rightOrientation: rotation,
            rightPosition: boxPosition, rightVolumes: [box], tieBreaker: 0));
        Assert.True(condition: wall.TryGetPush(position, rotation, capsule, FixedQ4816.Zero, out var push));
        Assert.Equal((push.Normal * push.Penetration), correction);
        Assert.InRange(correction.Length, Q(value: 0.0670), Q(value: 0.0673));
        Assert.InRange((correction.Normalize() - rotation.Rotate(vector: V(x: 1, y: 0, z: 1).Normalize())).Length, FixedQ4816.Zero, Q(value: 0.0003));
        Assert.True(condition: FixedDynamicBodyContacts.TryCorrection(correction: out var reversed, leftOrientation: rotation, leftPosition: boxPosition, leftVolumes: [box], rightOrientation: rotation,
            rightPosition: position, rightVolumes: [capsule], tieBreaker: 0));
        Assert.Equal(actual: reversed, expected: -correction);
        Assert.False(condition: FixedDynamicBodyContacts.TryCorrection(rotation.Rotate(vector: V(x: 1.3, y: 0, z: 1.3)), rotation, [capsule],
            boxPosition, rotation, [box], 0, out _));
        Assert.False(condition: FixedDynamicBodyContacts.TryCorrection(((position + correction) + (correction.Normalize() * Q(value: 0.001))),
            rotation, [capsule], boxPosition, rotation, [box], 0, out _));
    }
}
