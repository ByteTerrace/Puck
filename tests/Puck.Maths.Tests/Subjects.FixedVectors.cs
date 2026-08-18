using System.Numerics;

namespace Puck.Maths.Tests;

internal static partial class Subjects {
    // ---- the fixed-point vectors: FixedVector2 and FixedVector3 ----

    private static FixedVector2 Plane(long x, long y) =>
        new(
            X: Raw(value: x),
            Y: Raw(value: y)
        );
    private static FixedVector3 Space(long x, long y, long z) =>
        new(
            X: Raw(value: x),
            Y: Raw(value: y),
            Z: Raw(value: z)
        );

    // The narrow lane's operand fold. The sampled space is sixty-four bits wide and the fused kernels' narrow branch
    // opens only below 2³⁰ (2³¹ for the two-term kernels), so an unfolded full-range draw reaches it once in 2³² draws
    // and the branch would be pinned by the edge battery alone. Reducing the WHOLE raw by an ODD span — the same
    // argument Domain.SublatticeShift's span makes — keeps zero at zero, sends the power-of-two edge set to distinct
    // residues, and leaves every product strictly inside the narrow accumulator's own bound. Subject and oracle apply
    // the identical map, so every sampled operand reaches a defined comparison rather than being skipped
    // asymmetrically.
    private const long NarrowSpan = 536870909L;

    private static long NarrowRaw(long raw) =>
        (raw % NarrowSpan);
    private static void NarrowLanes(ReadOnlySpan<long> source, Span<long> destination) {
        for (var lane = 0; (lane < source.Length); ++lane) {
            destination[lane] = NarrowRaw(raw: source[lane]);
        }
    }

    // The scale-invariance statement's fold: an ODD span just under 2⁴⁵, which is the largest magnitude at which the
    // normalization's common precondition is still a pure LEFT shift (its leading bit is 45). Folded this way a
    // direction can be scaled by 2¹⁷ without leaving the carrier, so the law can say the unit vector is unmoved.
    private const long DirectionSpan = 35184372088831L;

    private static long DirectionRaw(long raw) =>
        (raw % DirectionSpan);
    // A zero scalar has no quotient and would leave the exact sublattice's multiply-then-divide identity vacuous, so
    // the substitute is ONE — the only value that keeps the scalar on the lattice the domain folded every operand
    // onto. The identical substitution runs wherever the scalar is read.
    private static long LatticeScalar(long raw) =>
        ((0L == raw)
            ? (1L << FixedQ4816.FractionBitCount)
            : raw
        );
    private static long PlaneComponent(FixedVector2 vector, int lane) =>
        ((0 == lane)
            ? vector.X.Value
            : vector.Y.Value
        );
    private static long SpaceComponent(FixedVector3 vector, int lane) =>
        lane switch {
            0 => vector.X.Value,
            1 => vector.Y.Value,
            _ => vector.Z.Value,
        };
    private static string Lanes(long[] raws) =>
        string.Join(
            separator: ", ",
            values: raws
        );

