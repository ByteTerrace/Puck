using System.Numerics;

namespace Puck.Maths.Tests;

internal static partial class Subjects {
    // ---- the binary field (GF(2^k) over packed carriers) ----

    // The five canonical minimum-weight fields with their PUBLISHED moduli beside them, so the degree and the tail a
    // law hands its oracle come from this table rather than off the field under test. Read off the field instead and
    // the same wrong tail would flow into both sides, which is exactly the defect a catalog table exists to catch.
    private static readonly (int Degree, ulong Tail)[] BinaryFieldCatalogModuli = [
        (8, 0x1BUL), (16, 0x2BUL), (32, 0x8DUL), (64, 0x1BUL), (128, 0x87UL),
    ];
    // The multiplicative group orders the Fermat statements run at. WRITTEN OUT rather than computed as
    // (1UL << k) − 1UL: at k = 64 that shift count is masked back to zero in C# and the expression silently yields
    // zero. Degree 128 has no row because 2^128 − 1 does not fit the ulong exponent Exponentiate takes.
    private static readonly (int Degree, ulong Order)[] BinaryFieldGroupOrders = [
        (8, 255UL), (16, 65_535UL), (32, 4_294_967_295UL), (64, ulong.MaxValue),
    ];
    // The region ladder. Every entry is a seam of the region-scaling ladder or one element on either side of one: the
    // byte carrier's vector rungs step 16, 32 and 64 BYTES and the sixteen-bit carrier's step 8, 16 and 32 ELEMENTS
    // (the same byte widths), while a nibble-split rung is preferred to the scalar loop only from four whole vectors
    // up, which puts thresholds at 64, 128 and 256 bytes. Zero is here because an empty region takes a null reference
    // through MemoryMarshal.GetReference and must still be legal; 259 is the top short length, kept so the two gates
    // finish on the same case.
    private static readonly int[] BinaryFieldRegionLengths =
        [0, 1, 7, 8, 9, 15, 16, 17, 31, 32, 33, 63, 64, 65, 127, 128, 129, 255, 256, 259];

    private const int BinaryFieldRegionCeiling = 259;
    // The prefix the three wide carriers additionally take the BigInteger oracle over, so carriage is not the only
    // evidence there. It covers every ladder length through 65.
    private const int BinaryFieldRegionSpot = 65;

    // The degrees the FromModulus round trip runs at: both ends of the admissible band, the catalog degrees the
    // BinaryPolynomial carrier can still express, and the seams on either side of the packed carrier's own top.
    private static readonly int[] BinaryFieldModulusDegrees = [1, 2, 3, 8, 16, 31, 32, 47, 62, 63];

    // The swept degree at a carrier of the given width: a degree in [1, width] drawn from the operand stream, so the
    // MODULUS sweeps beside the elements. That is what makes the degree-below-the-carrier-width path — the low mask,
    // the split at the degree and the iterated tail fold — load bearing at every carrier; the catalog fields never
    // reach it, because every catalog degree equals its carrier's width.
    private static int BinaryFieldDegree(long raw, int width) =>
        (1 + ((int)(unchecked((ulong)raw) % ((ulong)width))));
    // The packed element a sampled pair of raws becomes at a given degree, applied IDENTICALLY in the subject and in
    // the oracle. Two raws because a 128-bit carrier needs more than the sixty-four one raw carries; a plain
    // truncation rather than a modular fold, so the map is onto and every legal element of every swept field is
    // reachable.
    private static BigInteger BinaryFieldElement(long high, long low, int degree) =>
        (((((BigInteger)unchecked((ulong)high)) << 64) | unchecked((ulong)low))) & ((BigInteger.One << degree) - BigInteger.One);
    // The swept tail: the drawn element below t^degree with its constant term forced on, which is exactly Create's
    // own admission rule — a non-zero constant term and no coefficient at or above the degree — so every drawn
    // modulus is legal and no sample is skipped asymmetrically.
    private static BigInteger BinaryFieldTail(long high, long low, int degree) =>
        BinaryFieldElement(
            degree: degree,
            high: high,
            low: low
        ) | BigInteger.One;
    // The fixed region content: a deterministic, operand-free affine walk spread across the element space by the two
    // odd mixing constants the suite already uses, salted so a source region and a destination region never coincide.
    private static T BinaryFieldRegionWalk<T>(int index, ulong salt) where T : IBinaryInteger<T>, IUnsignedNumber<T> {
        var seed = unchecked((((ulong)index) + salt));
        var low = unchecked((seed * 0x9E3779B97F4A7C15UL));
        var high = unchecked(((seed ^ 0xD1B54A32D192ED03UL) * 0xBF58476D1CE4E5B9UL));

        return T.CreateTruncating(value: (((UInt128)high) << 64) | low);
    }

    /// <summary>Proves the field product and the reduction at every carrier: the five published catalog moduli read
    /// back off the shipped fields, the product against the shared-nothing oracle at the PUBLISHED pair, and — at a
    /// degree and an odd tail drawn from the operand stream at each of the five carriers — the product, the reduction
    /// of an arbitrary packed value, the two <c>IsReduced</c> readings and the defining congruence.</summary>
    /// <param name="left">The first operand vector, three raws.</param>
    /// <param name="right">The second operand vector, three raws.</param>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? BinaryFieldProductAndReductionExact(long[] left, long[] right) {
        foreach (var (degree, tail) in BinaryFieldCatalogModuli) {
            var a = BinaryFieldElement(
                high: left[0],
                low: left[1],
                degree: degree
            );
            var b = BinaryFieldElement(
                high: right[0],
                low: right[1],
                degree: degree
            );
            var published = ((BigInteger)tail);
            var failure = degree switch {
                8 => BinaryFieldCatalogRow(
                degree: degree,
                field: BinaryFields.Degree8,
                left: a,
                right: b,
                tail: published
            ),
                16 => BinaryFieldCatalogRow(
                degree: degree,
                field: BinaryFields.Degree16,
                left: a,
                right: b,
                tail: published
            ),
                32 => BinaryFieldCatalogRow(
                degree: degree,
                field: BinaryFields.Degree32,
                left: a,
                right: b,
                tail: published
            ),
                64 => BinaryFieldCatalogRow(
                degree: degree,
                field: BinaryFields.Degree64,
                left: a,
                right: b,
                tail: published
            ),
                _ => BinaryFieldCatalogRow(
                degree: degree,
                field: BinaryFields.Degree128,
                left: a,
                right: b,
                tail: published
            ),
            };

            if (failure is not null) { return failure; }
        }

