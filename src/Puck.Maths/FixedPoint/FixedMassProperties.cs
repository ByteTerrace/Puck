using System.Numerics;

namespace Puck.Maths;

/// <summary>
/// The scale-parameterized mass properties a deterministic rigid-body solver needs before it can step anything:
/// volume, mass and inertia about the centre of mass for the four solid primitives, the parallel-axis transfer that
/// moves an inertia tensor to a parallel frame, the compound accumulation that folds several parts onto one composite
/// centre of mass, and the inversions that turn mass and inertia into the reciprocals the impulse solver actually
/// multiplies by.
/// </summary>
/// <remarks>
/// <para>Every operand is a raw at a caller-declared fraction bit count, and every result is rounded exactly once to
/// a caller-declared one — mass and inertia occupy different bit windows depending on world scale and density, so no
/// single carrier fits both and each must be placed independently. Every count must lie in
/// <c>[0, <see cref="MaximumFractionBitCount"/>]</c>; a count outside it is refused rather than rounded, which also
/// bounds every internal shift.</para>
/// <para>Accumulation uses <see cref="BigInteger"/> rather than the sign-plus-<see cref="UInt128"/> scheme the
/// sibling kernels use: a sphere's mass numerator alone (<c>ρ·π·r³</c>) can run past 300 bits, wider than
/// <see cref="UInt128"/> can hold at a useful resolution. These kernels run once per body at construction, never per
/// tick, so exact arbitrary-width accumulation costs nothing that matters and buys a contract with no precision
/// envelope: each result is one ties-to-even rounding of the exact rational value, or a refusal.</para>
/// <para><see cref="PiRaw"/> is the correctly rounded <c>π</c> at <see cref="PiFractionBitCount"/> fraction bits,
/// shared with the angle constants rather than restated. Every result below is the exact rational value of its
/// formula with that rational in place of <c>π</c>, rounded once — a relative departure below <c>2^-62</c>, and the
/// only inexactness anywhere in this type.</para>
/// <para>Each primitive is centred on its own origin, so its centre of mass is the zero vector by construction and
/// is not returned; the axis of the cylinder and the capsule is <c>Y</c>. A compound's centre of mass is the one
/// that has to be computed, and <see cref="TryCompound"/> returns it, accumulating each part's parallel-axis
/// transfer against the exact rational composite centre rather than against the rounded one it reports, so no part
/// inherits the centre's rounding.</para>
/// </remarks>
internal static class FixedMassProperties {
    /// <summary>The largest fraction bit count any operand or result of this type may be carried at (<c>64</c>).</summary>
    internal const int MaximumFractionBitCount = 64;

    /// <summary>The number of fraction bits <see cref="PiRaw"/> is carried at (<c>61</c>).</summary>
    internal const int PiFractionBitCount = 61;

    /// <summary>The correctly rounded <c>π</c> at <see cref="PiFractionBitCount"/> fraction bits — the same constant
    /// the angle reduction reads, named here for what it is rather than restated.</summary>
    internal const long PiRaw = FixedQ4816.Atan2PiQ61;

    /// <summary>One part of a compound body: its mass, the position of its own centre of mass in the compound's frame,
    /// and its inertia tensor about that centre.</summary>
    /// <param name="Mass">The part's mass raw, which must be strictly positive.</param>
    /// <param name="CenterX">The part's centre-of-mass X raw.</param>
    /// <param name="CenterY">The part's centre-of-mass Y raw.</param>
    /// <param name="CenterZ">The part's centre-of-mass Z raw.</param>
    /// <param name="Ixx">The part's <c>(0,0)</c> inertia raw, about its own centre.</param>
    /// <param name="Iyy">The part's <c>(1,1)</c> inertia raw, about its own centre.</param>
    /// <param name="Izz">The part's <c>(2,2)</c> inertia raw, about its own centre.</param>
    /// <param name="Ixy">The part's <c>(0,1)</c> inertia raw, about its own centre.</param>
    /// <param name="Ixz">The part's <c>(0,2)</c> inertia raw, about its own centre.</param>
    /// <param name="Iyz">The part's <c>(1,2)</c> inertia raw, about its own centre.</param>
    internal readonly record struct CompoundPart(
        long Mass,
        long CenterX,
        long CenterY,
        long CenterZ,
        long Ixx,
        long Iyy,
        long Izz,
        long Ixy,
        long Ixz,
        long Iyz
    );

    /// <summary>Computes the volume of a solid sphere.</summary>
    /// <param name="radius">The radius raw, which must be non-negative.</param>
    /// <param name="fractionBitsLength">The radius's fraction bit count.</param>
    /// <param name="fractionBitsVolume">The volume's fraction bit count.</param>
    /// <param name="volume">The volume raw on success; zero on refusal.</param>
    /// <returns><see langword="false"/> when an operand is negative, a fraction bit count is out of range, or the
    /// rounded volume does not fit the signed 64-bit raw.</returns>
    internal static bool TrySphereVolume(long radius, int fractionBitsLength, int fractionBitsVolume, out long volume) {
        if ((radius < 0L) || !ScalesValid(first: fractionBitsLength, second: fractionBitsVolume)) {
            volume = 0L;
            return false;
        }

        BigInteger r = radius;

        // (4/3)·π·r³.
        return TryRound(
            numerator: ((4 * (BigInteger)PiRaw) * BigInteger.Pow(value: r, exponent: 3)),
            denominatorFactor: 3,
            denominatorShift: (PiFractionBitCount + (3L * fractionBitsLength)),
            numeratorShift: fractionBitsVolume,
            result: out volume
        );
    }

