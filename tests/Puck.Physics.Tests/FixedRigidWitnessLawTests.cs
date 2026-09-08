using Puck.Maths;

namespace Puck.Physics.Tests;

/// <summary>Laws for <see cref="FixedRigidWitness"/>: an off-centre witness anchor carries a lever arm a normal
/// impulse can turn into torque, and a box's support manifold reports whether its centre of mass lies over the
/// contact-anchor span.</summary>
public sealed class FixedRigidWitnessLawTests {
    private static readonly FixedRigidScales Scales = new(
        EffectiveMass: 32,
        InverseInertia: 40,
        InverseMass: 40
    );

    // A uniform cube of side 1 about its own centre: I = m/6 per axis — the same hand-derived shape
    // TwoBodyKernelLawTests uses; a kernel-level law needs some valid inverse mass/inertia, not a bit-exact one.
    private static FixedRigidBody MakeBox(double density) {
        var mass = density;
        var inertiaAxis = (mass / 6d);

        return new() {
            InverseMassRaw = ToRaw(value: (1d / mass), fractionBits: Scales.InverseMass),
            InverseInertiaXX = ToRaw(value: (1d / inertiaAxis), fractionBits: Scales.InverseInertia),
            InverseInertiaYY = ToRaw(value: (1d / inertiaAxis), fractionBits: Scales.InverseInertia),
            InverseInertiaZZ = ToRaw(value: (1d / inertiaAxis), fractionBits: Scales.InverseInertia),
        };
    }
    private static long ToRaw(double value, int fractionBits) =>
        ((long)Math.Round(a: (value * Math.Pow(x: 2d, y: fractionBits))));

    [Fact]
    public void OffCenterWitnessAnchorImpartsTorqueAndControlCenteredAnchorDoesNot() {
        var half = FixedQ4816.FromDouble(value: 0.5d);
        var volume = new FixedBodyColliderVolume(
            Kind: FixedBodyColliderKind.Box,
            Center: new FixedVector3(X: FixedQ4816.Zero, Y: half, Z: FixedQ4816.Zero),
            Endpoint: FixedVector3.Zero,
            HalfExtents: new FixedVector3(X: half, Y: half, Z: half),
            Rotation: FixedQuaternion.Identity,
            Radius: FixedQ4816.Zero
        );
        var normal = new FixedVector3(X: FixedQ4816.Zero, Y: FixedQ4816.One, Z: FixedQ4816.Zero);
        var witnessAnchor = FixedRigidWitness.Anchor(
            centerOffset: volume.Center,
            orientation: FixedQuaternion.Identity,
            volume: volume,
            worldDirection: -normal
        );

        // The box's bottom face has four corners equally extreme along -normal; the witness point lands on one of
        // them, off both the X and Z axes through the centre of mass — never the zero anchor a torque-free response
        // would use.
        Assert.NotEqual(expected: FixedVector3.Zero, actual: witnessAnchor);

        var ground = new FixedRigidBody();
        var box = MakeBox(density: 60d);
        var refusals = 0;

        FixedTwoBodyKernel.ApplyImpulse(
            bodyA: ground,
            anchorA: FixedVector3.Zero,
            bodyB: box,
            anchorB: witnessAnchor,
            normal: normal,
            impulseRaw: 500_000L,
            scales: Scales,
            refusals: ref refusals
        );

        Assert.Equal(expected: 0, actual: refusals);
        Assert.NotEqual(expected: FixedVector3.Zero, actual: box.AngularVelocity);

        // Control: the identical impulse at the CENTRE (zero) anchor — the bounding-sphere-style approximation this
        // witness point replaces effectively used whenever the struck surface passed through the centre — carries no
        // lever arm and so imparts no spin at all.
        var groundControl = new FixedRigidBody();
        var boxControl = MakeBox(density: 60d);
        var controlRefusals = 0;

        FixedTwoBodyKernel.ApplyImpulse(
            bodyA: groundControl,
            anchorA: FixedVector3.Zero,
            bodyB: boxControl,
            anchorB: FixedVector3.Zero,
            normal: normal,
            impulseRaw: 500_000L,
            scales: Scales,
            refusals: ref controlRefusals
        );

        Assert.Equal(expected: 0, actual: controlRefusals);
        Assert.Equal(expected: FixedVector3.Zero, actual: boxControl.AngularVelocity);
    }