        return (BinaryFieldDrawnRow(
            field: BinaryField<byte>.Create(
                degree: BinaryFieldDegree(
                    raw: left[2],
                    width: 8
                ),
                reductionTail: byte.CreateTruncating(value: BinaryFieldTail(
                    high: right[2],
                    low: left[2],
                    degree: BinaryFieldDegree(
                        raw: left[2],
                        width: 8
                    )
                ))
            ),
            width: 8,
            left: left,
            right: right
        ) ??
                (BinaryFieldDrawnRow(
            field: BinaryField<ushort>.Create(
                degree: BinaryFieldDegree(
                    raw: left[2],
                    width: 16
                ),
                reductionTail: ushort.CreateTruncating(value: BinaryFieldTail(
                    high: right[2],
                    low: left[2],
                    degree: BinaryFieldDegree(
                        raw: left[2],
                        width: 16
                    )
                ))
            ),
            width: 16,
            left: left,
            right: right
        ) ??
                (BinaryFieldDrawnRow(
            field: BinaryField<uint>.Create(
                degree: BinaryFieldDegree(
                    raw: left[2],
                    width: 32
                ),
                reductionTail: uint.CreateTruncating(value: BinaryFieldTail(
                    high: right[2],
                    low: left[2],
                    degree: BinaryFieldDegree(
                        raw: left[2],
                        width: 32
                    )
                ))
            ),
            width: 32,
            left: left,
            right: right
        ) ??
                (BinaryFieldDrawnRow(
            field: BinaryField<ulong>.Create(
                degree: BinaryFieldDegree(
                    raw: left[2],
                    width: 64
                ),
                reductionTail: ulong.CreateTruncating(value: BinaryFieldTail(
                    high: right[2],
                    low: left[2],
                    degree: BinaryFieldDegree(
                        raw: left[2],
                        width: 64
                    )
                ))
            ),
            width: 64,
            left: left,
            right: right
        ) ??
                BinaryFieldDrawnRow(
            field: BinaryField<UInt128>.Create(
                degree: BinaryFieldDegree(
                    raw: left[2],
                    width: 128
                ),
                reductionTail: UInt128.CreateTruncating(value: BinaryFieldTail(
                    high: right[2],
                    low: left[2],
                    degree: BinaryFieldDegree(
                        raw: left[2],
                        width: 128
                    )
                ))
            ),
            width: 128,
            left: left,
            right: right
        )))));
    }

    // One catalog field: its degree and tail against the PUBLISHED pair, its product against the oracle at that same
    // published pair, and the two facts every carrier-width degree forces — every element is already reduced, and
    // Reduce is therefore the identity.
    private static string? BinaryFieldCatalogRow<T>(BinaryField<T> field, int degree, BigInteger tail, BigInteger left, BigInteger right)
        where T : IBinaryInteger<T>, IUnsignedNumber<T> {
        var declaredDegree = field.Degree;
        var declaredTail = BigInteger.CreateChecked(value: field.ReductionTail);

        if (declaredDegree != degree) { return $"the catalog field of degree {degree} reports degree {declaredDegree}"; }
        if (declaredTail != tail) { return $"the catalog field of degree {degree} reports tail {declaredTail}, the published tail is {tail}"; }

        var a = T.CreateTruncating(value: left);
        var b = T.CreateTruncating(value: right);
        var product = BigInteger.CreateChecked(value: field.Multiply(
            left: a,
            right: b
        ));
        var expected = Oracles.BinaryFieldProduct(
            degree: degree,
            left: left,
            reductionTail: tail,
            right: right
        );

        if (product != expected) { return $"the degree-{degree} product of {left} and {right} is {product}, the oracle gives {expected}"; }
        if (
            !field.IsReduced(value: a) ||
            !field.IsReduced(value: b) ||
            !field.IsReduced(value: T.CreateTruncating(value: product))
        ) { return $"a degree-{degree} catalog element is not reduced"; }
        if (field.Reduce(value: a) != a) { return $"Reduce moved the already-reduced element {left} at degree {degree}"; }

        return null;
    }
    // One carrier at a modulus drawn from the operand stream: the product against the oracle at that drawn pair, the
    // reduction of an UNREDUCED carrier value against the long-division oracle, IsReduced against the raw's own
    // high-bit test, and the defining congruence t^degree is the tail.
    private static string? BinaryFieldDrawnRow<T>(BinaryField<T> field, int width, long[] left, long[] right)
        where T : IBinaryInteger<T>, IUnsignedNumber<T> {
        var degree = field.Degree;
        var tail = BigInteger.CreateChecked(value: field.ReductionTail);
        var a = BinaryFieldElement(
            high: left[0],
            low: left[1],
            degree: degree
        );
        var b = BinaryFieldElement(
            high: right[0],
            low: right[1],
            degree: degree
        );
        var product = BigInteger.CreateChecked(value: field.Multiply(
            left: T.CreateTruncating(value: a),
            right: T.CreateTruncating(value: b)
        ));
        var expected = Oracles.BinaryFieldProduct(
            degree: degree,
            left: a,
            reductionTail: tail,
            right: b
        );

        if (product != expected) { return $"the width-{width} degree-{degree} tail-{tail} product of {a} and {b} is {product}, the oracle gives {expected}"; }

        // The whole carrier value rather than the folded element: this is the one place an arbitrary packed value is
        // reduced, and the only operand class ReduceWide's iterated fold actually runs more than one pass on.
        var wide = BinaryFieldElement(
            high: left[1],
            low: right[0],
            degree: width
        );
        var packed = T.CreateTruncating(value: wide);
        var reduced = BigInteger.CreateChecked(value: field.Reduce(value: packed));
        var reference = Oracles.BinaryFieldReduce(
            degree: degree,
            reductionTail: tail,
            value: wide
        );

        if (reduced != reference) { return $"the width-{width} degree-{degree} tail-{tail} reduction of {wide} is {reduced}, the long-division oracle gives {reference}"; }
        if (!field.IsReduced(value: T.CreateTruncating(value: reduced))) { return $"a reduced value is not reduced at width {width} degree {degree}"; }
        if (field.IsReduced(value: packed) != (wide >> degree).IsZero) { return $"IsReduced disagrees with the raw's own high bits at width {width} degree {degree} on {wide}"; }

        if (degree < width) {
            var congruence = BigInteger.CreateChecked(value: field.Reduce(value: T.CreateTruncating(value: (BigInteger.One << degree))));

            if (congruence != tail) { return $"t^{degree} reduces to {congruence} at width {width}, the tail is {tail}"; }
        }

        return null;
    }

    /// <summary>Proves the field axioms bit-for-bit at all five carriers under a modulus drawn from the operand
    /// stream: commutativity, associativity, distributivity, the additive group in characteristic two, both
    /// identities, the annihilator, squaring as a product, and Frobenius additivity.</summary>
    /// <param name="left">The first operand vector, three raws.</param>
    /// <param name="right">The second operand vector, three raws.</param>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? BinaryFieldAxiomsExact(long[] left, long[] right) =>
        (BinaryFieldAxiomRow(
            field: BinaryField<byte>.Create(
                degree: BinaryFieldDegree(
                    raw: left[2],
                    width: 8
                ),
                reductionTail: byte.CreateTruncating(value: BinaryFieldTail(
                    high: right[2],
                    low: left[2],
                    degree: BinaryFieldDegree(
                        raw: left[2],
                        width: 8
                    )
                ))
            ),
            width: 8,
            left: left,
            right: right
        ) ??
         (BinaryFieldAxiomRow(
            field: BinaryField<ushort>.Create(
                degree: BinaryFieldDegree(
                    raw: left[2],
                    width: 16
                ),
                reductionTail: ushort.CreateTruncating(value: BinaryFieldTail(
                    high: right[2],
                    low: left[2],
                    degree: BinaryFieldDegree(
                        raw: left[2],
                        width: 16
                    )
                ))
            ),
            width: 16,
            left: left,
            right: right
        ) ??
         (BinaryFieldAxiomRow(
            field: BinaryField<uint>.Create(
                degree: BinaryFieldDegree(
                    raw: left[2],
                    width: 32
                ),
                reductionTail: uint.CreateTruncating(value: BinaryFieldTail(
                    high: right[2],
                    low: left[2],
                    degree: BinaryFieldDegree(
                        raw: left[2],
                        width: 32
                    )
                ))
            ),
            width: 32,
            left: left,
            right: right
        ) ??
         (BinaryFieldAxiomRow(
            field: BinaryField<ulong>.Create(
                degree: BinaryFieldDegree(
                    raw: left[2],
                    width: 64
                ),
                reductionTail: ulong.CreateTruncating(value: BinaryFieldTail(
                    high: right[2],
                    low: left[2],
                    degree: BinaryFieldDegree(
                        raw: left[2],
                        width: 64
                    )
                ))
            ),
            width: 64,
            left: left,
            right: right
        ) ??
         BinaryFieldAxiomRow(
            field: BinaryField<UInt128>.Create(
                degree: BinaryFieldDegree(
                    raw: left[2],
                    width: 128
                ),
                reductionTail: UInt128.CreateTruncating(value: BinaryFieldTail(
                    high: right[2],
                    low: left[2],
                    degree: BinaryFieldDegree(
                        raw: left[2],
                        width: 128
                    )
                ))
            ),
            width: 128,
            left: left,
            right: right
        )))));

    private static string? BinaryFieldAxiomRow<T>(BinaryField<T> field, int width, long[] left, long[] right)
        where T : IBinaryInteger<T>, IUnsignedNumber<T> {
        var degree = field.Degree;
        var one = field.One;
        var zero = field.Zero;
        var a = T.CreateTruncating(value: BinaryFieldElement(
            high: left[0],
            low: left[1],
            degree: degree
        ));
        var b = T.CreateTruncating(value: BinaryFieldElement(
            high: right[0],
            low: right[1],
            degree: degree
        ));
        var c = T.CreateTruncating(value: BinaryFieldElement(
            high: left[2],
            low: right[2],
            degree: degree
        ));
        var where = $"at width {width} degree {degree} tail {field.ReductionTail} on {a}, {b}, {c}";

        if (one == zero) { return $"One and Zero coincide {where}"; }
        if (
            !field.IsReduced(value: one) ||
            !field.IsReduced(value: zero)
        ) { return $"an identity is not reduced {where}"; }
        if (field.Multiply(
            left: a,
            right: b
        ) != field.Multiply(
            left: b,
            right: a
        )) { return $"the product is not commutative {where}"; }
        if (field.Multiply(
            left: field.Multiply(
                left: a,
                right: b
            ),
            right: c
        ) != field.Multiply(
            left: a,
            right: field.Multiply(
                left: b,
                right: c
            )
        )) { return $"the product is not associative {where}"; }
        if (field.Multiply(
            left: a,
            right: field.Add(
                left: b,
                right: c
            )
        ) != field.Add(
            left: field.Multiply(
                left: a,
                right: b
            ),
            right: field.Multiply(
                left: a,
                right: c
            )
        )) { return $"the product does not distribute over addition {where}"; }
        if (field.Add(
            left: a,
            right: zero
        ) != a) { return $"Zero is not the additive identity {where}"; }
        if (field.Add(
            left: a,
            right: a
        ) != zero) { return $"addition is not its own inverse {where}"; }
        if (field.Add(
            left: field.Add(
                left: a,
                right: b
            ),
            right: b
        ) != a) { return $"adding a value twice does not return the original {where}"; }
        if (
            (field.Multiply(
            left: a,
            right: one
        ) != a) ||
            (field.Multiply(
            left: one,
            right: a
        ) != a)
        ) { return $"One is not a two-sided multiplicative identity {where}"; }
        if (field.Multiply(
            left: a,
            right: zero
        ) != zero) { return $"Zero does not annihilate {where}"; }
        if (field.Square(value: a) != field.Multiply(
            left: a,
            right: a
        )) { return $"Square is not the product with itself {where}"; }
        if (field.Square(value: field.Add(
            left: a,
            right: b
        )) != field.Add(
            left: field.Square(value: a),
            right: field.Square(value: b)
        )) { return $"squaring is not additive {where}"; }

        return null;
    }

    /// <summary>Proves the multiplicative group at the five catalog fields: the inverse and the quotient and the root
    /// as BigInteger CERTIFICATES, the inverse a second time by extended Euclid, the small powers against a sequential
    /// fold, the group-order identities, and the zero refusals.</summary>
    /// <param name="left">The first operand vector, three raws.</param>
    /// <param name="right">The second operand vector, three raws.</param>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? BinaryFieldGroupExact(long[] left, long[] right) =>
        BinaryFieldGroupExact(
            everyDegree: false,
            left: left,
            right: right
        );
    /// <summary>The same statement, with the two second derivations — extended Euclid and the sequential fold —
    /// either confined to the narrow catalog degrees or extended to all five.</summary>
    /// <param name="left">The first operand vector, three raws.</param>
    /// <param name="right">The second operand vector, three raws.</param>
    /// <param name="everyDegree"><see langword="true"/> to run extended Euclid and the sequential fold at every
    /// catalog degree rather than only where they are cheap.</param>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? BinaryFieldGroupExact(long[] left, long[] right, bool everyDegree) {
        foreach (var (degree, tail) in BinaryFieldCatalogModuli) {
            var dividend = BinaryFieldElement(
                high: left[0],
                low: left[1],
                degree: degree
            );
            var drawn = BinaryFieldElement(
                high: right[0],
                low: right[1],
                degree: degree
            );
            var published = ((BigInteger)tail);
            // The extended-Euclid loop and the one-product-per-unit fold are both linear in the degree, so at Default
            // they run where they are cheap and deep.binary-field-multiplicative-group runs them everywhere.
            var second = (everyDegree || (degree <= 16));
            var order = 0UL;

            foreach (var (row, value) in BinaryFieldGroupOrders) {
                if (row == degree) { order = value; }
            }

            var failure = degree switch {
                8 => BinaryFieldGroupRow(
                degree: degree,
                dividend: dividend,
                drawn: drawn,
                field: BinaryFields.Degree8,
                foldCeiling: 13,
                order: order,
                second: second,
                tail: published
            ),
                16 => BinaryFieldGroupRow(
                degree: degree,
                dividend: dividend,
                drawn: drawn,
                field: BinaryFields.Degree16,
                foldCeiling: (everyDegree
                ? 13
                : 0),
                order: order,
                second: second,
                tail: published
            ),
                32 => BinaryFieldGroupRow(
                degree: degree,
                dividend: dividend,
                drawn: drawn,
                field: BinaryFields.Degree32,
                foldCeiling: (everyDegree
                ? 13
                : 0),
                order: order,
                second: second,
                tail: published
            ),
                64 => BinaryFieldGroupRow(
                degree: degree,
                dividend: dividend,
                drawn: drawn,
                field: BinaryFields.Degree64,
                foldCeiling: (everyDegree
                ? 13
                : 0),
                order: order,
                second: second,
                tail: published
            ),
                _ => BinaryFieldGroupRow(
                degree: degree,
                dividend: dividend,
                drawn: drawn,
                field: BinaryFields.Degree128,
                foldCeiling: (everyDegree
                ? 13
                : 0),
                order: order,
                second: second,
                tail: published
            ),
            };

            if (failure is not null) { return failure; }
        }

        return null;
    }

    private static string? BinaryFieldGroupRow<T>(BinaryField<T> field, int degree, BigInteger tail, BigInteger dividend, BigInteger drawn, bool second, ulong order, int foldCeiling)
        where T : IBinaryInteger<T>, IUnsignedNumber<T> {
        var one = field.One;
        var zero = field.Zero;
        // A zero element has no inverse and divides nothing. One is the substitute, applied identically here and in
        // every oracle below so each sampled pair reaches a defined comparison; the zero operand's own refusal is the
        // ladder at the end of this row.
        var value = (drawn.IsZero
            ? BigInteger.One
            : drawn
        );
        var packed = T.CreateTruncating(value: value);
        var numerator = T.CreateTruncating(value: dividend);
        var inverse = field.Inverse(value: packed);
        var certificate = Oracles.BinaryFieldProduct(
            left: value,
            right: BigInteger.CreateChecked(value: inverse),
            degree: degree,
            reductionTail: tail
        );

        if (!certificate.IsOne) { return $"the degree-{degree} inverse of {value} is {inverse}, whose product with it is {certificate} rather than one"; }

        if (second) {
            var euclidean = Oracles.BinaryFieldInverse(
                degree: degree,
                reductionTail: tail,
                value: value
            );

            if (euclidean.Sign < 0) { return $"the extended-Euclid oracle exhausted its step budget at degree {degree} tail {tail}, which reports a reducible modulus"; }
            if (BigInteger.CreateChecked(value: inverse) != euclidean) { return $"the degree-{degree} inverse of {value} is {inverse}, extended Euclid gives {euclidean}"; }
        }

        var quotient = field.Divide(
            left: numerator,
            right: packed
        );
        var recovered = Oracles.BinaryFieldProduct(
            left: BigInteger.CreateChecked(value: quotient),
            right: value,
            degree: degree,
            reductionTail: tail
        );

        if (recovered != dividend) { return $"{dividend} divided by {value} at degree {degree} is {quotient}, which re-multiplied by the divisor gives {recovered}"; }

        var root = field.SquareRoot(value: numerator);
        var rooted = BigInteger.CreateChecked(value: root);
        var squared = Oracles.BinaryFieldProduct(
            degree: degree,
            left: rooted,
            reductionTail: tail,
            right: rooted
        );

        if (squared != dividend) { return $"the degree-{degree} square root of {dividend} is {root}, whose square is {squared}"; }
        if (field.Square(value: root) != numerator) { return $"squaring the degree-{degree} root of {dividend} does not return it"; }
        if (field.SquareRoot(value: field.Square(value: numerator)) != numerator) { return $"the degree-{degree} root of the square of {dividend} does not return it"; }

        for (var exponent = 0; (exponent <= foldCeiling); ++exponent) {
            var power = BigInteger.CreateChecked(value: field.Exponentiate(
                exponent: ((ulong)exponent),
                value: packed
            ));
            var folded = Oracles.BinaryFieldRepeatedProduct(
                degree: degree,
                exponent: exponent,
                reductionTail: tail,
                value: value
            );

            if (power != folded) { return $"{value} raised to {exponent} at degree {degree} is {power}, the sequential fold gives {folded}"; }
        }

        if (0UL != order) {
            if (field.Exponentiate(
                exponent: order,
                value: packed
            ) != one) { return $"{value} raised to the group order {order} at degree {degree} is not one"; }
            if (field.Exponentiate(
                exponent: (order - 1UL),
                value: packed
            ) != inverse) { return $"{value} raised to {(order - 1UL)} at degree {degree} is not its inverse"; }
        }

        if (field.Exponentiate(
            exponent: 0UL,
            value: zero
        ) != one) { return $"zero raised to zero at degree {degree} is not one"; }
        if (field.Exponentiate(
            exponent: 0UL,
            value: packed
        ) != one) { return $"{value} raised to zero at degree {degree} is not one"; }

        return null;
    }

    /// <summary>Proves the two zero refusals at all five catalog fields. It runs ONCE per run rather than once per
    /// swept operand because both guards read only the divisor: <c>Inverse</c> refuses its own argument and
    /// <c>Divide</c> refuses through the inversion it delegates to, so no dividend can change either answer — and a
    /// caught exception costs some forty microseconds, which a per-operand ladder would spend four hundred times over
    /// for one bit of information.</summary>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? BinaryFieldGroupRefusals() =>
        (BinaryFieldZeroRefusal(
            degree: 8,
            dividend: ((byte)0x53),
            field: BinaryFields.Degree8
        ) ??
         (BinaryFieldZeroRefusal(
            degree: 16,
            dividend: ((ushort)0x53CA),
            field: BinaryFields.Degree16
        ) ??
         (BinaryFieldZeroRefusal(
            degree: 32,
            dividend: 0x53CA1BU,
            field: BinaryFields.Degree32
        ) ??
         (BinaryFieldZeroRefusal(
            degree: 64,
            dividend: 0x53CA1B8DUL,
            field: BinaryFields.Degree64
        ) ??
         BinaryFieldZeroRefusal(
            degree: 128,
            dividend: ((UInt128)0x53CA1B8D2BUL),
            field: BinaryFields.Degree128
        )))));

    private static string? BinaryFieldZeroRefusal<T>(BinaryField<T> field, int degree, T dividend)
        where T : IBinaryInteger<T>, IUnsignedNumber<T> {
        var zero = field.Zero;

        if (!Throws<DivideByZeroException>(action: () => _ = field.Inverse(value: zero))) { return $"the degree-{degree} field inverted zero"; }
        if (!Throws<DivideByZeroException>(action: () => _ = field.Divide(
            left: dividend,
            right: zero
        ))) { return $"the degree-{degree} field divided by zero"; }
        if (!Throws<DivideByZeroException>(action: () => _ = field.Divide(
            left: zero,
            right: zero
        ))) { return $"the degree-{degree} field divided zero by zero"; }

        return null;
    }

    /// <summary>Proves the four region primitives at the five catalog fields over a fixed length-and-scalar ladder:
    /// the elementwise product against a shared-nothing oracle where a vector rung exists, the aliasing contract, the
    /// degree-independence of region addition, and the two refusals.</summary>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? BinaryFieldRegionsExact() =>
        (BinaryFieldRegionField(
            classical: true,
            degree: 8,
            field: BinaryFields.Degree8,
            tail: 0x1B
        ) ??
         (BinaryFieldRegionField(
            classical: true,
            degree: 16,
            field: BinaryFields.Degree16,
            tail: 0x2B
        ) ??
         (BinaryFieldRegionField(
            classical: false,
            degree: 32,
            field: BinaryFields.Degree32,
            tail: 0x8D
        ) ??
         (BinaryFieldRegionField(
            classical: false,
            degree: 64,
            field: BinaryFields.Degree64,
            tail: 0x1B
        ) ??
         (BinaryFieldRegionField(
            classical: false,
            degree: 128,
            field: BinaryFields.Degree128,
            tail: 0x87
        ) ??
         BinaryFieldUnreducedAddRegion())))));

    private static string? BinaryFieldRegionField<T>(BinaryField<T> field, int degree, BigInteger tail, bool classical)
        where T : IBinaryInteger<T>, IUnsignedNumber<T> {
        var source = new T[BinaryFieldRegionCeiling];
        var seed = new T[BinaryFieldRegionCeiling];
        var destination = new T[BinaryFieldRegionCeiling];
        var scratch = new T[BinaryFieldRegionCeiling];
        var expected = new T[BinaryFieldRegionCeiling];
        var aliased = new T[BinaryFieldRegionCeiling];

        for (var index = 0; (index < BinaryFieldRegionCeiling); ++index) {
            source[index] = BinaryFieldRegionWalk<T>(
                index: index,
                salt: 0UL
            );
            seed[index] = BinaryFieldRegionWalk<T>(
                index: index,
                salt: 0x5DEECE66DUL
            );

            if (
                !field.IsReduced(value: source[index]) ||
                !field.IsReduced(value: seed[index])
            ) { return $"the degree-{degree} region content is not reduced at index {index}"; }
        }

        // The annihilator, the identity, the value that makes the tail fold maximally active, and the densest element.
        T[] scalars = [field.Zero, field.One, field.ReductionTail, T.AllBitsSet,];

        foreach (var scalar in scalars) {
            var scalarValue = BigInteger.CreateChecked(value: scalar);
            var aliasedScalar = BigInteger.CreateChecked(value: field.Add(
                left: field.One,
                right: scalar
            ));

            for (var index = 0; (index < BinaryFieldRegionCeiling); ++index) {
                var element = BigInteger.CreateChecked(value: source[index]);

                if (
                    classical ||
                    (index < BinaryFieldRegionSpot)
                ) {
                    expected[index] = T.CreateTruncating(value: Oracles.BinaryFieldProduct(
                        degree: degree,
                        left: scalarValue,
                        reductionTail: tail,
                        right: element
                    ));
                    aliased[index] = T.CreateTruncating(value: Oracles.BinaryFieldProduct(
                        degree: degree,
                        left: aliasedScalar,
                        reductionTail: tail,
                        right: element
                    ));

                    if (!classical) {
                        // The three wide carriers build their expectation from the field's own Multiply and are
                        // carriage; this prefix is where the BigInteger oracle still judges them.
                        if (expected[index] != field.Multiply(
                            left: scalar,
                            right: source[index]
                        )) { return $"the degree-{degree} scaled element at index {index} disagrees with the oracle"; }
                        if (aliased[index] != field.Multiply(
                            left: field.Add(
                                left: field.One,
                                right: scalar
                            ),
                            right: source[index]
                        )) { return $"the degree-{degree} aliased element at index {index} disagrees with the oracle"; }
                    }

                    continue;
                }

                expected[index] = field.Multiply(
                    left: scalar,
                    right: source[index]
                );
                aliased[index] = field.Multiply(
                    left: field.Add(
                        left: field.One,
                        right: scalar
                    ),
                    right: source[index]
                );
            }

            foreach (var length in BinaryFieldRegionLengths) {
                Array.Copy(
                    destinationArray: destination,
                    length: BinaryFieldRegionCeiling,
                    sourceArray: seed
                );
                field.ScaleRegion(
                    destination: destination.AsSpan(
                        length: length,
                        start: 0
                    ),
                    source: source.AsSpan(
                        length: length,
                        start: 0
                    ),
                    scalar: scalar
                );

                for (var index = 0; (index < length); ++index) {
                    if (destination[index] != expected[index]) { return $"ScaleRegion at degree {degree} scalar {scalar} length {length} wrote {destination[index]} at index {index}, expected {expected[index]}"; }
                }

                if (
                    (length < BinaryFieldRegionCeiling) &&
                    (destination[length] != seed[length])
                ) { return $"ScaleRegion at degree {degree} scalar {scalar} length {length} wrote past the region"; }

                Array.Copy(
                    destinationArray: destination,
                    length: BinaryFieldRegionCeiling,
                    sourceArray: seed
                );
                field.MultiplyAccumulateRegion(
                    destination: destination.AsSpan(
                        length: length,
                        start: 0
                    ),
                    source: source.AsSpan(
                        length: length,
                        start: 0
                    ),
                    scalar: scalar
                );

                for (var index = 0; (index < length); ++index) {
                    if (destination[index] != (seed[index] ^ expected[index])) { return $"MultiplyAccumulateRegion at degree {degree} scalar {scalar} length {length} wrote {destination[index]} at index {index}"; }
                }

                Array.Copy(
                    destinationArray: scratch,
                    length: BinaryFieldRegionCeiling,
                    sourceArray: source
                );
                field.ScaleRegionInPlace(
                    values: scratch.AsSpan(
                        length: length,
                        start: 0
                    ),
                    scalar: scalar
                );

                for (var index = 0; (index < length); ++index) {
                    if (scratch[index] != expected[index]) { return $"ScaleRegionInPlace at degree {degree} scalar {scalar} length {length} wrote {scratch[index]} at index {index}"; }
                }

                // The aliasing contract, stated as arithmetic: an exactly aliased scale equals the in-place form, an
                // exactly aliased accumulate leaves (1 + scalar)·value at every index — which holds only if every rung
                // loads its source before storing its destination at the same offset — and region addition onto itself
                // zeroes the region.
                Array.Copy(
                    destinationArray: scratch,
                    length: BinaryFieldRegionCeiling,
                    sourceArray: source
                );
                field.ScaleRegion(
                    destination: scratch.AsSpan(
                        length: length,
                        start: 0
                    ),
                    source: scratch.AsSpan(
                        length: length,
                        start: 0
                    ),
                    scalar: scalar
                );

                for (var index = 0; (index < length); ++index) {
                    if (scratch[index] != expected[index]) { return $"the aliased ScaleRegion at degree {degree} scalar {scalar} length {length} wrote {scratch[index]} at index {index}"; }
                }

                Array.Copy(
                    destinationArray: scratch,
                    length: BinaryFieldRegionCeiling,
                    sourceArray: source
                );
                field.MultiplyAccumulateRegion(
                    destination: scratch.AsSpan(
                        length: length,
                        start: 0
                    ),
                    source: scratch.AsSpan(
                        length: length,
                        start: 0
                    ),
                    scalar: scalar
                );

                for (var index = 0; (index < length); ++index) {
                    if (scratch[index] != aliased[index]) { return $"the aliased MultiplyAccumulateRegion at degree {degree} scalar {scalar} length {length} wrote {scratch[index]} at index {index}, expected {aliased[index]}"; }
                }

                Array.Copy(
                    destinationArray: destination,
                    length: BinaryFieldRegionCeiling,
                    sourceArray: seed
                );
                field.AddRegion(
                    destination: destination.AsSpan(
                        length: length,
                        start: 0
                    ),
                    source: source.AsSpan(
                        length: length,
                        start: 0
                    )
                );

                for (var index = 0; (index < length); ++index) {
                    if (destination[index] != (seed[index] ^ source[index])) { return $"AddRegion at degree {degree} length {length} wrote {destination[index]} at index {index}"; }
                }

                Array.Copy(
                    destinationArray: scratch,
                    length: BinaryFieldRegionCeiling,
                    sourceArray: source
                );
                field.AddRegion(
                    destination: scratch.AsSpan(
                        length: length,
                        start: 0
                    ),
                    source: scratch.AsSpan(
                        length: length,
                        start: 0
                    )
                );

                for (var index = 0; (index < length); ++index) {
                    if (scratch[index] != T.Zero) { return $"the aliased AddRegion at degree {degree} length {length} left {scratch[index]} at index {index}"; }
                }
            }
        }

        return BinaryFieldRegionRefusals(
            degree: degree,
            field: field
        );
    }
    // The two region refusals, at all three validating members, in BOTH directions of mismatch. Every row names the
    // public parameter 'source' — the intentional name, not a caller-argument expression — and a region operation is
    // required to refuse BEFORE it writes anything, which the untouched destination row states.
    private static string? BinaryFieldRegionRefusals<T>(BinaryField<T> field, int degree)
        where T : IBinaryInteger<T>, IUnsignedNumber<T> {
        var buffer = new T[8];
        var snapshot = new T[8];
        var scalar = field.One;

        // Distinct content per index, so a refusal that nevertheless wrote something is visible: the scaled or added
        // image of one index differs from what sat there.
        for (var index = 0; (index < 8); ++index) {
            buffer[index] = BinaryFieldRegionWalk<T>(
                index: index,
                salt: 0x9E3779B9UL
            );
            snapshot[index] = buffer[index];
        }

        // A shorter source and a longer source: the message describes the two lengths in one order, and either
        // direction must still be refused against the same parameter.
        foreach (var (destinationLength, sourceStart, sourceLength) in ((ReadOnlySpan<(int, int, int)>)[(4, 4, 3), (3, 4, 4)])) {
            if (!Throws<ArgumentOutOfRangeException>(
                action: () => field.ScaleRegion(
                    destination: buffer.AsSpan(
                        length: destinationLength,
                        start: 0
                    ),
                    source: buffer.AsSpan(
                        length: sourceLength,
                        start: sourceStart
                    ),
                    scalar: scalar
                ),
                paramName: "source"
            )) {
                return $"ScaleRegion at degree {degree} accepted a {sourceLength}-into-{destinationLength} mismatch, or named {RefusedParameter(action: () => field.ScaleRegion(
                destination: buffer.AsSpan(
                    length: destinationLength,
                    start: 0
                ),
                source: buffer.AsSpan(
                    length: sourceLength,
                    start: sourceStart
                ),
                scalar: scalar
            ))}";
            }
            if (!Throws<ArgumentOutOfRangeException>(
                action: () => field.MultiplyAccumulateRegion(
                    destination: buffer.AsSpan(
                        length: destinationLength,
                        start: 0
                    ),
                    source: buffer.AsSpan(
                        length: sourceLength,
                        start: sourceStart
                    ),
                    scalar: scalar
                ),
                paramName: "source"
            )) { return $"MultiplyAccumulateRegion at degree {degree} accepted a {sourceLength}-into-{destinationLength} mismatch"; }
            if (!Throws<ArgumentOutOfRangeException>(
                action: () => field.AddRegion(
                    destination: buffer.AsSpan(
                        length: destinationLength,
                        start: 0
                    ),
                    source: buffer.AsSpan(
                        length: sourceLength,
                        start: sourceStart
                    )
                ),
                paramName: "source"
            )) { return $"AddRegion at degree {degree} accepted a {sourceLength}-into-{destinationLength} mismatch"; }

            for (var index = 0; (index < 8); ++index) {
                if (buffer[index] != snapshot[index]) { return $"a refused {sourceLength}-into-{destinationLength} region call at degree {degree} wrote {buffer[index]} at index {index}"; }
            }
        }

        if (!Throws<ArgumentException>(
            action: () => field.ScaleRegion(
                destination: buffer.AsSpan(
                    length: 4,
                    start: 0
                ),
                source: buffer.AsSpan(
                    length: 4,
                    start: 1
                ),
                scalar: scalar
            ),
            paramName: "source"
        )) { return $"ScaleRegion at degree {degree} accepted a shifted overlap"; }
        if (!Throws<ArgumentException>(
            action: () => field.MultiplyAccumulateRegion(
                destination: buffer.AsSpan(
                    length: 4,
                    start: 0
                ),
                source: buffer.AsSpan(
                    length: 4,
                    start: 1
                ),
                scalar: scalar
            ),
            paramName: "source"
        )) { return $"MultiplyAccumulateRegion at degree {degree} accepted a shifted overlap"; }
        if (!Throws<ArgumentException>(
            action: () => field.AddRegion(
                destination: buffer.AsSpan(
                    length: 4,
                    start: 0
                ),
                source: buffer.AsSpan(
                    length: 4,
                    start: 1
                )
            ),
            paramName: "source"
        )) { return $"AddRegion at degree {degree} accepted a shifted overlap"; }

        return null;
    }
    // Region addition is the wing's one region member with no reduced-operand precondition, so it is stated where
    // that matters: a field whose degree sits BELOW its carrier's width, on content that violates every other region
    // member's precondition by construction. At a catalog field the statement is vacuous — degree equals width there,
    // so every packed value is already reduced.
    private static string? BinaryFieldUnreducedAddRegion() {
        var field = BinaryField<ushort>.Create(
            degree: 9,
            reductionTail: 0x11
        );
        var source = new ushort[BinaryFieldRegionCeiling];
        var destination = new ushort[BinaryFieldRegionCeiling];
        var seed = new ushort[BinaryFieldRegionCeiling];
        var unreduced = 0;

        for (var index = 0; (index < BinaryFieldRegionCeiling); ++index) {
            source[index] = BinaryFieldRegionWalk<ushort>(
                index: index,
                salt: 0UL
            );
            seed[index] = BinaryFieldRegionWalk<ushort>(
                index: index,
                salt: 0x5DEECE66DUL
            );

            if (
                !field.IsReduced(value: source[index]) ||
                !field.IsReduced(value: seed[index])
            ) { ++unreduced; }
        }

        if (unreduced < (BinaryFieldRegionCeiling / 2)) { return $"only {unreduced} of the {BinaryFieldRegionCeiling} degree-9 region elements are unreduced, so the statement does not reach its own operand class"; }

        foreach (var length in BinaryFieldRegionLengths) {
            Array.Copy(
                destinationArray: destination,
                length: BinaryFieldRegionCeiling,
                sourceArray: seed
            );
            field.AddRegion(
                destination: destination.AsSpan(
                    length: length,
                    start: 0
                ),
                source: source.AsSpan(
                    length: length,
                    start: 0
                )
            );

            for (var index = 0; (index < length); ++index) {
                if (destination[index] != ((ushort)(seed[index] ^ source[index]))) { return $"AddRegion at degree 9 length {length} wrote {destination[index]} at index {index} on unreduced content"; }
            }
        }

        return null;
    }

    /// <summary>Proves the whole construction surface: the admission ladder at five degrees per carrier, the refusal
    /// ladder with its parameter names, the FromModulus leading-term strip, the default descriptor's uniform refusal at
    /// every carrier supported and unsupported alike, and the five catalog fields' identity by record equality against
    /// their published moduli.</summary>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? BinaryFieldConstructionAndRefusals() {
        var failure = (BinaryFieldAdmission<byte>(width: 8) ??
                       (BinaryFieldAdmission<ushort>(width: 16) ??
                       (BinaryFieldAdmission<uint>(width: 32) ??
                       (BinaryFieldAdmission<ulong>(width: 64) ??
                       BinaryFieldAdmission<UInt128>(width: 128)))));

        if (failure is not null) { return failure; }

        // Both satisfy the IBinaryInteger/IUnsignedNumber constraint and neither is one of the five fixed widths the
        // carrier check admits; nuint is eight bytes wide on this target, so it would otherwise look exactly like
        // ulong to CarrierBitCount. The zero degree on the third row proves the carrier check runs FIRST.
        if (!Throws<NotSupportedException>(action: () => _ = BinaryField<char>.Create(
            degree: 1,
            reductionTail: ((char)1)
        ))) { return "the char carrier was admitted"; }
        if (!Throws<NotSupportedException>(action: () => _ = BinaryField<nuint>.Create(
            degree: 1,
            reductionTail: ((nuint)1)
        ))) { return "the nuint carrier was admitted"; }
        if (!Throws<NotSupportedException>(action: () => _ = BinaryField<char>.Create(
            degree: 0,
            reductionTail: ((char)0)
        ))) { return "the carrier check does not precede the degree check"; }

        // The below-one refusals ride the SAME shared range rule Create runs — ArgumentOutOfRangeException, not a
        // FromModulus-only ArgumentException — so both halves of the degree range report one exception type, and
        // ThrowsExactly is what refuses the base type standing in for the derived one.
        if (!ThrowsExactly<ArgumentOutOfRangeException>(
            action: () => _ = BinaryField<ulong>.FromModulus(modulus: BinaryPolynomial.Zero),
            paramName: "modulus"
        )) { return "FromModulus accepted the zero polynomial, or refused it outside the shared degree range rule"; }
        if (!ThrowsExactly<ArgumentOutOfRangeException>(
            action: () => _ = BinaryField<ulong>.FromModulus(modulus: new BinaryPolynomial(bits: 1UL)),
            paramName: "modulus"
        )) { return "FromModulus accepted the degree-zero constant, or refused it outside the shared degree range rule"; }
        // The carrier check precedes the degree rules in the one shared body, so an unsupported carrier reports itself
        // from FromModulus exactly as it does from Create.
        if (!Throws<NotSupportedException>(action: () => _ = BinaryField<nuint>.FromModulus(modulus: BinaryPolynomial.Zero))) { return "FromModulus's carrier check does not precede the degree check"; }
        // Both DERIVED failures name the parameter the caller supplied. Neither 'degree' nor 'reductionTail' appears in
        // FromModulus's signature, and the RefusedParameter readback is what turns a regression here into a named
        // counterexample rather than a bare boolean.
        if (!Throws<ArgumentOutOfRangeException>(
            action: () => _ = BinaryField<byte>.FromModulus(modulus: new BinaryPolynomial(bits: (1UL << 9) | 1UL)),
            paramName: "modulus"
        )) { return $"FromModulus accepted a modulus above the carrier's width, or refused naming {RefusedParameter(action: () => _ = BinaryField<byte>.FromModulus(modulus: new BinaryPolynomial(bits: (1UL << 9) | 1UL)))}"; }
        if (!Throws<ArgumentException>(
            action: () => _ = BinaryField<byte>.FromModulus(modulus: new BinaryPolynomial(bits: (1UL << 4) | 2UL)),
            paramName: "modulus"
        )) { return $"FromModulus accepted a modulus with a zero constant term, or refused naming {RefusedParameter(action: () => _ = BinaryField<byte>.FromModulus(modulus: new BinaryPolynomial(bits: (1UL << 4) | 2UL)))}"; }

        // Direct Create failures keep naming Create's OWN parameters: the shared validation core reports the name it is
        // handed, and the two factories must not have collapsed into one set of names.
        if (!Throws<ArgumentOutOfRangeException>(
            action: () => _ = BinaryField<byte>.Create(
                degree: 9,
                reductionTail: 1
            ),
            paramName: "degree"
        )) { return "Create's degree refusal stopped naming degree"; }
        if (!Throws<ArgumentException>(
            action: () => _ = BinaryField<byte>.Create(
                degree: 4,
                reductionTail: 2
            ),
            paramName: "reductionTail"
        )) { return "Create's tail refusal stopped naming reductionTail"; }

        foreach (var degree in BinaryFieldModulusDegrees) {
            var tail = ((1UL << (degree - 1)) | 1UL) & ((degree < 64)
                ? ((1UL << degree) - 1UL)
                : ulong.MaxValue
            );
            var field = BinaryField<ulong>.FromModulus(modulus: new BinaryPolynomial(bits: (1UL << degree) | tail));

            if (field.Degree != degree) { return $"FromModulus of t^{degree} + {tail} reports degree {field.Degree}"; }
            if (field.ReductionTail != tail) { return $"FromModulus of t^{degree} + {tail} reports tail {field.ReductionTail}"; }
        }

        var defaults = (BinaryFieldDefaultRefusals<byte>(width: 8) ??
                        (BinaryFieldDefaultRefusals<ushort>(width: 16) ??
                        (BinaryFieldDefaultRefusals<uint>(width: 32) ??
                        (BinaryFieldDefaultRefusals<ulong>(width: 64) ??
                        BinaryFieldDefaultRefusals<UInt128>(width: 128)))));

        if (defaults is not null) { return defaults; }

        // An UNSUPPORTED carrier has no constructed state at all — Create refuses it before the degree is read — so the
        // uninitialized diagnosis is the only thing its default value can say, and it says exactly what the five
        // supported carriers say.
        var unsupported = default(BinaryField<char>);

        if (!Throws<InvalidOperationException>(action: () => _ = unsupported.Multiply(
            left: ((char)3),
            right: ((char)3)
        ))) { return "the default char-carrier field multiplied"; }
        if (!Throws<InvalidOperationException>(action: () => _ = unsupported.Add(
            left: ((char)3),
            right: ((char)5)
        ))) { return "the default char-carrier field added"; }
        if (!Throws<InvalidOperationException>(action: () => _ = unsupported.Inverse(value: ((char)1)))) { return "the default char-carrier field inverted"; }
        if (!Throws<InvalidOperationException>(action: () => _ = unsupported.IsIrreducible())) { return "the default char-carrier field answered IsIrreducible"; }
        if (!Throws<InvalidOperationException>(action: () => _ = unsupported.One)) { return "the default char-carrier field answered One"; }

        // The two top rows are reachable ONLY through Create: BinaryPolynomial's packed carrier tops out at degree 63.
        if (BinaryField<byte>.Create(
            degree: 8,
            reductionTail: 0x1B
        ) != BinaryFields.Degree8) { return "BinaryFields.Degree8 is not the published field"; }
        if (BinaryField<ushort>.Create(
            degree: 16,
            reductionTail: 0x2B
        ) != BinaryFields.Degree16) { return "BinaryFields.Degree16 is not the published field"; }
        if (BinaryField<uint>.Create(
            degree: 32,
            reductionTail: 0x8DU
        ) != BinaryFields.Degree32) { return "BinaryFields.Degree32 is not the published field"; }
        if (BinaryField<ulong>.Create(
            degree: 64,
            reductionTail: 0x1BUL
        ) != BinaryFields.Degree64) { return "BinaryFields.Degree64 is not the published field"; }
        if (BinaryField<UInt128>.Create(
            degree: 128,
            reductionTail: ((UInt128)0x87)
        ) != BinaryFields.Degree128) { return "BinaryFields.Degree128 is not the published field"; }

        return null;
    }

    // The default-descriptor refusal at one carrier. The whole semantic surface is listed by NAME so a member that
    // quietly answers is reported as itself rather than as a bare boolean, and the list is what makes the policy
    // uniform: one exception type from every operation and both identities, never a mix of plausible answers and
    // incidental failures. The data readers are deliberately outside it and still report the uninitialized state.
    private static string? BinaryFieldDefaultRefusals<T>(int width)
        where T : IBinaryInteger<T>, IUnsignedNumber<T> {
        var empty = default(BinaryField<T>);
        var one = T.One;
        var scratch = new T[4];
        var other = new T[4];

        if (empty.Degree != 0) { return $"the default width-{width} field reports degree {empty.Degree}"; }
        if (empty.ReductionTail != T.Zero) { return $"the default width-{width} field reports tail {empty.ReductionTail}"; }

        // The PRINTABILITY half of the promise, stated on ToString itself: a default value formats as its raw state
        // rather than throwing from a guarded identity the synthesized member walk would have read.
        if (empty.ToString() != "BinaryField { Degree = 0, ReductionTail = 0 }") { return $"the default width-{width} field prints as {empty}"; }

        (string Name, Action Call)[] operations = [
            ("One", () => _ = empty.One),
            ("Zero", () => _ = empty.Zero),
            ("Add", () => _ = empty.Add(
                left: one,
                right: one
            )),
            ("AddRegion", () => empty.AddRegion(
                destination: scratch.AsSpan(),
                source: other.AsSpan()
            )),
            ("Divide", () => _ = empty.Divide(
                left: one,
                right: one
            )),
            ("Exponentiate", () => _ = empty.Exponentiate(
                exponent: 3UL,
                value: one
            )),
            ("Inverse", () => _ = empty.Inverse(value: one)),
            ("IsIrreducible", () => _ = empty.IsIrreducible()),
            ("IsReduced", () => _ = empty.IsReduced(value: one)),
            ("Multiply", () => _ = empty.Multiply(
                left: one,
                right: one
            )),
            ("MultiplyAccumulateRegion", () => empty.MultiplyAccumulateRegion(
                destination: scratch.AsSpan(),
                source: other.AsSpan(),
                scalar: one
            )),
            ("Reduce", () => _ = empty.Reduce(value: one)),
            ("ScaleRegion", () => empty.ScaleRegion(
                destination: scratch.AsSpan(),
                source: other.AsSpan(),
                scalar: one
            )),
            ("ScaleRegionInPlace", () => empty.ScaleRegionInPlace(
                values: scratch.AsSpan(),
                scalar: one
            )),
            ("Square", () => _ = empty.Square(value: one)),
            ("SquareRoot", () => _ = empty.SquareRoot(value: one)),
        ];

        foreach (var (name, call) in operations) {
            if (!Throws<InvalidOperationException>(action: call)) { return $"the default width-{width} field answered {name} instead of refusing"; }
        }

        // An unassigned array element is the SAME state, reached without writing `default` anywhere — which is the way
        // an uninitialized descriptor actually turns up in a caller.
        var fields = new BinaryField<T>[2];

        if (fields[0] != empty) { return $"an unassigned width-{width} field array element does not equal the default"; }
        if (!Throws<InvalidOperationException>(action: () => _ = fields[1].Multiply(
            left: one,
            right: one
        ))) { return $"an unassigned width-{width} field array element answered Multiply"; }

        // A CONSTRUCTED field is untouched by the guard: it answers, and record equality still reads the degree and the
        // tail rather than any encoding of them.
        var three = (T.One + (T.One + T.One));
        var built = BinaryField<T>.Create(
            degree: width,
            reductionTail: three
        );

        if (built.Degree != width) { return $"the width-{width} field reports degree {built.Degree}"; }
        if (built.ReductionTail != three) { return $"the width-{width} field reports tail {built.ReductionTail}"; }
        if (built != BinaryField<T>.Create(
            degree: width,
            reductionTail: three
        )) { return $"two width-{width} fields over the same modulus are unequal"; }
        if (built == empty) { return $"a constructed width-{width} field equals the default"; }
        if (built.One != T.One) { return $"the width-{width} field's multiplicative identity is {built.One}"; }
        if (built.Zero != T.Zero) { return $"the width-{width} field's additive identity is {built.Zero}"; }

        // ToString prints the descriptor's two data and nothing else: the identities are constants of the TYPE, not
        // carried state, so a hand-written PrintMembers keeps them out of the rendering.
        if (built.ToString() != $"BinaryField {{ Degree = {width}, ReductionTail = 3 }}") { return $"the width-{width} field prints as {built}"; }

        return null;
    }
    private static string? BinaryFieldAdmission<T>(int width)
        where T : IBinaryInteger<T>, IUnsignedNumber<T> {
        // Degree one, degree two, half the width, one below the width, and the width itself — the last is the row the
        // tail representation exists for, where a degree-k modulus would need k + 1 bits the carrier does not have.
        int[] degrees = [1, 2, (width >> 1), (width - 1), width,];

        foreach (var degree in degrees) {
            var tail = ((BigInteger.One << (degree - 1)) | BigInteger.One) & ((BigInteger.One << degree) - BigInteger.One);
            var packedTail = T.CreateTruncating(value: tail);
            var field = BinaryField<T>.Create(
                degree: degree,
                reductionTail: packedTail
            );
            var mask = T.CreateTruncating(value: ((BigInteger.One << degree) - BigInteger.One));

            if (field.Degree != degree) { return $"the width-{width} field of degree {degree} reports degree {field.Degree}"; }
            if (field.ReductionTail != packedTail) { return $"the width-{width} field of degree {degree} reports tail {field.ReductionTail}, expected {packedTail}"; }
            if (
                !field.IsReduced(value: field.One) ||
                !field.IsReduced(value: field.Zero) ||
                !field.IsReduced(value: mask)
            ) { return $"the width-{width} degree-{degree} field calls one of its own elements unreduced"; }

            if (degree < width) {
                var monomial = T.CreateTruncating(value: (BigInteger.One << degree));

                if (field.IsReduced(value: monomial)) { return $"the width-{width} degree-{degree} field calls t^{degree} reduced"; }
                if (field.Reduce(value: monomial) != packedTail) { return $"t^{degree} reduces to {field.Reduce(value: monomial)} at width {width}, the tail is {packedTail}"; }

                var illegal = T.CreateTruncating(value: (BigInteger.One << degree) | BigInteger.One);

                if (!Throws<ArgumentException>(
                    action: () => _ = BinaryField<T>.Create(
                        degree: degree,
                        reductionTail: illegal
                    ),
                    paramName: "reductionTail"
                )) { return $"the width-{width} carrier admitted a degree-{degree} tail carrying t^{degree}"; }
            }

            if (!Throws<ArgumentException>(
                action: () => _ = BinaryField<T>.Create(
                    degree: degree,
                    reductionTail: T.Zero
                ),
                paramName: "reductionTail"
            )) { return $"the width-{width} carrier admitted a zero tail at degree {degree}"; }
            if (!Throws<ArgumentException>(
                action: () => _ = BinaryField<T>.Create(
                    degree: degree,
                    reductionTail: (T.One + T.One)
                ),
                paramName: "reductionTail"
            )) { return $"the width-{width} carrier admitted an even tail at degree {degree}"; }
        }

        if (!Throws<ArgumentOutOfRangeException>(
            action: () => _ = BinaryField<T>.Create(
                degree: 0,
                reductionTail: T.One
            ),
            paramName: "degree"
        )) { return $"the width-{width} carrier admitted degree zero"; }
        if (!Throws<ArgumentOutOfRangeException>(
            action: () => _ = BinaryField<T>.Create(
                degree: -1,
                reductionTail: T.One
            ),
            paramName: "degree"
        )) { return $"the width-{width} carrier admitted a negative degree"; }
        if (!Throws<ArgumentOutOfRangeException>(
            action: () => _ = BinaryField<T>.Create(
                degree: (width + 1),
                reductionTail: T.One
            ),
            paramName: "degree"
        )) { return $"the width-{width} carrier admitted degree {(width + 1)}"; }

        return null;
    }

    /// <summary>Proves the irreducibility decision: exhaustive agreement with trial division over every legal modulus
    /// up to a degree, the published A001037 census over a wider band, the five catalog moduli, and the maximally
    /// reducible negative probe at every catalog degree.</summary>
    /// <param name="censusDegree">The highest degree the exhaustive census runs to.</param>
    /// <param name="trialDegree">The highest degree the per-modulus trial-division agreement runs to.</param>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? BinaryFieldIrreducibility(int censusDegree, int trialDegree) {
        for (var degree = 1; (degree <= censusDegree); ++degree) {
            var ceiling = (1UL << degree);
            var counted = 0;

            for (var tail = 1UL; (tail < ceiling); tail += 2UL) {
                var decided = ((degree <= 8)
                    ? BinaryField<byte>.Create(
                        degree: degree,
                        reductionTail: ((byte)tail)
                    ).IsIrreducible()
                    : BinaryField<ushort>.Create(
                        degree: degree,
                        reductionTail: ((ushort)tail)
                    ).IsIrreducible()
                );

                if (decided) { ++counted; }

                if (degree > trialDegree) { continue; }

                var reference = Oracles.BinaryPolynomialIsIrreducible(value: (((BigInteger.One << degree) | tail)));

                if (decided != reference) { return $"t^{degree} + {tail} is called {decided} by the field and {reference} by trial division"; }
            }

            // The published degree-one entry counts t as well as t + 1, and t has a zero constant term, which Create
            // refuses because t would then divide the modulus. Every irreducible of degree two or above has a non-zero
            // constant term, so those rows are the published values unchanged.
            var published = (BinaryIrreducibleCounts[(degree - 1)] - ((1 == degree)
                ? 1
                : 0));

            if (counted != published) { return $"there are {counted} constructible irreducible moduli of degree {degree}, the published count is {published}"; }
        }

        if (!BinaryFields.Degree8.IsIrreducible()) { return "the degree-8 catalog modulus is not irreducible"; }
        if (!BinaryFields.Degree16.IsIrreducible()) { return "the degree-16 catalog modulus is not irreducible"; }
        if (!BinaryFields.Degree32.IsIrreducible()) { return "the degree-32 catalog modulus is not irreducible"; }
        if (!BinaryFields.Degree64.IsIrreducible()) { return "the degree-64 catalog modulus is not irreducible"; }
        if (!BinaryFields.Degree128.IsIrreducible()) { return "the degree-128 catalog modulus is not irreducible"; }

        // t^(2^k) + 1 is (t + 1)^(2^k) by the Frobenius identity in characteristic two — a perfect power, and as
        // reducible as a polynomial gets. This is the only negative statement that reaches the three wide degrees.
        if (BinaryField<byte>.Create(
            degree: 8,
            reductionTail: 1
        ).IsIrreducible()) { return "t^8 + 1 is called irreducible"; }
        if (BinaryField<ushort>.Create(
            degree: 16,
            reductionTail: 1
        ).IsIrreducible()) { return "t^16 + 1 is called irreducible"; }
        if (BinaryField<uint>.Create(
            degree: 32,
            reductionTail: 1U
        ).IsIrreducible()) { return "t^32 + 1 is called irreducible"; }
        if (BinaryField<ulong>.Create(
            degree: 64,
            reductionTail: 1UL
        ).IsIrreducible()) { return "t^64 + 1 is called irreducible"; }
        if (BinaryField<UInt128>.Create(
            degree: 128,
            reductionTail: UInt128.One
        ).IsIrreducible()) { return "t^128 + 1 is called irreducible"; }

        return null;
    }
    /// <summary>Proves the byte field TOTALLY: every ordered product against the oracle, every square and root, every
    /// inverse against extended Euclid, the published generator count, and multiplication by every non-zero element as
    /// a bijection of the whole field.</summary>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? BinaryFieldDegree8Exhaustive() {
        var field = BinaryFields.Degree8;
        var tail = ((BigInteger)0x1B);
        var one = field.One;

        for (var left = 0; (left < 256); ++left) {
            for (var right = 0; (right < 256); ++right) {
                var product = ((BigInteger)field.Multiply(
                    left: ((byte)left),
                    right: ((byte)right)
                ));
                var expected = Oracles.BinaryFieldProduct(
                    degree: 8,
                    left: left,
                    reductionTail: tail,
                    right: right
                );

                if (product != expected) { return $"the degree-8 product of {left} and {right} is {product}, the oracle gives {expected}"; }
            }
        }

        for (var value = 0; (value < 256); ++value) {
            var element = ((byte)value);
            var root = field.SquareRoot(value: element);
            var squared = Oracles.BinaryFieldProduct(
                degree: 8,
                left: root,
                reductionTail: tail,
                right: root
            );

            if (field.Square(value: element) != field.Multiply(
                left: element,
                right: element
            )) { return $"Square is not the product with itself at {value}"; }
            if (squared != value) { return $"the degree-8 root of {value} is {root}, whose square is {squared}"; }
            if (field.SquareRoot(value: field.Square(value: element)) != element) { return $"the root of the square of {value} does not return it"; }
            if (field.Exponentiate(
                exponent: 0UL,
                value: element
            ) != one) { return $"{value} raised to zero is not one"; }
        }

        var generators = 0;
        var seen = new bool[256];

        for (var value = 1; (value < 256); ++value) {
            var element = ((byte)value);
            var inverse = field.Inverse(value: element);
            var euclidean = Oracles.BinaryFieldInverse(
                degree: 8,
                reductionTail: tail,
                value: value
            );

            if (euclidean.Sign < 0) { return $"the extended-Euclid oracle exhausted its step budget at {value}"; }
            if (inverse != euclidean) { return $"the degree-8 inverse of {value} is {inverse}, extended Euclid gives {euclidean}"; }

            // Twelve dividends rather than all 256: the quotient's certificate is one BigInteger product, and the
            // divisor band is what this sweep is total over.
            for (var dividend = 0; (dividend < 256); dividend += 21) {
                var quotient = field.Divide(
                    left: ((byte)dividend),
                    right: element
                );
                var recovered = Oracles.BinaryFieldProduct(
                    degree: 8,
                    left: quotient,
                    reductionTail: tail,
                    right: value
                );

                if (recovered != dividend) { return $"{dividend} divided by {value} is {quotient}, which re-multiplied gives {recovered}"; }
            }

            var order = 1;
            var power = element;

            while (one != power) {
                power = field.Multiply(
                    left: power,
                    right: element
                );
                ++order;

                if (order > 255) { return $"the multiplicative order of {value} exceeds the group order"; }
            }

            if (0 != (255 % order)) { return $"the multiplicative order of {value} is {order}, which does not divide 255"; }
            if (field.Exponentiate(
                exponent: ((ulong)order),
                value: element
            ) != one) { return $"{value} raised to its own order {order} is not one"; }
            if (255 == order) { ++generators; }

            Array.Clear(array: seen);

            for (var other = 0; (other < 256); ++other) {
                var image = field.Multiply(
                    left: element,
                    right: ((byte)other)
                );

                if (seen[image]) { return $"multiplication by {value} sends two elements to {image}"; }

                seen[image] = true;
            }
        }

        // Euler's totient of 255 = 3 × 5 × 17, so φ(255) = 2 × 4 × 16 = 128 — the number of generators of any cyclic
        // group of order 255.
        if (128 != generators) { return $"{generators} elements have the full multiplicative order 255, the published count is 128"; }

        return null;
    }

}