    /// <summary>Computes the volume of a solid box from its half-extents.</summary>
    /// <param name="halfX">The X half-extent raw, which must be non-negative.</param>
    /// <param name="halfY">The Y half-extent raw, which must be non-negative.</param>
    /// <param name="halfZ">The Z half-extent raw, which must be non-negative.</param>
    /// <param name="fractionBitsLength">The half-extents' fraction bit count.</param>
    /// <param name="fractionBitsVolume">The volume's fraction bit count.</param>
    /// <param name="volume">The volume raw on success; zero on refusal.</param>
    /// <returns><see langword="false"/> under the same conditions as <see cref="TrySphereVolume"/>.</returns>
    internal static bool TryBoxVolume(long halfX, long halfY, long halfZ, int fractionBitsLength, int fractionBitsVolume, out long volume) {
        if ((halfX < 0L) || (halfY < 0L) || (halfZ < 0L) || !ScalesValid(first: fractionBitsLength, second: fractionBitsVolume)) {
            volume = 0L;
            return false;
        }

        // 8·hx·hy·hz — the full extents are twice the half-extents in each of three axes.
        return TryRound(
            numerator: ((8 * (BigInteger)halfX) * halfY * halfZ),
            denominatorFactor: BigInteger.One,
            denominatorShift: (3L * fractionBitsLength),
            numeratorShift: fractionBitsVolume,
            result: out volume
        );
    }

    /// <summary>Computes the volume of a solid cylinder about the <c>Y</c> axis.</summary>
    /// <param name="radius">The radius raw, which must be non-negative.</param>
    /// <param name="height">The height raw, which must be non-negative.</param>
    /// <param name="fractionBitsLength">The lengths' fraction bit count.</param>
    /// <param name="fractionBitsVolume">The volume's fraction bit count.</param>
    /// <param name="volume">The volume raw on success; zero on refusal.</param>
    /// <returns><see langword="false"/> under the same conditions as <see cref="TrySphereVolume"/>.</returns>
    internal static bool TryCylinderVolume(long radius, long height, int fractionBitsLength, int fractionBitsVolume, out long volume) {
        if ((radius < 0L) || (height < 0L) || !ScalesValid(first: fractionBitsLength, second: fractionBitsVolume)) {
            volume = 0L;
            return false;
        }

        // π·r²·h.
        return TryRound(
            numerator: (((BigInteger)PiRaw * radius) * radius * height),
            denominatorFactor: BigInteger.One,
            denominatorShift: (PiFractionBitCount + (3L * fractionBitsLength)),
            numeratorShift: fractionBitsVolume,
            result: out volume
        );
    }

    /// <summary>Computes the volume of a solid capsule about the <c>Y</c> axis — a cylinder capped by two
    /// hemispheres.</summary>
    /// <param name="radius">The radius raw, which must be non-negative.</param>
    /// <param name="centerDistance">The distance between the two hemisphere centres, which must be non-negative; it is
    /// the cylindrical section's height, and zero degenerates the capsule to a sphere.</param>
    /// <param name="fractionBitsLength">The lengths' fraction bit count.</param>
    /// <param name="fractionBitsVolume">The volume's fraction bit count.</param>
    /// <param name="volume">The volume raw on success; zero on refusal.</param>
    /// <returns><see langword="false"/> under the same conditions as <see cref="TrySphereVolume"/>.</returns>
    internal static bool TryCapsuleVolume(long radius, long centerDistance, int fractionBitsLength, int fractionBitsVolume, out long volume) {
        if ((radius < 0L) || (centerDistance < 0L) || !ScalesValid(first: fractionBitsLength, second: fractionBitsVolume)) {
            volume = 0L;
            return false;
        }

        BigInteger r = radius;

        // π·r²·h + (4/3)·π·r³, over the common denominator 3.
        return TryRound(
            numerator: (((BigInteger)PiRaw * r * r) * ((3 * (BigInteger)centerDistance) + (4 * r))),
            denominatorFactor: 3,
            denominatorShift: (PiFractionBitCount + (3L * fractionBitsLength)),
            numeratorShift: fractionBitsVolume,
            result: out volume
        );
    }

    /// <summary>Computes the mass and the isotropic inertia of a solid sphere about its centre.</summary>
    /// <param name="density">The density raw, which must be non-negative.</param>
    /// <param name="fractionBitsDensity">The density's fraction bit count.</param>
    /// <param name="radius">The radius raw, which must be non-negative.</param>
    /// <param name="fractionBitsLength">The radius's fraction bit count.</param>
    /// <param name="fractionBitsMass">The mass's fraction bit count.</param>
    /// <param name="fractionBitsInertia">The inertia's fraction bit count.</param>
    /// <param name="mass">The mass raw on success; zero on refusal.</param>
    /// <param name="inertia">The inertia raw about any diameter on success; zero on refusal.</param>
    /// <returns><see langword="false"/> when an operand is negative, a fraction bit count is out of range, or either
    /// rounded result does not fit the signed 64-bit raw; both <see langword="out"/> parameters are zero in that
    /// case.</returns>
    internal static bool TrySphereBody(
        long density,
        int fractionBitsDensity,
        long radius,
        int fractionBitsLength,
        int fractionBitsMass,
        int fractionBitsInertia,
        out long mass,
        out long inertia
    ) {
        if ((density < 0L) || (radius < 0L) || !ScalesValid(first: fractionBitsDensity, second: fractionBitsLength, third: fractionBitsMass, fourth: fractionBitsInertia)) {
            mass = 0L;
            inertia = 0L;
            return false;
        }

        BigInteger d = density, r = radius;
        var lengthShift = ((long)fractionBitsDensity + PiFractionBitCount);

        // m = ρ·(4/3)·π·r³ and I = (2/5)·m·r² = ρ·(8/15)·π·r⁵.
        var okMass = TryRound(
            numerator: ((4 * d * PiRaw) * BigInteger.Pow(value: r, exponent: 3)),
            denominatorFactor: 3,
            denominatorShift: (lengthShift + (3L * fractionBitsLength)),
            numeratorShift: fractionBitsMass,
            result: out var roundedMass
        );
        var okInertia = TryRound(
            numerator: ((8 * d * PiRaw) * BigInteger.Pow(value: r, exponent: 5)),
            denominatorFactor: 15,
            denominatorShift: (lengthShift + (5L * fractionBitsLength)),
            numeratorShift: fractionBitsInertia,
            result: out var roundedInertia
        );

        if (!okMass || !okInertia) {
            mass = 0L;
            inertia = 0L;
            return false;
        }

        mass = roundedMass;
        inertia = roundedInertia;
        return true;
    }

