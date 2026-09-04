using Puck.Maths;

namespace Puck.Physics.Tests;

/// <summary>Laws for <see cref="FixedRigidWitness"/>: an off-centre witness anchor carries a lever arm a normal
/// impulse can turn into torque, and a box's own support manifold always spans its centre of mass.</summary>
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

    [Fact]
    public void SupportManifoldSpansTheBoxsCentreOfMassAndControlSphereIsOnePoint() {
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
            centerOffset: boxVolume.Center,
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

        // Every anchor is relative to the body's own centre of mass, so the centre sitting inside the polygon the
        // four corners span is exactly (minX <= 0 <= maxX) and (minZ <= 0 <= maxZ) — a body merely touching (never
        // overlapping) its support has no manifold point left to close against once it tips past this, which is what
        // keeps it upright without an artificial damping term.
        Assert.True(condition: ((minX <= FixedQ4816.Zero) && (FixedQ4816.Zero <= maxX)), userMessage: $"X span [{(double)minX:0.###}, {(double)maxX:0.###}] does not bracket the centre of mass");
        Assert.True(condition: ((minZ <= FixedQ4816.Zero) && (FixedQ4816.Zero <= maxZ)), userMessage: $"Z span [{(double)minZ:0.###}, {(double)maxZ:0.###}] does not bracket the centre of mass");

        // Control: a sphere has no support polygon — the identical call degenerates to the single witness point
        // Anchor() itself computes, never a four-point manifold.
        var sphereVolume = new FixedBodyColliderVolume(
            Kind: FixedBodyColliderKind.Sphere,
            Center: new FixedVector3(X: FixedQ4816.Zero, Y: half, Z: FixedQ4816.Zero),
            Endpoint: FixedVector3.Zero,
            HalfExtents: FixedVector3.Zero,
            Rotation: FixedQuaternion.Identity,
            Radius: half
        );
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
