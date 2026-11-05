using Puck.Maths;

namespace Puck.Physics.Tests;

// A collider's oriented frame lets the solver skip its exact query, so every pose the frame separates must be one the
// exact query finds no push for, whatever the collider's turn, size and distance from the origin; the hashes of the
// worlds that run cannot reach every pose near a turned surface.
public sealed class FixedColliderFrameLawTests {
    private static readonly FixedQ4816 Skin = FixedQ4816.FromDouble(value: 0.02d);

    private static double Next(ref Pcg32XshRr random, double minimum, double maximum) =>
        (minimum + ((maximum - minimum) * (random.NextUInt32() / 4294967296d)));
    private static FixedVector3 Vector(double x, double y, double z) =>
        new(X: FixedQ4816.FromDouble(value: x), Y: FixedQ4816.FromDouble(value: y), Z: FixedQ4816.FromDouble(value: z));
    private static FixedStaticCollider Collider(ref Pcg32XshRr random, FixedStaticColliderKind kind) {
        var center = Vector(x: Next(random: ref random, minimum: -200d, maximum: 200d), y: Next(random: ref random, minimum: -20d, maximum: 20d),
            z: Next(random: ref random, minimum: -200d, maximum: 200d));
        var rotation = FixedQuaternion.FromYawPitchRoll(yaw: FixedQ4816.FromDouble(value: Next(random: ref random, minimum: -3.1d, maximum: 3.1d)),
            pitch: FixedQ4816.FromDouble(value: Next(random: ref random, minimum: -1.5d, maximum: 1.5d)),
            roll: FixedQ4816.FromDouble(value: Next(random: ref random, minimum: -1.5d, maximum: 1.5d)));
        var extent = Vector(x: Next(random: ref random, minimum: 0.2d, maximum: 6d), y: Next(random: ref random, minimum: 0.05d, maximum: 3d),
            z: Next(random: ref random, minimum: 0.2d, maximum: 6d));

        return kind switch {
            FixedStaticColliderKind.OrientedBox => FixedStaticCollider.Box(center: center, rotation: rotation, halfExtents: extent),
            FixedStaticColliderKind.RoundedBox => FixedStaticCollider.RoundedBox(center: center, rotation: rotation, innerHalfExtents: extent,
                radius: FixedQ4816.FromDouble(value: Next(random: ref random, minimum: 0.01d, maximum: 0.3d))),
            FixedStaticColliderKind.Cylinder => FixedStaticCollider.Cylinder(center: center, radius: extent.X, halfHeight: extent.Y, rotation: rotation),
            FixedStaticColliderKind.ChamferedBox => FixedStaticCollider.ChamferedBox(center: center, rotation: rotation, innerHalfExtents: extent,
                chamfer: FixedQ4816.FromDouble(value: 0.04d), radius: FixedQ4816.FromDouble(value: 0.02d)),
            FixedStaticColliderKind.Superellipsoid => FixedStaticCollider.Superellipsoid(center: center, rotation: rotation, radii: extent,
                exponent: FixedQ4816.FromInteger(value: 4)),
            _ => throw new ArgumentOutOfRangeException(paramName: nameof(kind)),
        };
    }
    private static FixedBodyColliderVolume Volume(FixedBodyColliderKind kind) => kind switch {
        FixedBodyColliderKind.Capsule => new(Kind: kind, Center: Vector(x: 0d, y: 0.35d, z: 0d), Endpoint: Vector(x: 0d, y: 1.71d, z: 0d),
            HalfExtents: FixedVector3.Zero, Rotation: FixedQuaternion.Identity, Radius: FixedQ4816.FromDouble(value: 0.35d)),
        FixedBodyColliderKind.Sphere => new(Kind: kind, Center: Vector(x: 0d, y: 0.25d, z: 0d), Endpoint: FixedVector3.Zero,
            HalfExtents: FixedVector3.Zero, Rotation: FixedQuaternion.Identity, Radius: FixedQ4816.FromDouble(value: 0.25d)),
        FixedBodyColliderKind.Box => new(Kind: kind, Center: Vector(x: 0d, y: 0.2d, z: 0d), Endpoint: FixedVector3.Zero,
            HalfExtents: Vector(x: 0.3d, y: 0.2d, z: 0.15d), Rotation: FixedQuaternion.Identity, Radius: FixedQ4816.Zero),
        FixedBodyColliderKind.Ellipsoid => new(Kind: kind, Center: Vector(x: 0d, y: 0.3d, z: 0d), Endpoint: FixedVector3.Zero,
            HalfExtents: Vector(x: 0.4d, y: 0.3d, z: 0.2d), Rotation: FixedQuaternion.Identity, Radius: FixedQ4816.Zero),
        _ => throw new ArgumentOutOfRangeException(paramName: nameof(kind)),
    };

