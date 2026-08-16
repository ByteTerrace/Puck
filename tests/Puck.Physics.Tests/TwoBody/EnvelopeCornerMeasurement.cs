using Puck.Maths;

namespace Puck.Physics.Tests.TwoBody;

/// <summary>
/// Test-only measurement scaffolding, not production code. Kernel-grounded envelope-corner probes: every number here comes from
/// calling the real <see cref="FixedMassProperties"/> kernels at a candidate placement and reading their own
/// refusal, never from an independent floating-point log2 estimate. A placement either round-trips through the real
/// kernel or it does not.
/// </summary>
internal static class EnvelopeCornerMeasurement {
    /// <summary>The fraction bit count mass and inertia are derived at before any placement search. Construction runs
    /// once per body, never per tick, so a wide carrier costs nothing that matters; it is chosen wide enough that a
    /// refusal here is itself a finding (the corner's mass/inertia does not fit ANY signed 64-bit raw at all), never
    /// an artifact of an arbitrary choice.</summary>
    internal const int DerivationFractionBits = 40;

    /// <summary>One box body's authoring inputs, in real (double) units — the caller's job to convert to raw, never
    /// this type's, so the conversion itself stays visible in the caller.</summary>
    internal readonly record struct BoxCorner(double HalfX, double HalfY, double HalfZ, double Density) {
        internal double Mass => (Volume * Density);
        internal double Volume => (((8d * HalfX) * HalfY) * HalfZ);
    }
    /// <summary>The real, once-derived mass and diagonal inertia of a box corner at <see cref="DerivationFractionBits"/>.</summary>
    internal readonly record struct DerivedBox(bool Ok, long MassRaw, long Ixx, long Iyy, long Izz);