    /// <summary>Computes the mass and the diagonal inertia of a solid box about its centre, from its half-extents.</summary>
    /// <param name="density">The density raw, which must be non-negative.</param>
    /// <param name="fractionBitsDensity">The density's fraction bit count.</param>
    /// <param name="halfX">The X half-extent raw, which must be non-negative.</param>
    /// <param name="halfY">The Y half-extent raw, which must be non-negative.</param>
    /// <param name="halfZ">The Z half-extent raw, which must be non-negative.</param>
    /// <param name="fractionBitsLength">The half-extents' fraction bit count.</param>
    /// <param name="fractionBitsMass">The mass's fraction bit count.</param>
    /// <param name="fractionBitsInertia">The inertia's fraction bit count.</param>
    /// <param name="mass">The mass raw on success; zero on refusal.</param>
    /// <param name="ixx">The <c>(0,0)</c> inertia raw on success; zero on refusal.</param>
    /// <param name="iyy">The <c>(1,1)</c> inertia raw on success; zero on refusal.</param>
    /// <param name="izz">The <c>(2,2)</c> inertia raw on success; zero on refusal.</param>
    /// <returns><see langword="false"/> under the same conditions as <see cref="TrySphereBody"/>; every
    /// <see langword="out"/> parameter is zero in that case.</returns>
    /// <remarks>The off-diagonal entries vanish for a box aligned with its own frame, so they are not returned.</remarks>
    internal static bool TryBoxBody(
        long density,
        int fractionBitsDensity,
        long halfX,
        long halfY,
        long halfZ,
        int fractionBitsLength,
        int fractionBitsMass,
        int fractionBitsInertia,
        out long mass,
        out long ixx,
        out long iyy,
        out long izz
    ) {
        if ((density < 0L) || (halfX < 0L) || (halfY < 0L) || (halfZ < 0L) ||
            !ScalesValid(first: fractionBitsDensity, second: fractionBitsLength, third: fractionBitsMass, fourth: fractionBitsInertia)) {
            mass = 0L;
            ixx = 0L;
            iyy = 0L;
            izz = 0L;
            return false;
        }

        BigInteger d = density, hx = halfX, hy = halfY, hz = halfZ;
        var volumeNumerator = ((8 * d) * hx * hy * hz);
        var inertiaShift = ((long)fractionBitsDensity + (5L * fractionBitsLength));

        // m = ρ·8·hx·hy·hz, and I_xx = (m/12)·(Ly² + Lz²) = (m/3)·(hy² + hz²) once the full extents are halved.
        var okMass = TryRound(
            numerator: volumeNumerator,
            denominatorFactor: BigInteger.One,
            denominatorShift: ((long)fractionBitsDensity + (3L * fractionBitsLength)),
            numeratorShift: fractionBitsMass,
            result: out var roundedMass
        );
        var okXX = TryRound(numerator: (volumeNumerator * ((hy * hy) + (hz * hz))), denominatorFactor: 3, denominatorShift: inertiaShift, numeratorShift: fractionBitsInertia, result: out var roundedXX);
        var okYY = TryRound(numerator: (volumeNumerator * ((hx * hx) + (hz * hz))), denominatorFactor: 3, denominatorShift: inertiaShift, numeratorShift: fractionBitsInertia, result: out var roundedYY);
        var okZZ = TryRound(numerator: (volumeNumerator * ((hx * hx) + (hy * hy))), denominatorFactor: 3, denominatorShift: inertiaShift, numeratorShift: fractionBitsInertia, result: out var roundedZZ);

        if (!okMass || !okXX || !okYY || !okZZ) {
            mass = 0L;
            ixx = 0L;
            iyy = 0L;
            izz = 0L;
            return false;
        }

        mass = roundedMass;
        ixx = roundedXX;
        iyy = roundedYY;
        izz = roundedZZ;
        return true;
    }

