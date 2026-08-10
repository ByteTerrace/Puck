using System.Numerics;

namespace Puck.Maths.Tests;

/// <summary>Claim bodies for the <c>mass-properties</c> family — <see cref="FixedMassProperties"/>'s scale-parameterized
/// volumes, masses, inertia tensors, parallel-axis transfer, compound accumulation and inversions. Every agreement is
/// against an <see cref="Oracles"/> reference that reaches the same quantity by a DIFFERENT route: the box through its
/// full extents rather than folded half-extents, the sphere's inertia through <c>(2/5)·M·r²</c> rather than the
/// collapsed <c>(8/15)·ρ·π·r⁵</c>, the capsule through its parts and a hemisphere's flat-face moment rather than
/// through the coefficient <c>83/320</c>, and the compound by carrying every part OUT to the origin and the total back
/// IN rather than transferring each part straight to the centre.
/// <para>The two sides share exactly one thing, deliberately and by declaration: the pinned rational
/// <see cref="FixedMassProperties.PiRaw"/>. That the constant IS the correctly rounded <c>π</c> is
/// <c>mass-properties.pinned-pi-is-correctly-rounded</c>'s business, decided against this suite's own Machin
/// enclosure, so the shared value is pinned rather than assumed.</para></summary>
internal static class MassPropertyClaims {
    // Dimensions and densities are folded onto a twenty-bit band and fraction bit counts onto [0, 32]. That is not
    // timidity about range: the products the subject forms are exact in BigInteger at any width, so a wider fold would
    // buy only refusals. The band keeps a healthy mixture of answers and carrier overflows, and the refusal side of
    // the biconditional is asserted on every draw either way.
    private static long FoldDimension(long raw) => ((long)(((ulong)raw) % (1UL << 20)));

    private static long FoldPositive(long raw) => (FoldDimension(raw: raw) + 1L);

    private static int FoldScale(long raw) => ((int)(((ulong)raw) % 33UL));

    // A signed offset with a modest reach, so a compound's parts sit around its centre rather than at the extremes.
    private static long FoldOffset(long raw) => ((long)((short)raw));

    // A signed inertia entry. The compound and transfer laws accept any symmetric tensor; physical realizability is
    // not a precondition of either kernel and is deliberately not imposed here.
    private static long FoldEntry(long raw) => (((long)(((ulong)raw) % (1UL << 30))) - (1L << 29));

    /// <summary>The four primitive volumes against the independent oracle, at swept dimensions and scales.</summary>
    /// <param name="left">Lanes 0..2 = the three dimensions (radius or half-extents, height or centre distance).</param>
    /// <param name="right">Lanes 0..1 drive the length and volume fraction bit counts.</param>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? VolumesVsOracle(long[] left, long[] right) {
        var first = FoldDimension(raw: left[0]);
        var second = FoldDimension(raw: left[1]);
        var third = FoldDimension(raw: left[2]);
        var fractionBitsLength = FoldScale(raw: right[0]);
        var fractionBitsVolume = FoldScale(raw: right[1]);

        for (var shape = 0; (shape < 4); ++shape) {
            var candidate = 0L;
            var subjectOk = false;

            switch (shape) {
                case 0:
                    subjectOk = FixedMassProperties.TrySphereVolume(radius: first, fractionBitsLength: fractionBitsLength, fractionBitsVolume: fractionBitsVolume, volume: out candidate);
                    break;
                case 1:
                    subjectOk = FixedMassProperties.TryBoxVolume(halfX: first, halfY: second, halfZ: third, fractionBitsLength: fractionBitsLength, fractionBitsVolume: fractionBitsVolume, volume: out candidate);
                    break;
                case 2:
                    subjectOk = FixedMassProperties.TryCylinderVolume(radius: first, height: second, fractionBitsLength: fractionBitsLength, fractionBitsVolume: fractionBitsVolume, volume: out candidate);
                    break;
                default:
                    subjectOk = FixedMassProperties.TryCapsuleVolume(radius: first, centerDistance: second, fractionBitsLength: fractionBitsLength, fractionBitsVolume: fractionBitsVolume, volume: out candidate);
                    break;
            }

            var oracleOk = Oracles.TryPrimitiveVolume(
                shape: shape,
                first: first,
                second: second,
                third: third,
                fractionBitsLength: fractionBitsLength,
                fractionBitsVolume: fractionBitsVolume,
                volume: out var expected
            );
            var operands = $"shape {shape} at ({first},{second},{third}) @ {fractionBitsLength} -> {fractionBitsVolume}";

            if (subjectOk != oracleOk) {
                return $"volume outcome mismatch for {operands}: subject={subjectOk} oracle={oracleOk}";
            }

            if (!subjectOk) {
                if (candidate != 0L) { return $"volume refused for {operands} but left {candidate} behind"; }

                continue;
            }

            if (candidate != expected) {
                return $"volume mismatch for {operands}: subject={candidate} oracle={expected}";
            }
        }

