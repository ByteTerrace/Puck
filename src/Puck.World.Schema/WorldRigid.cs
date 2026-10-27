using Puck.Maths;
using Puck.Physics;

namespace Puck.World;

/// <summary>A kit's rigid-dynamics facet: authoring a body as a passive rigid entity — a billiard ball, a bowling
/// pin, a chess piece — rather than a locomotion-driven avatar. Presence is the whole switch: a kit authoring this
/// hands its body's integration to the rigid solver instead of the grounded/free motion program, so <see cref="Mass"/>,
/// <see cref="Restitution"/>, <see cref="Friction"/>, <see cref="RollingFriction"/>, <see cref="LinearDamping"/> and
/// <see cref="AngularDamping"/> are the WHOLE authored physics — nothing here is a C# constant. Mass and inertia derive
/// from <see cref="WorldKit.Collider"/> (one sphere, capsule, box or ellipsoid volume, including a creation's single
/// primitive) and <see cref="Mass"/> through <see cref="FixedMassProperties"/>, so
/// authoring only the mass, never the density or the tensor, keeps the derived-limits rule: the SHAPE decides how mass
/// distributes, the author decides only how much of it there is.</summary>
/// <param name="Mass">The body's mass, in the same units <c>gravity.attractors</c> masses use. Must be strictly
/// positive after fixed-point compilation and yield representable room-scale mass/inertia properties — a rigid body
/// with no compiled mass is not a rigid body, it is a decoration.</param>
/// <param name="Restitution">The coefficient of restitution against the static world and against another rigid body,
/// in <c>[0, 1]</c>. Zero (the default) is a dead-stop collision; one is a lossless bounce.</param>
/// <param name="Friction">The Coulomb friction coefficient at a contact point, against the static world and — as
/// the pair's average — against another rigid body: the tangential (slip) impulse the contact solver applies is
/// clamped to this times the contact's own normal impulse magnitude, coupled through the body's own inertia rather
/// than decaying linear and angular velocity independently. Applied only while in contact, so it never slows a
/// free-flying body. Non-negative (a coefficient over 1 is physically ordinary); zero (the default) is
/// frictionless.</param>
/// <param name="RollingFriction">The angular-velocity decay rate, per second, applied while a rigid body is in
/// contact with a surface — the resistance that lets a rolling ball settle instead of spinning forever. Non-negative;
/// the applied decay is clamped so a wide step never reverses spin.</param>
/// <param name="LinearDamping">The linear-velocity decay rate, per second — applied as <c>(1 - rate·dt)</c> each
/// tick, so the same authored value decays velocity the same way whatever the world's simulation rate. Non-negative;
/// zero (the default) applies none.</param>
/// <param name="AngularDamping">The angular-velocity decay rate, per second, on the same terms as
/// <see cref="LinearDamping"/>. Non-negative; zero (the default) applies none.</param>
public sealed record WorldRigid(float Mass, float Restitution = 0f, float Friction = 0f, float RollingFriction = 0f, float LinearDamping = 0f, float AngularDamping = 0f);
/// <summary>The one-time fixed-point compilation of a kit's <see cref="WorldRigid"/> facet: derived mass/inertia
/// (via <see cref="FixedMassProperties"/>, from the kit's own compiled collider) already inverted at
/// <see cref="Scales"/>, the collider's own centre-of-mass offset from the body's root (the same offset every
/// collider volume already carries — a sphere/capsule/box rests on the body root, so its solved centre sits above
/// it), and a conservative bounding radius the substep-count derivation reads.</summary>
public readonly record struct FixedWorldRigid(
    FixedQ4816 Mass,
    long InverseMassRaw,
    long InverseInertiaXX,
    long InverseInertiaYY,
    long InverseInertiaZZ,
    FixedQ4816 Restitution,
    FixedQ4816 Friction,
    FixedQ4816 RollingFriction,
    FixedQ4816 LinearDamping,
    FixedQ4816 AngularDamping,
    FixedVector3 CenterOffset,
    FixedQ4816 BoundingRadius
) {
    /// <summary>Where mass/inertia reciprocals are placed — the library's own room-scale placement, which comfortably
    /// covers the census this engine's worlds author (roughly 0.05..50 units of mass at metre scale).</summary>
    public static FixedRigidScales Scales => FixedRigidScales.RoomScale;

    private const int InertiaFractionBitCount = 32;
    private const int MassFractionBitCount = 32;

    /// <summary>Compiles an authored <see cref="WorldRigid"/> facet against its kit's already-compiled collider.
    /// Validation (<see cref="WorldDefinitionValidator"/>) has already proved that the derived room-scale mass
    /// properties are representable by the time this runs.</summary>
    /// <param name="rigid">The authored facet.</param>
    /// <param name="collider">The kit's own compiled collider — exactly one sphere, capsule, box or ellipsoid.</param>
    /// <exception cref="InvalidOperationException">The caller bypassed document validation and the derived mass
    /// properties are not representable at <see cref="Scales"/>.</exception>
    public static FixedWorldRigid Compile(WorldRigid rigid, FixedWorldCollider collider) {
        if (!TryCompile(
            collider: collider,
            compiled: out var compiled,
            reason: out var reason,
            rigid: rigid
        )) {
            throw new InvalidOperationException(message: reason);
        }

        return compiled;
    }
    /// <summary>Attempts the fixed-point mass-property derivation without throwing, so the document validator can
    /// refuse values whose authored primitives or mass leave the engine's room-scale representation.</summary>
    /// <param name="rigid">The authored facet.</param>
    /// <param name="collider">The kit's one already-compiled primitive collider.</param>
    /// <param name="compiled">The compiled facet on success; otherwise the default value.</param>
    /// <param name="reason">The named representation failure; empty on success.</param>
    /// <returns><see langword="true"/> exactly when every derived mass property is representable.</returns>
    public static bool TryCompile(WorldRigid rigid, FixedWorldCollider collider, out FixedWorldRigid compiled, out string reason) {
        compiled = default;

        if (collider.Volumes is not { Length: 1 }) {
            reason = "A rigid kit requires exactly one primitive collider volume.";
            return false;
        }

        var fixedMaximum = ((double)FixedQ4816.MaxValue);

        if (
            !float.IsFinite(f: rigid.Mass) ||
            !float.IsFinite(f: rigid.Restitution) ||
            !float.IsFinite(f: rigid.Friction) ||
            !float.IsFinite(f: rigid.RollingFriction) ||
            !float.IsFinite(f: rigid.LinearDamping) ||
            !float.IsFinite(f: rigid.AngularDamping) ||
            (Math.Abs(value: rigid.Mass) > fixedMaximum) ||
            (Math.Abs(value: rigid.Restitution) > fixedMaximum) ||
            (Math.Abs(value: rigid.Friction) > fixedMaximum) ||
            (Math.Abs(value: rigid.RollingFriction) > fixedMaximum) ||
            (Math.Abs(value: rigid.LinearDamping) > fixedMaximum) ||
            (Math.Abs(value: rigid.AngularDamping) > fixedMaximum)
        ) {
            reason = "A rigid kit's authored coefficients leave the engine's fixed-point representation.";
            return false;
        }

        var volume = collider.Volumes[0];

        // FixedMassProperties' own volume kernels are internal to Puck.Maths, so density is instead solved
        // backwards from a UNIT-density body: every Try<Shape>Body formula is exactly linear in density, so the
        // unit-density mass equals the shape's volume at MassFractionBitCount, and scaling the unit-density inertia
        // by (targetMass / unitMass) is the identical result TryXxxBody would report at the solved density —
        // computed once instead of twice.
        const long UnitDensity = (1L << MassFractionBitCount);
        long unitMass, unitIxx, unitIyy, unitIzz;
        var unitInertiaFractionBits = InertiaFractionBitCount;
        FixedVector3 centerOffset;
        FixedQ4816 boundingRadius;

        switch (volume.Kind) {
            case FixedBodyColliderKind.Sphere: {
                    if (!FixedMassProperties.TrySphereBody(
                        density: UnitDensity,
                        fractionBitsDensity: MassFractionBitCount,
                        radius: volume.Radius.Value,
                        fractionBitsLength: FixedQ4816.FractionBitCount,
                        fractionBitsMass: MassFractionBitCount,
                        fractionBitsInertia: InertiaFractionBitCount,
                        mass: out unitMass,
                        inertia: out var inertia
                    )) {
                        reason = "A rigid sphere's mass properties are not representable.";
                        return false;
                    }

                    unitIxx = inertia;
                    unitIyy = inertia;
                    unitIzz = inertia;
                    centerOffset = volume.Center;
                    boundingRadius = volume.Radius;
                    break;
                }
            case FixedBodyColliderKind.Capsule: {
                    var segment = (volume.Endpoint - volume.Center);
                    var centerDistance = segment.Length;

                    if (!FixedMassProperties.TryCapsuleBody(
                        density: UnitDensity,
                        fractionBitsDensity: MassFractionBitCount,
                        radius: volume.Radius.Value,
                        centerDistance: centerDistance.Value,
                        fractionBitsLength: FixedQ4816.FractionBitCount,
                        fractionBitsMass: MassFractionBitCount,
                        fractionBitsInertia: InertiaFractionBitCount,
                        mass: out unitMass,
                        axial: out var axial,
                        perpendicular: out var perpendicular
                    )) {
                        reason = "A rigid capsule's mass properties are not representable.";
                        return false;
                    }

                    // The capsule collider's own axis is world/body Y (see FixedWorldCollider.Compile); the mass kernel
                    // derives about its own Y axis too, so axial maps straight to Y and perpendicular to X/Z.
                    unitIxx = perpendicular;
                    unitIyy = axial;
                    unitIzz = perpendicular;
                    centerOffset = (volume.Center + (segment / FixedQ4816.FromInteger(value: 2L)));
                    boundingRadius = (volume.Radius + (centerDistance / FixedQ4816.FromInteger(value: 2L)));
                    break;
                }
            case FixedBodyColliderKind.Ellipsoid:
            case FixedBodyColliderKind.Box: {
                    // At a fixed mass an ellipsoid's diagonal moments are 3/5 of a box's with the same
                    // radii. Keep extra bits in the unit-density moments before the mass scaling; small
                    // round bodies otherwise lose much of their inertia to the unit-density Q32 grid.
                    var ellipsoid = (volume.Kind == FixedBodyColliderKind.Ellipsoid);
                    var massRadii = volume.HalfExtents;

                    if (ellipsoid && !TryPrincipalEllipsoidRadii(radii: out massRadii, volume: volume)) {
                        reason = "A rigid ellipsoid's creation-local rotation does not align its principal inertia axes with the body; rotate the body instead of the primitive.";
                        return false;
                    }
                    if (ellipsoid) { unitInertiaFractionBits = 48; }
                    if (!FixedMassProperties.TryBoxBody(
                        density: UnitDensity,
                        fractionBitsDensity: MassFractionBitCount,
                        halfX: massRadii.X.Value,
                        halfY: massRadii.Y.Value,
                        halfZ: massRadii.Z.Value,
                        fractionBitsLength: FixedQ4816.FractionBitCount,
                        fractionBitsMass: MassFractionBitCount,
                        fractionBitsInertia: unitInertiaFractionBits,
                        mass: out unitMass,
                        ixx: out unitIxx,
                        iyy: out unitIyy,
                        izz: out unitIzz
                    )) {
                        reason = "A rigid box or ellipsoid's mass properties are not representable.";
                        return false;
                    }

                    if (ellipsoid) {
                        unitIxx = ((long)((((Int128)unitIxx) * 3) / 5));
                        unitIyy = ((long)((((Int128)unitIyy) * 3) / 5));
                        unitIzz = ((long)((((Int128)unitIzz) * 3) / 5));
                    }

                    centerOffset = volume.Center;
                    boundingRadius = (ellipsoid ? FixedQ4816.Max(x: volume.HalfExtents.X,
                        y: FixedQ4816.Max(x: volume.HalfExtents.Y, y: volume.HalfExtents.Z)) : volume.HalfExtents.Length);
                    break;
                }
            default:
                reason = $"A rigid kit's collider kind '{volume.Kind}' has no closed-form mass properties.";
                return false;
        }

        // targetMass/unitMass at MassFractionBitCount, then each unit-density inertia component scaled by that
        // ratio — the same result density = targetMass/unitMass fed back into TryXxxBody would produce, since every
        // formula above is linear in density.
        var targetMass = FixedQ4816.FromDouble(value: rigid.Mass);
        const int MassScaleShift = (MassFractionBitCount - FixedQ4816.FractionBitCount);

        if (
            (targetMass <= FixedQ4816.Zero) ||
            (targetMass.Value > (long.MaxValue >> MassScaleShift))
        ) {
            reason = "A rigid kit's authored mass is not representable at the engine's mass scale.";
            return false;
        }

        var targetMassRaw = (targetMass.Value << MassScaleShift);

        if (
            !FusedArithmetic.TryScaledReciprocal(
            fractionBitsIn: MassFractionBitCount,
            fractionBitsOut: MassFractionBitCount,
            result: out var inverseUnitMass,
            value: unitMass
        ) ||
            !FusedArithmetic.TryMixedScaleProduct(
            a: targetMassRaw,
            b: inverseUnitMass,
            fractionBitsA: MassFractionBitCount,
            fractionBitsB: MassFractionBitCount,
            fractionBitsOut: MassFractionBitCount,
            result: out var massRatio
        ) ||
            !FusedArithmetic.TryMixedScaleProduct(
            a: unitIxx,
            b: massRatio,
            fractionBitsA: unitInertiaFractionBits,
            fractionBitsB: MassFractionBitCount,
            fractionBitsOut: InertiaFractionBitCount,
            result: out var ixx
        ) ||
            !FusedArithmetic.TryMixedScaleProduct(
            a: unitIyy,
            b: massRatio,
            fractionBitsA: unitInertiaFractionBits,
            fractionBitsB: MassFractionBitCount,
            fractionBitsOut: InertiaFractionBitCount,
            result: out var iyy
        ) ||
            !FusedArithmetic.TryMixedScaleProduct(
            a: unitIzz,
            b: massRatio,
            fractionBitsA: unitInertiaFractionBits,
            fractionBitsB: MassFractionBitCount,
            fractionBitsOut: InertiaFractionBitCount,
            result: out var izz
        )
        ) {
            reason = "A rigid kit's mass/inertia scaling is not representable.";
            return false;
        }

        var mass = targetMassRaw;

        if (
            !FixedMassProperties.TryInvertMass(
            mass: mass,
            fractionBitsMass: MassFractionBitCount,
            fractionBitsOut: Scales.InverseMass,
            inverseMass: out var inverseMass
        ) ||
            !FixedMassProperties.TryInvertInertia(
            ixx: ixx,
            iyy: iyy,
            izz: izz,
            ixy: 0L,
            ixz: 0L,
            iyz: 0L,
            fractionBitsInertia: InertiaFractionBitCount,
            fractionBitsOut: Scales.InverseInertia,
            invXX: out var invXX,
            invYY: out var invYY,
            invZZ: out var invZZ,
            invXY: out _,
            invXZ: out _,
            invYZ: out _
        )
        ) {
            reason = "A rigid kit's inverse mass/inertia is not representable at the engine's room-scale placement.";
            return false;
        }

        compiled = new FixedWorldRigid(
            Mass: targetMass,
            InverseMassRaw: inverseMass,
            InverseInertiaXX: invXX,
            InverseInertiaYY: invYY,
            InverseInertiaZZ: invZZ,
            Restitution: FixedQ4816.FromDouble(value: rigid.Restitution),
            Friction: FixedQ4816.FromDouble(value: rigid.Friction),
            RollingFriction: FixedQ4816.FromDouble(value: rigid.RollingFriction),
            LinearDamping: FixedQ4816.FromDouble(value: rigid.LinearDamping),
            AngularDamping: FixedQ4816.FromDouble(value: rigid.AngularDamping),
            CenterOffset: centerOffset,
            BoundingRadius: boundingRadius
        );
        reason = "";
        return true;
    }

    // On a principal axis an ellipsoid's support point lies on that axis. This also admits rotations
    // inside an equal-radius plane and permutations of the principal axes; the resulting body-axis
    // radii, rather than the primitive's original ordering, determine its diagonal inertia. The
    // tolerance covers two fixed-point rotations and support normalization, not physical tilt.
    private static bool TryPrincipalEllipsoidRadii(FixedBodyColliderVolume volume, out FixedVector3 radii) {
        var x = (FixedRigidWitness.LocalSupportPoint(volume, FixedVector3.UnitX) - volume.Center);
        var y = (FixedRigidWitness.LocalSupportPoint(volume, FixedVector3.UnitY) - volume.Center);
        var z = (FixedRigidWitness.LocalSupportPoint(volume, FixedVector3.UnitZ) - volume.Center);
        var tolerance = (FixedQ4816.Epsilon * FixedQ4816.FromInteger(value: 2));

        radii = new FixedVector3(X: FixedQ4816.Abs(value: x.X), Y: FixedQ4816.Abs(value: y.Y), Z: FixedQ4816.Abs(value: z.Z));
        return ((FixedQ4816.Abs(value: x.Y) <= tolerance) && (FixedQ4816.Abs(value: x.Z) <= tolerance) &&
            (FixedQ4816.Abs(value: y.X) <= tolerance) && (FixedQ4816.Abs(value: y.Z) <= tolerance) &&
            (FixedQ4816.Abs(value: z.X) <= tolerance) && (FixedQ4816.Abs(value: z.Y) <= tolerance));
    }
}
