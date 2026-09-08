using System.Buffers;
using System.Numerics;

namespace Puck.Maths.Tests;

internal static partial class Subjects {
    // ---- phase 4: the graph zeta ----

    // The odd prime the zeta's field lane runs at, above every order the family reaches, so the trace recursion divides
    // by units the whole way up.
    private const ulong ZetaModulus = 1000003UL;

    /// <summary>Proves the trace recursion's coefficients to be the characteristic polynomial: at every digraph of the
    /// family and at two field materials they equal the signed sums of principal minors an independent
    /// <see cref="BigInteger"/> enumeration computes, and at order two they equal
    /// <see cref="QuadraticInflation.Trace"/> and <see cref="QuadraticInflation.Determinant"/>.</summary>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    /// <remarks>The oracle shares no step with the subject: it forms no power, takes no trace, and divides nowhere,
    /// while the subject reads only <see cref="PresentedAlgebra{TValue, TOps}.Trace"/> of
    /// <see cref="PresentedAlgebra{TValue, TOps}.Power"/> and then divides by every index up to the order. The
    /// inflation half is a third route again — a continued-fraction period folded as convergent matrices.</remarks>
    public static string? ZetaCharacteristicVsMinors() {
        foreach (var (name, order, entries) in ZetaDigraphs()) {
            if (ZetaCoefficientsHold<RealQuadratic, RationalMaterial>(
                convert: ZetaRational,
                entries: entries,
                material: default,
                name: $"{name} over the rationals",
                order: order
            ) is { } rational) {
                return rational;
            }

            if (ZetaCoefficientsHold<ulong, PrimeFieldMaterial>(
                name: $"{name} over GF({ZetaModulus})",
                order: order,
                entries: entries,
                material: new PrimeFieldMaterial(field: PrimeField64.Create(modulus: ZetaModulus)),
                convert: ZetaResidue
            ) is { } prime) {
                return prime;
            }
        }

        // Order two, where the two coefficients have names of their own: det(I − tM) is 1 − Trace·t + Determinant·t².
        foreach (var (p, q, d, r) in SubstitutionSurds) {
            var inflation = QuadraticInflation.FromQuadraticIrrational(
                d: d,
                p: p,
                q: q,
                r: r
            );
            var algebra = PresentedAlgebra<RealQuadratic, RationalMaterial>.Create(presentation: CodiscreteQuiver<RealQuadratic, RationalMaterial>(
                material: default,
                order: 2
            ));
            var adjacency = algebra.FromSupport(
                keys: [0L, 1L, 2L, 3L],
                coefficients: [ZetaRational(value: inflation.A), ZetaRational(value: inflation.B), ZetaRational(value: inflation.C), ZetaRational(value: inflation.D)]
            );

            if (!GraphZeta<RealQuadratic, RationalMaterial>.TryCreate(
                algebra: algebra,
                degreeBound: 5,
                obstruction: out var obstruction,
                order: 2,
                value: adjacency,
                zeta: out var zeta
            )) {
                return $"√{d}: the inflation matrix was refused at index {obstruction.BlockedIndex}";
            }

            if (zeta!.PowerSum(length: 1) != ZetaRational(value: inflation.Trace)) { return $"√{d}: the first power sum is not the inflation matrix's trace"; }
            if (zeta.Coefficient(degree: 1) != ZetaRational(value: -inflation.Trace)) { return $"√{d}: the linear coefficient is not minus the inflation matrix's trace"; }
            if (zeta.Coefficient(degree: 2) != ZetaRational(value: inflation.Determinant)) { return $"√{d}: the quadratic coefficient is not the inflation matrix's determinant"; }
        }

        return null;
    }
    /// <summary>Proves the power sums to be closed-walk counts: the trace of every power the recursion reads equals the
    /// diagonal of the same power an independent <see cref="BigInteger"/> matrix multiplication builds.</summary>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    /// <remarks>The subject squares and multiplies through the algebra's compiled cells and pairs the result with the
    /// unit; the oracle multiplies matrices repeatedly and adds up a diagonal. Length zero is part of the statement: it
    /// is the order's worth of ones, which is what pins the order the recursion runs at.</remarks>
    public static string? ZetaTracesVsWalkCounts() {
        var counts = new BigInteger[36];

        foreach (var (name, order, entries) in ZetaDigraphs()) {
            var algebra = PresentedAlgebra<RealQuadratic, RationalMaterial>.Create(presentation: CodiscreteQuiver<RealQuadratic, RationalMaterial>(
                material: default,
                order: order
            ));

            if (!GraphZeta<RealQuadratic, RationalMaterial>.TryCreate(
                algebra: algebra,
                value: ZetaAdjacency<RealQuadratic, RationalMaterial>(
                    algebra: algebra,
                    convert: ZetaRational,
                    entries: entries
                ),
                order: order,
                degreeBound: ((2 * order) + 2),
                zeta: out var zeta,
                obstruction: out var obstruction
            )) {
                return $"{name}: refused at index {obstruction.BlockedIndex}";
            }

            for (var length = 0; (length <= order); ++length) {
                var window = counts.AsSpan(
                    length: (order * order),
                    start: 0
                );
                var trace = BigInteger.Zero;

                Oracles.WalkCount(
                    adjacency: entries,
                    length: length,
                    order: order,
                    result: window
                );

                for (var vertex = 0; (vertex < order); ++vertex) { trace += window[((vertex * order) + vertex)]; }

                if (zeta!.PowerSum(length: length) != ZetaRational(value: trace)) {
                    return $"{name}: the power sum at length {length} is not the closed-walk count {trace}";
                }
            }
        }

        return null;
    }
    /// <summary>Proves the dynamical zeta to be the reciprocal of the characteristic polynomial in the truncated ring,
    /// under a nilpotence certificate the guarded sum issues rather than assumes.</summary>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    /// <remarks>The round-trip is checked in both orders and at degree bounds above, at, and BELOW the order, because an
    /// inverse modulo <c>t^(d+1)</c> depends on nothing above that degree — so truncating the polynomial does not
    /// truncate the statement. The characteristic polynomial's coefficients are already pinned to the minor oracle, so a
    /// round-trip on top of that determines the zeta uniquely.</remarks>
    public static string? ZetaReciprocalRoundTrip() {
        foreach (var (name, order, entries) in ZetaDigraphs()) {
            var algebra = PresentedAlgebra<RealQuadratic, RationalMaterial>.Create(presentation: CodiscreteQuiver<RealQuadratic, RationalMaterial>(
                material: default,
                order: order
            ));
            var adjacency = ZetaAdjacency<RealQuadratic, RationalMaterial>(
                algebra: algebra,
                convert: ZetaRational,
                entries: entries
            );

            foreach (var degreeBound in ((int[])[0, (order - 1), order, ((2 * order) + 2)])) {
                if (degreeBound < 0) { continue; }

                if (!GraphZeta<RealQuadratic, RationalMaterial>.TryCreate(
                    algebra: algebra,
                    degreeBound: degreeBound,
                    obstruction: out var obstruction,
                    order: order,
                    value: adjacency,
                    zeta: out var zeta
                )) {
                    return $"{name} at degree bound {degreeBound}: refused at index {obstruction.BlockedIndex}";
                }

                var series = zeta!.Series;

                if (ClosureCertificate.Nilpotent != zeta.Certificate) {
                    return $"{name} at degree bound {degreeBound}: the reciprocal reports {zeta.Certificate}, where the augmentation part of a series whose constant term is one is nilpotent";
                }

                if (
                    (zeta.DegreeBound != degreeBound) ||
                    (series.Presentation.NormalFormCount != (degreeBound + 1))
                ) {
                    return $"{name} at degree bound {degreeBound}: the series ring holds {series.Presentation.NormalFormCount} place(s)";
                }

                if (
                    !series.AreEqual(
                    left: series.Multiply(
                        left: zeta.CharacteristicPolynomial,
                        right: zeta.DynamicalZeta
                    ),
                    right: series.Identity
                ) ||
                    !series.AreEqual(
                    left: series.Multiply(
                        left: zeta.DynamicalZeta,
                        right: zeta.CharacteristicPolynomial
                    ),
                    right: series.Identity
                )
                ) {
                    return $"{name} at degree bound {degreeBound}: the polynomial and the zeta do not multiply to the unit of the truncated ring";
                }

                // The truncation is the ring's, not the readout's: every coefficient the ring holds is the one the
                // recursion reached, and the readout still carries the degrees above it.
                for (var degree = 0; (degree <= degreeBound); ++degree) {
                    var expected = ((degree <= order)
                        ? zeta.Coefficient(degree: degree)
                        : RealQuadratic.Zero
                    );

                    if (zeta.CharacteristicPolynomial[degree] != expected) {
                        return $"{name} at degree bound {degreeBound}: the polynomial element differs from the coefficient readout at degree {degree}";
                    }
                }

                if (zeta.DynamicalZeta[0L] != RealQuadratic.One) { return $"{name} at degree bound {degreeBound}: the zeta's constant term is not one"; }
            }
        }

        return null;
    }
    /// <summary>Proves the trace recursion's licence: it is refused wherever the material cannot invert one of the
    /// indexes it divides by, and the refusal names that index.</summary>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    /// <remarks>The three refusals the boundary map names are measured here — a counting material, an integer material
    /// and a prime field whose characteristic is at or below the order — and the fixed-point material is measured with
    /// them, since a trace-based zeta is exact-only and it is not a certified field. The prime-field boundary is
    /// measured on BOTH sides: the same modulus that blocks at one order answers at the order below it.</remarks>
    public static string? ZetaLimitsRefuse() {
        // A material that certifies no inverses cannot reach even the first divisor, so the refusal is at index one.
        if (ZetaRefusesAt<BigInteger, CountingMaterial>(
            blockedIndex: 1,
            material: default,
            name: "a counting material",
            order: 4
        ) is { } counting) { return counting; }
        if (ZetaRefusesAt<BigInteger, IntegerMaterial>(
            blockedIndex: 1,
            material: default,
            name: "an integer material",
            order: 4
        ) is { } integer) { return integer; }
        if (ZetaRefusesAt<FixedQ4816, FixedMaterial>(
            blockedIndex: 1,
            material: default,
            name: "the house scalar",
            order: 2
        ) is { } house) { return house; }

        // A field of characteristic p blocks exactly at p, and only once the order reaches it.
        if (ZetaRefusesAt<ulong, PrimeFieldMaterial>(
            name: "GF(5) at order six",
            order: 6,
            material: new PrimeFieldMaterial(field: PrimeField64.Create(modulus: 5UL)),
            blockedIndex: 5
        ) is { } wide) { return wide; }
        if (ZetaRefusesAt<ulong, PrimeFieldMaterial>(
            name: "GF(5) at order five",
            order: 5,
            material: new PrimeFieldMaterial(field: PrimeField64.Create(modulus: 5UL)),
            blockedIndex: 5
        ) is { } exact) { return exact; }
        if (ZetaRefusesAt<ulong, PrimeFieldMaterial>(
            name: "GF(3) at order three",
            order: 3,
            material: new PrimeFieldMaterial(field: PrimeField64.Create(modulus: 3UL)),
            blockedIndex: 3
        ) is { } narrow) { return narrow; }
        if (ZetaRefusesAt<ulong, PrimeFieldMaterial>(
            name: "GF(5) at order four",
            order: 4,
            material: new PrimeFieldMaterial(field: PrimeField64.Create(modulus: 5UL)),
            blockedIndex: -1
        ) is { } below) { return below; }
        if (ZetaRefusesAt<ulong, PrimeFieldMaterial>(
            name: "GF(3) at order two",
            order: 2,
            material: new PrimeFieldMaterial(field: PrimeField64.Create(modulus: 3UL)),
            blockedIndex: -1
        ) is { } under) { return under; }

        var algebra = PresentedAlgebra<RealQuadratic, RationalMaterial>.Create(presentation: CodiscreteQuiver<RealQuadratic, RationalMaterial>(
            material: default,
            order: 3
        ));
        var free = PresentedAlgebra<RealQuadratic, RationalMaterial>.Create(presentation: Presentations.FreeMonoid<RealQuadratic, RationalMaterial>(
            letterCount: 2,
            material: default
        ));

        // The order is pinned by the algebra's own unit trace, so a quiver on three objects answers at three and at
        // nothing else — which is what stops the recursion from recovering the coefficients of a polynomial the
        // element does not have.
        foreach (var wrongOrder in ((int[])[1, 2])) {
            if ("order" != RefusedParameter(action: () => _ = GraphZeta<RealQuadratic, RationalMaterial>.TryCreate(
                algebra: algebra,
                value: algebra.Identity,
                order: wrongOrder,
                degreeBound: 6,
                zeta: out _,
                obstruction: out _
            ))) {
                return $"a quiver on three objects answered at order {wrongOrder}, where its unit trace is three ones";
            }
        }

        (string Parameter, Action Call)[] refusals = [
            ("algebra", () => _ = GraphZeta<RealQuadratic, RationalMaterial>.TryCreate(
                algebra: null!,
                degreeBound: 6,
                obstruction: out _,
                order: 3,
                value: default,
                zeta: out _
            )),
            ("algebra", () => _ = GraphZeta<RealQuadratic, RationalMaterial>.TryCreate(
                algebra: free,
                value: free.Identity,
                order: 1,
                degreeBound: 6,
                zeta: out _,
                obstruction: out _
            )),
            ("value", () => _ = GraphZeta<RealQuadratic, RationalMaterial>.TryCreate(
                algebra: algebra,
                value: free.Identity,
                order: 3,
                degreeBound: 6,
                zeta: out _,
                obstruction: out _
            )),
            ("order", () => _ = GraphZeta<RealQuadratic, RationalMaterial>.TryCreate(
                algebra: algebra,
                value: algebra.Identity,
                order: 0,
                degreeBound: 6,
                zeta: out _,
                obstruction: out _
            )),
            ("order", () => _ = GraphZeta<RealQuadratic, RationalMaterial>.TryCreate(
                algebra: algebra,
                value: algebra.Identity,
                order: 10,
                degreeBound: 6,
                zeta: out _,
                obstruction: out _
            )),
            ("degreeBound", () => _ = GraphZeta<RealQuadratic, RationalMaterial>.TryCreate(
                algebra: algebra,
                value: algebra.Identity,
                order: 3,
                degreeBound: -1,
                zeta: out _,
                obstruction: out _
            )),
            ("degreeBound", () => _ = GraphZeta<RealQuadratic, RationalMaterial>.TryCreate(
                algebra: algebra,
                value: algebra.Identity,
                order: 3,
                degreeBound: 512,
                zeta: out _,
                obstruction: out _
            )),
        ];

        foreach (var (parameter, call) in refusals) {
            if (parameter != RefusedParameter(action: call)) { return $"a call this readout cannot answer was admitted, or refused without naming {parameter}"; }
        }

        if (!GraphZeta<RealQuadratic, RationalMaterial>.TryCreate(
            algebra: algebra,
            value: algebra.Identity,
            order: 3,
            degreeBound: 6,
            zeta: out var built,
            obstruction: out _
        )) {
            return "the unit of a quiver on three objects was refused over the rationals";
        }

        (string Parameter, Action Call)[] readouts = [
            ("degree", () => _ = built!.Coefficient(degree: -1)),
            ("degree", () => _ = built!.Coefficient(degree: 4)),
            ("length", () => _ = built!.PowerSum(length: -1)),
            ("length", () => _ = built!.PowerSum(length: 4)),
        ];

        foreach (var (parameter, call) in readouts) {
            if (parameter != RefusedParameter(action: call)) { return $"a readout outside the order was answered, or refused without naming {parameter}"; }
        }

        return null;
    }