    /// <summary>Computes the mass and the two distinct inertia moments of a solid cylinder about its centre, with its
    /// axis along <c>Y</c>.</summary>
    /// <param name="density">The density raw, which must be non-negative.</param>
    /// <param name="fractionBitsDensity">The density's fraction bit count.</param>
    /// <param name="radius">The radius raw, which must be non-negative.</param>
    /// <param name="height">The height raw, which must be non-negative.</param>
    /// <param name="fractionBitsLength">The lengths' fraction bit count.</param>
    /// <param name="fractionBitsMass">The mass's fraction bit count.</param>
    /// <param name="fractionBitsInertia">The inertia's fraction bit count.</param>
    /// <param name="mass">The mass raw on success; zero on refusal.</param>
    /// <param name="axial">The moment about the <c>Y</c> axis on success; zero on refusal.</param>
    /// <param name="perpendicular">The moment about <c>X</c> and <c>Z</c> alike on success; zero on refusal.</param>
    /// <returns><see langword="false"/> under the same conditions as <see cref="TrySphereBody"/>; every
    /// <see langword="out"/> parameter is zero in that case.</returns>
    internal static bool TryCylinderBody(
        long density,
        int fractionBitsDensity,
        long radius,
        long height,
        int fractionBitsLength,
        int fractionBitsMass,
        int fractionBitsInertia,
        out long mass,
        out long axial,
        out long perpendicular
    ) {
        if ((density < 0L) || (radius < 0L) || (height < 0L) ||
            !ScalesValid(first: fractionBitsDensity, second: fractionBitsLength, third: fractionBitsMass, fourth: fractionBitsInertia)) {
            mass = 0L;
            axial = 0L;
            perpendicular = 0L;
            return false;
        }

        BigInteger d = density, r = radius, h = height;
        var volumeNumerator = ((d * PiRaw) * r * r * h);
        var scaleShift = (((long)fractionBitsDensity + PiFractionBitCount) + (3L * fractionBitsLength));
        var inertiaShift = (scaleShift + (2L * fractionBitsLength));

        // m = ρ·π·r²·h, axial = (1/2)·m·r², perpendicular = (m/12)·(3r² + h²).
        var okMass = TryRound(numerator: volumeNumerator, denominatorFactor: BigInteger.One, denominatorShift: scaleShift, numeratorShift: fractionBitsMass, result: out var roundedMass);
        var okAxial = TryRound(numerator: (volumeNumerator * r * r), denominatorFactor: 2, denominatorShift: inertiaShift, numeratorShift: fractionBitsInertia, result: out var roundedAxial);
        var okPerpendicular = TryRound(
            numerator: (volumeNumerator * ((3 * r * r) + (h * h))),
            denominatorFactor: 12,
            denominatorShift: inertiaShift,
            numeratorShift: fractionBitsInertia,
            result: out var roundedPerpendicular
        );

        if (!okMass || !okAxial || !okPerpendicular) {
            mass = 0L;
            axial = 0L;
            perpendicular = 0L;
            return false;
        }

        mass = roundedMass;
        axial = roundedAxial;
        perpendicular = roundedPerpendicular;
        return true;
    }

    /// <summary>Computes the mass and the two distinct inertia moments of a solid capsule about its centre, with its
    /// axis along <c>Y</c>.</summary>
    /// <param name="density">The density raw, which must be non-negative.</param>
    /// <param name="fractionBitsDensity">The density's fraction bit count.</param>
    /// <param name="radius">The radius raw, which must be non-negative.</param>
    /// <param name="centerDistance">The distance between the two hemisphere centres, which must be non-negative.</param>
    /// <param name="fractionBitsLength">The lengths' fraction bit count.</param>
    /// <param name="fractionBitsMass">The mass's fraction bit count.</param>
    /// <param name="fractionBitsInertia">The inertia's fraction bit count.</param>
    /// <param name="mass">The mass raw on success; zero on refusal.</param>
    /// <param name="axial">The moment about the <c>Y</c> axis on success; zero on refusal.</param>
    /// <param name="perpendicular">The moment about <c>X</c> and <c>Z</c> alike on success; zero on refusal.</param>
    /// <returns><see langword="false"/> under the same conditions as <see cref="TrySphereBody"/>; every
    /// <see langword="out"/> parameter is zero in that case.</returns>
    /// <remarks>Writing <c>h</c> for <paramref name="centerDistance"/>, the moments are
    /// <c>I_axial = ½·m_cyl·r² + (2/5)·m_sph·r²</c> and
    /// <c>I_perp = (m_cyl/12)(3r² + h²) + m_sph[(83/320)r² + (h/2 + 3r/8)²]</c>, where <c>m_sph</c> is the two
    /// hemispheres' combined mass and <c>83/320</c> is a hemisphere's own centroidal perpendicular coefficient. At
    /// <c>h = 0</c> both collapse exactly to the sphere's <c>(2/5)·m·r²</c>, because
    /// <c>83/320 + (3/8)² = 2/5</c> identically — the degeneracy that would break under a wrong coefficient or a
    /// wrong parallel-axis offset, and the reason both are stated as exact rationals here rather than approximated.</remarks>
    internal static bool TryCapsuleBody(
        long density,
        int fractionBitsDensity,
        long radius,
        long centerDistance,
        int fractionBitsLength,
        int fractionBitsMass,
        int fractionBitsInertia,
        out long mass,
        out long axial,
        out long perpendicular
    ) {
        if ((density < 0L) || (radius < 0L) || (centerDistance < 0L) ||
            !ScalesValid(first: fractionBitsDensity, second: fractionBitsLength, third: fractionBitsMass, fourth: fractionBitsInertia)) {
            mass = 0L;
            axial = 0L;
            perpendicular = 0L;
            return false;
        }

        BigInteger d = density, r = radius, h = centerDistance;
        var common = ((d * PiRaw) * r * r);
        var scaleShift = (((long)fractionBitsDensity + PiFractionBitCount) + (3L * fractionBitsLength));
        var inertiaShift = (scaleShift + (2L * fractionBitsLength));
        var capOffset = ((4 * h) + (3 * r));

        // m = ρ·π·r²·(3h + 4r)/3.
        var okMass = TryRound(numerator: (common * ((3 * h) + (4 * r))), denominatorFactor: 3, denominatorShift: scaleShift, numeratorShift: fractionBitsMass, result: out var roundedMass);

        // axial = ρ·π·r⁴·(15h + 16r)/30 — the cylinder's ½·m_cyl·r² and the hemispheres' (2/5)·m_sph·r² over 30.
        var okAxial = TryRound(
            numerator: ((common * r * r) * ((15 * h) + (16 * r))),
            denominatorFactor: 30,
            denominatorShift: inertiaShift,
            numeratorShift: fractionBitsInertia,
            result: out var roundedAxial
        );

        // perpendicular over the common denominator 720: the cylinder's (m_cyl/12)(3r² + h²) contributes
        // 60·h·(3r² + h²), and the hemispheres' m_sph[(83/320)r² + ((4h + 3r)/8)²] contributes r·(249r² + 15(4h+3r)²).
        var okPerpendicular = TryRound(
            numerator: (common * (((60 * h) * ((3 * r * r) + (h * h))) + (r * ((249 * r * r) + (15 * capOffset * capOffset))))),
            denominatorFactor: 720,
            denominatorShift: inertiaShift,
            numeratorShift: fractionBitsInertia,
            result: out var roundedPerpendicular
        );

        if (!okMass || !okAxial || !okPerpendicular) {
            mass = 0L;
            axial = 0L;
            perpendicular = 0L;
            return false;
        }

        mass = roundedMass;
        axial = roundedAxial;
        perpendicular = roundedPerpendicular;
        return true;
    }