    /// <summary>The subject plane's two fused products, sampled raws in and raws out.</summary>
    /// <param name="u1">The first vector's X raw.</param>
    /// <param name="v1">The first vector's Y raw.</param>
    /// <param name="u2">The second vector's X raw.</param>
    /// <param name="v2">The second vector's Y raw.</param>
    /// <returns>The dot product's raw and the wedge product's raw.</returns>
    public static (long U, long V) PlaneProducts(long u1, long v1, long u2, long v2) {
        var left = Plane(
            x: u1,
            y: v1
        );
        var right = Plane(
            x: u2,
            y: v2
        );

        return (
            FixedVector2.Dot(
            left: left,
            right: right
        ).Value,
            FixedVector2.Wedge(
            left: left,
            right: right
        ).Value
        );
    }
    /// <summary>The oracle for the plane's two fused products — ONE ties-to-even rounding of each exact product sum at
    /// shift sixteen.</summary>
    /// <param name="u1">The first vector's X raw.</param>
    /// <param name="v1">The first vector's Y raw.</param>
    /// <param name="u2">The second vector's X raw.</param>
    /// <param name="v2">The second vector's Y raw.</param>
    /// <returns>The reference dot and wedge raws.</returns>
    public static (long U, long V) PlaneProductsOracle(long u1, long v1, long u2, long v2) => (
        Oracles.FusedDot(
        left: [u1, v1],
        right: [u2, v2]
    ),
        Oracles.FusedWedge(
        leftX: u1,
        leftY: v1,
        rightX: u2,
        rightY: v2
    )
    );
    /// <summary>The subject plane's two fused products ON THE NARROW BRANCH — every operand folded below <c>2²⁹</c>, so
    /// every OR gate opens and the plain <see cref="long"/> accumulator is the one that runs.</summary>
    /// <param name="u1">The first vector's X raw.</param>
    /// <param name="v1">The first vector's Y raw.</param>
    /// <param name="u2">The second vector's X raw.</param>
    /// <param name="v2">The second vector's Y raw.</param>
    /// <returns>The dot product's raw and the wedge product's raw.</returns>
    public static (long U, long V) NarrowPlaneProducts(long u1, long v1, long u2, long v2) =>
        PlaneProducts(
            u1: NarrowRaw(raw: u1),
            v1: NarrowRaw(raw: v1),
            u2: NarrowRaw(raw: u2),
            v2: NarrowRaw(raw: v2)
        );
    /// <summary>The oracle for the narrow branch's two fused products — the SAME one-rounding reference the wide-lane
    /// case uses, on the identically folded operands.</summary>
    /// <param name="u1">The first vector's X raw.</param>
    /// <param name="v1">The first vector's Y raw.</param>
    /// <param name="u2">The second vector's X raw.</param>
    /// <param name="v2">The second vector's Y raw.</param>
    /// <returns>The reference dot and wedge raws.</returns>
    public static (long U, long V) NarrowPlaneProductsOracle(long u1, long v1, long u2, long v2) =>
        PlaneProductsOracle(
            u1: NarrowRaw(raw: u1),
            v1: NarrowRaw(raw: v1),
            u2: NarrowRaw(raw: u2),
            v2: NarrowRaw(raw: v2)
        );
    /// <summary>The subject space's dot product at the mixed pair and at both self-pairs — three genuine three-term
    /// statements, the two self-dots being the values the norm law cross-checks against the squared length.</summary>
    /// <param name="left">The first vector's raws.</param>
    /// <param name="right">The second vector's raws.</param>
    /// <param name="result">The three dot products' raws.</param>
    public static void SpaceDotLanes(ReadOnlySpan<long> left, ReadOnlySpan<long> right, Span<long> result) {
        var a = Space(
            x: left[0],
            y: left[1],
            z: left[2]
        );
        var b = Space(
            x: right[0],
            y: right[1],
            z: right[2]
        );

        result[0] = FixedVector3.Dot(
            left: a,
            right: b
        ).Value;
        result[1] = FixedVector3.Dot(
            left: a,
            right: a
        ).Value;
        result[2] = FixedVector3.Dot(
            left: b,
            right: b
        ).Value;
    }
    /// <summary>The oracle for the space's three dot products — ONE ties-to-even rounding of each exact three-product
    /// sum at shift sixteen.</summary>
    /// <param name="left">The first vector's raws.</param>
    /// <param name="right">The second vector's raws.</param>
    /// <param name="result">The three reference raws.</param>
    public static void SpaceDotLanesOracle(ReadOnlySpan<long> left, ReadOnlySpan<long> right, Span<long> result) {
        result[0] = Oracles.FusedDot(
            left: left,
            right: right
        );
        result[1] = Oracles.FusedDot(
            left: left,
            right: left
        );
        result[2] = Oracles.FusedDot(
            left: right,
            right: right
        );
    }
    /// <summary>The subject space's cross product, all three lanes.</summary>
    /// <param name="left">The first vector's raws.</param>
    /// <param name="right">The second vector's raws.</param>
    /// <param name="result">The cross product's three raws.</param>
    public static void SpaceCrossLanes(ReadOnlySpan<long> left, ReadOnlySpan<long> right, Span<long> result) {
        var product = FixedVector3.Cross(
            left: Space(
                x: left[0],
                y: left[1],
                z: left[2]
            ),
            right: Space(
                x: right[0],
                y: right[1],
                z: right[2]
            )
        );

        result[0] = product.X.Value;
        result[1] = product.Y.Value;
        result[2] = product.Z.Value;
    }
    /// <summary>The oracle for the space's cross product — each lane ONE ties-to-even rounding of its exact two-product
    /// difference at shift sixteen.</summary>
    /// <param name="left">The first vector's raws.</param>
    /// <param name="right">The second vector's raws.</param>
    /// <param name="result">The three reference raws.</param>
    public static void SpaceCrossLanesOracle(ReadOnlySpan<long> left, ReadOnlySpan<long> right, Span<long> result) =>
        Oracles.FusedCross(
            left: left,
            result: result,
            right: right
        );
    /// <summary>The subject space's three dot products ON THE NARROW BRANCH.</summary>
    /// <param name="left">The first vector's raws.</param>
    /// <param name="right">The second vector's raws.</param>
    /// <param name="result">The three dot products' raws.</param>
    public static void NarrowSpaceDotLanes(ReadOnlySpan<long> left, ReadOnlySpan<long> right, Span<long> result) {
        Span<long> foldedLeft = stackalloc long[3];
        Span<long> foldedRight = stackalloc long[3];

        NarrowLanes(
            destination: foldedLeft,
            source: left
        );
        NarrowLanes(
            destination: foldedRight,
            source: right
        );
        SpaceDotLanes(
            left: foldedLeft,
            result: result,
            right: foldedRight
        );
    }
    /// <summary>The oracle for the narrow branch's three dot products.</summary>
    /// <param name="left">The first vector's raws.</param>
    /// <param name="right">The second vector's raws.</param>
    /// <param name="result">The three reference raws.</param>
    public static void NarrowSpaceDotLanesOracle(ReadOnlySpan<long> left, ReadOnlySpan<long> right, Span<long> result) {
        Span<long> foldedLeft = stackalloc long[3];
        Span<long> foldedRight = stackalloc long[3];

        NarrowLanes(
            destination: foldedLeft,
            source: left
        );
        NarrowLanes(
            destination: foldedRight,
            source: right
        );
        SpaceDotLanesOracle(
            left: foldedLeft,
            result: result,
            right: foldedRight
        );
    }
    /// <summary>The subject space's cross product ON THE NARROW BRANCH.</summary>
    /// <param name="left">The first vector's raws.</param>
    /// <param name="right">The second vector's raws.</param>
    /// <param name="result">The cross product's three raws.</param>
    public static void NarrowSpaceCrossLanes(ReadOnlySpan<long> left, ReadOnlySpan<long> right, Span<long> result) {
        Span<long> foldedLeft = stackalloc long[3];
        Span<long> foldedRight = stackalloc long[3];

        NarrowLanes(
            destination: foldedLeft,
            source: left
        );
        NarrowLanes(
            destination: foldedRight,
            source: right
        );
        SpaceCrossLanes(
            left: foldedLeft,
            result: result,
            right: foldedRight
        );
    }
    /// <summary>The oracle for the narrow branch's cross product.</summary>
    /// <param name="left">The first vector's raws.</param>
    /// <param name="right">The second vector's raws.</param>
    /// <param name="result">The three reference raws.</param>
    public static void NarrowSpaceCrossLanesOracle(ReadOnlySpan<long> left, ReadOnlySpan<long> right, Span<long> result) {
        Span<long> foldedLeft = stackalloc long[3];
        Span<long> foldedRight = stackalloc long[3];

        NarrowLanes(
            destination: foldedLeft,
            source: left
        );
        NarrowLanes(
            destination: foldedRight,
            source: right
        );
        SpaceCrossLanesOracle(
            left: foldedLeft,
            result: result,
            right: foldedRight
        );
    }
    /// <summary>The fused one-rounding side of the product canary: the plane's dot and wedge and the space's dot, all
    /// on narrow-folded operands where nothing saturates and every operand is strictly inside the accumulator's own
    /// bound.</summary>
    /// <param name="left">The first vector's raws.</param>
    /// <param name="right">The second vector's raws.</param>
    /// <param name="result">The three kernels' raws.</param>
    public static void NarrowFusedProductLanes(ReadOnlySpan<long> left, ReadOnlySpan<long> right, Span<long> result) {
        Span<long> foldedLeft = stackalloc long[3];
        Span<long> foldedRight = stackalloc long[3];

        NarrowLanes(
            destination: foldedLeft,
            source: left
        );
        NarrowLanes(
            destination: foldedRight,
            source: right
        );

        var planeLeft = Plane(
            x: foldedLeft[0],
            y: foldedLeft[1]
        );
        var planeRight = Plane(
            x: foldedRight[0],
            y: foldedRight[1]
        );

        result[0] = FixedVector2.Dot(
            left: planeLeft,
            right: planeRight
        ).Value;
        result[1] = FixedVector2.Wedge(
            left: planeLeft,
            right: planeRight
        ).Value;
        result[2] = FixedVector3.Dot(
            left: Space(
                x: foldedLeft[0],
                y: foldedLeft[1],
                z: foldedLeft[2]
            ),
            right: Space(
                x: foldedRight[0],
                y: foldedRight[1],
                z: foldedRight[2]
            )
        ).Value;
    }
    /// <summary>The per-product-rounding side of the product canary — the discipline a kernel without a fused
    /// accumulator is forced into, in exact <see cref="BigInteger"/>.</summary>
    /// <param name="left">The first vector's raws.</param>
    /// <param name="right">The second vector's raws.</param>
    /// <param name="result">The three reference raws.</param>
    public static void NarrowPerProductLanes(ReadOnlySpan<long> left, ReadOnlySpan<long> right, Span<long> result) {
        Span<long> foldedLeft = stackalloc long[3];
        Span<long> foldedRight = stackalloc long[3];

        NarrowLanes(
            destination: foldedLeft,
            source: left
        );
        NarrowLanes(
            destination: foldedRight,
            source: right
        );

        result[0] = Oracles.PerProductDot(
            left: foldedLeft[..2],
            right: foldedRight[..2]
        );
        result[1] = Oracles.PerProductWedge(
            leftX: foldedLeft[0],
            leftY: foldedLeft[1],
            rightX: foldedRight[0],
            rightY: foldedRight[1]
        );
        result[2] = Oracles.PerProductDot(
            left: foldedLeft,
            right: foldedRight
        );
    }
    /// <summary>The fused one-rounding side of the norm canary: the space's and the plane's squared lengths on
    /// narrow-folded operands, where the norm never saturates.</summary>
    /// <param name="left">The first vector's raws.</param>
    /// <param name="right">The second vector's raws.</param>
    /// <param name="result">The three squared lengths' raws.</param>
    public static void NarrowFusedSquaredNormLanes(ReadOnlySpan<long> left, ReadOnlySpan<long> right, Span<long> result) {
        Span<long> foldedLeft = stackalloc long[3];
        Span<long> foldedRight = stackalloc long[3];

        NarrowLanes(
            destination: foldedLeft,
            source: left
        );
        NarrowLanes(
            destination: foldedRight,
            source: right
        );

        result[0] = Space(
            x: foldedLeft[0],
            y: foldedLeft[1],
            z: foldedLeft[2]
        ).LengthSquared.Value;
        result[1] = Plane(
            x: foldedLeft[0],
            y: foldedLeft[1]
        ).LengthSquared.Value;
        result[2] = Space(
            x: foldedRight[0],
            y: foldedRight[1],
            z: foldedRight[2]
        ).LengthSquared.Value;
    }
    /// <summary>The per-square-rounding side of the norm canary — each raw Q32 square rounded to Q16 on its own before
    /// the exact sum.</summary>
    /// <param name="left">The first vector's raws.</param>
    /// <param name="right">The second vector's raws.</param>
    /// <param name="result">The three reference raws.</param>
    public static void NarrowPerSquareLanes(ReadOnlySpan<long> left, ReadOnlySpan<long> right, Span<long> result) {
        Span<long> foldedLeft = stackalloc long[3];
        Span<long> foldedRight = stackalloc long[3];

        NarrowLanes(
            destination: foldedLeft,
            source: left
        );
        NarrowLanes(
            destination: foldedRight,
            source: right
        );

        result[0] = ((long)Oracles.PerSquareNorm(raws: foldedLeft));
        result[1] = ((long)Oracles.PerSquareNorm(raws: foldedLeft[..2]));
        result[2] = ((long)Oracles.PerSquareNorm(raws: foldedRight));
    }
    /// <summary>The plane's wedge and dot, as the first side of the kinship twin.</summary>
    /// <param name="left">The first vector's two raws.</param>
    /// <param name="right">The second vector's two raws.</param>
    /// <param name="result">The wedge raw and the dot raw.</param>
    public static void PlaneWedgeAndDotLanes(ReadOnlySpan<long> left, ReadOnlySpan<long> right, Span<long> result) {
        var a = Plane(
            x: left[0],
            y: left[1]
        );
        var b = Plane(
            x: right[0],
            y: right[1]
        );

        result[0] = FixedVector2.Wedge(
            left: a,
            right: b
        ).Value;
        result[1] = FixedVector2.Dot(
            left: a,
            right: b
        ).Value;
    }
    /// <summary>The space's cross Z lane and dot at the EMBEDDED plane, as the second side of the kinship twin.</summary>
    /// <param name="left">The first vector's two raws.</param>
    /// <param name="right">The second vector's two raws.</param>
    /// <param name="result">The cross Z raw and the dot raw.</param>
    public static void SpaceEmbeddedWedgeAndDotLanes(ReadOnlySpan<long> left, ReadOnlySpan<long> right, Span<long> result) {
        var a = Space(
            x: left[0],
            y: left[1],
            z: 0L
        );
        var b = Space(
            x: right[0],
            y: right[1],
            z: 0L
        );

        result[0] = FixedVector3.Cross(
            left: a,
            right: b
        ).Z.Value;
        result[1] = FixedVector3.Dot(
            left: a,
            right: b
        ).Value;
    }
    /// <summary>The independent witness both sides of the kinship twin must also equal, on the same operand
    /// stream.</summary>
    /// <param name="left">The first vector's two raws.</param>
    /// <param name="right">The second vector's two raws.</param>
    /// <param name="result">The reference wedge raw and dot raw.</param>
    public static void PlaneWedgeAndDotOracleLanes(ReadOnlySpan<long> left, ReadOnlySpan<long> right, Span<long> result) {
        result[0] = Oracles.FusedWedge(
            leftX: left[0],
            leftY: left[1],
            rightX: right[0],
            rightY: right[1]
        );
        result[1] = Oracles.FusedDot(
            left: left,
            right: right
        );
    }
    /// <summary>Proves the componentwise lift is EXACTLY the carrier's own operator applied lane by lane: the wrapping
    /// sum, difference and negation against arbitrary-width arithmetic, the scalar multiply and divide against one
    /// ties-to-even rounding each, and the interpolation against the true mathematical result with no intermediate
    /// wrap and one final rounding — stated for both types on the same operands, so a divergence between the two lifts
    /// shows up here rather than only through the kinship seam.</summary>
    /// <param name="left">The first operand's four lanes: the first vector and the scalar.</param>
    /// <param name="right">The second operand's four lanes: the second vector and the divisor.</param>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? VectorComponentwiseMatchesOracle(long[] left, long[] right) {
        var a = Space(
            x: left[0],
            y: left[1],
            z: left[2]
        );
        var b = Space(
            x: right[0],
            y: right[1],
            z: right[2]
        );
        var scaleRaw = left[3];
        var divisorRaw = NonZeroDivisor(b: right[3]);
        var scale = Raw(value: scaleRaw);
        var divisor = Raw(value: divisorRaw);
        var sum = (a + b);
        var difference = (a - b);
        var negated = (-a);
        var scaled = (a * scale);
        var quotient = (a / divisor);
        var interpolated = FixedVector3.Lerp(
            amount: scale,
            from: a,
            to: b
        );
        var planeA = Plane(
            x: left[0],
            y: left[1]
        );
        var planeB = Plane(
            x: right[0],
            y: right[1]
        );
        var planeSum = (planeA + planeB);
        var planeDifference = (planeA - planeB);
        var planeNegated = (-planeA);
        var planeScaled = (planeA * scale);
        var planeQuotient = (planeA / divisor);
        var planeInterpolated = FixedVector2.Lerp(
            amount: scale,
            from: planeA,
            to: planeB
        );

