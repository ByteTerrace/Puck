using Puck.Maths;
using Puck.Physics;

namespace Puck.World;

/// <summary>A kit's rigid-dynamics facet: authoring a body as a passive rigid entity — a billiard ball, a bowling
/// pin, a chess piece — rather than a locomotion-driven avatar. Presence is the whole switch: a kit authoring this
/// hands its body's integration to the rigid solver instead of the grounded/free motion program, so <see cref="Mass"/>,
/// <see cref="Restitution"/>, <see cref="Friction"/>, <see cref="RollingFriction"/>, <see cref="LinearDamping"/> and
/// <see cref="AngularDamping"/> are the WHOLE authored physics — nothing here is a C# constant. Mass and inertia derive
/// from <see cref="WorldKit.Collider"/> (required: sphere, capsule, or box — never <c>fromCreation</c>, whose compound
/// shape has no single closed-form inertia here) and <see cref="Mass"/> through <see cref="FixedMassProperties"/>, so
/// authoring only the mass, never the density or the tensor, keeps the derived-limits rule: the SHAPE decides how mass
/// distributes, the author decides only how much of it there is.</summary>
/// <param name="Mass">The body's mass, in the same units <c>gravity.attractors</c> masses use. Must be strictly
/// positive — a rigid body with no mass is not a rigid body, it is a decoration.</param>
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
    private const int MassFractionBitCount = 32;
    private const int InertiaFractionBitCount = 32;

    /// <summary>Compiles an authored <see cref="WorldRigid"/> facet against its kit's already-compiled collider.
    /// Validation (<see cref="WorldDefinitionValidator"/>) has already refused a rigid kit with no collider, a
    /// non-primitive collider, or an out-of-range authored value by the time this runs.</summary>
    /// <param name="rigid">The authored facet.</param>
    /// <param name="collider">The kit's own compiled collider — exactly one volume, a sphere, capsule, or box.</param>
    /// <exception cref="InvalidOperationException">The derived mass properties are not representable at
    /// <see cref="Scales"/> — practically unreachable for the authored range the validator admits, kept as a loud
    /// failure rather than a silently wrong body.</exception>
    public static FixedWorldRigid Compile(WorldRigid rigid, FixedWorldCollider collider) {
        var volume = collider.Volumes[0];

        // FixedMassProperties' own volume kernels are internal to Puck.Maths, so density is instead solved
        // backwards from a UNIT-density body: every Try<Shape>Body formula is exactly linear in density, so the
        // unit-density mass equals the shape's volume at MassFractionBitCount, and scaling the unit-density inertia
        // by (targetMass / unitMass) is the identical result TryXxxBody would report at the solved density —
        // computed once instead of twice.
        const long unitDensity = (1L << MassFractionBitCount);
        long unitMass, unitIxx, unitIyy, unitIzz;
        FixedVector3 centerOffset;
        FixedQ4816 boundingRadius;

        switch (volume.Kind) {
            case FixedBodyColliderKind.Sphere: {
                if (!FixedMassProperties.TrySphereBody(
                    density: unitDensity,
                    fractionBitsDensity: MassFractionBitCount,
                    radius: volume.Radius.Value,
                    fractionBitsLength: FixedQ4816.FractionBitCount,
                    fractionBitsMass: MassFractionBitCount,
                    fractionBitsInertia: InertiaFractionBitCount,
                    mass: out unitMass,
                    inertia: out var inertia
                )) {
                    throw new InvalidOperationException(message: "A rigid sphere's mass properties are not representable.");
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
                    density: unitDensity,
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
                    throw new InvalidOperationException(message: "A rigid capsule's mass properties are not representable.");
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
            case FixedBodyColliderKind.Box: {
                if (!FixedMassProperties.TryBoxBody(
                    density: unitDensity,
                    fractionBitsDensity: MassFractionBitCount,
                    halfX: volume.HalfExtents.X.Value,
                    halfY: volume.HalfExtents.Y.Value,
                    halfZ: volume.HalfExtents.Z.Value,
                    fractionBitsLength: FixedQ4816.FractionBitCount,
                    fractionBitsMass: MassFractionBitCount,
                    fractionBitsInertia: InertiaFractionBitCount,
                    mass: out unitMass,
                    ixx: out unitIxx,
                    iyy: out unitIyy,
                    izz: out unitIzz
                )) {
                    throw new InvalidOperationException(message: "A rigid box's mass properties are not representable.");
                }

                centerOffset = volume.Center;
                boundingRadius = volume.HalfExtents.Length;
                break;
            }
            default:
                throw new InvalidOperationException(message: $"A rigid kit's collider kind '{volume.Kind}' has no closed-form mass properties.");
        }

        // targetMass/unitMass at MassFractionBitCount, then each unit-density inertia component scaled by that
        // ratio — the same result density = targetMass/unitMass fed back into TryXxxBody would produce, since every
        // formula above is linear in density.
        var targetMassRaw = (FixedQ4816.FromDouble(value: rigid.Mass).Value << (MassFractionBitCount - FixedQ4816.FractionBitCount));

        if (
            !FusedArithmetic.TryScaledReciprocal(
            value: unitMass,
            fractionBitsIn: MassFractionBitCount,
            fractionBitsOut: MassFractionBitCount,
            result: out var inverseUnitMass
        ) ||
            !FusedArithmetic.TryMixedScaleProduct(
            a: targetMassRaw,
            fractionBitsA: MassFractionBitCount,
            b: inverseUnitMass,
            fractionBitsB: MassFractionBitCount,
            fractionBitsOut: MassFractionBitCount,
            result: out var massRatio
        ) ||
            !FusedArithmetic.TryMixedScaleProduct(
            a: unitIxx,
            fractionBitsA: InertiaFractionBitCount,
            b: massRatio,
            fractionBitsB: MassFractionBitCount,
            fractionBitsOut: InertiaFractionBitCount,
            result: out var ixx
        ) ||
            !FusedArithmetic.TryMixedScaleProduct(
            a: unitIyy,
            fractionBitsA: InertiaFractionBitCount,
            b: massRatio,
            fractionBitsB: MassFractionBitCount,
            fractionBitsOut: InertiaFractionBitCount,
            result: out var iyy
        ) ||
            !FusedArithmetic.TryMixedScaleProduct(
            a: unitIzz,
            fractionBitsA: InertiaFractionBitCount,
            b: massRatio,
            fractionBitsB: MassFractionBitCount,
            fractionBitsOut: InertiaFractionBitCount,
            result: out var izz
        )
        ) {
            throw new InvalidOperationException(message: "A rigid kit's mass/inertia scaling is not representable.");
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
            throw new InvalidOperationException(message: "A rigid kit's inverse mass/inertia is not representable at the engine's room-scale placement.");
        }

        return new FixedWorldRigid(
            Mass: FixedQ4816.FromDouble(value: rigid.Mass),
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
    }
}