    /// <summary>Transfers a symmetric inertia tensor from a body's centre of mass to a parallel frame displaced by
    /// <c>(offsetX, offsetY, offsetZ)</c> — the parallel-axis theorem,
    /// <c>I' = I + m·(|d|²·δ − d⊗d)</c>, each entry rounded exactly once.</summary>
    /// <param name="ixx">The <c>(0,0)</c> entry about the centre of mass.</param>
    /// <param name="iyy">The <c>(1,1)</c> entry about the centre of mass.</param>
    /// <param name="izz">The <c>(2,2)</c> entry about the centre of mass.</param>
    /// <param name="ixy">The <c>(0,1)</c> entry about the centre of mass.</param>
    /// <param name="ixz">The <c>(0,2)</c> entry about the centre of mass.</param>
    /// <param name="iyz">The <c>(1,2)</c> entry about the centre of mass.</param>
    /// <param name="fractionBitsInertia">The inertia entries' fraction bit count, on input and output alike.</param>
    /// <param name="mass">The body's mass raw, which must be non-negative.</param>
    /// <param name="fractionBitsMass">The mass's fraction bit count.</param>
    /// <param name="offsetX">The displacement's X raw.</param>
    /// <param name="offsetY">The displacement's Y raw.</param>
    /// <param name="offsetZ">The displacement's Z raw.</param>
    /// <param name="fractionBitsLength">The displacement's fraction bit count.</param>
    /// <param name="txx">The transferred <c>(0,0)</c> entry on success; zero on refusal.</param>
    /// <param name="tyy">The transferred <c>(1,1)</c> entry on success; zero on refusal.</param>
    /// <param name="tzz">The transferred <c>(2,2)</c> entry on success; zero on refusal.</param>
    /// <param name="txy">The transferred <c>(0,1)</c> entry on success; zero on refusal.</param>
    /// <param name="txz">The transferred <c>(0,2)</c> entry on success; zero on refusal.</param>
    /// <param name="tyz">The transferred <c>(1,2)</c> entry on success; zero on refusal.</param>
    /// <returns><see langword="false"/> when the mass is negative, a fraction bit count is out of range, or any
    /// transferred entry does not fit the signed 64-bit raw; every <see langword="out"/> parameter is zero in that
    /// case.</returns>
    internal static bool TryTranslateInertia(
        long ixx,
        long iyy,
        long izz,
        long ixy,
        long ixz,
        long iyz,
        int fractionBitsInertia,
        long mass,
        int fractionBitsMass,
        long offsetX,
        long offsetY,
        long offsetZ,
        int fractionBitsLength,
        out long txx,
        out long tyy,
        out long tzz,
        out long txy,
        out long txz,
        out long tyz
    ) {
        if ((mass < 0L) || !ScalesValid(first: fractionBitsInertia, second: fractionBitsMass, third: fractionBitsLength)) {
            txx = 0L;
            tyy = 0L;
            tzz = 0L;
            txy = 0L;
            txz = 0L;
            tyz = 0L;
            return false;
        }

        BigInteger m = mass, dx = offsetX, dy = offsetY, dz = offsetZ;
        var transferShift = ((long)fractionBitsMass + (2L * fractionBitsLength));

        // Each entry is I + m·term over the common denominator 2^(fractionBitsMass + 2·fractionBitsLength); the
        // stored entry is lifted onto that denominator so the addition is exact and only the narrowing rounds.
        var okXX = TryTransfer(entry: ixx, mass: m, term: ((dy * dy) + (dz * dz)), transferShift: transferShift, fractionBitsInertia: fractionBitsInertia, result: out var rxx);
        var okYY = TryTransfer(entry: iyy, mass: m, term: ((dx * dx) + (dz * dz)), transferShift: transferShift, fractionBitsInertia: fractionBitsInertia, result: out var ryy);
        var okZZ = TryTransfer(entry: izz, mass: m, term: ((dx * dx) + (dy * dy)), transferShift: transferShift, fractionBitsInertia: fractionBitsInertia, result: out var rzz);
        var okXY = TryTransfer(entry: ixy, mass: m, term: -(dx * dy), transferShift: transferShift, fractionBitsInertia: fractionBitsInertia, result: out var rxy);
        var okXZ = TryTransfer(entry: ixz, mass: m, term: -(dx * dz), transferShift: transferShift, fractionBitsInertia: fractionBitsInertia, result: out var rxz);
        var okYZ = TryTransfer(entry: iyz, mass: m, term: -(dy * dz), transferShift: transferShift, fractionBitsInertia: fractionBitsInertia, result: out var ryz);

        if (!okXX || !okYY || !okZZ || !okXY || !okXZ || !okYZ) {
            txx = 0L;
            tyy = 0L;
            tzz = 0L;
            txy = 0L;
            txz = 0L;
            tyz = 0L;
            return false;
        }

        txx = rxx;
        tyy = ryy;
        tzz = rzz;
        txy = rxy;
        txz = rxz;
        tyz = ryz;
        return true;
    }