    /// <summary>Returns the number of bits needed to hold a strictly positive raw magnitude.</summary>
    internal static int BitLength(long value) {
        if (value == 0L) {
            return 0;
        }

        var magnitude = ((ulong)Math.Abs(value: value));

        return (64 - System.Numerics.BitOperations.LeadingZeroCount(value: magnitude));
    }
    /// <summary>Derives a box's mass and inertia through the real <see cref="FixedMassProperties.TryBoxBody"/> kernel.</summary>
    internal static DerivedBox Derive(BoxCorner corner) {
        var ok = FixedMassProperties.TryBoxBody(
            density: FixedQ4816.FromDouble(value: corner.Density).Value,
            fractionBitsDensity: FixedQ4816.FractionBitCount,
            halfX: FixedQ4816.FromDouble(value: corner.HalfX).Value,
            halfY: FixedQ4816.FromDouble(value: corner.HalfY).Value,
            halfZ: FixedQ4816.FromDouble(value: corner.HalfZ).Value,
            fractionBitsLength: FixedQ4816.FractionBitCount,
            fractionBitsMass: DerivationFractionBits,
            fractionBitsInertia: DerivationFractionBits,
            mass: out var mass,
            ixx: out var ixx,
            iyy: out var iyy,
            izz: out var izz
        );

        return new(
            Ixx: ixx,
            Iyy: iyy,
            Izz: izz,
            MassRaw: mass,
            Ok: ok
        );
    }
    /// <summary>The same adaptive search as <see cref="DeriveMassAdaptive"/>, for the largest of the box's three
    /// diagonal inertia entries (the one closest to overflowing at any given placement).</summary>
    internal static AdaptiveMass DeriveInertiaAdaptive(BoxCorner corner) {
        for (var placement = FixedMassProperties.MaximumFractionBitCount; (placement >= 0); --placement) {
            if (FixedMassProperties.TryBoxBody(
                density: FixedQ4816.FromDouble(value: corner.Density).Value,
                fractionBitsDensity: FixedQ4816.FractionBitCount,
                halfX: FixedQ4816.FromDouble(value: corner.HalfX).Value,
                halfY: FixedQ4816.FromDouble(value: corner.HalfY).Value,
                halfZ: FixedQ4816.FromDouble(value: corner.HalfZ).Value,
                fractionBitsLength: FixedQ4816.FractionBitCount,
                fractionBitsMass: 0,
                fractionBitsInertia: placement,
                mass: out _,
                ixx: out var ixx,
                iyy: out var iyy,
                izz: out var izz
            )) {
                var widest = Math.Max(
                    val1: Math.Max(
                        val1: ixx,
                        val2: iyy
                    ),
                    val2: izz
                );

                return new(
                    Ok: true,
                    Placement: placement,
                    Raw: widest
                );
            }
        }

        return new(
            Ok: false,
            Placement: -1,
            Raw: 0L
        );
    }
    /// <summary>Finds the widest mass placement (most fraction bits, most resolution) that does not overflow, by
    /// linear search from <see cref="FixedMassProperties.MaximumFractionBitCount"/> downward — the real kernel's own
    /// refusal is the stopping condition, not an estimate.</summary>
    internal static AdaptiveMass DeriveMassAdaptive(BoxCorner corner) {
        for (var placement = FixedMassProperties.MaximumFractionBitCount; (placement >= 0); --placement) {
            if (FixedMassProperties.TryBoxBody(
                density: FixedQ4816.FromDouble(value: corner.Density).Value,
                fractionBitsDensity: FixedQ4816.FractionBitCount,
                halfX: FixedQ4816.FromDouble(value: corner.HalfX).Value,
                halfY: FixedQ4816.FromDouble(value: corner.HalfY).Value,
                halfZ: FixedQ4816.FromDouble(value: corner.HalfZ).Value,
                fractionBitsLength: FixedQ4816.FractionBitCount,
                fractionBitsMass: placement,
                fractionBitsInertia: 0,
                mass: out var mass,
                ixx: out _,
                iyy: out _,
                izz: out _
            )) {
                return new(
                    Ok: true,
                    Placement: placement,
                    Raw: mass
                );
            }
        }

        return new(
            Ok: false,
            Placement: -1,
            Raw: 0L
        );
    }
    /// <summary>Whether <see cref="FixedMassProperties.TryInvertInertia"/> succeeds at a candidate output placement,
    /// for a diagonal (off-diagonal-free) tensor.</summary>
    internal static bool InertiaInvertsAt(long ixx, long iyy, long izz, int placement) =>
        FixedMassProperties.TryInvertInertia(
            fractionBitsInertia: DerivationFractionBits,
            fractionBitsOut: placement,
            invXX: out _,
            invXY: out _,
            invXZ: out _,
            invYY: out _,
            invYZ: out _,
            invZZ: out _,
            ixx: ixx,
            ixy: 0L,
            ixz: 0L,
            iyy: iyy,
            iyz: 0L,
            izz: izz
        );
    /// <summary>Whether <see cref="FixedMassProperties.TryInvertMass"/> succeeds at a candidate output placement.</summary>
    internal static bool MassInvertsAt(long massRaw, int placement) =>
        FixedMassProperties.TryInvertMass(
            fractionBitsMass: DerivationFractionBits,
            fractionBitsOut: placement,
            inverseMass: out _,
            mass: massRaw
        );
    /// <summary>The contiguous inclusive range of placements <c>[0, 64]</c> at which a predicate holds, found by an
    /// exhaustive scan (65 kernel calls) rather than a bisection, so a non-contiguous successful set — itself a
    /// finding — is not silently assumed away.</summary>
    internal static (int Min, int Max, bool Contiguous) SuccessRange(Func<int, bool> predicate) {
        var min = -1;
        var max = -1;
        var gapAfterSuccess = false;
        var seenSuccess = false;

        for (var placement = 0; (placement <= FixedMassProperties.MaximumFractionBitCount); ++placement) {
            if (predicate(placement)) {
                if (min < 0) {
                    min = placement;
                }

                if (
                    seenSuccess &&
                    (placement != (max + 1))
                ) {
                    gapAfterSuccess = true;
                }

                max = placement;
                seenSuccess = true;
            }
        }

        return (min, max, !gapAfterSuccess);
    }

    /// <summary>The real mass (or inertia) magnitude's own binary exponent, found by searching for the WIDEST
    /// derivation placement that does not overflow — a placement-independent measurement (unlike a fixed derivation
    /// scale, which refuses outright once a corner's true magnitude no longer fits it).</summary>
    internal readonly record struct AdaptiveMass(bool Ok, long Raw, int Placement) {
        /// <summary>The value's own approximate binary exponent (<c>floor(log2(value))</c>), independent of
        /// <see cref="Placement"/> — this is what a window-width comparison across corners derived at DIFFERENT
        /// placements must read, never the raw bit length alone.</summary>
        internal int Exponent => ((Ok && (Raw != 0L))
            ? ((BitLength(value: Raw) - Placement) - 1)
            : int.MinValue
        );
    }
}