    [InlineData(FixedStaticColliderKind.OrientedBox)]
    [InlineData(FixedStaticColliderKind.RoundedBox)]
    [InlineData(FixedStaticColliderKind.Cylinder)]
    [InlineData(FixedStaticColliderKind.ChamferedBox)]
    [InlineData(FixedStaticColliderKind.Superellipsoid)]
    [Theory]
    public void EveryPoseTheFrameSeparatesTakesNoPush(FixedStaticColliderKind kind) {
        var random = Pcg32XshRr.Create(state: 0x5EEDUL, stream: ((ulong)kind));
        var separated = 0;
        var samples = 0;

        for (var shape = 0; (shape < 24); shape++) {
            var collider = Collider(kind: kind, random: ref random);
            var frame = FixedColliderFrame.Of(collider: in collider);

            if (!frame.IsOriented) { continue; }
            var reach = frame.Reach;

            foreach (var volumeKind in (ReadOnlySpan<FixedBodyColliderKind>)[FixedBodyColliderKind.Capsule, FixedBodyColliderKind.Sphere,
                FixedBodyColliderKind.Box, FixedBodyColliderKind.Ellipsoid]) {
                var volume = Volume(kind: volumeKind);

                for (var pose = 0; (pose < 40); pose++) {
                    // Poses near the surface: a local point within the reach plus the body's size and a little more.
                    var local = Vector(x: Next(random: ref random, minimum: -((double)reach.X + 2.2d), maximum: ((double)reach.X + 2.2d)),
                        y: Next(random: ref random, minimum: -((double)reach.Y + 2.2d), maximum: ((double)reach.Y + 2.2d)),
                        z: Next(random: ref random, minimum: -((double)reach.Z + 2.2d), maximum: ((double)reach.Z + 2.2d)));
                    var position = (collider.Center + collider.Rotation.Rotate(vector: local));
                    var orientation = FixedQuaternion.FromYawPitchRoll(yaw: FixedQ4816.FromDouble(value: Next(random: ref random, minimum: -3.1d, maximum: 3.1d)),
                        pitch: FixedQ4816.FromDouble(value: Next(random: ref random, minimum: -0.4d, maximum: 0.4d)), roll: FixedQ4816.Zero);
                    var (center, extent) = FixedColliderBounds.WorldBounds(orientation: in orientation, position: position, volume: in volume);

                    samples++;
                    if (!frame.Separates(bodyExtent: in extent, offset: (center - collider.Center), skin: Skin)) { continue; }
                    separated++;
                    Assert.False(condition: collider.TryGetPush(orientation: in orientation, position: position, push: out var push, skin: Skin,
                        uncertain: out _, volume: in volume), userMessage: $"{kind} {volumeKind} shape {shape} pose {pose}: pushed {push.Penetration}");
                }
            }
        }
        // The draw reaches both sides of the certificate.
        Assert.InRange(actual: separated, low: (samples / 10), high: (samples - (samples / 10)));
    }
}