    /// <summary>Accumulates several parts into one composite body: the summed mass, the composite centre of mass, and
    /// the inertia tensor about that centre.</summary>
    /// <param name="parts">The parts, each carrying its own mass, centre and centroidal inertia. At least one is
    /// required and every mass must be strictly positive.</param>
    /// <param name="fractionBitsMass">The masses' fraction bit count, on input and output alike.</param>
    /// <param name="fractionBitsLength">The centres' fraction bit count, on input and output alike.</param>
    /// <param name="fractionBitsInertia">The inertia entries' fraction bit count, on input and output alike.</param>
    /// <param name="mass">The composite mass raw on success; zero on refusal.</param>
    /// <param name="centerX">The composite centre's X raw on success; zero on refusal.</param>
    /// <param name="centerY">The composite centre's Y raw on success; zero on refusal.</param>
    /// <param name="centerZ">The composite centre's Z raw on success; zero on refusal.</param>
    /// <param name="ixx">The composite <c>(0,0)</c> entry on success; zero on refusal.</param>
    /// <param name="iyy">The composite <c>(1,1)</c> entry on success; zero on refusal.</param>
    /// <param name="izz">The composite <c>(2,2)</c> entry on success; zero on refusal.</param>
    /// <param name="ixy">The composite <c>(0,1)</c> entry on success; zero on refusal.</param>
    /// <param name="ixz">The composite <c>(0,2)</c> entry on success; zero on refusal.</param>
    /// <param name="iyz">The composite <c>(1,2)</c> entry on success; zero on refusal.</param>
    /// <returns><see langword="false"/> when no part is supplied, when a mass is not strictly positive, when a fraction
    /// bit count is out of range, or when any result does not fit the signed 64-bit raw; every <see langword="out"/>
    /// parameter is zero in that case.</returns>
    /// <remarks>Every part's parallel-axis contribution is taken against the exact rational composite centre, never
    /// against the rounded centre this returns, so no part inherits the centre's rounding. The composite mass is an
    /// exact integer sum of raws at one scale, so it rounds nothing at all.</remarks>
    internal static bool TryCompound(
        ReadOnlySpan<CompoundPart> parts,
        int fractionBitsMass,
        int fractionBitsLength,
        int fractionBitsInertia,
        out long mass,
        out long centerX,
        out long centerY,
        out long centerZ,
        out long ixx,
        out long iyy,
        out long izz,
        out long ixy,
        out long ixz,
        out long iyz
    ) {
        if (parts.IsEmpty || !ScalesValid(first: fractionBitsMass, second: fractionBitsLength, third: fractionBitsInertia)) {
            Clear(mass: out mass, centerX: out centerX, centerY: out centerY, centerZ: out centerZ, ixx: out ixx, iyy: out iyy, izz: out izz, ixy: out ixy, ixz: out ixz, iyz: out iyz);
            return false;
        }

        var totalMass = BigInteger.Zero;
        var momentX = BigInteger.Zero;
        var momentY = BigInteger.Zero;
        var momentZ = BigInteger.Zero;

        foreach (var part in parts) {
            if (part.Mass <= 0L) {
                Clear(mass: out mass, centerX: out centerX, centerY: out centerY, centerZ: out centerZ, ixx: out ixx, iyy: out iyy, izz: out izz, ixy: out ixy, ixz: out ixz, iyz: out iyz);
                return false;
            }

            totalMass += part.Mass;
            momentX += ((BigInteger)part.Mass * part.CenterX);
            momentY += ((BigInteger)part.Mass * part.CenterY);
            momentZ += ((BigInteger)part.Mass * part.CenterZ);
        }

        // The exact composite centre is (momentX, momentY, momentZ) / totalMass in raw units; the reported centre is
        // its one rounding, and the tensor below reads the exact rational instead.
        var okMass = TryNarrow(value: totalMass, result: out var roundedMass);
        var okX = TryRoundExact(numerator: momentX, denominator: totalMass, result: out var roundedX);
        var okY = TryRoundExact(numerator: momentY, denominator: totalMass, result: out var roundedY);
        var okZ = TryRoundExact(numerator: momentZ, denominator: totalMass, result: out var roundedZ);

        var squaredMass = (totalMass * totalMass);
        var transferShift = ((long)fractionBitsMass + (2L * fractionBitsLength));
        var accumulatorXX = BigInteger.Zero;
        var accumulatorYY = BigInteger.Zero;
        var accumulatorZZ = BigInteger.Zero;
        var accumulatorXY = BigInteger.Zero;
        var accumulatorXZ = BigInteger.Zero;
        var accumulatorYZ = BigInteger.Zero;

        foreach (var part in parts) {
            // The part's offset from the exact composite centre, as a numerator over the shared denominator totalMass.
            var offsetX = (((BigInteger)part.CenterX * totalMass) - momentX);
            var offsetY = (((BigInteger)part.CenterY * totalMass) - momentY);
            var offsetZ = (((BigInteger)part.CenterZ * totalMass) - momentZ);
            BigInteger partMass = part.Mass;

            accumulatorXX += ((part.Ixx * squaredMass) << ((int)transferShift)) + ((partMass * ((offsetY * offsetY) + (offsetZ * offsetZ))) << fractionBitsInertia);
            accumulatorYY += ((part.Iyy * squaredMass) << ((int)transferShift)) + ((partMass * ((offsetX * offsetX) + (offsetZ * offsetZ))) << fractionBitsInertia);
            accumulatorZZ += ((part.Izz * squaredMass) << ((int)transferShift)) + ((partMass * ((offsetX * offsetX) + (offsetY * offsetY))) << fractionBitsInertia);
            accumulatorXY += ((part.Ixy * squaredMass) << ((int)transferShift)) - ((partMass * offsetX * offsetY) << fractionBitsInertia);
            accumulatorXZ += ((part.Ixz * squaredMass) << ((int)transferShift)) - ((partMass * offsetX * offsetZ) << fractionBitsInertia);
            accumulatorYZ += ((part.Iyz * squaredMass) << ((int)transferShift)) - ((partMass * offsetY * offsetZ) << fractionBitsInertia);
        }

        var denominator = (squaredMass << ((int)transferShift));
        var okXX = TryRoundExact(numerator: accumulatorXX, denominator: denominator, result: out var resultXX);
        var okYY = TryRoundExact(numerator: accumulatorYY, denominator: denominator, result: out var resultYY);
        var okZZ = TryRoundExact(numerator: accumulatorZZ, denominator: denominator, result: out var resultZZ);
        var okXY = TryRoundExact(numerator: accumulatorXY, denominator: denominator, result: out var resultXY);
        var okXZ = TryRoundExact(numerator: accumulatorXZ, denominator: denominator, result: out var resultXZ);
        var okYZ = TryRoundExact(numerator: accumulatorYZ, denominator: denominator, result: out var resultYZ);

        if (!okMass || !okX || !okY || !okZ || !okXX || !okYY || !okZZ || !okXY || !okXZ || !okYZ) {
            Clear(mass: out mass, centerX: out centerX, centerY: out centerY, centerZ: out centerZ, ixx: out ixx, iyy: out iyy, izz: out izz, ixy: out ixy, ixz: out ixz, iyz: out iyz);
            return false;
        }

        mass = roundedMass;
        centerX = roundedX;
        centerY = roundedY;
        centerZ = roundedZ;
        ixx = resultXX;
        iyy = resultYY;
        izz = resultZZ;
        ixy = resultXY;
        ixz = resultXZ;
        iyz = resultYZ;
        return true;
    }