    // The digraphs every zeta claim runs on. The named shapes are the cases the coefficients care about — a cycle,
    // whose polynomial is 1 ± t^n; an acyclic matrix, which is nilpotent, so its polynomial and its zeta are both the
    // unit; a complete digraph, where only one coefficient survives; and matrices carrying negative entries — and the
    // drawn ones make the family wide as well as pointed. Six is the top order because the minor oracle enumerates
    // every principal minor and expands each by permutations.
    private static (string Name, int Order, BigInteger[] Entries)[] ZetaDigraphs() {
        List<(string Name, int Order, BigInteger[] Entries)> digraphs = [
            ("empty(1)", 1, [0]),
            ("loop(1)", 1, [1]),
            ("weighted(1)", 1, [3]),
            ("cycle(2)", 2, [0, 1, 1, 0]),
            ("acyclic(2)", 2, [0, 1, 0, 0]),
            ("complete(2)", 2, [1, 1, 1, 1]),
            ("signed(2)", 2, [2, -1, -3, 1]),
            ("cycle(3)", 3, [0, 1, 0, 0, 0, 1, 1, 0, 0]),
            ("acyclic(3)", 3, [0, 1, 1, 0, 0, 1, 0, 0, 0]),
            ("complete(3)", 3, [1, 1, 1, 1, 1, 1, 1, 1, 1]),
            ("cycle(4)", 4, [0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 1, 0, 0, 0]),
            ("acyclic(4)", 4, [0, 1, 1, 1, 0, 0, 1, 1, 0, 0, 0, 1, 0, 0, 0, 0]),
            ("two-loops(4)", 4, [0, 1, 0, 0, 1, 0, 0, 0, 0, 0, 0, 1, 0, 0, 1, 0]),
            ("cycle(5)", 5, [0, 1, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 1, 1, 0, 0, 0, 0]),
            ("star(5)", 5, [0, 1, 1, 1, 1, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0]),
            ("cycle(6)", 6, [0, 1, 0, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0, 1, 1, 0, 0, 0, 0, 0]),
            ("two-triangles(6)", 6, [0, 1, 0, 0, 0, 0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0, 1, 0, 0, 0, 1, 0, 0]),
        ];

        var rng = Pcg32XshRr.Create(
            state: 0x5E7AUL,
            stream: 17UL
        );

        for (var order = 1; (order <= 6); ++order) {
            for (var draw = 0; (draw < 3); ++draw) {
                var entries = new BigInteger[(order * order)];

                for (var index = 0; (index < entries.Length); ++index) {
                    entries[index] = (((long)rng.NextUInt32(
                    maximum: 6U,
                    minimum: 0U
                )) - 3L);
                }

                digraphs.Add(item: ($"draw({order}.{draw})", order, entries));
            }
        }

        return [.. digraphs];
    }
    // One digraph's coefficients at one material, against the minor oracle carried into that material by the same map
    // the matrix went in through.
    private static string? ZetaCoefficientsHold<TValue, TOps>(string name, int order, BigInteger[] entries, TOps material, Func<BigInteger, TValue> convert)
        where TOps : struct, IMaterialOps<TValue, TOps> {
        var algebra = PresentedAlgebra<TValue, TOps>.Create(presentation: CodiscreteQuiver<TValue, TOps>(
            material: material,
            order: order
        ));
        var comparer = EqualityComparer<TValue>.Default;
        var degreeBound = ((2 * order) + 2);
        var expected = new BigInteger[(order + 1)];

        if (!GraphZeta<TValue, TOps>.TryCreate(
            algebra: algebra,
            value: ZetaAdjacency<TValue, TOps>(
                algebra: algebra,
                convert: convert,
                entries: entries
            ),
            order: order,
            degreeBound: degreeBound,
            zeta: out var zeta,
            obstruction: out var obstruction
        )) {
            return $"{name}: refused at index {obstruction.BlockedIndex} of {obstruction.Order}, where the material inverts every divisor the recursion needs";
        }

        if (
            (zeta!.Order != order) ||
            (zeta.DegreeBound != degreeBound) ||
            !ReferenceEquals(
            objA: zeta.Algebra,
            objB: algebra
        )
        ) {
            return $"{name}: the readout reports order {zeta.Order} at degree bound {zeta.DegreeBound} over the wrong algebra";
        }

        Oracles.CharacteristicPolynomial(
            matrix: entries,
            order: order,
            result: expected
        );

        for (var degree = 0; (degree <= order); ++degree) {
            if (!comparer.Equals(
                x: zeta.Coefficient(degree: degree),
                y: convert(expected[degree])
            )) {
                return $"{name}: the coefficient at degree {degree} is not the signed sum of the principal minors of that size, which is {expected[degree]}";
            }
        }

        return null;
    }
    // The adjacency element of one digraph: the matrix entered key by key, since a quiver key IS the ordered pair.
    private static PresentedAlgebra<TValue, TOps>.Element ZetaAdjacency<TValue, TOps>(PresentedAlgebra<TValue, TOps> algebra, BigInteger[] entries, Func<BigInteger, TValue> convert)
        where TOps : struct, IMaterialOps<TValue, TOps> {
        var coefficients = new TValue[entries.Length];
        var keys = new long[entries.Length];

        for (var index = 0; (index < entries.Length); ++index) {
            coefficients[index] = convert(entries[index]);
            keys[index] = index;
        }

        return algebra.FromSupport(
            coefficients: coefficients,
            keys: keys
        );
    }
    // One material's verdict on the trace recursion, measured on the unit of a codiscrete quiver so the element itself
    // is never in question: a blocked index of minus one is the claim that the material carries the whole recursion.
    private static string? ZetaRefusesAt<TValue, TOps>(string name, int order, TOps material, int blockedIndex)
        where TOps : struct, IMaterialOps<TValue, TOps> {
        var algebra = PresentedAlgebra<TValue, TOps>.Create(presentation: CodiscreteQuiver<TValue, TOps>(
            material: material,
            order: order
        ));
        var admitted = GraphZeta<TValue, TOps>.TryCreate(
            algebra: algebra,
            value: algebra.Identity,
            order: order,
            degreeBound: (order + 2),
            zeta: out _,
            obstruction: out var obstruction
        );

        if (admitted != (blockedIndex < 0)) {
            return $"{name}: the recursion was {(admitted
                ? "admitted"
                : "refused")}, where index {blockedIndex} says otherwise";
        }

        if (obstruction.BlockedIndex != blockedIndex) { return $"{name}: the refusal names index {obstruction.BlockedIndex}, not {blockedIndex}"; }
        if (obstruction.Order != order) { return $"{name}: the refusal reports order {obstruction.Order}, not {order}"; }

        return null;
    }
    // The two carriers the zeta's field lane runs on, as maps from the oracle's exact integers.
    private static RealQuadratic ZetaRational(BigInteger value) =>
        RealQuadratic.Rational(value: value);
    private static ulong ZetaResidue(BigInteger value) =>
        ((ulong)(((value % ZetaModulus) + ZetaModulus) % ZetaModulus));

    // ---- phase 4: the second product ----

    // The three letter products the claims run on. `max` is commutative and associative, `left projection` is
    // associative and NOT commutative, and NAND is commutative and NOT associative — which is how the certificate is
    // shown to COMPUTE those flags rather than to read them off the entry.
    private static readonly int[] ShuffleLeftProduct = [0, 0, 1, 1];
    private static readonly int[] ShuffleMaxProduct = [0, 1, 1, 1];
    private static readonly int[] ShuffleNandProduct = [1, 1, 1, 0];
    // The multi-term canary's floors. MEASURED on the emitted cells: 14 of 225 cells of the two-letter shuffle at window
    // three carry more than one term and 14 carry a charge above one; 56 of 961 and 72 at window four; 25 of 49 and 65
    // at the one-letter quasi-shuffle at window six; 88 of 225 and 134 at two letters and window three; 500 of 961 and
    // 1079 at window four. Each floor is two thirds of its own measurement — a third below it — so a product that
    // degenerated to concatenation, one term at charge one everywhere, fails every row.
    private static readonly (int Letters, int Window, int[] Product, int MultiTerm, int Scaled)[] ShuffleMultiTermFloors = [
        (2, 3, [], 9, 9),
        (2, 4, [], 37, 48),
        (1, 6, [0], 16, 43),
        (2, 3, ShuffleMaxProduct, 58, 89),
        (2, 4, ShuffleMaxProduct, 333, 719),
    ];

    /// <summary>Proves every cell of the second product is the charged sum of the distinct interleavings of its two
    /// words, at both products and over two materials.</summary>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    /// <remarks>The oracle generates every step-kind sequence of every block count and TESTS it against the two words,
    /// where the entry reads three shorter cells, so the two share no construction. The truncation is the claim's own:
    /// the enumeration is untruncated and the words past the window are dropped here.</remarks>
    public static string? ShuffleMatchesEnumeration() =>
        (ShuffleCellsMatchEnumeration<BigInteger, IntegerMaterial>(
            convert: static count => count,
            letterCount: 2,
            letterProduct: [],
            material: default,
            windowDegree: 4
        )
            ?? (ShuffleCellsMatchEnumeration<BigInteger, IntegerMaterial>(
            convert: static count => count,
            letterCount: 3,
            letterProduct: [],
            material: default,
            windowDegree: 3
        )
            ?? (ShuffleCellsMatchEnumeration<BigInteger, IntegerMaterial>(
            convert: static count => count,
            letterCount: 2,
            letterProduct: ShuffleMaxProduct,
            material: default,
            windowDegree: 3
        )
            ?? (ShuffleCellsMatchEnumeration<BigInteger, IntegerMaterial>(
            convert: static count => count,
            letterCount: 2,
            letterProduct: ShuffleLeftProduct,
            material: default,
            windowDegree: 3
        )
            ?? (ShuffleCellsMatchEnumeration<BigInteger, IntegerMaterial>(
            convert: static count => count,
            letterCount: 1,
            letterProduct: [0],
            material: default,
            windowDegree: 6
        )
            ?? (ShuffleCellsMatchEnumeration<RealQuadratic, RationalMaterial>(
            letterCount: 2,
            windowDegree: 3,
            letterProduct: [],
            material: default,
            convert: static count => RealQuadratic.Rational(value: count)
        )
            ?? (ShuffleCellsMatchEnumeration<BigInteger, CountingMaterial>(
            convert: static count => count,
            letterCount: 2,
            letterProduct: ShuffleMaxProduct,
            material: default,
            windowDegree: 3
        )
            ?? ShuffleCertifies())))))));
    /// <summary>Proves the smallest instance of the second product — the seven words of two letters at a window of
    /// two — composes exactly as the interleaving enumeration says, and that its unit fixes every one of them.</summary>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? ShuffleComposesAtSmokeWindow() =>
        (ShuffleCellsMatchEnumeration<BigInteger, IntegerMaterial>(
            convert: static count => count,
            letterCount: 2,
            letterProduct: [],
            material: default,
            windowDegree: 2
        )
            ?? ShuffleCellsMatchEnumeration<BigInteger, IntegerMaterial>(
            convert: static count => count,
            letterCount: 1,
            letterProduct: [0],
            material: default,
            windowDegree: 3
        ));
    /// <summary>Proves the one-letter shuffle's structure constants ARE the binomial coefficients, read two ways: as the
    /// multiplicity one letter's own shuffle carries, and as the number of terms the shuffle of two DIFFERENT letters
    /// splits into.</summary>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    /// <remarks>Pascal's triangle is built by addition alone, so it reaches the same numbers without a factorial, a
    /// product or a division anywhere.</remarks>
    public static string? ShuffleMatchesBinomials() {
        const int Window = 10;

        var pascal = Oracles.PascalTriangle(rows: (2 * Window));
        var compiled = PresentedAlgebra<BigInteger, IntegerMaterial>.Create(presentation: Presentations.Shuffle<BigInteger, IntegerMaterial>(
            letterCount: 1,
            windowDegree: Window,
            material: default
        )).Compile();

        for (var left = 0; (left <= Window); ++left) {
            for (var right = 0; (right <= Window); ++right) {
                var entries = compiled.TargetCount(
                    leftKey: left,
                    rightKey: right
                );

                if ((left + right) > Window) {
                    if (0 != entries) { return $"a^{left} shuffled with a^{right} carries {entries} term(s) past the window, where every interleaving of them is too long to hold"; }

                    continue;
                }

                if (
                    (1 != entries) ||
                    ((left + right) != compiled.Target(
                    leftKey: left,
                    rightKey: right
                ))
                ) {
                    return $"a^{left} shuffled with a^{right} carries {entries} term(s), where one letter admits the single word a^{(left + right)}";
                }

                if (compiled.Charge(
                    leftKey: left,
                    rightKey: right
                ) != pascal[(left + right)][left]) {
                    return $"a^{left} shuffled with a^{right} carries the multiplicity {compiled.Charge(
                        leftKey: left,
                        rightKey: right
                    )}, where Pascal's triangle gives C({(left + right)}, {left}) = {pascal[(left + right)][left]}";
                }
            }
        }

        // The same binomial as a TERM count: two different letters interleave into that many distinct words, each
        // reached exactly once, so the coefficient and the support size are the same number read at two instances.
        const int Pair = 6;

        var words = ShuffleWords(
            letterCount: 2,
            windowDegree: Pair
        );
        var pairCompiled = PresentedAlgebra<BigInteger, IntegerMaterial>.Create(presentation: Presentations.Shuffle<BigInteger, IntegerMaterial>(
            letterCount: 2,
            windowDegree: Pair,
            material: default
        )).Compile();

        for (var lefts = 0; (lefts <= Pair); ++lefts) {
            for (var rights = 0; ((lefts + rights) <= Pair); ++rights) {
                var left = ShuffleSymbolOf(
                    words: words,
                    word: [.. Enumerable.Repeat(
                            count: lefts,
                            element: 0
                        )]
                );
                var right = ShuffleSymbolOf(
                    words: words,
                    word: [.. Enumerable.Repeat(
                            count: rights,
                            element: 1
                        )]
                );
                var entries = pairCompiled.TargetCount(
                    leftKey: left,
                    rightKey: right
                );

                if (entries != pascal[(lefts + rights)][lefts]) {
                    return $"a^{lefts} shuffled with b^{rights} splits into {entries} word(s), where Pascal's triangle gives C({(lefts + rights)}, {lefts})";
                }

                for (var index = 0; (index < entries); ++index) {
                    if (!pairCompiled.Charge(
                        index: index,
                        leftKey: left,
                        rightKey: right
                    ).IsOne) {
                        return $"a^{lefts} shuffled with b^{rights} reaches one of its words more than once, where two different letters interleave into distinct words";
                    }
                }
            }
        }

        return null;
    }
    /// <summary>Proves the empty letter product IS the shuffle: the entry's default argument builds the same
    /// presentation, no collision term leaks into it, and a non-empty letter product adds exactly the shortened terms
    /// while leaving the shuffle's own cell untouched.</summary>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? QuasiShuffleDegeneratesToShuffle() {
        foreach (var (letters, window) in (((int Letters, int Window)[])[(2, 3), (2, 4), (1, 6), (3, 3)])) {
            var product = new int[(letters * letters)];

            for (var left = 0; (left < letters); ++left) {
                for (var right = 0; (right < letters); ++right) {
                    product[((left * letters) + right)] = Math.Max(
                    val1: left,
                    val2: right
                );
                }
            }

            var words = ShuffleWords(
                letterCount: letters,
                windowDegree: window
            );
            var declared = PresentedAlgebra<BigInteger, IntegerMaterial>.Create(presentation: Presentations.Shuffle<BigInteger, IntegerMaterial>(
                letterCount: letters,
                letterProduct: [],
                material: default,
                windowDegree: window
            )).Compile();
            var plain = PresentedAlgebra<BigInteger, IntegerMaterial>.Create(presentation: Presentations.Shuffle<BigInteger, IntegerMaterial>(
                letterCount: letters,
                windowDegree: window,
                material: default
            )).Compile();
            var quasi = PresentedAlgebra<BigInteger, IntegerMaterial>.Create(presentation: Presentations.Shuffle<BigInteger, IntegerMaterial>(
                letterCount: letters,
                letterProduct: product,
                material: default,
                windowDegree: window
            )).Compile();
            var richer = 0;

            for (var left = 0; (left < words.Length); ++left) {
                for (var right = 0; (right < words.Length); ++right) {
                    var shuffleTerms = plain.TargetCount(
                        leftKey: left,
                        rightKey: right
                    );
                    var top = (words[left].Length + words[right].Length);

                    if (declared.TargetCount(
                        leftKey: left,
                        rightKey: right
                    ) != shuffleTerms) {
                        return $"shuffle({letters},{window}): the default letter product and the empty one disagree at the ordered pair ({left}, {right})";
                    }

                    // Every interleaving of two words that never collides is their combined length, so a shuffle cell
                    // lands on the top length alone; a collision shortens, so the quasi-shuffle's own top-length terms
                    // must BE the shuffle's cell, charge for charge.
                    var matched = 0;

                    for (var index = 0; (index < quasi.TargetCount(
                        leftKey: left,
                        rightKey: right
                    )); ++index) {
                        var target = quasi.Target(
                            index: index,
                            leftKey: left,
                            rightKey: right
                        );

                        if (words[((int)target)].Length != top) { continue; }

                        ++matched;

                        var found = -1;

                        for (var probe = 0; (probe < shuffleTerms); ++probe) {
                            if (plain.Target(
                                index: probe,
                                leftKey: left,
                                rightKey: right
                            ) == target) { found = probe; }
                        }

                        if (
                            (found < 0) ||
                            (plain.Charge(
                            index: found,
                            leftKey: left,
                            rightKey: right
                        ) != quasi.Charge(
                            index: index,
                            leftKey: left,
                            rightKey: right
                        ))
                        ) {
                            return $"quasi({letters},{window}): the top-length term {target} of the ordered pair ({left}, {right}) is not the shuffle's own";
                        }
                    }

                    if (matched != shuffleTerms) {
                        return $"quasi({letters},{window}): the ordered pair ({left}, {right}) carries {matched} top-length term(s) against the shuffle's {shuffleTerms}";
                    }

                    if (quasi.TargetCount(
                        leftKey: left,
                        rightKey: right
                    ) > shuffleTerms) { ++richer; }
                }
            }

            // The collision term is load-bearing rather than decorative: it must actually add terms, on most of the
            // table rather than nowhere.
            if (richer < ((words.Length * words.Length) / 2)) {
                return $"quasi({letters},{window}): only {richer} of {(words.Length * words.Length)} ordered pairs carry a term the shuffle does not, so the collision term is barely present";
            }
        }

        return null;
    }
    /// <summary>Proves the prefix-sum identity the collision term satisfies: the depth-<c>n</c> iterated prefix sums of
    /// the constant sequence multiply pointwise exactly as the quasi-shuffle's structure constants say, and the shuffle
    /// — the same entry without the collision term — does not.</summary>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    /// <remarks>
    /// <para>
    /// A word of length <c>n</c> over one letter names the iterated sum <c>Σ N ≥ i₁ &gt; … &gt; i_n ≥ 1</c>, and
    /// multiplying two such sums merges their index sets: the terms where no index coincides are the interleavings, and
    /// the terms where indices DO coincide are exactly the collisions. So the identity holds for the quasi-shuffle and
    /// fails for the shuffle, which is what makes the letter product load-bearing here.
    /// </para>
    /// <para>
    /// The sequences come from <see cref="FiniteCalculus{TValue, TOps}.TryAntidifference"/> on
    /// <see cref="Presentations.Shift"/> — a different presentation, a different product and a different key scheme —
    /// and are themselves pinned against Pascal's triangle first, so neither side is taken on trust.
    /// </para>
    /// </remarks>
    public static string? QuasiShuffleMatchesPrefixSums() {
        const int Bound = 14;
        const int Window = 10;

        var calculus = FiniteCalculus<BigInteger, IntegerMaterial>.Create(
            degreeBound: Bound,
            material: default
        );
        var jets = calculus.Algebra;
        var pascal = Oracles.PascalTriangle(rows: Bound);
        var ones = new BigInteger[(Bound + 1)];

        Array.Fill(
            array: ones,
            value: BigInteger.One
        );

        if (!calculus.TryAntidifference(
            antidifference: out var prefix,
            obstruction: out var obstruction
        )) {
            return $"the antidifference of a jet ring of {(Bound + 1)} places was refused after {obstruction.StepsTaken} step(s)";
        }

        var harmonic = new PresentedAlgebra<BigInteger, IntegerMaterial>.Element[(Window + 1)];

        harmonic[0] = calculus.Sequence(values: ones);

        for (var depth = 1; (depth <= Window); ++depth) {
            harmonic[depth] = jets.Multiply(
                left: prefix,
                right: jets.Multiply(
                    left: calculus.Shift,
                    right: harmonic[(depth - 1)]
                )
            );
        }

        for (var depth = 0; (depth <= Window); ++depth) {
            for (var place = 0; (place <= Bound); ++place) {
                var expected = ((depth <= place)
                    ? pascal[place][depth]
                    : BigInteger.Zero
                );

                if (harmonic[depth][key: place] != expected) {
                    return $"the depth-{depth} prefix sum reads {harmonic[depth][key: place]} at place {place}, where the iterated sum is C({place}, {depth}) = {expected}";
                }
            }
        }

        foreach (var (name, product, minimumDifferences) in (((string Name, int[] Product, int Minimum)[])[("the quasi-shuffle", [0], 0), ("the shuffle", [], 293)])) {
            var compiled = PresentedAlgebra<BigInteger, IntegerMaterial>.Create(presentation: Presentations.Shuffle<BigInteger, IntegerMaterial>(
                letterCount: 1,
                letterProduct: product,
                material: default,
                windowDegree: Window
            )).Compile();
            var differences = 0;

            for (var left = 0; (left <= Window); ++left) {
                for (var right = 0; ((left + right) <= Window); ++right) {
                    for (var place = 0; (place <= Bound); ++place) {
                        var merged = BigInteger.Zero;

                        for (var index = 0; (index < compiled.TargetCount(
                            leftKey: left,
                            rightKey: right
                        )); ++index) {
                            merged += (compiled.Charge(
                                index: index,
                                leftKey: left,
                                rightKey: right
                            ) * harmonic[((int)compiled.Target(
                                index: index,
                                leftKey: left,
                                rightKey: right
                            ))][key: place]);
                        }

                        if (merged != (harmonic[left][key: place] * harmonic[right][key: place])) { ++differences; }
                    }
                }
            }

            if (
                (0 == minimumDifferences) &&
                (0 != differences)
            ) {
                return $"{name}: {differences} product(s) of two iterated sums are not the merged sum its structure constants give";
            }

            if (differences < minimumDifferences) {
                return $"{name}: only {differences} product(s) of two iterated sums differ from the merged sum, under the measured floor of {minimumDifferences}, so the collision term is not what carries the identity";
            }
        }

        return null;
    }
    /// <summary>Proves the second product refuses the arguments that name more words than a finite basis holds, and the
    /// collision that names a letter the alphabet does not carry — the latter with the ordered pair that blocked.</summary>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? ShuffleLimitsRefuse() {
        (string Name, Action Build)[] refusals = [
            ("two letters at a window of nine, which names 1023 words", static () => _ = Presentations.Shuffle<BigInteger, IntegerMaterial>(
                letterCount: 2,
                windowDegree: 9,
                material: default
            )),
            ("three letters at a window of six, which names 1093 words", static () => _ = Presentations.Shuffle<BigInteger, IntegerMaterial>(
                letterCount: 3,
                windowDegree: 6,
                material: default
            )),
            ("512 letters at a window of one, which names 513 words", static () => _ = Presentations.Shuffle<BigInteger, IntegerMaterial>(
                letterCount: 512,
                windowDegree: 1,
                material: default
            )),
            ("an alphabet past the normal-form cap", static () => _ = Presentations.Shuffle<BigInteger, IntegerMaterial>(
                letterCount: 513,
                windowDegree: 0,
                material: default
            )),
            ("a negative alphabet", static () => _ = Presentations.Shuffle<BigInteger, IntegerMaterial>(
                letterCount: -1,
                windowDegree: 2,
                material: default
            )),
            ("a negative window", static () => _ = Presentations.Shuffle<BigInteger, IntegerMaterial>(
                letterCount: 2,
                windowDegree: -1,
                material: default
            )),
            ("a window past the normal-form cap", static () => _ = Presentations.Shuffle<BigInteger, IntegerMaterial>(
                letterCount: 1,
                windowDegree: 512,
                material: default
            )),
            ("a letter product that is not one entry per ordered pair", static () => _ = Presentations.Shuffle<BigInteger, IntegerMaterial>(
                letterCount: 2,
                letterProduct: [0, 1, 1],
                material: default,
                windowDegree: 3
            )),
            ("a collision leaving the alphabet", static () => _ = Presentations.Shuffle<BigInteger, IntegerMaterial>(
                letterCount: 2,
                letterProduct: [0, 1, 1, 2],
                material: default,
                windowDegree: 3
            )),
            ("a collision below the alphabet", static () => _ = Presentations.Shuffle<BigInteger, IntegerMaterial>(
                letterCount: 2,
                letterProduct: [-1, 1, 1, 0],
                material: default,
                windowDegree: 3
            )),
        ];

        foreach (var (name, build) in refusals) {
            if (RefusesDeclaration(
                build: build,
                name: name
            ) is { } refusal) { return refusal; }
        }

        // The collision refusal names the pair that blocked, which is the whole content of the row: an alphabet holding
        // no such letter is not a budget, and the caller is told which entry of its own table is the offending one.
        try {
            _ = Presentations.Shuffle<BigInteger, IntegerMaterial>(
                letterCount: 3,
                letterProduct: [0, 1, 2, 1, 1, 2, 2, 2, 7],
                material: default,
                windowDegree: 2
            );

            return "a collision naming the letter seven of a three-letter alphabet was admitted";
        } catch (ArgumentException blocked) {
            if (
                !blocked.Message.Contains(
                comparisonType: StringComparison.Ordinal,
                value: "(2, 2)"
            ) ||
                !blocked.Message.Contains(
                comparisonType: StringComparison.Ordinal,
                value: "7"
            )
            ) {
                return $"the collision refusal does not name the ordered pair that blocked: {blocked.Message}";
            }
        }

        // The cap is derived, so an admitted argument tuple carries exactly the words the closed form counts. The three
        // NEAR-cap tuples this row used to carry — (1, 511), (2, 8) and (511, 1), at 512, 511 and 512 words — are
        // asserted by presented.shuffle-near-cap-basis, together with every tuple below them, and are not restated
        // here: this row reads HasCompiledNormalFormBasis, which emits one rule per ordered pair of words, so three of
        // them would cost roughly 785,000 rules to make a statement that is already gated. That case reads
        // HasFiniteNormalForms instead, which needs no composition table and so can afford the near-cap widths.
        foreach (var (letters, window, expected) in (((int Letters, int Window, int Words)[])[
            (0, 4, 1), (1, 0, 1), (2, 1, 3), (2, 4, 31), (3, 4, 121), (4, 3, 85),
        ])) {
            var presentation = Presentations.Shuffle<BigInteger, IntegerMaterial>(
                letterCount: letters,
                windowDegree: window,
                material: default
            );

            if (
                !presentation.HasCompiledNormalFormBasis ||
                (presentation.NormalFormCount != expected) ||
                (presentation.GeneratorCount != expected)
            ) {
                return $"shuffle({letters},{window}): {presentation.GeneratorCount} generator(s) and {presentation.NormalFormCount} normal form(s), where the words of length at most {window} number {expected}";
            }
        }

        return null;
    }
    /// <summary>The multi-term canary: the interleaving really splits a product into several words, and really carries
    /// the multiplicity each is reached with, on more cells than the measured floor.</summary>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    /// <remarks>Without it a second product that quietly degenerated to concatenation — one term per cell at charge
    /// one — would satisfy the commutativity, associativity and unitality flags, the degeneracy claim and every refusal
    /// case, because all of those hold just as well of concatenation.</remarks>
    public static string? ShuffleMultiTermCanary() {
        foreach (var (letters, window, product, multiTermFloor, scaledFloor) in ShuffleMultiTermFloors) {
            var compiled = PresentedAlgebra<BigInteger, IntegerMaterial>.Create(presentation: Presentations.Shuffle<BigInteger, IntegerMaterial>(
                letterCount: letters,
                letterProduct: product,
                material: default,
                windowDegree: window
            )).Compile();
            var multiTerm = 0;
            var scaled = 0;

            for (var left = 0; (left < compiled.KeyCount); ++left) {
                for (var right = 0; (right < compiled.KeyCount); ++right) {
                    var entries = compiled.TargetCount(
                        leftKey: left,
                        rightKey: right
                    );

                    if (entries > 1) { ++multiTerm; }

                    for (var index = 0; (index < entries); ++index) {
                        if (compiled.Charge(
                            index: index,
                            leftKey: left,
                            rightKey: right
                        ) > BigInteger.One) { ++scaled; }
                    }
                }
            }

            if (
                (multiTerm < multiTermFloor) ||
                (scaled < scaledFloor)
            ) {
                return $"shuffle({letters},{window},[{string.Join(
                    separator: "",
                    values: product
                )}]): {multiTerm} cell(s) carry more than one term and {scaled} carry a multiplicity above one, against the floors {multiTermFloor} and {scaledFloor}";
            }
        }

        return null;
    }

    // The certificate of the second product, which is COMPUTED rather than declared: both products are commutative,
    // associative and unital at a letter product that is itself both, and the flags follow the letter product where it
    // is not — a projection is not commutative and NAND is not associative, and the certificate says so.
    private static string? ShuffleCertifies() {
        (string Name, int Letters, int Window, int[] Product, bool Commutative, bool Associative)[] cases = [
            ("the shuffle", 2, 4, [], true, true),
            ("the quasi-shuffle at the larger letter", 2, 4, ShuffleMaxProduct, true, true),
            ("the quasi-shuffle at one letter", 1, 8, [0], true, true),
            ("the quasi-shuffle at a projection", 2, 3, ShuffleLeftProduct, false, true),
            ("the quasi-shuffle at a non-associative collision", 2, 3, ShuffleNandProduct, true, false),
        ];

        foreach (var (name, letters, window, product, commutative, associative) in cases) {
            var certificate = PresentedAlgebra<BigInteger, IntegerMaterial>
                .Create(presentation: Presentations.Shuffle<BigInteger, IntegerMaterial>(
                letterCount: letters,
                letterProduct: product,
                material: default,
                windowDegree: window
            ))
                .Certify(overlapLimit: (1L << 20));

            if (
                !certificate.HasIdentity ||
                (certificate.IsCommutative != commutative) ||
                (certificate.IsAssociative != associative)
            ) {
                return $"{name}({letters},{window}): commutative={certificate.IsCommutative} associative={certificate.IsAssociative} unital={certificate.HasIdentity}, where the letter product gives {commutative} and {associative}";
            }
        }

        return null;
    }
    // One instance's whole compiled table against the brute interleaving enumeration, plus the basis it is keyed by and
    // the unit that fixes it.
    private static string? ShuffleCellsMatchEnumeration<TValue, TOps>(int letterCount, int windowDegree, int[] letterProduct, TOps material, Func<BigInteger, TValue> convert)
        where TOps : struct, IMaterialOps<TValue, TOps> {
        var name = $"shuffle({letterCount},{windowDegree},[{string.Join(
            separator: "",
            values: letterProduct
        )}])";
        var words = ShuffleWords(
            letterCount: letterCount,
            windowDegree: windowDegree
        );
        var presentation = Presentations.Shuffle<TValue, TOps>(
            letterCount: letterCount,
            letterProduct: letterProduct,
            material: material,
            windowDegree: windowDegree
        );
        var algebra = PresentedAlgebra<TValue, TOps>.Create(presentation: presentation);
        var comparer = EqualityComparer<TValue>.Default;
        var compiled = algebra.Compile();

        if (
            !presentation.HasCompiledNormalFormBasis ||
            (presentation.NormalFormCount != words.Length)
        ) {
            return $"{name}: {presentation.NormalFormCount} normal form(s), where the words of length at most {windowDegree} number {words.Length}";
        }

        // The generators ARE the normal forms, so a key is the one-letter word naming its own word.
        for (var key = 0; (key < words.Length); ++key) {
            var normal = presentation.NormalFormWord(key: key);

            if (
                (1 != normal.Length) ||
                (key != normal[0])
            ) { return $"{name}: the normal form at key {key} is not the one-letter word naming its own word"; }
        }

        for (var left = 0; (left < words.Length); ++left) {
            for (var right = 0; (right < words.Length); ++right) {
                var entries = compiled.TargetCount(
                    leftKey: left,
                    rightKey: right
                );
                var expected = Oracles.Interleavings(
                    left: words[left],
                    right: words[right],
                    letterProduct: letterProduct,
                    letterCount: letterCount
                )
                    .Where(predicate: entry => (entry.Word.Length <= windowDegree))
                    .ToArray();

                if (entries != expected.Length) {
                    return $"{name}: the ordered pair ({left}, {right}) carries {entries} term(s), where the enumeration finds {expected.Length} interleaving(s) the window holds";
                }

                // Both sides are in the canonical order — the enumeration by construction, the cell by ascending key —
                // so they are compared term by term rather than searched.
                for (var index = 0; (index < entries); ++index) {
                    var symbol = ShuffleSymbolOf(
                        words: words,
                        word: expected[index].Word
                    );

                    if (compiled.Target(
                        index: index,
                        leftKey: left,
                        rightKey: right
                    ) != symbol) {
                        return $"{name}: term {index} of the ordered pair ({left}, {right}) lands on key {compiled.Target(
                            index: index,
                            leftKey: left,
                            rightKey: right
                        )}, where the enumeration's word is key {symbol}";
                    }

                    if (!comparer.Equals(
                        x: compiled.Charge(
                            index: index,
                            leftKey: left,
                            rightKey: right
                        ),
                        y: convert(expected[index].Multiplicity)
                    )) {
                        return $"{name}: term {index} of the ordered pair ({left}, {right}) carries a charge the enumeration counts as {expected[index].Multiplicity}";
                    }
                }
            }
        }

        // The unit is the empty word, and it is the empty word alone.
        var identity = algebra.Identity;

        if (
            (1 != identity.SupportCount) ||
            (0L != identity.Keys[0]) ||
            !comparer.Equals(
            x: identity.Coefficients[0],
            y: material.One
        )
        ) {
            return $"{name}: the unit carries {identity.SupportCount} term(s), where the empty word alone is this presentation's unit";
        }

        for (var key = 0; (key < words.Length); ++key) {
            var basis = algebra.Generator(symbol: key);

            if (
                !algebra.AreEqual(
                left: algebra.Multiply(
                    left: identity,
                    right: basis
                ),
                right: basis
            ) ||
                !algebra.AreEqual(
                left: algebra.Multiply(
                    left: basis,
                    right: identity
                ),
                right: basis
            )
            ) {
                return $"{name}: the empty word does not fix the word at key {key} from both sides";
            }
        }

        return null;
    }
    // The words of one alphabet at one length window, in the canonical order the keys follow: every word of one length
    // extended by every letter, which is length-major and lexicographic within a length.
    private static int[][] ShuffleWords(int letterCount, int windowDegree) {
        var words = new List<int[]> { Array.Empty<int>() };
        var cursor = 0;

        for (var length = 1; (length <= windowDegree); ++length) {
            var reached = words.Count;

            for (; (cursor < reached); ++cursor) {
                for (var letter = 0; (letter < letterCount); ++letter) { words.Add(item: [.. words[cursor], letter]); }
            }
        }

        return [.. words];
    }
    // A word's key, by binary search over the canonical order.
    private static int ShuffleSymbolOf(int[][] words, int[] word) {
        var low = 0;
        var high = words.Length;

        while (low < high) {
            var middle = ((low + high) >> 1);
            var probe = words[middle];
            var order = ((probe.Length != word.Length)
                ? (probe.Length - word.Length)
                : probe.AsSpan().SequenceCompareTo(other: word)
            );

            if (0 == order) { return middle; }

            if (order < 0) { low = (middle + 1); } else { high = middle; }
        }

        throw new InvalidOperationException(message: $"no word [{string.Join(
            separator: ",",
            values: word
        )}] in this basis");
    }

    // ---- phase 4: knot state sums ----

    // The width the state sums run in. A plat of s strands needs the tangle at width s, and the four-strand plat is what
    // carries the trefoils and the figure-eight, so nothing here reaches past the width a fast suite holds.
    private const int KnotWidth = 4;

    // The declared diagrams. Each is a plat-closed braid word: cups below joining adjacent strand pairs, the word's
    // crossings between them, caps above joining the same pairs, and the value read at the empty diagram. `Kinks` counts
    // the first-move curls this diagram carries over the standard one, and `Reduced` is the PUBLISHED reduced bracket of
    // the knot it presents, written as its nonzero terms in the crossing charge. Nothing here is fitted: the two trefoil
    // words differ only in the sign of their crossings, and the exhaustive smoothing enumeration — which reads neither
    // column — is what says the table describes these words.
    private static readonly (string Name, int Strands, int[] Word, int Kinks, (int Exponent, int Coefficient)[] Reduced)[] KnotDiagrams = [
        ("the unknot", 2, [], 0, [(0, 1)]),
        ("the unknot with one kink", 2, [1], -1, [(0, 1)]),
        ("the unknot with the mirror kink", 2, [-1], 1, [(0, 1)]),
        ("the two-component unlink", 4, [], 0, [(2, -1), (-2, -1)]),
        ("the trefoil", 4, [2, 2, 2], 0, [(5, -1), (-3, -1), (-7, 1)]),
        ("the mirror trefoil", 4, [-2, -2, -2], 0, [(-5, -1), (3, -1), (7, 1)]),
        ("the figure-eight", 4, [2, -1, 2, 2], 0, [(8, 1), (4, -1), (0, 1), (-4, -1), (-8, 1)]),
    ];
    // The rational evaluation points (D12): the coefficient ring holds no formal variable, so the bracket is read at
    // enough points of a field material instead. Any nonzero rational serves, since every one of them is a unit.
    private static readonly BigInteger[] KnotRationalPoints = [2, 3, 5];
    // The prime-field points, each with its MEASURED separation floor: of the twenty-one unordered pairs of declared
    // diagrams, 21 separate at GF(101) and at GF(65537) and 18 at GF(13), so each floor is two thirds of its own
    // measurement. A small field loses separations because the values collide there, not because the construction does.
    private static readonly (ulong Modulus, ulong Point, int Floor)[] KnotPrimePoints = [
        (101UL, 2UL, 14),
        (65_537UL, 5UL, 14),
        (13UL, 2UL, 12),
    ];
    // The words the smoothing enumeration runs on, out to the eight crossings two-to-the-crossings can afford. The last
    // two are not tabulated knots — they are words whose brackets nothing but the enumeration predicts, which is what
    // makes the enumeration a check on the subject rather than a second reading of the table.
    private static readonly (int Strands, int[] Word)[] KnotSmoothingWords = [
        (2, []),
        (2, [1]),
        (2, [-1]),
        (2, [1, 1, 1, 1]),
        (4, []),
        (4, [2, 2, 2]),
        (4, [-2, -2, -2]),
        (4, [2, -1, 2, 2]),
        (4, [2, 2, 2, 2, 2]),
        (4, [2, 2, 2, 2, 2, 2]),
        (4, [2, -1, 2, 2, -3, 2, 1]),
        (4, [1, 2, 3, -1, -2, -3, 2, 2]),
    ];
    // The words the move claims perturb, and the separation floors at the rational points: all twenty-one pairs separate
    // at each of them, so the floor is fourteen.
    private static readonly int[][] KnotMoveWords = [[2, 2, 2], [2, -1, 2, 2], [1, -2, 3]];

    private const int KnotRationalFloor = 14;

    /// <summary>Proves the braid relations hold on the crossing images — which the free source never imposed — and that
    /// the loop charge is exactly what makes them hold.</summary>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    /// <remarks>A free monoid carries no word-matched rule, so the morphism admits every assignment by the universal
    /// property and the relations below are a MEASURED property of the images rather than an admission condition. The
    /// second move forces the loop charge, and the last block shows it: at any other charge the crossing and its mirror
    /// no longer compose to the straight-through diagram.</remarks>
    public static string? BraidRelationHolds() {
        foreach (var point in KnotRationalPoints) {
            if (KnotBraidRelations(
                sum: KnotRationalSum(point: point),
                name: $"the rationals at {point}"
            ) is { } detail) { return detail; }
        }

        foreach (var (modulus, point, _) in KnotPrimePoints) {
            if (KnotBraidRelations(
                sum: KnotPrimeSum(
                    modulus: modulus,
                    point: point
                ),
                name: $"GF({modulus}) at {point}"
            ) is { } detail) { return detail; }
        }

        // The universal property, stated rather than assumed: the free source refuses nothing, and the obstruction it
        // hands back reads as "nothing blocked" on every field.
        var free = PresentedAlgebra<RealQuadratic, RationalMaterial>.Create(presentation: Presentations.FreeMonoid<RealQuadratic, RationalMaterial>(
            letterCount: 6,
            material: default
        ));
        var reference = KnotRationalSum(point: 2);
        var images = new PresentedAlgebra<RealQuadratic, RationalMaterial>.Element[6];

        for (var strand = 1; (strand < KnotWidth); ++strand) {
            images[PlatStateSum<RealQuadratic, RationalMaterial>.Letter(crossing: strand)] = reference.Crossing(
                crossing: strand,
                strands: KnotWidth
            );
            images[PlatStateSum<RealQuadratic, RationalMaterial>.Letter(crossing: -strand)] = reference.Crossing(
                crossing: -strand,
                strands: KnotWidth
            );
        }

        if (!PresentedFunctor<RealQuadratic, RationalMaterial>.TryCreate(
            source: free,
            target: reference.Algebra,
            images: images,
            functor: out var functor,
            obstruction: out var obstruction
        )) {
            return "the free monoid on the crossing letters refused an assignment, where a source with no word-matched rule imposes no relation to refuse";
        }

        if (
            (-1 != obstruction.RuleIndex) ||
            (-1L != obstruction.LeftKey) ||
            (-1L != obstruction.RightKey)
        ) {
            return "the admitted crossing assignment carries an obstruction, where nothing blocked it";
        }

        if (
            (6 != functor!.ImageCount) ||
            functor.IsWordMorphism ||
            !ReferenceEquals(
            objA: functor.Source,
            objB: free
        ) ||
            !ReferenceEquals(
            objA: functor.Target,
            objB: reference.Algebra
        )
        ) {
            return $"the crossing morphism reports {functor.ImageCount} image(s) and a word morphism of {functor.IsWordMorphism}, where a two-term smoothing names no word";
        }

        // The loop charge, load-bearing: the second move holds at the charge it forces and at no other.
        foreach (var wrong in ((RealQuadratic[])[RealQuadratic.One, RealQuadratic.Zero, RealQuadratic.Rational(value: -4)])) {
            var broken = new PlatStateSum<RealQuadratic, RationalMaterial>(
                maximumWidth: KnotWidth,
                crossingCharge: RealQuadratic.Rational(value: 2),
                inverseCharge: RealQuadratic.Rational(
                    numerator: BigInteger.One,
                    denominator: 2
                ),
                loopCharge: wrong,
                material: default
            );

            if (broken.Algebra.AreEqual(
                left: broken.Braid(
                    strands: KnotWidth,
                    word: [2, -2]
                ),
                right: broken.Identity(strands: KnotWidth)
            )) {
                return $"the second move holds at a loop charge of {wrong.RationalNumerator}/{wrong.Denominator}, where only the charge the move itself forces admits it";
            }
        }

        return null;
    }
    /// <summary>Proves the state sum of every declared diagram is the published bracket of the knot it presents,
    /// evaluated at several points of two field materials.</summary>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    /// <remarks>The table carries integers and nothing else; the Horner fold turns them into a fraction and the material
    /// divides it, so the same published numbers answer over the rationals and over three prime fields without a ninth
    /// material and without a formal variable anywhere in the presentation.</remarks>
    public static string? StateSumMatchesTabulated() {
        foreach (var point in KnotRationalPoints) {
            var failure = KnotValuesMatchTable(
                sum: KnotRationalSum(point: point),
                name: $"the rationals at {point}",
                point: point,
                scalar: static (numerator, denominator) => RealQuadratic.Rational(
                    denominator: denominator,
                    numerator: numerator
                )
            );

            if (failure is not null) { return failure; }
        }

        foreach (var (modulus, point, _) in KnotPrimePoints) {
            var failure = KnotValuesMatchTable(
                sum: KnotPrimeSum(
                    modulus: modulus,
                    point: point
                ),
                name: $"GF({modulus}) at {point}",
                point: new BigInteger(value: point),
                scalar: (numerator, denominator) => KnotPrimeScalar(
                    denominator: denominator,
                    modulus: modulus,
                    numerator: numerator
                )
            );

            if (failure is not null) { return failure; }
        }

        return null;
    }
    /// <summary>Proves the state sum agrees with an exhaustive enumeration of every smoothing of every crossing, and
    /// that the enumeration in turn reproduces the published table.</summary>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    /// <remarks>Two oracles rather than one, because neither alone catches both failure modes: the published table
    /// catches a wrong construction and shares no step with it, while the enumeration catches a mis-transcribed table
    /// and knows nothing about knots at all. The enumeration builds each state's whole closed diagram as one graph and
    /// counts its components, where the subject composes one planar diagram into another.</remarks>
    public static string? StateSumMatchesSmoothingEnumeration() {
        foreach (var (name, strands, word, kinks, reduced) in KnotDiagrams) {
            var (lowest, coefficients) = KnotPolynomial(terms: reduced);
            var tabulated = Oracles.BracketNormalization(
                coefficients: coefficients,
                kinkExponent: kinks,
                lowest: lowest
            );
            var enumerated = Oracles.BracketStateSum(
                strandCount: strands,
                word: word
            );

            if (
                (tabulated.Lowest != enumerated.Lowest) ||
                !tabulated.Coefficients.SequenceEqual(second: enumerated.Coefficients)
            ) {
                return $"{name}: the smoothing enumeration reaches a bracket the published table does not, so the table does not describe this diagram";
            }
        }

        foreach (var point in ((BigInteger[])[2, 3])) {
            var sum = KnotRationalSum(point: point);

            foreach (var (strands, word) in KnotSmoothingWords) {
                var enumerated = Oracles.BracketStateSum(
                    strandCount: strands,
                    word: word
                );

                var (numerator, denominator) = Oracles.BracketHorner(
                    coefficients: enumerated.Coefficients,
                    lowest: enumerated.Lowest,
                    point: point
                );

                if (!sum.Evaluate(
                    strands: strands,
                    word: word
                ).Equals(other: RealQuadratic.Rational(
                    denominator: denominator,
                    numerator: numerator
                ))) {
                    return $"the plat of [{string.Join(
                        separator: ",",
                        values: word
                    )}] on {strands} strands reads a value the smoothing enumeration does not, at the point {point}";
                }
            }
        }

        return null;
    }
    /// <summary>Proves the declared moves act on the value exactly as they must: the second and third leave it fixed,
    /// and the first multiplies it by the kink factor and by nothing else.</summary>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    /// <remarks>The first move is where the honesty lives. The value is an invariant of the DIAGRAM, not of the knot: a
    /// curl multiplies it by minus the crossing charge cubed, so two diagrams of one knot may read differently, and the
    /// writhe each declared diagram carries is what the published table's kink column corrects for.</remarks>
    public static string? StateSumMoveInvariant() {
        foreach (var point in ((BigInteger[])[2, 3])) {
            var sum = KnotRationalSum(point: point);
            var charge = RealQuadratic.Rational(value: point);
            var cube = ((charge * charge) * charge);
            var kink = -(RealQuadratic.One / cube);
            var mirrorKink = -cube;

            foreach (var word in KnotMoveWords) {
                var value = sum.Evaluate(
                    strands: KnotWidth,
                    word: word
                );

                // The second move: a crossing and its mirror inserted anywhere, at any strand, change nothing.
                for (var position = 0; (position <= word.Length); ++position) {
                    for (var strand = 1; (strand < KnotWidth); ++strand) {
                        var inserted = new int[(word.Length + 2)];

                        word.AsSpan(
                            length: position,
                            start: 0
                        ).CopyTo(destination: inserted);
                        inserted[position] = strand;
                        inserted[(position + 1)] = -strand;
                        word.AsSpan(start: position).CopyTo(destination: inserted.AsSpan(start: (position + 2)));

                        if (!sum.Evaluate(
                            strands: KnotWidth,
                            word: inserted
                        ).Equals(other: value)) {
                            return $"inserting a crossing and its mirror at strand {strand}, position {position} of [{string.Join(
                                separator: ",",
                                values: word
                            )}] moved the value";
                        }
                    }
                }

                // The first move: a curl at a strand pair the closure joins, above and below, scales by the kink factor.
                if (
                    !sum.Evaluate(
                    strands: KnotWidth,
                    word: [.. word, 1]
                ).Equals(other: (value * kink)) ||
                    !sum.Evaluate(
                    strands: KnotWidth,
                    word: [1, .. word]
                ).Equals(other: (value * kink)) ||
                    !sum.Evaluate(
                    strands: KnotWidth,
                    word: [.. word, -1]
                ).Equals(other: (value * mirrorKink))
                ) {
                    return $"a curl on [{string.Join(
                        separator: ",",
                        values: word
                    )}] does not scale the value by minus the crossing charge cubed";
                }
            }

            // The third move inside a word: the braid relation, read through the closure rather than at the element.
            if (!sum.Evaluate(
                strands: KnotWidth,
                word: [1, 2, 1, 3]
            ).Equals(other: sum.Evaluate(
                strands: KnotWidth,
                word: [2, 1, 2, 3]
            ))) {
                return $"the third move inside a word moved the value at the point {point}";
            }
        }

        return null;
    }
    /// <summary>Proves what the state sum refuses and what it declines to claim: an odd plat and a plat wider than the
    /// catalogue holds are refused, the braid group has no finite basis, its word problem is a budget rather than an
    /// answer, and equality of values is not equality of knots.</summary>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? KnotLimitsRefuse() {
        var basis = Oracles.PlanarDiagrams(maximumWidth: KnotWidth);

        // A plat closes adjacent pairs, so an odd strand count names no closing cup at all: an odd boundary has no
        // perfect matching, and the catalogue carries no such diagram to name.
        for (var strands = 1; (strands <= KnotWidth); strands += 2) {
            if (0 != basis.Count(predicate: diagram => (((0 == diagram.InputWidth) && (strands == diagram.OutputWidth)) || ((strands == diagram.InputWidth) && (0 == diagram.OutputWidth))))) {
                return $"the catalogue carries a closing layer of {strands} wires, where an odd boundary has no perfect matching";
            }
        }

        // The width cap is the knot construction's own bound: a plat of eight strands is a tangle of width eight.
        if (RefusesDeclaration(
            name: "a plat wider than the diagrams a finite basis holds",
            build: static () => _ = Presentations.PlanarTangle<RealQuadratic, RationalMaterial>(
                maximumWidth: 8,
                loopCharge: RealQuadratic.One,
                material: default
            )
        ) is { } capped) {
            return capped;
        }

        var free = PresentedAlgebra<RealQuadratic, RationalMaterial>.Create(presentation: Presentations.FreeMonoid<RealQuadratic, RationalMaterial>(
            letterCount: 6,
            material: default
        ));

        // No finite basis for the braid group, and this is an impossibility rather than a budget: the words over the
        // crossing letters are infinite, so no finite normal-form set exists and every basis-dependent readout says so.
        if (
            free.Presentation.HasFiniteNormalForms ||
            (0 != free.Presentation.NormalFormCount) ||
            (0 != free.Compile().KeyCount)
        ) {
            return "the free monoid on the crossing letters reports a finite basis, where an infinite word set has none";
        }

        if (RefusesDeclaration(
            name: "a zeta of the free monoid on the crossing letters",
            build: () => _ = GraphZeta<RealQuadratic, RationalMaterial>.TryCreate(
                algebra: free,
                value: free.Identity,
                order: 1,
                degreeBound: 4,
                zeta: out _,
                obstruction: out _
            )
        ) is { } unbased) {
            return unbased;
        }

        // The word problem is a LIMIT and not the row above: a bounded normalization that runs out of budget reports the
        // budget, which stays distinct from a failure.
        if (ClosureOutcome.SearchLimitReached != free.Certify(overlapLimit: (1L << 10)).Outcome) {
            return "the free monoid's certificate does not report its budget, where a bounded search that did not finish must say so rather than answer";
        }

        // Equality of values is not equality of knots, and the witness is the direction this construction can witness:
        // two diagrams of the SAME knot read differently, because a curl scales the value.
        var sum = KnotRationalSum(point: 2);
        var plain = sum.Evaluate(
            strands: 2,
            word: []
        );
        var kinked = sum.Evaluate(
            strands: 2,
            word: [1]
        );

        if (plain.Equals(other: kinked)) {
            return "a curled unknot reads the same value as the round one, where the first move scales the value and the readout is an invariant of the diagram";
        }

        // A point is a choice: where the loop charge vanishes every diagram reads the material's zero, so enough points
        // determine a bounded-degree polynomial only when the points are not degenerate.
        var degenerate = KnotPrimeSum(
            modulus: 17,
            point: 2
        );

        if (0UL != degenerate.LoopCharge) {
            return "the crossing charge two of GF(17) does not annihilate the loop charge, where its fourth power is minus one";
        }

        foreach (var (name, strands, word, _, _) in KnotDiagrams) {
            if (0UL != degenerate.Evaluate(
                strands: strands,
                word: word
            )) {
                return $"{name} reads a nonzero value where the loop charge is zero, so the collapse at a degenerate point is not total";
            }
        }

        return null;
    }
    /// <summary>The separation canary: the state sum must actually tell the declared diagrams apart, and the two trefoil
    /// chiralities in particular.</summary>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    /// <remarks>This is the strongest canary the phase carries. An invariant that collapsed to a constant — a loop charge
    /// silently equal to the material's one, a smoothing that lost its second term, a closure that read the wrong key —
    /// would satisfy every twin, every relation, every refusal and every move claim above, because all of those hold just
    /// as well of a constant. Only a floor on how many pairs the values separate catches it, and only the trefoils prove
    /// the readout sees chirality at all, since the two words differ by nothing but the sign of their crossings.</remarks>
    public static string? StateSumSeparatesCanary() {
        foreach (var point in KnotRationalPoints) {
            var sum = KnotRationalSum(point: point);
            var values = KnotDiagrams.Select(selector: entry => sum.Evaluate(
                strands: entry.Strands,
                word: entry.Word
            ).ToString()).ToArray();

            if (KnotSeparations(
                floor: KnotRationalFloor,
                name: $"the rationals at {point}",
                values: values
            ) is { } detail) { return detail; }
        }

        foreach (var (modulus, point, floor) in KnotPrimePoints) {
            var sum = KnotPrimeSum(
                modulus: modulus,
                point: point
            );
            var values = KnotDiagrams.Select(selector: entry => sum.Evaluate(
                strands: entry.Strands,
                word: entry.Word
            ).ToString()).ToArray();

            if (KnotSeparations(
                floor: floor,
                name: $"GF({modulus}) at {point}",
                values: values
            ) is { } detail) { return detail; }
        }

        return null;
    }

    // The separations of one point's values: how many of the unordered pairs read differently, against the floor
    // measured there, with the two chiralities and the figure-eight required to separate whatever the count.
    private static string? KnotSeparations(string?[] values, int floor, string name) {
        var separated = 0;

        for (var left = 0; (left < values.Length); ++left) {
            for (var right = (left + 1); (right < values.Length); ++right) {
                if (!string.Equals(
                    a: values[left],
                    b: values[right],
                    comparisonType: StringComparison.Ordinal
                )) { ++separated; }
            }
        }

        if (separated < floor) {
            return $"{name}: the state sum separates {separated} of the declared pairs, against the floor of {floor}";
        }

        var trefoil = Array.FindIndex(
            array: KnotDiagrams,
            match: entry => ("the trefoil" == entry.Name)
        );
        var mirror = Array.FindIndex(
            array: KnotDiagrams,
            match: entry => ("the mirror trefoil" == entry.Name)
        );
        var figureEight = Array.FindIndex(
            array: KnotDiagrams,
            match: entry => ("the figure-eight" == entry.Name)
        );

        if (string.Equals(
            a: values[trefoil],
            b: values[mirror],
            comparisonType: StringComparison.Ordinal
        )) {
            return $"{name}: the two trefoil chiralities read the same value, where their words differ in the sign of every crossing";
        }

        if (
            string.Equals(
            a: values[figureEight],
            b: values[trefoil],
            comparisonType: StringComparison.Ordinal
        ) ||
            string.Equals(
            a: values[figureEight],
            b: values[mirror],
            comparisonType: StringComparison.Ordinal
        )
        ) {
            return $"{name}: the figure-eight reads a trefoil's value";
        }

        return null;
    }
    // Every declared diagram's value against the published table, read through the Horner fold and divided by the
    // material rather than by the oracle.
    private static string? KnotValuesMatchTable<TValue, TOps>(PlatStateSum<TValue, TOps> sum, string name, BigInteger point, Func<BigInteger, BigInteger, TValue> scalar)
        where TOps : struct, IMaterialOps<TValue, TOps> {
        var comparer = EqualityComparer<TValue>.Default;

        foreach (var (diagram, strands, word, kinks, reduced) in KnotDiagrams) {
            var (lowest, coefficients) = KnotPolynomial(terms: reduced);
            var bracket = Oracles.BracketNormalization(
                coefficients: coefficients,
                kinkExponent: kinks,
                lowest: lowest
            );

            var (numerator, denominator) = Oracles.BracketHorner(
                coefficients: bracket.Coefficients,
                lowest: bracket.Lowest,
                point: point
            );

            if (!comparer.Equals(
                x: sum.Evaluate(
                    strands: strands,
                    word: word
                ),
                y: scalar(
                    arg1: numerator,
                    arg2: denominator
                )
            )) {
                return $"{name}: {diagram} reads a value the published bracket does not give it";
            }
        }

        return null;
    }
    // The braid relations on one state sum's images, at every strand count a plat of this width closes.
    private static string? KnotBraidRelations<TValue, TOps>(PlatStateSum<TValue, TOps> sum, string name)
        where TOps : struct, IMaterialOps<TValue, TOps> {
        var algebra = sum.Algebra;

        for (var strands = 2; (strands <= KnotWidth); strands += 2) {
            var identity = sum.Identity(strands: strands);

            for (var strand = 1; (strand < strands); ++strand) {
                if (
                    !algebra.AreEqual(
                    left: sum.Braid(
                        strands: strands,
                        word: [strand, -strand]
                    ),
                    right: identity
                ) ||
                    !algebra.AreEqual(
                    left: sum.Braid(
                        strands: strands,
                        word: [-strand, strand]
                    ),
                    right: identity
                )
                ) {
                    return $"{name}: the crossing at strand {strand} of {strands} and its mirror do not compose to the straight-through diagram";
                }

                for (var other = (strand + 1); (other < strands); ++other) {
                    if (1 == (other - strand)) {
                        if (!algebra.AreEqual(
                            left: sum.Braid(
                                strands: strands,
                                word: [strand, other, strand]
                            ),
                            right: sum.Braid(
                                strands: strands,
                                word: [other, strand, other]
                            )
                        )) {
                            return $"{name}: the braid relation fails at the adjacent strands {strand} and {other} of {strands}";
                        }

                        continue;
                    }

                    if (!algebra.AreEqual(
                        left: sum.Braid(
                            strands: strands,
                            word: [strand, other]
                        ),
                        right: sum.Braid(
                            strands: strands,
                            word: [other, strand]
                        )
                    )) {
                        return $"{name}: crossings at the distant strands {strand} and {other} of {strands} do not commute";
                    }
                }
            }
        }

        // The morphism's own fold against the per-letter one. They agree at every nonempty word; at the empty word the
        // source's unit maps to the ALGEBRA's unit, which is the sum of the identity diagrams across every width, where
        // the per-letter fold seeds at the identity of one width — and the closure identifies the two, because a cup of
        // that width composes with no other identity.
        var comparer = EqualityComparer<TValue>.Default;

        foreach (var (diagram, strands, word, _, _) in KnotDiagrams) {
            if (
                (0 != word.Length) &&
                !algebra.AreEqual(
                left: sum.Braid(
                    strands: strands,
                    word: word
                ),
                right: sum.MappedBraid(
                    strands: strands,
                    word: word
                )
            )
            ) {
                return $"{name}: {diagram} maps to one element through the morphism and to another through its own images";
            }

            if (!comparer.Equals(
                x: sum.Close(
                    strands: strands,
                    braid: sum.Braid(
                        strands: strands,
                        word: word
                    )
                ),
                y: sum.Close(
                    strands: strands,
                    braid: sum.MappedBraid(
                        strands: strands,
                        word: word
                    )
                )
            )) {
                return $"{name}: {diagram} closes to two different values through the morphism and through its own images";
            }
        }

        return null;
    }
    // A sparse Laurent polynomial written as its nonzero terms, so a published table reads as the polynomial it is.
    private static (int Lowest, BigInteger[] Coefficients) KnotPolynomial((int Exponent, int Coefficient)[] terms) {
        var highest = terms.Max(selector: term => term.Exponent);
        var lowest = terms.Min(selector: term => term.Exponent);
        var coefficients = new BigInteger[((highest - lowest) + 1)];

        foreach (var (exponent, coefficient) in terms) { coefficients[(exponent - lowest)] = coefficient; }

        return (lowest, coefficients);
    }
    // One exact fraction reduced into a prime field, by Fermat's exponent rather than by the field's own inversion, so
    // the prediction shares no step with the material it is compared against.
    private static ulong KnotPrimeScalar(BigInteger numerator, BigInteger denominator, ulong modulus) {
        var prime = new BigInteger(value: modulus);
        var inverse = BigInteger.ModPow(
            exponent: (prime - 2),
            modulus: prime,
            value: (((denominator % prime) + prime) % prime)
        );

        return ((ulong)((((((numerator % prime) + prime) % prime) * inverse) % prime)));
    }
    // The charge one closed curve carries, DERIVED rather than declared: composing a crossing with its mirror leaves the
    // straight-through diagram plus the square of the crossing charge, its inverse square and the loop charge times the
    // hook, so the second move holds exactly at minus the sum of those two squares.
    private static TValue KnotLoopCharge<TValue, TOps>(TValue crossingCharge, TValue inverseCharge, TOps material)
        where TOps : struct, IMaterialOps<TValue, TOps> =>
        ((ISignedMaterial<TValue, TOps>)material).Negate(value: material.Add(
            left: material.Multiply(
                left: crossingCharge,
                right: crossingCharge
            ),
            right: material.Multiply(
                left: inverseCharge,
                right: inverseCharge
            )
        ));
    private static PlatStateSum<RealQuadratic, RationalMaterial> KnotRationalSum(BigInteger point) {
        var charge = RealQuadratic.Rational(value: point);
        var inverse = RealQuadratic.Rational(
            numerator: BigInteger.One,
            denominator: point
        );

        return new(
            maximumWidth: KnotWidth,
            crossingCharge: charge,
            inverseCharge: inverse,
            loopCharge: KnotLoopCharge<RealQuadratic, RationalMaterial>(
                crossingCharge: charge,
                inverseCharge: inverse,
                material: default
            ),
            material: default
        );
    }
    // The declared prime-field points are units of their declared moduli, which is the field licence this construction
    // needs and which the limits claim states; a point that is not one names no crossing at all.
    private static PlatStateSum<ulong, PrimeFieldMaterial> KnotPrimeSum(ulong modulus, ulong point) {
        var material = PrimeFieldMaterial.Create(modulus: modulus);

        if (!material.TryInvert(
            inverse: out var inverse,
            value: point
        )) {
            throw new InvalidOperationException(message: $"the point {point} is not a unit of GF({modulus})");
        }

        return new(
            maximumWidth: KnotWidth,
            crossingCharge: point,
            inverseCharge: inverse,
            loopCharge: KnotLoopCharge(
                crossingCharge: point,
                inverseCharge: inverse,
                material: material
            ),
            material: material
        );
    }

    /// <summary>One plat-closed state sum: the tangle algebra its diagrams live in, the free monoid on the crossing
    /// letters at each strand count, and the morphism that smooths each crossing into a two-term element.</summary>
    private sealed class PlatStateSum<TValue, TOps>
        where TOps : struct, IMaterialOps<TValue, TOps> {
        private readonly PresentedAlgebra<TValue, TOps> m_algebra;
        private readonly IReadOnlyList<Oracles.PlanarDiagram> m_basis;
        private readonly PresentedFunctor<TValue, TOps>?[] m_functors;
        private readonly TOps m_material;

        public PlatStateSum(int maximumWidth, TValue crossingCharge, TValue inverseCharge, TValue loopCharge, TOps material) {
            m_algebra = PresentedAlgebra<TValue, TOps>.Create(presentation: Presentations.PlanarTangle<TValue, TOps>(
                loopCharge: loopCharge,
                material: material,
                maximumWidth: maximumWidth
            ));
            m_basis = Oracles.PlanarDiagrams(maximumWidth: maximumWidth);
            m_functors = new PresentedFunctor<TValue, TOps>?[(maximumWidth + 1)];
            m_material = material;

            LoopCharge = loopCharge;

            // A plat closes adjacent pairs, so only the even strand counts carry one.
            for (var strands = 2; (strands <= maximumWidth); strands += 2) {
                var free = PresentedAlgebra<TValue, TOps>.Create(presentation: Presentations.FreeMonoid<TValue, TOps>(
                    letterCount: (2 * (strands - 1)),
                    material: material
                ));
                var identity = m_algebra.Generator(symbol: PlanarSymbolOf(
                    basis: m_basis,
                    inputWidth: strands,
                    outputWidth: strands,
                    partner: StraightThroughDiagram(strands: strands)
                ));
                var images = new PresentedAlgebra<TValue, TOps>.Element[(2 * (strands - 1))];

                for (var strand = 1; (strand < strands); ++strand) {
                    var hook = m_algebra.Generator(symbol: HookDiagram(
                        basis: m_basis,
                        index: strand,
                        strands: strands
                    ));

                    images[Letter(crossing: strand)] = m_algebra.Add(
                        left: Scale(
                            coefficient: crossingCharge,
                            value: identity
                        ),
                        right: Scale(
                            coefficient: inverseCharge,
                            value: hook
                        )
                    );
                    images[Letter(crossing: -strand)] = m_algebra.Add(
                        left: Scale(
                            coefficient: inverseCharge,
                            value: identity
                        ),
                        right: Scale(
                            coefficient: crossingCharge,
                            value: hook
                        )
                    );
                }

                if (!PresentedFunctor<TValue, TOps>.TryCreate(
                    functor: out var functor,
                    images: images,
                    obstruction: out _,
                    source: free,
                    target: m_algebra
                )) {
                    throw new InvalidOperationException(message: $"the crossing assignment at {strands} strands was refused, where a free source imposes no relation");
                }

                m_functors[strands] = functor;
            }
        }

        /// <summary>The tangle algebra every diagram of this state sum is an element of.</summary>
        public PresentedAlgebra<TValue, TOps> Algebra => m_algebra;
        /// <summary>The charge one closed curve carries.</summary>
        public TValue LoopCharge { get; }

        // The closing layer's matching: adjacent boundary positions joined in pairs.
        private static int[] AdjacentPairs(int wires) {
            var partner = new int[wires];

            for (var position = 0; (position < wires); position += 2) {
                partner[position] = (position + 1);
                partner[(position + 1)] = position;
            }

            return partner;
        }
        private PresentedAlgebra<TValue, TOps>.Element Scale(in PresentedAlgebra<TValue, TOps>.Element value, TValue coefficient) {
            var coefficients = new TValue[value.SupportCount];

            for (var index = 0; (index < coefficients.Length); ++index) {
                coefficients[index] = m_material.Multiply(
                left: coefficient,
                right: value.Coefficients[index]
            );
            }

            return m_algebra.FromSupport(
                keys: value.Keys,
                coefficients: coefficients
            );
        }

        /// <summary>The ordered product of a braid word's crossing images, seeded at the straight-through diagram.</summary>
        public PresentedAlgebra<TValue, TOps>.Element Braid(int strands, ReadOnlySpan<int> word) {
            var element = Identity(strands: strands);

            foreach (var crossing in word) {
                element = m_algebra.Multiply(
                left: element,
                right: Crossing(
                    crossing: crossing,
                    strands: strands
                )
            );
            }

            return element;
        }
        /// <summary>Closes a braid with the cup and cap layers and reads the value at the empty diagram.</summary>
        /// <remarks>The closure is an ordinary product: a cup layer is a diagram with no inputs, a cap layer one with no
        /// outputs, and composing them around a braid lands on the empty diagram, whose key is zero. The readout is the
        /// pairing against the covector carrying the material's one there — not the trace, because this algebra's unit is
        /// the SUM of the identity diagrams and the trace would sum every width.</remarks>
        public TValue Close(int strands, in PresentedAlgebra<TValue, TOps>.Element braid) {
            var closed = m_algebra.Multiply(
                left: m_algebra.Multiply(
                    left: m_algebra.Generator(symbol: PlanarSymbolOf(
                        basis: m_basis,
                        inputWidth: 0,
                        outputWidth: strands,
                        partner: AdjacentPairs(wires: strands)
                    )),
                    right: braid
                ),
                right: m_algebra.Generator(symbol: PlanarSymbolOf(
                    basis: m_basis,
                    inputWidth: strands,
                    outputWidth: 0,
                    partner: AdjacentPairs(wires: strands)
                ))
            );

            return m_algebra.Pair(
                covector: m_algebra.Generator(symbol: 0),
                value: closed
            );
        }
        /// <summary>The two-term element one crossing smooths into.</summary>
        public PresentedAlgebra<TValue, TOps>.Element Crossing(int strands, int crossing) =>
            m_functors[strands]!.Image(symbol: Letter(crossing: crossing));
        /// <summary>The bracket of one plat-closed braid word.</summary>
        public TValue Evaluate(int strands, ReadOnlySpan<int> word) =>
            Close(
                strands: strands,
                braid: Braid(
                    strands: strands,
                    word: word
                )
            );
        /// <summary>The straight-through diagram at one strand count, which is the braid group's unit there.</summary>
        public PresentedAlgebra<TValue, TOps>.Element Identity(int strands) =>
            m_algebra.Generator(symbol: PlanarSymbolOf(
                basis: m_basis,
                inputWidth: strands,
                outputWidth: strands,
                partner: StraightThroughDiagram(strands: strands)
            ));
        /// <summary>The free monoid's letter one crossing names: two letters per strand, a crossing and its mirror.</summary>
        public static int Letter(int crossing) =>
            ((2 * (Math.Abs(value: crossing) - 1)) + ((crossing > 0)
                ? 0
                : 1));
        /// <summary>The same braid, formed as one word of the free monoid and carried across by the morphism.</summary>
        public PresentedAlgebra<TValue, TOps>.Element MappedBraid(int strands, ReadOnlySpan<int> word) {
            var functor = m_functors[strands]!;
            var free = functor.Source;
            var source = free.Identity;

            foreach (var crossing in word) {
                source = free.Multiply(
                left: source,
                right: free.Generator(symbol: Letter(crossing: crossing))
            );
            }

            return functor.Map(value: source);
        }
    }

    // ---- the hypercomplex family: FixedComplex, FixedSplit, FixedDual, FixedQuaternion ----
    //
    // FixedQuaternion stores (X, Y, Z, W) = (e₁, e₂, e₃, e₀); the Cayley–Dickson tower indexes (e₀, e₁, e₂, e₃). The
    // permutation between the two orders is declared HERE, once, and named in every leg that leans on the doubling
    // oracle — it is the ONLY convention the subject and that oracle share.
    private static void QuaternionToDoublingLanes(ReadOnlySpan<long> quaternion, Span<long> doubling) {
        doubling[0] = quaternion[3];
        doubling[1] = quaternion[0];
        doubling[2] = quaternion[1];
        doubling[3] = quaternion[2];
    }
    private static void DoublingToQuaternionLanes(ReadOnlySpan<long> doubling, Span<long> quaternion) {
        quaternion[0] = doubling[1];
        quaternion[1] = doubling[2];
        quaternion[2] = doubling[3];
        quaternion[3] = doubling[0];
    }

    // Four raw Q32 squares total at most 2^128 exactly, which is where the quaternion norm's carry test fires; the
    // planar two-square sum cannot reach it.
    private static readonly BigInteger FourSquareCarry = (BigInteger.One << 128);

    // round(log2(e)·2^16) = round(94548.4622…). Carried here as its own literal with its own provenance rather than
    // read from the subject's private field.
    private const long Log2ERaw = 94548L;
    // round(π·2^16). The raw endpoint FixedQ4816.Atan2 attains at both signs; the VALUE it names is strictly inside
    // (−π, π], which is why the closed raw range and the documented open value range are the same statement.
    private const long PiRaw = 205887L;

    // Maps a raw onto a non-positive one, TOTALLY: a positive raw is negated and everything else passes through, so the
    // two's-complement minimum reaches the refusal branch instead of being folded away by a sign-bit mask.
    private static long NonPositiveRaw(long raw) =>
        ((raw > 0L)
            ? -raw
            : raw
        );
    // Whether every sampled raw is small enough that the scale-freedom ladder's shifts cannot leave the carrier.
    private static bool WithinScaleGuard(ReadOnlySpan<long> values, long bound) {
        foreach (var value in values) {
            if (
                (value <= -bound) ||
                (value >= bound)
            ) { return false; }
        }

        return true;
    }

    // The scale pairs every direction-only construction is swept against. A common power of two multiplies both exact
    // product sums by that power, and the shared normalizer's preconditioner divides it straight back out, so agreement
    // is exact rather than toleranced.
    private static readonly (int Left, int Right)[] ScaleFreedomShifts = [(1, 1), (3, 5), (7, 0), (0, 10)];

}