    // The manifold's own anchor span decides whether the box's centre of mass sits over its support: computes the
    // bottom-face manifold for a unit box centred at (0, half, 0) against a live, off-Center centreOffset and returns
    // whether the (minX, maxX) x (minZ, maxZ) span the four anchors trace brackets the origin — the local frame every
    // anchor is expressed in (see FixedRigidWitness.Anchor). Shared by the in-footprint case (must bracket) and its
    // negative control (a centreOffset outside the footprint — must NOT).
    private static bool BottomManifoldBracketsOrigin(FixedVector3 centerOffset) {
        var half = FixedQ4816.FromDouble(value: 0.5d);
        var boxVolume = new FixedBodyColliderVolume(
            Kind: FixedBodyColliderKind.Box,
            Center: new FixedVector3(X: FixedQ4816.Zero, Y: half, Z: FixedQ4816.Zero),
            Endpoint: FixedVector3.Zero,
            HalfExtents: new FixedVector3(X: half, Y: half, Z: half),
            Rotation: FixedQuaternion.Identity,
            Radius: FixedQ4816.Zero
        );
        var normal = new FixedVector3(X: FixedQ4816.Zero, Y: FixedQ4816.One, Z: FixedQ4816.Zero);
        Span<FixedVector3> anchors = stackalloc FixedVector3[4];
        var count = FixedRigidWitness.SupportManifold(
            anchors: anchors,
            centerOffset: centerOffset,
            normal: normal,
            orientation: FixedQuaternion.Identity,
            volume: boxVolume
        );

        Assert.Equal(expected: 4, actual: count);

        var minX = anchors[0].X;
        var maxX = anchors[0].X;
        var minZ = anchors[0].Z;
        var maxZ = anchors[0].Z;

        for (var index = 1; (index < 4); index++) {
            minX = FixedQ4816.Min(x: minX, y: anchors[index].X);
            maxX = FixedQ4816.Max(x: maxX, y: anchors[index].X);
            minZ = FixedQ4816.Min(x: minZ, y: anchors[index].Z);
            maxZ = FixedQ4816.Max(x: maxZ, y: anchors[index].Z);
        }

        return (
            (minX <= FixedQ4816.Zero) &&
            (FixedQ4816.Zero <= maxX) &&
            (minZ <= FixedQ4816.Zero) &&
            (FixedQ4816.Zero <= maxZ)
        );
    }

    [Fact]
    public void SupportManifoldSpansAnOffCentreMassAndControlPastTheEdgeDoesNot() {
        // The centre of mass is offset from the box's own geometric centre (a hollowed or asymmetrically loaded
        // body) but still over its footprint (|0.2| < half.X, |0.1| < half.Z) — every anchor is relative to THIS
        // offset, not the collider's Center, so a build that read the wrong reference point would shift the whole
        // span off zero rather than merely re-centring it symmetrically (a centred CoM brackets the origin whether
        // or not the offset is actually applied, so it alone cannot catch that class of bug).
        Assert.True(
            condition: BottomManifoldBracketsOrigin(centerOffset: new FixedVector3(
                X: FixedQ4816.FromDouble(value: 0.2d),
                Y: FixedQ4816.FromDouble(value: 0.5d),
                Z: FixedQ4816.FromDouble(value: -0.1d)
            )),
            userMessage: "an in-footprint centre of mass must sit inside its own support manifold"
        );

        // Control: the centre of mass moved PAST the box's own half-extent (0.8 > half.X 0.5) — physically, the body
        // has already tipped past its support and there is no manifold point left on the near side to close against.
        // The span must NOT bracket the origin here; a manifold build that always brackets (the historical bug this
        // law now catches: a centred CoM control brackets trivially, in-footprint or not) would fail this assertion.
        Assert.False(
            condition: BottomManifoldBracketsOrigin(centerOffset: new FixedVector3(
                X: FixedQ4816.FromDouble(value: 0.8d),
                Y: FixedQ4816.FromDouble(value: 0.5d),
                Z: FixedQ4816.Zero
            )),
            userMessage: "a centre of mass past the box's own half-extent must NOT read as supported"
        );
    }

    [Fact]
    public void SupportManifoldControlSphereIsAlwaysOnePoint() {
        // Control: a sphere has no support polygon — the identical call degenerates to the single witness point
        // Anchor() itself computes, never a four-point manifold.
        var half = FixedQ4816.FromDouble(value: 0.5d);
        var sphereVolume = new FixedBodyColliderVolume(
            Kind: FixedBodyColliderKind.Sphere,
            Center: new FixedVector3(X: FixedQ4816.Zero, Y: half, Z: FixedQ4816.Zero),
            Endpoint: FixedVector3.Zero,
            HalfExtents: FixedVector3.Zero,
            Rotation: FixedQuaternion.Identity,
            Radius: half
        );
        var normal = new FixedVector3(X: FixedQ4816.Zero, Y: FixedQ4816.One, Z: FixedQ4816.Zero);
        Span<FixedVector3> sphereAnchors = stackalloc FixedVector3[4];
        var sphereCount = FixedRigidWitness.SupportManifold(
            anchors: sphereAnchors,
            centerOffset: sphereVolume.Center,
            normal: normal,
            orientation: FixedQuaternion.Identity,
            volume: sphereVolume
        );

        Assert.Equal(expected: 1, actual: sphereCount);
    }
}