    /// <summary>Inverts a mass at a chosen output scale, refusing when the reciprocal underflows that scale's own
    /// resolution.</summary>
    /// <param name="mass">The mass raw, which must be strictly positive.</param>
    /// <param name="fractionBitsMass">The mass's fraction bit count.</param>
    /// <param name="fractionBitsOut">The inverse mass's fraction bit count.</param>
    /// <param name="inverseMass">The inverse mass raw on success; zero on refusal.</param>
    /// <returns><see langword="false"/> when the mass is not strictly positive, when a fraction bit count is out of
    /// range, when the correctly rounded reciprocal is zero — the requested scale has no resolution left to carry it,
    /// and answering zero would silently declare the body immovable — or when it does not fit the signed 64-bit
    /// raw.</returns>
    internal static bool TryInvertMass(long mass, int fractionBitsMass, int fractionBitsOut, out long inverseMass) {
        if ((mass <= 0L) || !ScalesValid(first: fractionBitsMass, second: fractionBitsOut)) {
            inverseMass = 0L;
            return false;
        }

        // (1/m)·2^fractionBitsOut = 2^(fractionBitsOut + fractionBitsMass) / massRaw.
        if (!TryRound(
            numerator: BigInteger.One,
            denominatorFactor: mass,
            denominatorShift: 0L,
            numeratorShift: ((long)fractionBitsOut + fractionBitsMass),
            result: out var rounded
        ) || (rounded == 0L)) {
            inverseMass = 0L;
            return false;
        }

        inverseMass = rounded;
        return true;
    }

    /// <summary>Inverts a symmetric inertia tensor at a chosen output scale, refusing when the inverse underflows that
    /// scale's own resolution.</summary>
    /// <param name="ixx">The <c>(0,0)</c> entry.</param>
    /// <param name="iyy">The <c>(1,1)</c> entry.</param>
    /// <param name="izz">The <c>(2,2)</c> entry.</param>
    /// <param name="ixy">The <c>(0,1)</c> entry.</param>
    /// <param name="ixz">The <c>(0,2)</c> entry.</param>
    /// <param name="iyz">The <c>(1,2)</c> entry.</param>
    /// <param name="fractionBitsInertia">The entries' fraction bit count.</param>
    /// <param name="fractionBitsOut">The inverse entries' fraction bit count.</param>
    /// <param name="invXX">The inverse's <c>(0,0)</c> entry on success; zero on refusal.</param>
    /// <param name="invYY">The inverse's <c>(1,1)</c> entry on success; zero on refusal.</param>
    /// <param name="invZZ">The inverse's <c>(2,2)</c> entry on success; zero on refusal.</param>
    /// <param name="invXY">The inverse's <c>(0,1)</c> entry on success; zero on refusal.</param>
    /// <param name="invXZ">The inverse's <c>(0,2)</c> entry on success; zero on refusal.</param>
    /// <param name="invYZ">The inverse's <c>(1,2)</c> entry on success; zero on refusal.</param>
    /// <returns><see langword="false"/> when a fraction bit count is out of range, when
    /// <see cref="FixedSymmetricSolve.TryInvertSymmetric3"/> itself refuses (an exactly singular tensor, an entry
    /// magnitude past its envelope, or an entry that does not fit the raw), or when every rounded entry is zero —
    /// the requested scale carries no part of the inverse, and answering an all-zero tensor would silently declare the
    /// body unable to rotate.</returns>
    /// <remarks>The delegated output shift is <c>fractionBitsInertia + fractionBitsOut</c>: the raw ratio
    /// <c>adj/det</c> is the inverse of the raw tensor, which carries one factor of <c>2^fractionBitsInertia</c> too
    /// few for a result at <paramref name="fractionBitsOut"/>.</remarks>
    internal static bool TryInvertInertia(
        long ixx,
        long iyy,
        long izz,
        long ixy,
        long ixz,
        long iyz,
        int fractionBitsInertia,
        int fractionBitsOut,
        out long invXX,
        out long invYY,
        out long invZZ,
        out long invXY,
        out long invXZ,
        out long invYZ
    ) {
        if (!ScalesValid(first: fractionBitsInertia, second: fractionBitsOut) ||
            !FixedSymmetricSolve.TryInvertSymmetric3(
                a: ixx,
                b: ixy,
                c: ixz,
                d: iyy,
                e: iyz,
                f: izz,
                outputFractionShift: (fractionBitsInertia + fractionBitsOut),
                invA: out var resultXX,
                invB: out var resultXY,
                invC: out var resultXZ,
                invD: out var resultYY,
                invE: out var resultYZ,
                invF: out var resultZZ
            ) ||
            ((resultXX == 0L) && (resultYY == 0L) && (resultZZ == 0L) && (resultXY == 0L) && (resultXZ == 0L) && (resultYZ == 0L))) {
            invXX = 0L;
            invYY = 0L;
            invZZ = 0L;
            invXY = 0L;
            invXZ = 0L;
            invYZ = 0L;
            return false;
        }

        invXX = resultXX;
        invYY = resultYY;
        invZZ = resultZZ;
        invXY = resultXY;
        invXZ = resultXZ;
        invYZ = resultYZ;
        return true;
    }