        for (var lane = 0; (lane < 3); ++lane) {
            var x = left[lane];
            var y = right[lane];
            var expectedSum = Oracles.WrapToRaw(value: (((BigInteger)x) + y));
            var expectedDifference = Oracles.WrapToRaw(value: (((BigInteger)x) - y));
            var expectedNegation = Oracles.WrapToRaw(value: -((BigInteger)x));
            var expectedScaled = Oracles.RoundDyadic(
                exact: (((BigInteger)x) * scaleRaw),
                shift: FixedQ4816.FractionBitCount
            );
            var expectedQuotient = Oracles.RoundDyadicRatio(
                numerator: new BigInteger(value: x),
                denominator: new BigInteger(value: divisorRaw),
                shift: FixedQ4816.FractionBitCount
            );
            var expectedLerp = Oracles.LerpRaw(
                amount: scaleRaw,
                from: x,
                to: y
            );

            if (SpaceComponent(
                lane: lane,
                vector: sum
            ) != expectedSum) { return $"the sum is wrong at lane {lane} for ({x}, {y})"; }
            if (SpaceComponent(
                lane: lane,
                vector: difference
            ) != expectedDifference) { return $"the difference is wrong at lane {lane} for ({x}, {y})"; }
            if (SpaceComponent(
                lane: lane,
                vector: negated
            ) != expectedNegation) { return $"the negation is wrong at lane {lane} for {x}"; }
            if (SpaceComponent(
                lane: lane,
                vector: scaled
            ) != expectedScaled) { return $"the scaling is wrong at lane {lane} for ({x}, {scaleRaw})"; }
            if (SpaceComponent(
                lane: lane,
                vector: quotient
            ) != expectedQuotient) { return $"the division is wrong at lane {lane} for ({x}, {divisorRaw})"; }
            if (SpaceComponent(
                lane: lane,
                vector: interpolated
            ) != expectedLerp) { return $"the interpolation is wrong at lane {lane} for ({x}, {y}, {scaleRaw})"; }

            if (lane > 1) { continue; }

            if (PlaneComponent(
                lane: lane,
                vector: planeSum
            ) != expectedSum) { return $"the plane sum is wrong at lane {lane} for ({x}, {y})"; }
            if (PlaneComponent(
                lane: lane,
                vector: planeDifference
            ) != expectedDifference) { return $"the plane difference is wrong at lane {lane} for ({x}, {y})"; }
            if (PlaneComponent(
                lane: lane,
                vector: planeNegated
            ) != expectedNegation) { return $"the plane negation is wrong at lane {lane} for {x}"; }
            if (PlaneComponent(
                lane: lane,
                vector: planeScaled
            ) != expectedScaled) { return $"the plane scaling is wrong at lane {lane} for ({x}, {scaleRaw})"; }
            if (PlaneComponent(
                lane: lane,
                vector: planeQuotient
            ) != expectedQuotient) { return $"the plane division is wrong at lane {lane} for ({x}, {divisorRaw})"; }
            if (PlaneComponent(
                lane: lane,
                vector: planeInterpolated
            ) != expectedLerp) { return $"the plane interpolation is wrong at lane {lane} for ({x}, {y}, {scaleRaw})"; }
        }

        // Both ends of the interpolation are EXACT, at every swept component including the carrier extremes.
        if (FixedVector3.Lerp(
            from: a,
            to: b,
            amount: FixedQ4816.Zero
        ) != a) { return "the interpolation is not exact at zero"; }
        if (FixedVector3.Lerp(
            from: a,
            to: b,
            amount: FixedQ4816.One
        ) != b) { return "the interpolation is not exact at one"; }
        if (FixedVector2.Lerp(
            from: planeA,
            to: planeB,
            amount: FixedQ4816.Zero
        ) != planeA) { return "the plane interpolation is not exact at zero"; }
        if (FixedVector2.Lerp(
            from: planeA,
            to: planeB,
            amount: FixedQ4816.One
        ) != planeB) { return "the plane interpolation is not exact at one"; }