        return null;
    }

    /// <summary>The sphere's mass and inertia against the independent oracle.</summary>
    /// <param name="left">Lane 0 = the density, lane 1 = the radius.</param>
    /// <param name="right">Lanes 0..3 drive the density, length, mass and inertia fraction bit counts.</param>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? SphereVsOracle(long[] left, long[] right) {
        var density = FoldDimension(raw: left[0]);
        var radius = FoldDimension(raw: left[1]);
        var fd = FoldScale(raw: right[0]);
        var fl = FoldScale(raw: right[1]);
        var fm = FoldScale(raw: right[2]);
        var fi = FoldScale(raw: right[3]);

        var subjectOk = FixedMassProperties.TrySphereBody(
            density: density,
            fractionBitsDensity: fd,
            radius: radius,
            fractionBitsLength: fl,
            fractionBitsMass: fm,
            fractionBitsInertia: fi,
            mass: out var mass,
            inertia: out var inertia
        );
        var oracleOk = Oracles.TrySphereBody(
            density: density,
            fractionBitsDensity: fd,
            radius: radius,
            fractionBitsLength: fl,
            fractionBitsMass: fm,
            fractionBitsInertia: fi,
            mass: out var expectedMass,
            inertia: out var expectedInertia
        );

        return Compare(
            subjectOk: subjectOk,
            oracleOk: oracleOk,
            subject: [mass, inertia],
            expected: [expectedMass, expectedInertia],
            operands: $"sphere (density {density}@{fd}, radius {radius}@{fl} -> mass @{fm}, inertia @{fi})"
        );
    }

    /// <summary>The box's mass and diagonal inertia against the independent oracle, which works in FULL extents.</summary>
    /// <param name="left">Lane 0 = the density, lanes 1..3 = the three half-extents.</param>
    /// <param name="right">Lanes 0..3 drive the density, length, mass and inertia fraction bit counts.</param>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? BoxVsOracle(long[] left, long[] right) {
        var density = FoldDimension(raw: left[0]);
        var halfX = FoldDimension(raw: left[1]);
        var halfY = FoldDimension(raw: left[2]);
        var halfZ = FoldDimension(raw: left[3]);
        var fd = FoldScale(raw: right[0]);
        var fl = FoldScale(raw: right[1]);
        var fm = FoldScale(raw: right[2]);
        var fi = FoldScale(raw: right[3]);

        var subjectOk = FixedMassProperties.TryBoxBody(
            density: density,
            fractionBitsDensity: fd,
            halfX: halfX,
            halfY: halfY,
            halfZ: halfZ,
            fractionBitsLength: fl,
            fractionBitsMass: fm,
            fractionBitsInertia: fi,
            mass: out var mass,
            ixx: out var ixx,
            iyy: out var iyy,
            izz: out var izz
        );
        var oracleOk = Oracles.TryBoxBody(
            density: density,
            fractionBitsDensity: fd,
            halfX: halfX,
            halfY: halfY,
            halfZ: halfZ,
            fractionBitsLength: fl,
            fractionBitsMass: fm,
            fractionBitsInertia: fi,
            mass: out var expectedMass,
            ixx: out var expectedXX,
            iyy: out var expectedYY,
            izz: out var expectedZZ
        );

        return Compare(
            subjectOk: subjectOk,
            oracleOk: oracleOk,
            subject: [mass, ixx, iyy, izz],
            expected: [expectedMass, expectedXX, expectedYY, expectedZZ],
            operands: $"box (density {density}@{fd}, halves ({halfX},{halfY},{halfZ})@{fl} -> mass @{fm}, inertia @{fi})"
        );
    }

    /// <summary>The cylinder's mass and two moments against the independent oracle.</summary>
    /// <param name="left">Lane 0 = the density, lane 1 = the radius, lane 2 = the height.</param>
    /// <param name="right">Lanes 0..3 drive the density, length, mass and inertia fraction bit counts.</param>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? CylinderVsOracle(long[] left, long[] right) {
        var density = FoldDimension(raw: left[0]);
        var radius = FoldDimension(raw: left[1]);
        var height = FoldDimension(raw: left[2]);
        var fd = FoldScale(raw: right[0]);
        var fl = FoldScale(raw: right[1]);
        var fm = FoldScale(raw: right[2]);
        var fi = FoldScale(raw: right[3]);

        var subjectOk = FixedMassProperties.TryCylinderBody(
            density: density,
            fractionBitsDensity: fd,
            radius: radius,
            height: height,
            fractionBitsLength: fl,
            fractionBitsMass: fm,
            fractionBitsInertia: fi,
            mass: out var mass,
            axial: out var axial,
            perpendicular: out var perpendicular
        );
        var oracleOk = Oracles.TryCylinderBody(
            density: density,
            fractionBitsDensity: fd,
            radius: radius,
            height: height,
            fractionBitsLength: fl,
            fractionBitsMass: fm,
            fractionBitsInertia: fi,
            mass: out var expectedMass,
            axial: out var expectedAxial,
            perpendicular: out var expectedPerpendicular
        );

        return Compare(
            subjectOk: subjectOk,
            oracleOk: oracleOk,
            subject: [mass, axial, perpendicular],
            expected: [expectedMass, expectedAxial, expectedPerpendicular],
            operands: $"cylinder (density {density}@{fd}, radius {radius}, height {height} @{fl} -> mass @{fm}, inertia @{fi})"
        );
    }

    /// <summary>The capsule's mass and two moments against the parts-assembled oracle — the law that actually pins the
    /// <c>83/320</c> hemisphere coefficient and the <c>3r/8</c> centroid offset, neither of which the oracle names.</summary>
    /// <param name="left">Lane 0 = the density, lane 1 = the radius, lane 2 = the hemisphere-centre distance.</param>
    /// <param name="right">Lanes 0..3 drive the density, length, mass and inertia fraction bit counts.</param>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? CapsuleVsOracle(long[] left, long[] right) {
        var density = FoldDimension(raw: left[0]);
        var radius = FoldDimension(raw: left[1]);
        var centerDistance = FoldDimension(raw: left[2]);
        var fd = FoldScale(raw: right[0]);
        var fl = FoldScale(raw: right[1]);
        var fm = FoldScale(raw: right[2]);
        var fi = FoldScale(raw: right[3]);

        var subjectOk = FixedMassProperties.TryCapsuleBody(
            density: density,
            fractionBitsDensity: fd,
            radius: radius,
            centerDistance: centerDistance,
            fractionBitsLength: fl,
            fractionBitsMass: fm,
            fractionBitsInertia: fi,
            mass: out var mass,
            axial: out var axial,
            perpendicular: out var perpendicular
        );
        var oracleOk = Oracles.TryCapsuleBody(
            density: density,
            fractionBitsDensity: fd,
            radius: radius,
            centerDistance: centerDistance,
            fractionBitsLength: fl,
            fractionBitsMass: fm,
            fractionBitsInertia: fi,
            mass: out var expectedMass,
            axial: out var expectedAxial,
            perpendicular: out var expectedPerpendicular
        );

        return Compare(
            subjectOk: subjectOk,
            oracleOk: oracleOk,
            subject: [mass, axial, perpendicular],
            expected: [expectedMass, expectedAxial, expectedPerpendicular],
            operands: $"capsule (density {density}@{fd}, radius {radius}, centres {centerDistance} @{fl} -> mass @{fm}, inertia @{fi})"
        );
    }

    /// <summary>The parallel-axis transfer against the general-tensor oracle.</summary>
    /// <param name="left">Lanes 0..5 = the six distinct entries, lane 6 = the mass, lanes 7..9 = the displacement.</param>
    /// <param name="right">Lanes 0..2 drive the inertia, mass and length fraction bit counts.</param>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? ParallelAxisVsOracle(long[] left, long[] right) {
        long[] entries = [FoldEntry(raw: left[0]), FoldEntry(raw: left[1]), FoldEntry(raw: left[2]), FoldEntry(raw: left[3]), FoldEntry(raw: left[4]), FoldEntry(raw: left[5])];
        var mass = FoldPositive(raw: left[6]);
        long[] offsets = [FoldOffset(raw: left[7]), FoldOffset(raw: left[8]), FoldOffset(raw: left[9])];
        var fi = FoldScale(raw: right[0]);
        var fm = FoldScale(raw: right[1]);
        var fl = FoldScale(raw: right[2]);

        var subjectOk = FixedMassProperties.TryTranslateInertia(
            ixx: entries[0],
            iyy: entries[1],
            izz: entries[2],
            ixy: entries[3],
            ixz: entries[4],
            iyz: entries[5],
            fractionBitsInertia: fi,
            mass: mass,
            fractionBitsMass: fm,
            offsetX: offsets[0],
            offsetY: offsets[1],
            offsetZ: offsets[2],
            fractionBitsLength: fl,
            txx: out var txx,
            tyy: out var tyy,
            tzz: out var tzz,
            txy: out var txy,
            txz: out var txz,
            tyz: out var tyz
        );
        var expected = new long[6];
        var oracleOk = Oracles.TryTranslateInertia(
            entries: entries,
            fractionBitsInertia: fi,
            mass: mass,
            fractionBitsMass: fm,
            offsets: offsets,
            fractionBitsLength: fl,
            transferred: expected
        );

        return Compare(
            subjectOk: subjectOk,
            oracleOk: oracleOk,
            subject: [txx, tyy, tzz, txy, txz, tyz],
            expected: expected,
            operands: $"parallel axis (mass {mass}@{fm}, offset ({offsets[0]},{offsets[1]},{offsets[2]})@{fl}, inertia @{fi})"
        );
    }

    /// <summary>The compound accumulation of two parts against the origin-first oracle.</summary>
    /// <param name="left">The first part's mass, centre and six entries.</param>
    /// <param name="right">The second part's mass, centre and six entries, and its last lane the shared scales.</param>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? CompoundVsOracle(long[] left, long[] right) {
        FixedMassProperties.CompoundPart[] parts = [Part(lanes: left), Part(lanes: right)];
        var fm = FoldScale(raw: left[9]);
        var fl = FoldScale(raw: right[9]);
        var fi = FoldScale(raw: left[8]);

        var subjectOk = FixedMassProperties.TryCompound(
            parts: parts,
            fractionBitsMass: fm,
            fractionBitsLength: fl,
            fractionBitsInertia: fi,
            mass: out var mass,
            centerX: out var centerX,
            centerY: out var centerY,
            centerZ: out var centerZ,
            ixx: out var ixx,
            iyy: out var iyy,
            izz: out var izz,
            ixy: out var ixy,
            ixz: out var ixz,
            iyz: out var iyz
        );
        var center = new long[3];
        var tensor = new long[6];
        var oracleOk = Oracles.TryCompound(
            parts: parts,
            fractionBitsMass: fm,
            fractionBitsLength: fl,
            fractionBitsInertia: fi,
            center: center,
            tensor: tensor,
            mass: out var expectedMass
        );

        return Compare(
            subjectOk: subjectOk,
            oracleOk: oracleOk,
            subject: [mass, centerX, centerY, centerZ, ixx, iyy, izz, ixy, ixz, iyz],
            expected: [expectedMass, center[0], center[1], center[2], tensor[0], tensor[1], tensor[2], tensor[3], tensor[4], tensor[5]],
            operands: $"compound of two parts (mass @{fm}, length @{fl}, inertia @{fi})"
        );
    }

    /// <summary>The degeneracy that proves the capsule's own coefficients: at a hemisphere-centre distance of zero a
    /// capsule IS a sphere, so its mass and BOTH its moments must equal the sphere kernel's raws exactly — not within
    /// a tolerance, because the two exact rationals coincide identically before either is rounded. The identity that
    /// makes them coincide, <c>83/320 + (3/8)² = 2/5</c>, is asserted first as exact integers, so a failure says which
    /// half broke: the algebra or the kernel.</summary>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? CapsuleDegeneratesToSphere() {
        // 83/320 + 9/64 = 2/5, cross-multiplied to stay in integers: (83·64 + 9·320)·5 == 2·(320·64).
        var hemisphere = (((83 * 64) + (9 * 320)) * 5);
        var sphere = (2 * (320 * 64));

        if (hemisphere != sphere) {
            return $"the capsule's own degeneracy identity 83/320 + (3/8)^2 = 2/5 does not hold as integers: {hemisphere} != {sphere}";
        }

        long[] densities = [1L, 997L, 65536L, 1048575L];
        long[] radii = [1L, 3L, 22938L, 65536L];
        int[] lengthScales = [0, 8, 16];
        int[] resultScales = [0, 16, 24];

        foreach (var density in densities) {
            foreach (var radius in radii) {
                foreach (var lengthScale in lengthScales) {
                    foreach (var resultScale in resultScales) {
                        var capsuleOk = FixedMassProperties.TryCapsuleBody(
                            density: density,
                            fractionBitsDensity: 0,
                            radius: radius,
                            centerDistance: 0L,
                            fractionBitsLength: lengthScale,
                            fractionBitsMass: resultScale,
                            fractionBitsInertia: resultScale,
                            mass: out var capsuleMass,
                            axial: out var capsuleAxial,
                            perpendicular: out var capsulePerpendicular
                        );
                        var sphereOk = FixedMassProperties.TrySphereBody(
                            density: density,
                            fractionBitsDensity: 0,
                            radius: radius,
                            fractionBitsLength: lengthScale,
                            fractionBitsMass: resultScale,
                            fractionBitsInertia: resultScale,
                            mass: out var sphereMass,
                            inertia: out var sphereInertia
                        );
                        var operands = $"(density {density}, radius {radius}, length @{lengthScale}, results @{resultScale})";

                        if (capsuleOk != sphereOk) {
                            return $"a degenerate capsule and the sphere disagreed on whether they are representable at {operands}: capsule={capsuleOk} sphere={sphereOk}";
                        }

                        if (!capsuleOk) { continue; }

                        if (capsuleMass != sphereMass) {
                            return $"a degenerate capsule's mass {capsuleMass} is not the sphere's {sphereMass} at {operands}";
                        }

                        if (capsuleAxial != sphereInertia) {
                            return $"a degenerate capsule's AXIAL moment {capsuleAxial} is not the sphere's {sphereInertia} at {operands}";
                        }

                        if (capsulePerpendicular != sphereInertia) {
                            return $"a degenerate capsule's PERPENDICULAR moment {capsulePerpendicular} is not the sphere's {sphereInertia} at {operands} — the 83/320 coefficient or the 3r/8 offset is wrong";
                        }
                    }
                }
            }
        }

        return null;
    }

    /// <summary>The inversions refuse rather than answer a zero a caller would read as "infinitely heavy" or "unable
    /// to rotate": an inverse mass that rounds to zero at the requested scale, and an inverse inertia whose every entry
    /// does. Both are checked against the neighbouring scale that DOES carry the answer, so the law pins the boundary
    /// rather than merely observing a refusal.</summary>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? InversionRefusesBelowResolution() {
        if (FixedMassProperties.TryInvertMass(mass: long.MaxValue, fractionBitsMass: 0, fractionBitsOut: 0, inverseMass: out var underflowed) || (underflowed != 0L)) {
            return $"TryInvertMass answered {underflowed} where the reciprocal underflows the requested scale entirely";
        }

        if (FixedMassProperties.TryInvertMass(mass: 0L, fractionBitsMass: 16, fractionBitsOut: 16, inverseMass: out var zeroMass) || (zeroMass != 0L)) {
            return $"TryInvertMass accepted a zero mass (or left {zeroMass} behind)";
        }

        // A unit mass at Q16 inverts to a unit inverse mass at Q16, exactly.
        if (!FixedMassProperties.TryInvertMass(mass: 65536L, fractionBitsMass: 16, fractionBitsOut: 16, inverseMass: out var unit) || (unit != 65536L)) {
            return $"TryInvertMass answered {unit} for a unit mass at Q16, expected the exact 65536";
        }

        // A diagonal inertia of 2^40 at zero fraction bits: every entry of the true inverse is below one half.
        if (FixedMassProperties.TryInvertInertia(
                ixx: (1L << 40),
                iyy: (1L << 40),
                izz: (1L << 40),
                ixy: 0L,
                ixz: 0L,
                iyz: 0L,
                fractionBitsInertia: 0,
                fractionBitsOut: 0,
                invXX: out var ixxOut,
                invYY: out var iyyOut,
                invZZ: out var izzOut,
                invXY: out var ixyOut,
                invXZ: out var ixzOut,
                invYZ: out var iyzOut
            ) || (ixxOut != 0L) || (iyyOut != 0L) || (izzOut != 0L) || (ixyOut != 0L) || (ixzOut != 0L) || (iyzOut != 0L)) {
            return $"TryInvertInertia answered ({ixxOut},{iyyOut},{izzOut},{ixyOut},{ixzOut},{iyzOut}) where every entry of the true inverse underflows the requested scale";
        }

        // The identity tensor at Q16 inverts to the identity tensor at Q16, exactly.
        if (!FixedMassProperties.TryInvertInertia(
                ixx: 65536L,
                iyy: 65536L,
                izz: 65536L,
                ixy: 0L,
                ixz: 0L,
                iyz: 0L,
                fractionBitsInertia: 16,
                fractionBitsOut: 16,
                invXX: out var unitXX,
                invYY: out var unitYY,
                invZZ: out var unitZZ,
                invXY: out var unitXY,
                invXZ: out var unitXZ,
                invYZ: out var unitYZ
            ) || (unitXX != 65536L) || (unitYY != 65536L) || (unitZZ != 65536L) || (unitXY != 0L) || (unitXZ != 0L) || (unitYZ != 0L)) {
            return $"TryInvertInertia answered ({unitXX},{unitYY},{unitZZ},{unitXY},{unitXZ},{unitYZ}) for the Q16 identity tensor, expected the identity back";
        }

        return null;
    }

    /// <summary>The one constant the mass-property subject and its oracles share is the correctly rounded <c>π</c>:
    /// this decides that against this suite's own Machin arctangent enclosure, which is derived from an alternating
    /// series rather than transcribed from any digit table. The enclosure is taken at 128 bits and rounded down to
    /// <see cref="FixedMassProperties.PiFractionBitCount"/>; both ends must round to the same raw, which is what makes
    /// the verdict a decision rather than an estimate.</summary>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? PinnedPiIsCorrectlyRounded() {
        var enclosure = Oracles.Pi(bitCount: 128);
        var reduction = (BigInteger.One << (128 - FixedMassProperties.PiFractionBitCount));
        var low = Oracles.RoundRationalTiesToEven(numerator: enclosure.Low, denominator: reduction);
        var high = Oracles.RoundRationalTiesToEven(numerator: enclosure.High, denominator: reduction);

        if (low != high) {
            return $"the Machin enclosure at 128 bits is too wide to decide the correctly rounded pi at {FixedMassProperties.PiFractionBitCount} bits: [{low}, {high}]";
        }

        var pinned = FixedMassProperties.PiRaw;

        return ((low == pinned)
            ? null
            : $"the pinned pi raw {pinned} is not the correctly rounded pi at {FixedMassProperties.PiFractionBitCount} fraction bits, which is {low}");
    }

    private static FixedMassProperties.CompoundPart Part(long[] lanes) =>
        new(
            Mass: FoldPositive(raw: lanes[0]),
            CenterX: FoldOffset(raw: lanes[1]),
            CenterY: FoldOffset(raw: lanes[2]),
            CenterZ: FoldOffset(raw: lanes[3]),
            Ixx: FoldEntry(raw: lanes[4]),
            Iyy: FoldEntry(raw: lanes[5]),
            Izz: FoldEntry(raw: lanes[6]),
            Ixy: FoldEntry(raw: lanes[7]),
            Ixz: FoldEntry(raw: lanes[8]),
            Iyz: FoldEntry(raw: lanes[9])
        );

    // The shared verdict: the two sides agree on representability, a refusal leaves every output at zero (checked
    // against the SUBJECT, never merely against the oracle), and an answer matches entry for entry.
    private static string? Compare(bool subjectOk, bool oracleOk, ReadOnlySpan<long> subject, ReadOnlySpan<long> expected, string operands) {
        if (subjectOk != oracleOk) {
            return $"{operands}: outcome mismatch, subject={subjectOk} oracle={oracleOk}";
        }

        if (!subjectOk) {
            for (var index = 0; (index < subject.Length); ++index) {
                if (subject[index] != 0L) {
                    return $"{operands}: refused but left {subject[index]} in slot {index}";
                }
            }

            return null;
        }

        for (var index = 0; (index < subject.Length); ++index) {
            if (subject[index] != expected[index]) {
                return $"{operands}: slot {index} subject={subject[index]} oracle={expected[index]}";
            }
        }

        return null;
    }
}