    // Every fraction bit count this type accepts sits in [0, MaximumFractionBitCount]. The bound is what keeps every
    // internal shift below a few hundred bits, so a hostile count can neither allocate an enormous BigInteger nor
    // alias a shift.
    private static bool ScaleValid(int fractionBitCount) =>
        ((fractionBitCount >= 0) && (fractionBitCount <= MaximumFractionBitCount));
    private static bool ScalesValid(int first, int second) =>
        (ScaleValid(fractionBitCount: first) && ScaleValid(fractionBitCount: second));
    private static bool ScalesValid(int first, int second, int third) =>
        (ScalesValid(first: first, second: second) && ScaleValid(fractionBitCount: third));
    private static bool ScalesValid(int first, int second, int third, int fourth) =>
        (ScalesValid(first: first, second: second, third: third) && ScaleValid(fractionBitCount: fourth));

    // One entry of the parallel-axis transfer: the stored entry lifted onto the transfer denominator, plus the mass
    // term lifted onto the inertia scale, rounded once.
    private static bool TryTransfer(long entry, BigInteger mass, BigInteger term, long transferShift, int fractionBitsInertia, out long result) =>
        TryRoundExact(
            numerator: (((BigInteger)entry << ((int)transferShift)) + ((mass * term) << fractionBitsInertia)),
            denominator: (BigInteger.One << ((int)transferShift)),
            result: out result
        );

    // The exact rational value (numerator · 2^numeratorShift) / (denominatorFactor · 2^denominatorShift), rounded
    // once. The two shifts are folded onto whichever side keeps both exponents non-negative, so nothing is rounded
    // before the single division.
    private static bool TryRound(BigInteger numerator, BigInteger denominatorFactor, long denominatorShift, long numeratorShift, out long result) {
        var net = (numeratorShift - denominatorShift);
        var scaledNumerator = ((net >= 0L) ? (numerator << ((int)net)) : numerator);
        var scaledDenominator = ((net >= 0L) ? denominatorFactor : (denominatorFactor << ((int)-net)));

        return TryRoundExact(numerator: scaledNumerator, denominator: scaledDenominator, result: out result);
    }

    // The exact rational numerator/denominator rounded to the nearest raw, ties to even, refusing rather than
    // wrapping. The tie is decided by comparing the remainder against the DISTANCE TO THE NEXT multiple, the
    // formulation every sibling kernel in this family uses, rather than by doubling the remainder.
    private static bool TryRoundExact(BigInteger numerator, BigInteger denominator, out long result) {
        var negative = ((numerator.Sign < 0) != (denominator.Sign < 0));
        var magnitude = BigInteger.Abs(value: numerator);
        var divisor = BigInteger.Abs(value: denominator);

        if (divisor.IsZero) {
            result = 0L;
            return false;
        }

        var quotient = BigInteger.DivRem(dividend: magnitude, divisor: divisor, remainder: out var remainder);
        var distanceToNext = (divisor - remainder);

        if ((remainder > distanceToNext) || ((remainder == distanceToNext) && !((quotient & BigInteger.One).IsZero))) {
            quotient += BigInteger.One;
        }

        return TryNarrow(value: (negative ? -quotient : quotient), result: out result);
    }

    private static bool TryNarrow(BigInteger value, out long result) {
        if ((value < long.MinValue) || (value > long.MaxValue)) {
            result = 0L;
            return false;
        }

        result = ((long)value);
        return true;
    }

    private static void Clear(
        out long mass,
        out long centerX,
        out long centerY,
        out long centerZ,
        out long ixx,
        out long iyy,
        out long izz,
        out long ixy,
        out long ixz,
        out long iyz
    ) {
        mass = 0L;
        centerX = 0L;
        centerY = 0L;
        centerZ = 0L;
        ixx = 0L;
        iyy = 0L;
        izz = 0L;
        ixy = 0L;
        ixz = 0L;
        iyz = 0L;
    }
}