        return null;
    }
    /// <summary>Proves both norms against an independently rooted reference, refusal predicates included: the squared
    /// length is ONE ties-to-even rounding of the exact sum of squares, the length is the nearest integer root of that
    /// same exact sum, each Try surface accepts exactly where the reference is representable and leaves the default
    /// behind where it is not, and each saturating property answers the accepted value or the carrier's maximum. The
    /// self dot product is cross-checked against the squared norm wherever the norm does not refuse, the two norms are
    /// even, and both are lane-symmetric.</summary>
    /// <param name="left">The first operand's three lanes.</param>
    /// <param name="right">The second operand's three lanes.</param>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    /// <remarks>Every case is evaluated TWICE: once on the full-range operands, which mostly drive the refusal path,
    /// and once on narrow-folded ones, which mostly drive the success path — so both branches of both norms are swept
    /// at every draw rather than only where the sampler happens to land.</remarks>
    public static string? VectorNormMatchesOracle(long[] left, long[] right) {
        foreach (var folded in ((ReadOnlySpan<bool>)[false, true])) {
            var x = (folded
                ? NarrowRaw(raw: left[0])
                : left[0]
            );
            var y = (folded
                ? NarrowRaw(raw: left[1])
                : left[1]
            );
            var z = (folded
                ? NarrowRaw(raw: left[2])
                : left[2]
            );
            var space = Space(
                x: x,
                y: y,
                z: z
            );
            var plane = Plane(
                x: x,
                y: y
            );
            var spaceRaws = new[] { x, y, z, };
            var planeRaws = new[] { x, y, };
            var spaceSquaredAccepted = space.TryLengthSquared(squaredLength: out var spaceSquared);
            var spaceLengthAccepted = space.TryLength(length: out var spaceLength);
            var planeSquaredAccepted = plane.TryLengthSquared(squaredLength: out var planeSquared);
            var planeLengthAccepted = plane.TryLength(length: out var planeLength);

            if (NormStatements(
                name: "space",
                raws: spaceRaws,
                squaredAccepted: spaceSquaredAccepted,
                squared: spaceSquared,
                squaredSaturating: space.LengthSquared,
                lengthAccepted: spaceLengthAccepted,
                length: spaceLength,
                lengthSaturating: space.Length
            ) is { } spaceDetail) { return spaceDetail; }

            if (NormStatements(
                name: "plane",
                raws: planeRaws,
                squaredAccepted: planeSquaredAccepted,
                squared: planeSquared,
                squaredSaturating: plane.LengthSquared,
                lengthAccepted: planeLengthAccepted,
                length: planeLength,
                lengthSaturating: plane.Length
            ) is { } planeDetail) { return planeDetail; }

            // The two rounding bodies carry the SAME rule at the same scale, and they PART at the carrier boundary:
            // the self dot product always reports the WRAPPED rounding, so it agrees with the squared norm wherever
            // the norm is representable and diverges from the norm's saturation exactly where it is not — which is
            // why both members exist.
            var expectedSquared = Oracles.RoundedSquaredNorm(raws: spaceRaws);
            var expectedPlaneSquared = Oracles.RoundedSquaredNorm(raws: planeRaws);
            var spaceSelfDot = FixedVector3.Dot(
                left: space,
                right: space
            ).Value;
            var planeSelfDot = FixedVector2.Dot(
                left: plane,
                right: plane
            ).Value;

            if (spaceSelfDot != Oracles.WrapToRaw(value: expectedSquared)) { return $"the space self dot product is not the wrapped squared norm at ({Lanes(raws: spaceRaws)})"; }
            if (planeSelfDot != Oracles.WrapToRaw(value: expectedPlaneSquared)) { return $"the plane self dot product is not the wrapped squared norm at ({Lanes(raws: planeRaws)})"; }

            if (expectedSquared <= long.MaxValue) {
                if (spaceSelfDot != space.LengthSquared.Value) { return $"the space self dot product disagrees with the squared norm at ({Lanes(raws: spaceRaws)})"; }
            } else if (space.LengthSquared != FixedQ4816.MaxValue) {
                return $"the space squared norm did not saturate where the self dot product wrapped at ({Lanes(raws: spaceRaws)})";
            }

            if (expectedPlaneSquared <= long.MaxValue) {
                if (planeSelfDot != plane.LengthSquared.Value) { return $"the plane self dot product disagrees with the squared norm at ({Lanes(raws: planeRaws)})"; }
            } else if (plane.LengthSquared != FixedQ4816.MaxValue) {
                return $"the plane squared norm did not saturate where the self dot product wrapped at ({Lanes(raws: planeRaws)})";
            }

            // Both norms are EVEN and lane-symmetric, at every swept raw including the asymmetric two's-complement
            // minimum, where negation is a fixed point and the magnitude is unchanged.
            if ((-space).LengthSquared != space.LengthSquared) { return $"the space squared norm is not even at ({Lanes(raws: spaceRaws)})"; }
            if ((-space).Length != space.Length) { return $"the space norm is not even at ({Lanes(raws: spaceRaws)})"; }
            if ((-plane).LengthSquared != plane.LengthSquared) { return $"the plane squared norm is not even at ({Lanes(raws: planeRaws)})"; }
            if ((-plane).Length != plane.Length) { return $"the plane norm is not even at ({Lanes(raws: planeRaws)})"; }
            if (Space(
                x: z,
                y: x,
                z: y
            ).Length != space.Length) { return $"the space norm is not lane-symmetric at ({Lanes(raws: spaceRaws)})"; }
            if (Space(
                x: z,
                y: x,
                z: y
            ).LengthSquared != space.LengthSquared) { return $"the space squared norm is not lane-symmetric at ({Lanes(raws: spaceRaws)})"; }
            if (Plane(
                x: y,
                y: x
            ).Length != plane.Length) { return $"the plane norm is not lane-symmetric at ({Lanes(raws: planeRaws)})"; }
            if (Plane(
                x: y,
                y: x
            ).LengthSquared != plane.LengthSquared) { return $"the plane squared norm is not lane-symmetric at ({Lanes(raws: planeRaws)})"; }
        }

        return null;
    }

    // One vector's four norm statements: the Try surface's verdict against the reference's representability, its value
    // against the reference, the default it leaves behind on a refusal, and the saturating property that reads the same
    // fact through FixedQ4816.MaxValue. The caller reads its own type's four members and hands the observations here,
    // so the statement is written once for both arities.
    private static string? NormStatements(
        string name,
        long[] raws,
        bool squaredAccepted,
        FixedQ4816 squared,
        FixedQ4816 squaredSaturating,
        bool lengthAccepted,
        FixedQ4816 length,
        FixedQ4816 lengthSaturating
    ) {
        var expectedSquared = Oracles.RoundedSquaredNorm(raws: raws);
        var expectedRoot = Oracles.NormRoot(raws: raws);
        var squaredRepresentable = (expectedSquared <= long.MaxValue);
        var rootRepresentable = (expectedRoot <= long.MaxValue);
        var operands = Lanes(raws: raws);

        if (squaredAccepted != squaredRepresentable) { return $"the {name} squared norm reported {squaredAccepted} at ({operands})"; }
        if (
            squaredAccepted &&
            (squared.Value != ((long)expectedSquared))
        ) { return $"the {name} squared norm is {squared.Value}, expected {expectedSquared}, at ({operands})"; }
        if (
            !squaredAccepted &&
            (squared != default)
        ) { return $"the refused {name} squared norm left {squared.Value} behind at ({operands})"; }
        if (squaredSaturating != (squaredRepresentable
            ? FixedQ4816.FromRawBits(value: ((long)expectedSquared))
            : FixedQ4816.MaxValue)) { return $"the saturating {name} squared norm is {squaredSaturating.Value} at ({operands})"; }

        if (lengthAccepted != rootRepresentable) { return $"the {name} norm reported {lengthAccepted} at ({operands})"; }
        if (
            lengthAccepted &&
            (length.Value != ((long)expectedRoot))
        ) { return $"the {name} norm is {length.Value}, expected {expectedRoot}, at ({operands})"; }
        if (
            !lengthAccepted &&
            (length != default)
        ) { return $"the refused {name} norm left {length.Value} behind at ({operands})"; }
        if (lengthSaturating != (rootRepresentable
            ? FixedQ4816.FromRawBits(value: ((long)expectedRoot))
            : FixedQ4816.MaxValue)) { return $"the saturating {name} norm is {lengthSaturating.Value} at ({operands})"; }

        return null;
    }

    /// <summary>Proves the plane embeds in the space exactly at the norm surface: both Try members report the same
    /// verdict and the same value for a plane and for that plane embedded at <c>z = 0</c>, refusals included, and both
    /// saturating properties agree — so the saturation boundary is one fact at both arities.</summary>
    /// <param name="left">The first operand's two lanes.</param>
    /// <param name="right">The second operand's two lanes.</param>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? VectorPlaneInSpaceExact(long[] left, long[] right) {
        foreach (var raws in ((ReadOnlySpan<long[]>)[left, right])) {
            var plane = Plane(
                x: raws[0],
                y: raws[1]
            );
            var space = Space(
                x: raws[0],
                y: raws[1],
                z: 0L
            );
            var operands = $"{raws[0]}, {raws[1]}";

            if (plane.TryLengthSquared(squaredLength: out var planeSquared) != space.TryLengthSquared(squaredLength: out var spaceSquared)) { return $"the squared norm verdicts differ at the embedded plane ({operands})"; }
            if (planeSquared != spaceSquared) { return $"the squared norms differ at the embedded plane ({operands})"; }
            if (plane.TryLength(length: out var planeLength) != space.TryLength(length: out var spaceLength)) { return $"the norm verdicts differ at the embedded plane ({operands})"; }
            if (planeLength != spaceLength) { return $"the norms differ at the embedded plane ({operands})"; }
            if (plane.LengthSquared != space.LengthSquared) { return $"the saturating squared norms differ at the embedded plane ({operands})"; }
            if (plane.Length != space.Length) { return $"the saturating norms differ at the embedded plane ({operands})"; }
        }

        return null;
    }
    /// <summary>Proves the family's algebra EXACTLY on the fixed-point sublattice, where every product, sum and
    /// difference below is exact in Q16 and the identities are equalities of integers rather than approximations: the
    /// planar Lagrange identity, the cross product's orthogonality to both factors, the Jacobi identity, the vector
    /// triple product expansion, bilinearity in the first argument, the symmetry and the two antisymmetries, and the
    /// scalar multiply undone exactly by the scalar divide.</summary>
    /// <param name="left">The first operand's six lanes: two space vectors.</param>
    /// <param name="right">The second operand's six lanes: a third space vector and the scalar.</param>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? VectorExactAlgebra(long[] left, long[] right) {
        var a = Space(
            x: left[0],
            y: left[1],
            z: left[2]
        );
        var b = Space(
            x: left[3],
            y: left[4],
            z: left[5]
        );
        var c = Space(
            x: right[0],
            y: right[1],
            z: right[2]
        );
        var planeA = Plane(
            x: left[0],
            y: left[1]
        );
        var planeB = Plane(
            x: left[3],
            y: left[4]
        );
        var planeC = Plane(
            x: right[0],
            y: right[1]
        );
        var scaleRaw = LatticeScalar(raw: right[3]);
        var scale = Raw(value: scaleRaw);

        // The planar Lagrange identity, as the subject computes it: both sides carry 2³², so the raws compare
        // directly against the exact integer identity (m·n)² + (m∧n)² == |m|²·|n|².
        var planeDot = new BigInteger(value: FixedVector2.Dot(
            left: planeA,
            right: planeB
        ).Value);
        var planeWedge = new BigInteger(value: FixedVector2.Wedge(
            left: planeA,
            right: planeB
        ).Value);
        var planeSquaredA = new BigInteger(value: planeA.LengthSquared.Value);
        var planeSquaredB = new BigInteger(value: planeB.LengthSquared.Value);

        if (((planeDot * planeDot) + (planeWedge * planeWedge)) != (planeSquaredA * planeSquaredB)) { return $"the Lagrange identity fails at ({Lanes(raws: [left[0], left[1], left[3], left[4]])})"; }

        // Cross is orthogonal to both factors, which no amount of correct rounding would give if a lane of Cross were
        // mis-assigned.
        var cross = FixedVector3.Cross(
            left: a,
            right: b
        );

        if (FixedVector3.Dot(
            left: a,
            right: cross
        ) != FixedQ4816.Zero) { return $"the cross product is not orthogonal to its first factor at ({Lanes(raws: left)})"; }
        if (FixedVector3.Dot(
            left: b,
            right: cross
        ) != FixedQ4816.Zero) { return $"the cross product is not orthogonal to its second factor at ({Lanes(raws: left)})"; }

        // The Jacobi identity closes exactly, and the vector triple product expands exactly — the second pins the
        // scalar multiply and the subtraction INSIDE a cross-product identity rather than beside one.
        var jacobi = (
            (FixedVector3.Cross(
            left: a,
            right: FixedVector3.Cross(
                left: b,
                right: c
            )
        ) +
            FixedVector3.Cross(
            left: b,
            right: FixedVector3.Cross(
                left: c,
                right: a
            )
        )) +
            FixedVector3.Cross(
            left: c,
            right: FixedVector3.Cross(
                left: a,
                right: b
            )
        )
        );

        if (jacobi != FixedVector3.Zero) { return $"the Jacobi identity does not close at ({Lanes(raws: left)}) against ({Lanes(raws: right)})"; }

        var expansion = ((b * FixedVector3.Dot(
            left: a,
            right: c
        )) - (c * FixedVector3.Dot(
            left: a,
            right: b
        )));

        if (FixedVector3.Cross(
            left: a,
            right: FixedVector3.Cross(
                left: b,
                right: c
            )
        ) != expansion) { return $"the vector triple product does not expand at ({Lanes(raws: left)}) against ({Lanes(raws: right)})"; }

        // All three products are exactly bilinear in their first argument on the lattice.
        if (FixedVector3.Dot(
            left: (a + b),
            right: c
        ) != (FixedVector3.Dot(
            left: a,
            right: c
        ) + FixedVector3.Dot(
            left: b,
            right: c
        ))) { return $"the dot product is not bilinear at ({Lanes(raws: left)})"; }
        if (FixedVector3.Cross(
            left: (a + b),
            right: c
        ) != (FixedVector3.Cross(
            left: a,
            right: c
        ) + FixedVector3.Cross(
            left: b,
            right: c
        ))) { return $"the cross product is not bilinear at ({Lanes(raws: left)})"; }
        if (FixedVector2.Wedge(
            left: (planeA + planeB),
            right: planeC
        ) != (FixedVector2.Wedge(
            left: planeA,
            right: planeC
        ) + FixedVector2.Wedge(
            left: planeB,
            right: planeC
        ))) { return $"the wedge product is not bilinear at ({Lanes(raws: left)})"; }

        // Dot is exactly symmetric, Wedge and Cross exactly antisymmetric, and each product is exactly zero on a
        // repeated argument.
        if (FixedVector3.Dot(
            left: a,
            right: b
        ) != FixedVector3.Dot(
            left: b,
            right: a
        )) { return $"the dot product is not symmetric at ({Lanes(raws: left)})"; }
        if (FixedVector3.Cross(
            left: a,
            right: b
        ) != (-FixedVector3.Cross(
            left: b,
            right: a
        ))) { return $"the cross product is not antisymmetric at ({Lanes(raws: left)})"; }
        if (FixedVector2.Wedge(
            left: planeA,
            right: planeB
        ) != (-FixedVector2.Wedge(
            left: planeB,
            right: planeA
        ))) { return $"the wedge product is not antisymmetric at ({Lanes(raws: left)})"; }
        if (FixedVector3.Cross(
            left: a,
            right: a
        ) != FixedVector3.Zero) { return $"the cross product of a repeated argument is not zero at ({Lanes(raws: left)})"; }
        if (FixedVector2.Wedge(
            left: planeA,
            right: planeA
        ) != FixedQ4816.Zero) { return $"the wedge product of a repeated argument is not zero at ({Lanes(raws: left)})"; }

        // The scalar multiply is undone EXACTLY by the scalar divide on the lattice, which pins the division inside an
        // exact identity rather than only against a rounding reference.
        if (((a * scale) / scale) != a) { return $"the scalar round trip moved the space vector at ({Lanes(raws: left)}) by {scaleRaw}"; }
        if (((planeA * scale) / scale) != planeA) { return $"the scalar round trip moved the plane vector at ({Lanes(raws: left)}) by {scaleRaw}"; }

        return null;
    }
    /// <summary>Proves the family's identity, annihilator and involution structure at FULL raw range: the declared zero
    /// is one fact for both types, it is a two-sided additive identity and annihilates every product, negation is an
    /// involution and a two-sided additive inverse even at the two's-complement minimum, the two antisymmetries and the
    /// symmetry hold bit-for-bit everywhere rather than only on an exact lattice, the unit scalar is neutral for the
    /// multiply and the divide, and the absorbing element reads the same at the norm and direction surfaces.</summary>
    /// <param name="left">The first operand's three lanes.</param>
    /// <param name="right">The second operand's three lanes.</param>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? VectorIdentityAndNegation(long[] left, long[] right) {
        if (FixedVector2.AdditiveIdentity != FixedVector2.Zero) { return "the plane's additive identity is not its zero"; }
        if (FixedVector3.AdditiveIdentity != FixedVector3.Zero) { return "the space's additive identity is not its zero"; }
        if (default(FixedVector2) != FixedVector2.Zero) { return "the plane's default value is not zero"; }
        if (default(FixedVector3) != FixedVector3.Zero) { return "the space's default value is not zero"; }
        if ((FixedVector2.Zero.X.Value | FixedVector2.Zero.Y.Value) != 0L) { return "the plane's zero carries a non-zero component"; }
        if ((FixedVector3.Zero.X.Value | FixedVector3.Zero.Y.Value | FixedVector3.Zero.Z.Value) != 0L) { return "the space's zero carries a non-zero component"; }

        var a = Space(
            x: left[0],
            y: left[1],
            z: left[2]
        );
        var b = Space(
            x: right[0],
            y: right[1],
            z: right[2]
        );
        var planeA = Plane(
            x: left[0],
            y: left[1]
        );
        var planeB = Plane(
            x: right[0],
            y: right[1]
        );
        var operands = Lanes(raws: left);

        // Zero is a two-sided additive identity, the difference of a vector with itself is zero, and the wrapping sum
        // still closes on zero at the carrier minimum, where negation is a fixed point.
        if (
            ((a + FixedVector3.Zero) != a) ||
            ((FixedVector3.Zero + a) != a)
        ) { return $"the space's zero is not a two-sided additive identity at ({operands})"; }
        if (
            ((planeA + FixedVector2.Zero) != planeA) ||
            ((FixedVector2.Zero + planeA) != planeA)
        ) { return $"the plane's zero is not a two-sided additive identity at ({operands})"; }
        if ((a - a) != FixedVector3.Zero) { return $"the space vector is not its own difference identity at ({operands})"; }
        if ((planeA - planeA) != FixedVector2.Zero) { return $"the plane vector is not its own difference identity at ({operands})"; }
        if ((a + (-a)) != FixedVector3.Zero) { return $"the space negation is not an additive inverse at ({operands})"; }
        if ((planeA + (-planeA)) != FixedVector2.Zero) { return $"the plane negation is not an additive inverse at ({operands})"; }
        if ((-(-a)) != a) { return $"the space negation is not an involution at ({operands})"; }
        if ((-(-planeA)) != planeA) { return $"the plane negation is not an involution at ({operands})"; }

        // The two antisymmetries and the symmetry, at FULL range: the rounding is sign-symmetric and the carrier's wrap
        // is its own negation at the minimum, so these hold bit-for-bit everywhere.
        if (FixedVector2.Wedge(
            left: planeA,
            right: planeB
        ) != (-FixedVector2.Wedge(
            left: planeB,
            right: planeA
        ))) { return $"the wedge product is not antisymmetric at full range at ({operands})"; }
        if (FixedVector3.Cross(
            left: a,
            right: b
        ) != (-FixedVector3.Cross(
            left: b,
            right: a
        ))) { return $"the cross product is not antisymmetric at full range at ({operands})"; }
        if (FixedVector2.Dot(
            left: planeA,
            right: planeB
        ) != FixedVector2.Dot(
            left: planeB,
            right: planeA
        )) { return $"the plane dot product is not symmetric at full range at ({operands})"; }
        if (FixedVector3.Dot(
            left: a,
            right: b
        ) != FixedVector3.Dot(
            left: b,
            right: a
        )) { return $"the space dot product is not symmetric at full range at ({operands})"; }

        // Zero annihilates every product, and every product is zero on a repeated argument — with no rounding anywhere.
        if (FixedVector2.Dot(
            left: FixedVector2.Zero,
            right: planeA
        ) != FixedQ4816.Zero) { return $"the plane's zero does not annihilate the dot product at ({operands})"; }
        if (FixedVector2.Wedge(
            left: FixedVector2.Zero,
            right: planeA
        ) != FixedQ4816.Zero) { return $"the plane's zero does not annihilate the wedge product at ({operands})"; }
        if (FixedVector3.Dot(
            left: FixedVector3.Zero,
            right: a
        ) != FixedQ4816.Zero) { return $"the space's zero does not annihilate the dot product at ({operands})"; }
        if (FixedVector3.Cross(
            left: FixedVector3.Zero,
            right: a
        ) != FixedVector3.Zero) { return $"the space's zero does not annihilate the cross product at ({operands})"; }
        if (FixedVector2.Wedge(
            left: planeA,
            right: planeA
        ) != FixedQ4816.Zero) { return $"the wedge product of a repeated argument is not zero at ({operands})"; }
        if (FixedVector3.Cross(
            left: a,
            right: a
        ) != FixedVector3.Zero) { return $"the cross product of a repeated argument is not zero at ({operands})"; }

        // The unit scalar is neutral for both scalar operators and the zero scalar annihilates the multiply, exactly.
        if ((a * FixedQ4816.One) != a) { return $"the unit scalar is not neutral for the space multiply at ({operands})"; }
        if ((planeA * FixedQ4816.One) != planeA) { return $"the unit scalar is not neutral for the plane multiply at ({operands})"; }
        if ((a / FixedQ4816.One) != a) { return $"the unit scalar is not neutral for the space divide at ({operands})"; }
        if ((planeA / FixedQ4816.One) != planeA) { return $"the unit scalar is not neutral for the plane divide at ({operands})"; }
        if ((a * FixedQ4816.Zero) != FixedVector3.Zero) { return $"the zero scalar does not annihilate the space multiply at ({operands})"; }
        if ((planeA * FixedQ4816.Zero) != FixedVector2.Zero) { return $"the zero scalar does not annihilate the plane multiply at ({operands})"; }

        // The absorbing element is the same value at every surface.
        if (FixedVector3.Zero.LengthSquared != FixedQ4816.Zero) { return "the space's zero has a non-zero squared norm"; }
        if (FixedVector3.Zero.Length != FixedQ4816.Zero) { return "the space's zero has a non-zero norm"; }
        if (FixedVector2.Zero.LengthSquared != FixedQ4816.Zero) { return "the plane's zero has a non-zero squared norm"; }
        if (FixedVector2.Zero.Length != FixedQ4816.Zero) { return "the plane's zero has a non-zero norm"; }
        if (
            !FixedVector3.Zero.TryLengthSquared(squaredLength: out var zeroSquared) ||
            (zeroSquared != FixedQ4816.Zero)
        ) { return "the space's zero refused its squared norm"; }
        if (
            !FixedVector2.Zero.TryLength(length: out var zeroLength) ||
            (zeroLength != FixedQ4816.Zero)
        ) { return "the plane's zero refused its norm"; }
        if (FixedVector3.Zero.Normalize() != FixedVector3.Zero) { return "the space's zero does not normalize to zero"; }

        return null;
    }
    /// <summary>Proves the two vector types' construction contract on their own raw ladders: the positional constructor
    /// is lane-faithful over a sixteen-raw ladder placed at each lane in turn with distinct sentinels beside it, record
    /// equality is componentwise over the raws, the right-handed orientation ladder pins every basis cross and the
    /// planar wedge, dividing by the zero scalar REFUSES for every vector including zero, and dividing by the carrier's
    /// minimum is not a refusal at all.</summary>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? VectorConstructionAndRefusals() {
        foreach (var raw in VectorConstructorLadder) {
            var planeAtX = new FixedVector2(
                X: Raw(value: raw),
                Y: Raw(value: VectorSentinelY)
            );
            var planeAtY = new FixedVector2(
                X: Raw(value: VectorSentinelX),
                Y: Raw(value: raw)
            );

            if (
                (planeAtX.X.Value != raw) ||
                (planeAtX.Y.Value != VectorSentinelY)
            ) { return $"the plane constructor moved the ladder raw {raw} at lane 0"; }
            if (
                (planeAtY.X.Value != VectorSentinelX) ||
                (planeAtY.Y.Value != raw)
            ) { return $"the plane constructor moved the ladder raw {raw} at lane 1"; }

            var spaceAtX = new FixedVector3(
                X: Raw(value: raw),
                Y: Raw(value: VectorSentinelY),
                Z: Raw(value: VectorSentinelZ)
            );
            var spaceAtY = new FixedVector3(
                X: Raw(value: VectorSentinelX),
                Y: Raw(value: raw),
                Z: Raw(value: VectorSentinelZ)
            );
            var spaceAtZ = new FixedVector3(
                X: Raw(value: VectorSentinelX),
                Y: Raw(value: VectorSentinelY),
                Z: Raw(value: raw)
            );

            if (
                (spaceAtX.X.Value != raw) ||
                (spaceAtX.Y.Value != VectorSentinelY) ||
                (spaceAtX.Z.Value != VectorSentinelZ)
            ) { return $"the space constructor moved the ladder raw {raw} at lane 0"; }
            if (
                (spaceAtY.X.Value != VectorSentinelX) ||
                (spaceAtY.Y.Value != raw) ||
                (spaceAtY.Z.Value != VectorSentinelZ)
            ) { return $"the space constructor moved the ladder raw {raw} at lane 1"; }
            if (
                (spaceAtZ.X.Value != VectorSentinelX) ||
                (spaceAtZ.Y.Value != VectorSentinelY) ||
                (spaceAtZ.Z.Value != raw)
            ) { return $"the space constructor moved the ladder raw {raw} at lane 2"; }

            // Record equality is componentwise over the raws: equal components compare equal, and a single moved lane
            // compares unequal.
            if (planeAtX != new FixedVector2(
                X: Raw(value: raw),
                Y: Raw(value: VectorSentinelY)
            )) { return $"the plane's record equality failed at the ladder raw {raw}"; }
            if (spaceAtX != new FixedVector3(
                X: Raw(value: raw),
                Y: Raw(value: VectorSentinelY),
                Z: Raw(value: VectorSentinelZ)
            )) { return $"the space's record equality failed at the ladder raw {raw}"; }
            if (planeAtX == planeAtY) { return $"the plane's record equality ignored a moved lane at the ladder raw {raw}"; }
            if (spaceAtX == spaceAtZ) { return $"the space's record equality ignored a moved lane at the ladder raw {raw}"; }
        }

        // The right-handed orientation ladder: each ordered pair of basis directions crosses to the signed third.
        foreach (var (leftAxis, rightAxis, axis, sign) in VectorOrientationLadder) {
            var product = FixedVector3.Cross(
                left: BasisDirection(
                    axis: leftAxis,
                    sign: 1L
                ),
                right: BasisDirection(
                    axis: rightAxis,
                    sign: 1L
                )
            );

            if (product != BasisDirection(
                axis: axis,
                sign: sign
            )) { return $"the cross of basis {leftAxis} and basis {rightAxis} is not {sign} times basis {axis}"; }
        }

        for (var axis = 0; (axis < 3); ++axis) {
            if (FixedVector3.Cross(
                left: BasisDirection(
                    axis: axis,
                    sign: 1L
                ),
                right: BasisDirection(
                    axis: axis,
                    sign: 1L
                )
            ) != FixedVector3.Zero) { return $"the cross of basis {axis} with itself is not zero"; }
        }

        if (FixedVector2.Wedge(
            left: Plane(
                x: VectorOneRaw,
                y: 0L
            ),
            right: Plane(
                x: 0L,
                y: VectorOneRaw
            )
        ) != FixedQ4816.One) { return "the counterclockwise plane basis wedge is not one"; }
        if (FixedVector2.Wedge(
            left: Plane(
                x: 0L,
                y: VectorOneRaw
            ),
            right: Plane(
                x: VectorOneRaw,
                y: 0L
            )
        ) != (-FixedQ4816.One)) { return "the reversed plane basis wedge is not minus one"; }

        // Dividing by the zero scalar REFUSES, for every vector including zero — the throw is reached on the first
        // component, so no vector escapes it.
        var planeDividend = Plane(
            x: long.MinValue,
            y: long.MaxValue
        );
        var spaceDividend = Space(
            x: long.MinValue,
            y: 1L,
            z: long.MaxValue
        );

        if (!Throws<DivideByZeroException>(action: () => _ = (planeDividend / FixedQ4816.Zero))) { return "the plane divided by the zero scalar without refusing"; }
        if (!Throws<DivideByZeroException>(action: () => _ = (spaceDividend / FixedQ4816.Zero))) { return "the space divided by the zero scalar without refusing"; }
        if (!Throws<DivideByZeroException>(action: static () => _ = (FixedVector2.Zero / FixedQ4816.Zero))) { return "the plane's zero divided by the zero scalar without refusing"; }
        if (!Throws<DivideByZeroException>(action: static () => _ = (FixedVector3.Zero / FixedQ4816.Zero))) { return "the space's zero divided by the zero scalar without refusing"; }

        // Dividing by the carrier's minimum is NOT a refusal: the sign-magnitude quotient is defined there and wraps
        // like every other operator, which separates the documented exception from an overflow.
        var planeQuotient = (planeDividend / FixedQ4816.MinValue);
        var spaceQuotient = (spaceDividend / FixedQ4816.MinValue);

        foreach (var (actual, dividend) in ((ReadOnlySpan<(long Actual, long Dividend)>)[
            (planeQuotient.X.Value, long.MinValue),
            (planeQuotient.Y.Value, long.MaxValue),
            (spaceQuotient.X.Value, long.MinValue),
            (spaceQuotient.Y.Value, 1L),
            (spaceQuotient.Z.Value, long.MaxValue),
        ])) {
            var expected = Oracles.RoundDyadicRatio(
                numerator: new BigInteger(value: dividend),
                denominator: new BigInteger(value: long.MinValue),
                shift: FixedQ4816.FractionBitCount
            );

            if (actual != expected) { return $"dividing {dividend} by the carrier minimum gave {actual}, expected {expected}"; }
        }

        return null;
    }
    /// <summary>Proves the space's normalization against two independently derived references and its own structure:
    /// bit-for-bit against the staged reference, within one raw per component of the ideal single-rounding unit vector,
    /// the returned direction's own norm within four of <c>2¹⁶</c>, zero normalizing to zero and no non-zero direction
    /// normalizing to zero, the signs carried, and exact invariance under a power-of-two rescale.</summary>
    /// <param name="left">The first operand's three lanes.</param>
    /// <param name="right">The second operand's three lanes.</param>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? VectorNormalizeMatchesOracles(long[] left, long[] right) {
        foreach (var raws in ((ReadOnlySpan<long[]>)[left, right])) {
            var vector = Space(
                x: raws[0],
                y: raws[1],
                z: raws[2]
            );
            var unit = vector.Normalize();
            var actual = new[] { unit.X.Value, unit.Y.Value, unit.Z.Value, };
            var staged = new long[3];
            var ideal = new long[3];
            var operands = Lanes(raws: raws);

            Oracles.StagedUnitVector(
                raws: raws,
                result: staged
            );
            Oracles.IdealUnitVector(
                raws: raws,
                result: ideal
            );

            for (var lane = 0; (lane < 3); ++lane) {
                if (actual[lane] != staged[lane]) { return $"the staged pipeline disagrees at lane {lane} for ({operands}): {actual[lane]} against {staged[lane]}"; }
                if (BigInteger.Abs(value: (((BigInteger)actual[lane]) - ideal[lane])) > BigInteger.One) { return $"the ideal direction disagrees by more than one raw at lane {lane} for ({operands}): {actual[lane]} against {ideal[lane]}"; }

                // The sign is CARRIED, never inverted: a component whose ratio underflows the Q16 grid lands on zero,
                // and a zero input component stays exactly zero, but no component ever comes back with the opposite
                // sign.
                if (
                    (0L != actual[lane]) &&
                    (Math.Sign(value: actual[lane]) != Math.Sign(value: raws[lane]))
                ) { return $"the sign moved at lane {lane} for {raws[lane]}"; }
                if (
                    (0L == raws[lane]) &&
                    (0L != actual[lane])
                ) { return $"a zero component normalized to {actual[lane]} at lane {lane}"; }
            }

            if ((raws[0] | raws[1] | raws[2]) != 0L) {
                if (unit == FixedVector3.Zero) { return $"a non-zero direction normalized to zero at ({operands})"; }

                var norm = Oracles.NearestIntegerRoot(value: Oracles.SquaredNorm(raws: actual));

                if (BigInteger.Abs(value: (norm - (BigInteger.One << FixedQ4816.FractionBitCount))) > 4) { return $"the unit norm is {norm} at ({operands})"; }
            } else if (unit != FixedVector3.Zero) {
                return "the zero direction did not normalize to zero";
            }
        }

        if (FixedVector3.Zero.Normalize() != FixedVector3.Zero) { return "zero does not normalize to zero"; }

        // Power-of-two scale invariance, on a direction folded below the precondition's own leading bit — where the
        // common precondition is a pure LEFT shift, so the shift the rescale eats is exactly the one the right shift
        // divides back out.
        var foldedX = DirectionRaw(raw: left[0]);
        var foldedY = DirectionRaw(raw: left[1]);
        var foldedZ = DirectionRaw(raw: left[2]);
        var baseline = Space(
            x: foldedX,
            y: foldedY,
            z: foldedZ
        ).Normalize();

        foreach (var shift in ((ReadOnlySpan<int>)[1, 8, 17])) {
            var rescaled = Space(
                x: (foldedX << shift),
                y: (foldedY << shift),
                z: (foldedZ << shift)
            ).Normalize();

            if (rescaled != baseline) { return $"the unit direction moved under a 2^{shift} rescale of ({foldedX}, {foldedY}, {foldedZ})"; }
        }

        return null;
    }
    /// <summary>Proves <see cref="FixedVector3.OrthonormalBasis"/> builds a mutually perpendicular triad and does so
    /// deterministically, over both swept operands. Perpendicularity is checked against NORMALIZED copies of all
    /// three vectors — <c>OrthonormalBasis</c> accepts a non-unit normal by contract, which leaves
    /// <c>tangent2</c>'s magnitude tracking the normal's own, so an un-normalized dot product would conflate
    /// direction error with magnitude — using an exact BigInteger dot product that calls no <c>FixedVector3</c>
    /// arithmetic.</summary>
    /// <param name="left">The first swept normal candidate's three raws.</param>
    /// <param name="right">The second swept normal candidate's three raws.</param>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? VectorOrthonormalBasisIsOrthogonalAndDeterministic(long[] left, long[] right) {
        foreach (var raws in ((ReadOnlySpan<long[]>)[left, right])) {
            var normal = Space(
                x: raws[0],
                y: raws[1],
                z: raws[2]
            );
            var operands = Lanes(raws: raws);

            FixedVector3.OrthonormalBasis(
                normal: normal,
                tangent1: out var tangent1,
                out var tangent2
            );
            FixedVector3.OrthonormalBasis(
                normal: normal,
                tangent1: out var tangent1Again,
                out var tangent2Again
            );

            if (
                (tangent1 != tangent1Again) ||
                (tangent2 != tangent2Again)
            ) { return $"a repeated call with the same raws ({operands}) returned a different basis"; }

            var unitNormal = normal.Normalize();
            var t1DotN = ExactDotRaw(
                left: tangent1,
                right: unitNormal
            );

            // ENVELOPE: each dot is a sum of three Q16 products of two near-unit vectors, so a genuine directional
            // defect reads as a fraction of the full unit-dot scale 2^32; the tolerance below, 2^20, is four orders
            // of magnitude tighter than that and still comfortably covers a few raw units of Normalize's own
            // rounding error on EITHER operand times the other's near-unit magnitude — not a claim of exactness.
            if (BigInteger.Abs(value: t1DotN) > (1L << 20)) { return $"tangent1 is not perpendicular to normal at ({operands}): dot={t1DotN}"; }

            // ENVELOPE: Cross(tangent1, normal) carries normal's own raw magnitude through into its own raw result
            // (tangent1 is near-unit), so a normal near the FixedQ4816 carrier's own extreme leaves Cross's rounded
            // Q16 narrowing no headroom — Cross has no refusing face, so this is a pre-existing characteristic of
            // the shared kernel, not something this basis construction introduces. Skip the two tangent2 checks
            // above three-quarters of the carrier's magnitude, where the narrowing has no headroom. Skip them too
            // below one raw Q16 unit of the normal's OWN magnitude: tangent2's fused-rounding error is bounded in
            // ABSOLUTE raw units (Cross rounds once), but a small normal has few significant bits to begin with, so
            // that same fixed absolute error is a much larger ANGULAR error once tangent2 is normalized back to
            // unit length — tangent1 has no such dependency (its own construction and Normalize do not read
            // normal's magnitude), which is why only the tangent2 checks are gated here. The determinism check
            // above still ran either way and still holds.
            var normalMagnitude = FusedArithmetic.RawMagnitude(value: normal.X.Value) |
                                    FusedArithmetic.RawMagnitude(value: normal.Y.Value) |
                                    FusedArithmetic.RawMagnitude(value: normal.Z.Value);

            if (
                (normalMagnitude > (1UL << 61)) ||
                (normalMagnitude < (1UL << FixedQ4816.FractionBitCount))
            ) {
                continue;
            }

            var unitTangent2 = tangent2.Normalize();
            var t2DotN = ExactDotRaw(
                left: unitTangent2,
                right: unitNormal
            );
            var t1DotT2 = ExactDotRaw(
                left: tangent1,
                right: unitTangent2
            );

            if (BigInteger.Abs(value: t2DotN) > (1L << 20)) { return $"tangent2 is not perpendicular to normal at ({operands}): dot={t2DotN}"; }
            if (BigInteger.Abs(value: t1DotT2) > (1L << 20)) { return $"tangent1 is not perpendicular to tangent2 at ({operands}): dot={t1DotT2}"; }
        }

        return null;
    }
    /// <summary>Pins <see cref="FixedVector3.OrthonormalBasis"/>'s documented contract for a non-unit normal: the
    /// least-aligned-component branch selection does not read the normal's magnitude, so it is unaffected by scale,
    /// while <c>tangent2</c>'s own magnitude tracks the normal's, at one magnitude below unit and one above.</summary>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? VectorOrthonormalBasisTracksNonUnitMagnitude() {
        var direction = new FixedVector3(
            X: FixedQ4816.FromInteger(value: 1L),
            Y: FixedQ4816.FromInteger(value: 2L),
            Z: FixedQ4816.FromInteger(value: 3L)
        ).Normalize();

        foreach (var magnitude in ((ReadOnlySpan<double>)[0.4d, 2.5d])) {
            var normal = (direction * FixedQ4816.FromDouble(value: magnitude));

            FixedVector3.OrthonormalBasis(
                normal: normal,
                tangent1: out var tangent1,
                out var tangent2
            );

            // Root-based, matching Normalize's own established envelope (vector.normalize-vs-ideal-and-staged):
            // agreement within four raw Q16 units of the ROOT, not the squared value — Normalize's precondition
            // shift depends on the candidate's own magnitude, so the squared-norm error is not scale-invariant.
            var tangent1Root = Oracles.NearestIntegerRoot(value: ExactDotRaw(
                left: tangent1,
                right: tangent1
            ));

            if (BigInteger.Abs(value: (tangent1Root - (1L << FixedQ4816.FractionBitCount))) > 4L) { return $"tangent1 is not unit length at normal magnitude {magnitude}: root(|tangent1|^2) raw = {tangent1Root}"; }

            var tangent2Root = Oracles.NearestIntegerRoot(value: ExactDotRaw(
                left: tangent2,
                right: tangent2
            ));
            var expectedRoot = ((BigInteger)(magnitude * (1L << FixedQ4816.FractionBitCount)));

            if (BigInteger.Abs(value: (tangent2Root - expectedRoot)) > 8L) { return $"tangent2's magnitude does not track the normal's at magnitude {magnitude}: root(|tangent2|^2) raw = {tangent2Root}, expected near {expectedRoot}"; }
        }

        return null;
    }

    // The exact BigInteger dot product of two vectors' raw Q16 components, at Q32 — used to check perpendicularity
    // and length without routing through FixedVector3.Dot, the arithmetic under test elsewhere in this family.
    private static BigInteger ExactDotRaw(FixedVector3 left, FixedVector3 right) =>
        (((((BigInteger)left.X.Value) * right.X.Value) + (((BigInteger)left.Y.Value) * right.Y.Value)) + (((BigInteger)left.Z.Value) * right.Z.Value));

    /// <summary>Proves the presentation seam against hand-derived IEEE-754 <c>binary32</c> bit patterns, compared
    /// through <see cref="BitConverter.SingleToInt32Bits"/> so no floating-point arithmetic enters the law: every
    /// ladder raw is placed at X, then Y, then Z in turn with two distinct sentinels beside it, and reads back at
    /// exactly its own lane.</summary>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? VectorPresentationMatchesLadder() {
        foreach (var (raw, bits) in PresentationBinary32Ladder) {
            var atX = Space(
                x: raw,
                y: VectorPresentationSentinelA,
                z: VectorPresentationSentinelB
            ).ToVector3();
            var atY = Space(
                x: VectorPresentationSentinelA,
                y: raw,
                z: VectorPresentationSentinelB
            ).ToVector3();
            var atZ = Space(
                x: VectorPresentationSentinelA,
                y: VectorPresentationSentinelB,
                z: raw
            ).ToVector3();

            if (BitConverter.SingleToInt32Bits(value: atX.X) != bits) { return $"the raw {raw} converted to {BitConverter.SingleToInt32Bits(value: atX.X):X8} at lane 0, expected {bits:X8}"; }
            if (BitConverter.SingleToInt32Bits(value: atY.Y) != bits) { return $"the raw {raw} converted to {BitConverter.SingleToInt32Bits(value: atY.Y):X8} at lane 1, expected {bits:X8}"; }
            if (BitConverter.SingleToInt32Bits(value: atZ.Z) != bits) { return $"the raw {raw} converted to {BitConverter.SingleToInt32Bits(value: atZ.Z):X8} at lane 2, expected {bits:X8}"; }

            if (BitConverter.SingleToInt32Bits(value: atX.Y) != VectorPresentationSentinelABits) { return $"the first sentinel moved beside the ladder raw {raw} at lane 1"; }
            if (BitConverter.SingleToInt32Bits(value: atX.Z) != VectorPresentationSentinelBBits) { return $"the second sentinel moved beside the ladder raw {raw} at lane 2"; }
            if (BitConverter.SingleToInt32Bits(value: atY.X) != VectorPresentationSentinelABits) { return $"the first sentinel moved beside the ladder raw {raw} at lane 0"; }
            if (BitConverter.SingleToInt32Bits(value: atZ.Y) != VectorPresentationSentinelBBits) { return $"the second sentinel moved beside the ladder raw {raw} at lane 1"; }
        }

        return null;
    }
    /// <summary>Proves the INBOUND seam against hand-derived expectations: each ladder row is a binary32 bit pattern
    /// fed in through <see cref="BitConverter.Int32BitsToSingle"/>, so no floating-point arithmetic authors the
    /// operand, and the expectation is the exact integer the format decides. Every row is placed at X, then Y, then Z
    /// in turn with two distinct sentinels beside it, and lands at exactly its own lane.</summary>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? VectorAdoptionMatchesLadder() {
        foreach (var (bits, expected) in AdoptionBinary32Ladder) {
            var value = BitConverter.Int32BitsToSingle(value: bits);
            var atX = FixedVector3.FromVector3(value: new System.Numerics.Vector3(
                x: value,
                y: VectorAdoptionSentinelA,
                z: VectorAdoptionSentinelB
            ));
            var atY = FixedVector3.FromVector3(value: new System.Numerics.Vector3(
                x: VectorAdoptionSentinelA,
                y: value,
                z: VectorAdoptionSentinelB
            ));
            var atZ = FixedVector3.FromVector3(value: new System.Numerics.Vector3(
                x: VectorAdoptionSentinelA,
                y: VectorAdoptionSentinelB,
                z: value
            ));

            if (atX.X.Value != expected) { return $"the binary32 {bits:X8} adopted as {atX.X.Value} at lane 0, expected {expected}"; }
            if (atY.Y.Value != expected) { return $"the binary32 {bits:X8} adopted as {atY.Y.Value} at lane 1, expected {expected}"; }
            if (atZ.Z.Value != expected) { return $"the binary32 {bits:X8} adopted as {atZ.Z.Value} at lane 2, expected {expected}"; }

            if (atX.Y.Value != VectorAdoptionSentinelARaw) { return $"the first sentinel moved beside the ladder row {bits:X8} at lane 1"; }
            if (atX.Z.Value != VectorAdoptionSentinelBRaw) { return $"the second sentinel moved beside the ladder row {bits:X8} at lane 2"; }
            if (atY.X.Value != VectorAdoptionSentinelARaw) { return $"the first sentinel moved beside the ladder row {bits:X8} at lane 0"; }
            if (atZ.Y.Value != VectorAdoptionSentinelBRaw) { return $"the second sentinel moved beside the ladder row {bits:X8} at lane 1"; }
        }

        return null;
    }
    /// <summary>Proves both vector records print their declared components and nothing else: the hand-written
    /// <c>PrintMembers</c> bound is what keeps the saturating derived norms from executing during formatting, so the
    /// saturation sentinel <see cref="FixedQ4816.MaxValue"/> can never print in the position of a measured length.</summary>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? VectorRecordPrintsComponentsOnly() {
        // The expected strings are assembled BY HAND from the Q48.16 text contract — MaxValue's exact expansion
        // 140737488355327 + (2^16 - 1)/2^16 and MinValue's exact integer -2^47 — never captured from the subject.
        const string ExpectedSaturated3 = "FixedVector3 { X = 140737488355327.9999847412109375, Y = 140737488355327.9999847412109375, Z = 140737488355327.9999847412109375 }";
        const string ExpectedMixed2 = "FixedVector2 { X = 140737488355327.9999847412109375, Y = -140737488355328 }";
        const string ExpectedInterior3 = "FixedVector3 { X = 1.5, Y = -0.25, Z = 0 }";
        const string ExpectedInterior2 = "FixedVector2 { X = 1.5, Y = -0.25 }";

        // The all-extreme corners, where every Try norm refuses: the rendering must still be componentwise, with no
        // Length or LengthSquared token and no saturation sentinel beyond the components' own values.
        var saturated3 = Space(
            x: FixedQ4816.MaxValue.Value,
            y: FixedQ4816.MaxValue.Value,
            z: FixedQ4816.MaxValue.Value
        );

        if (
            saturated3.TryLength(length: out _) ||
            saturated3.TryLengthSquared(squaredLength: out _)
        ) { return "the all-MaxValue corner stopped refusing its norms, so the statement's corner moved"; }

        var actualSaturated3 = saturated3.ToString();

        if (ExpectedSaturated3 != actualSaturated3) { return $"the saturated FixedVector3 printed \"{actualSaturated3}\""; }

        var mixed2 = new FixedVector2(
            X: FixedQ4816.MaxValue,
            Y: FixedQ4816.MinValue
        );

        if (mixed2.TryLengthSquared(squaredLength: out _)) { return "the mixed-extreme plane stopped refusing its squared norm, so the statement's corner moved"; }

        var actualMixed2 = mixed2.ToString();

        if (ExpectedMixed2 != actualMixed2) { return $"the mixed-extreme FixedVector2 printed \"{actualMixed2}\""; }

        var actualInterior3 = Space(
            x: 98304L,
            y: -16384L,
            z: 0L
        ).ToString();

        if (ExpectedInterior3 != actualInterior3) { return $"the interior FixedVector3 printed \"{actualInterior3}\""; }

        var actualInterior2 = new FixedVector2(
            X: FixedQ4816.FromRawBits(value: 98304L),
            Y: FixedQ4816.FromRawBits(value: -16384L)
        ).ToString();

        if (ExpectedInterior2 != actualInterior2) { return $"the interior FixedVector2 printed \"{actualInterior2}\""; }

        return null;
    }

    private const long VectorOneRaw = (1L << FixedQ4816.FractionBitCount);
    // The lane sentinels: distinct, non-zero, and distinct from every ladder raw, so a swapped or duplicated component
    // cannot read back as its neighbour.
    private const long VectorSentinelX = 12345L;
    private const long VectorSentinelY = -6789L;
    private const long VectorSentinelZ = 424242L;

    // The construction ladder: both carrier extremes, both narrow-gate seams either side of 2³¹, the 2³⁰ seam, zero,
    // both single-raw quanta, both unit raws, and both integer-boundary raws.
    private static readonly long[] VectorConstructorLadder = [
        long.MinValue, long.MaxValue,
        ((1L << 31) - 1L), -((1L << 31) - 1L), (1L << 31), -(1L << 31), ((1L << 31) + 1L), -((1L << 31) + 1L),
        (1L << 30), 0L, 1L, -1L, 65536L, -65536L, (1L << 47), -(1L << 47),
    ];
    // The right-handed orientation of Euclidean three-space, as basis-index triples: the cross of basis i and basis j
    // is the signed basis k. Published mathematics, authored outside this tree's arithmetic and captured from no
    // subject output — which is what makes a transposed lane assignment or a flipped sign a FAILURE rather than a
    // consistent alternative convention.
    private static readonly (int Left, int Right, int Axis, long Sign)[] VectorOrientationLadder = [
        (0, 1, 2, 1L), (1, 2, 0, 1L), (2, 0, 1, 1L),
        (1, 0, 2, -1L), (2, 1, 0, -1L), (0, 2, 1, -1L),
    ];

    private static FixedVector3 BasisDirection(int axis, long sign) =>
        Space(
            x: ((0 == axis)
            ? (sign * VectorOneRaw)
            : 0L),
            y: ((1 == axis)
            ? (sign * VectorOneRaw)
            : 0L),
            z: ((2 == axis)
            ? (sign * VectorOneRaw)
            : 0L)
        );

    // The presentation ladder, each expectation derived from the FORMAT rather than from the kernel, and SHARED by every
    // binary32 presentation seam in the suite — FixedVector3.ToVector3 and FixedQuaternion.ToQuaternion perform the
    // identical conversion chain, so authoring the table twice would duplicate only the risk of a transcription slip.
    // That chain is the user-defined narrowing to double followed by the standard narrowing to float, so every row is
    // the raw's exact real value raw·2⁻¹⁶ rounded once to binary64 and once to binary32. The three 2²⁴ rows are the
    // interesting ones: 256 + ½ulp is a tie that goes DOWN to the even mantissa zero, 256 + 1ulp is exact, and
    // 256 + 1½ulp is a tie that goes UP to the even mantissa two. Both carrier extremes land on ±2⁴⁷, because at that
    // magnitude the binary64 step already absorbs the 2⁻¹⁶ residue.
    internal static readonly (long Raw, int Bits)[] PresentationBinary32Ladder = [
        (0L, 0x00000000),
        (65536L, 0x3F800000),                                   //  1
        (-65536L, unchecked((int)0xBF800000)),                  // −1
        (1L, 0x37800000),                                       //  2⁻¹⁶
        (-1L, unchecked((int)0xB7800000)),                      // −2⁻¹⁶
        (196608L, 0x40400000),                                  //  3
        (16777216L, 0x43800000),                                //  256, exact
        (16777217L, 0x43800000),                                //  256 + ½ulp → tie to even, down
        (16777218L, 0x43800001),                                //  256 + 1ulp, exact
        (16777219L, 0x43800002),                                //  256 + 1½ulp → tie to even, up
        (140737488355328L, 0x4F000000),                         //  2³¹
        (-140737488355328L, unchecked((int)0xCF000000)),         // −2³¹
        (long.MaxValue, 0x57000000),                            //  2⁴⁷
        (long.MinValue, unchecked((int)0xD7000000)),            // −2⁴⁷
    ];

    private const long VectorPresentationSentinelA = 65536L;
    private const int VectorPresentationSentinelABits = 0x3F800000;
    private const long VectorPresentationSentinelB = -196608L;
    private const int VectorPresentationSentinelBBits = unchecked((int)0xC0400000);

    // The INBOUND ladder, the mirror of PresentationBinary32Ladder and derived the same way — from the format, never
    // from the kernel. A binary32 widens to binary64 exactly and the 2¹⁶ scale is a pure exponent shift, so the
    // product is EXACT and the whole conversion is one round-half-to-even of that exact dyadic, then saturation.
    // Every expectation below is that integer, resolved by hand.
    //
    // The rows that decide the law: three exact HALVES that must resolve ties to even in three different directions
    // (½→0 down, 1½→2 up, 2½→2 down) and their negatives; the smallest representable quantum; the classic 0.1f,
    // whose binary32 value is 13421773·2⁻²⁷ and whose scaled value 13421773/2048 = 6553.60009765625 rounds up; both
    // infinities and both float extremes, which all scale far past the carrier and must saturate rather than wrap;
    // a NaN, which folds to zero; and negative zero, which must land on 0 rather than carrying a sign into an
    // integer that has no signed zero.
    private static readonly (int Bits, long Expected)[] AdoptionBinary32Ladder = [
        (0x00000000, 0L),                                       //  0
        (unchecked((int)0x80000000), 0L),                       // −0 → the carrier has no signed zero
        (0x3F800000, 65536L),                                   //  1
        (unchecked((int)0xBF800000), -65536L),                  // −1
        (0x3FC00000, 98304L),                                   //  1.5
        (0x37800000, 1L),                                       //  2⁻¹⁶, the quantum, exact
        (unchecked((int)0xB7800000), -1L),                      // −2⁻¹⁶
        (0x37000000, 0L),                                       //  2⁻¹⁷ → scaled ½, tie to even, DOWN
        (unchecked((int)0xB7000000), 0L),                       // −2⁻¹⁷ → scaled −½, tie to even, DOWN to zero
        (0x37C00000, 2L),                                       //  1.5·2⁻¹⁶ → scaled 1½, tie to even, UP
        (unchecked((int)0xB7C00000), -2L),                      // −1.5·2⁻¹⁶ → scaled −1½, tie to even, UP in magnitude
        (0x38200000, 2L),                                       //  2.5·2⁻¹⁶ → scaled 2½, tie to even, DOWN
        (unchecked((int)0xB8200000), -2L),                      // −2.5·2⁻¹⁶ → scaled −2½, tie to even, DOWN in magnitude
        (0x3DCCCCCD, 6554L),                                    //  0.1f = 13421773·2⁻²⁷ → 6553.60009765625, up
        (0x43800000, 16777216L),                                //  256
        (0x7F7FFFFF, long.MaxValue),                            //  float.MaxValue → saturates
        (unchecked((int)0xFF7FFFFF), long.MinValue),            // −float.MaxValue → saturates
        (0x7F800000, long.MaxValue),                            //  +infinity → saturates
        (unchecked((int)0xFF800000), long.MinValue),            // −infinity → saturates
        (unchecked((int)0xFFC00000), 0L),                       //  NaN → zero
    ];

    private const float VectorAdoptionSentinelA = 1f;
    private const long VectorAdoptionSentinelARaw = 65536L;
    private const float VectorAdoptionSentinelB = -3f;
    private const long VectorAdoptionSentinelBRaw = -196608L;

    /// <summary>Proves the QUATERNION inbound seam over the same binary32 ladder the vector seam is held to, across
    /// all FOUR lanes, and proves the two facts a three-lane ladder cannot state: that the seam is componentwise, and
    /// that it does NOT renormalize.</summary>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    /// <remarks>The non-unit rows are the point. <see cref="FixedQuaternion.FromQuaternion"/> is a REPRESENTATION
    /// change, not a repair: Q16 quantization moves a unit rotation off the sphere by itself, so a seam that quietly
    /// renormalized would hide its own error and would also silently discard a deliberately non-unit operand.
    /// Normalization is the caller's, which is only true if nothing here does it.</remarks>
    public static string? QuaternionAdoptionMatchesLadder() {
        foreach (var (bits, expected) in AdoptionBinary32Ladder) {
            var value = BitConverter.Int32BitsToSingle(value: bits);
            var atX = FixedQuaternion.FromQuaternion(value: new System.Numerics.Quaternion(
                w: VectorAdoptionSentinelA,
                x: value,
                y: VectorAdoptionSentinelA,
                z: VectorAdoptionSentinelB
            ));
            var atY = FixedQuaternion.FromQuaternion(value: new System.Numerics.Quaternion(
                w: VectorAdoptionSentinelA,
                x: VectorAdoptionSentinelA,
                y: value,
                z: VectorAdoptionSentinelB
            ));
            var atZ = FixedQuaternion.FromQuaternion(value: new System.Numerics.Quaternion(
                w: VectorAdoptionSentinelA,
                x: VectorAdoptionSentinelA,
                y: VectorAdoptionSentinelB,
                z: value
            ));
            var atW = FixedQuaternion.FromQuaternion(value: new System.Numerics.Quaternion(
                w: value,
                x: VectorAdoptionSentinelA,
                y: VectorAdoptionSentinelB,
                z: VectorAdoptionSentinelA
            ));

            if (atX.X.Value != expected) { return $"the binary32 {bits:X8} adopted as {atX.X.Value} at lane 0, expected {expected}"; }
            if (atY.Y.Value != expected) { return $"the binary32 {bits:X8} adopted as {atY.Y.Value} at lane 1, expected {expected}"; }
            if (atZ.Z.Value != expected) { return $"the binary32 {bits:X8} adopted as {atZ.Z.Value} at lane 2, expected {expected}"; }
            if (atW.W.Value != expected) { return $"the binary32 {bits:X8} adopted as {atW.W.Value} at lane 3, expected {expected}"; }

            if (atX.Y.Value != VectorAdoptionSentinelARaw) { return $"the first sentinel moved beside the ladder row {bits:X8} at lane 1"; }
            if (atX.Z.Value != VectorAdoptionSentinelBRaw) { return $"the second sentinel moved beside the ladder row {bits:X8} at lane 2"; }
            if (atX.W.Value != VectorAdoptionSentinelARaw) { return $"the first sentinel moved beside the ladder row {bits:X8} at lane 3"; }
            if (atY.X.Value != VectorAdoptionSentinelARaw) { return $"the first sentinel moved beside the ladder row {bits:X8} at lane 0"; }
            if (atZ.Y.Value != VectorAdoptionSentinelBRaw) { return $"the second sentinel moved beside the ladder row {bits:X8} at lane 1"; }
            if (atW.Z.Value != VectorAdoptionSentinelARaw) { return $"the first sentinel moved beside the ladder row {bits:X8} at lane 2"; }
        }

        // NOT renormalized. (2, 0, 0, 0) has norm 2, and every lane must survive untouched — a seam that normalized
        // would land (1, 0, 0, 0) and the raw would read 65536 instead of 131072.
        var doubled = FixedQuaternion.FromQuaternion(value: new System.Numerics.Quaternion(
            w: 0f,
            x: 2f,
            y: 0f,
            z: 0f
        ));

        if (
            (doubled.X.Value != 131072L) ||
            (doubled.Y.Value != 0L) ||
            (doubled.Z.Value != 0L) ||
            (doubled.W.Value != 0L)
        ) {
            return $"a non-unit quaternion was repaired on the way in: ({doubled.X.Value}, {doubled.Y.Value}, {doubled.Z.Value}, {doubled.W.Value}), expected (131072, 0, 0, 0)";
        }

        // The same statement where a normalizing seam would be hardest to catch: an operand already NEAR the sphere.
        // Half of each lane is norm 1 exactly, so scaling it by 3 gives norm 3 with every lane still representable.
        var scaled = FixedQuaternion.FromQuaternion(value: new System.Numerics.Quaternion(
            w: 1.5f,
            x: 1.5f,
            y: 1.5f,
            z: 1.5f
        ));

        if (
            (scaled.X.Value != 98304L) ||
            (scaled.Y.Value != 98304L) ||
            (scaled.Z.Value != 98304L) ||
            (scaled.W.Value != 98304L)
        ) {
            return $"a uniformly non-unit quaternion was rescaled on the way in: ({scaled.X.Value}, {scaled.Y.Value}, {scaled.Z.Value}, {scaled.W.Value}), expected four lanes of 98304";
        }

        // WHY normalization is the caller's job, demonstrated rather than asserted. (1,2,2,4)/5 is EXACTLY unit in
        // the reals, but 0.2 and 0.4 are not binary fractions, so Q16 cannot hold them and the adopted quaternion
        // lands OFF the sphere. Measured as raws: unit means the sum of squares is exactly 2^32.
        const long UnitSumOfSquares = (1L << 32);
        var offSphere = FixedQuaternion.FromQuaternion(value: new System.Numerics.Quaternion(
            w: 0.8f,
            x: 0.2f,
            y: 0.4f,
            z: 0.4f
        ));
        var offSum = (((offSphere.X.Value * offSphere.X.Value) + (offSphere.Y.Value * offSphere.Y.Value)) + ((offSphere.Z.Value * offSphere.Z.Value) + (offSphere.W.Value * offSphere.W.Value)));

        if (offSum == UnitSumOfSquares) {
            return "an exactly-unit rotation whose components are NOT binary fractions survived adoption exactly unit — either the ladder rows changed or something renormalized";
        }

        // The DISCRIMINATING half: quantization does not move EVERY rotation off the sphere, so the row above is a
        // real property of that operand and not a tautology about the carrier. Halves are exact in Q16, so this one
        // lands on the sphere precisely — 4 x 32768^2 = 2^32.
        var onSphere = FixedQuaternion.FromQuaternion(value: new System.Numerics.Quaternion(
            w: 0.5f,
            x: 0.5f,
            y: 0.5f,
            z: 0.5f
        ));
        var onSum = (((onSphere.X.Value * onSphere.X.Value) + (onSphere.Y.Value * onSphere.Y.Value)) + ((onSphere.Z.Value * onSphere.Z.Value) + (onSphere.W.Value * onSphere.W.Value)));

        if (onSum != UnitSumOfSquares) {
            return $"an exactly representable unit rotation did not survive adoption unit: sum of squares {onSum}, expected {UnitSumOfSquares}";
        }

        return null;
    }

}
