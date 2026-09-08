using System.Numerics;

namespace Puck.Maths.Tests;

internal static partial class Subjects {
    // ---- phase 3: the twins and canaries the slices owed ----

    // The floor a live re-association charge must move the flattener's answer past, at the octonion and sedenion
    // floors. Observed 168 of 512 and 1848 of 4096 — the associator's own support — so each floor sits a tenth below
    // the measurement and a REGRESSION that quietly dropped the declaration reports zero and fails, without the case
    // resting on the certificate's nonassociative-triple count for its anchor.
    private static readonly (int Floors, int Minimum)[] LiveReassociationFloors = [(3, 150), (4, 1_600)];

    /// <summary>Proves that the coherence flag IS measured route independence, that a live re-association charge really
    /// moves the answer, and that coherence and faithfulness are different statements — the second witnessed by a
    /// coherent nontrivial cochain over a product that associates.</summary>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    /// <remarks>
    /// <para>
    /// The certificate decides coherence from the DECLARED charges, which are internal and unreadable from here. This
    /// case recomputes the same condition from BEHAVIOUR: at a presentation whose cells are monomial, both bracketings
    /// of a triple normalize to one term at one key, so the ratio of their coefficients is exactly the charge the
    /// normalizer applied — and the five-vertex coherence identity is then re-evaluated here on those measured charges.
    /// A mis-oriented identity inside <c>Certify</c> that still separated the perturbed cochain would pass the coherence
    /// slice's own case and fail this one.
    /// </para>
    /// <para>
    /// Coherence is NOT "the five bracketings of a quadruple agree" — with a nontrivial charge they must not, and that
    /// disagreement is the bracket-sensitivity this file also counts. It is that the charge a bracketing collects does
    /// not depend on the order the brackets were removed in, so the normalizer's one fixed walk is answering a question
    /// about the term rather than about itself.
    /// </para>
    /// <para>
    /// The separating instance is the two-element group carrying the 3-cocycle <c>(−1)^(a·b·c)</c>. Its cells are the
    /// group's own, so its product associates and its nonassociative-triple count is zero, and yet its declared charges do not
    /// vanish: the tree normalizer answers a bracketing with a sign the compiled cells never carry. That is the
    /// documented boundary — coherence is computed and reported, faithfulness is neither — made a measurement.
    /// </para>
    /// </remarks>
    public static string? CoherenceIsRouteIndependence() {
        // The canary: a live floor must move the flattener's left-normed answer on a pinned share of the triples.
        foreach (var (floors, minimum) in LiveReassociationFloors) {
            var live = PresentedAlgebra<BigInteger, IntegerMaterial>.Create(presentation: Presentations.CayleyDickson<BigInteger, IntegerMaterial>(
                basisRelabelling: [],
                floors: floors,
                liveAssociator: true,
                material: default
            ));
            var moved = MovedBrackets(
                algebra: live,
                refusals: out var refused
            );

            if (0L != refused) {
                return $"cayley-dickson({floors}, live): {refused} normalization(s) refused, so the count below measures the step limit rather than the declaration";
            }

            if (moved < minimum) {
                return $"cayley-dickson({floors}, live): the declared associator moves only {moved} of {((live.Presentation.NormalFormCount * live.Presentation.NormalFormCount) * live.Presentation.NormalFormCount)} right-nested triples off the left-normed product, under the floor of {minimum}";
            }

            // The escape a coherent-looking declaration used to have, closed at admission and measured here.
            if (SpellingsOfOneElementAgree(
                algebra: live,
                name: $"cayley-dickson({floors}, live)"
            ) is { } spelling) { return spelling; }
        }

        // The flag against the measured identity, at a coherent floor and at a cochain that is not a cocycle.
        var coherent = PresentedAlgebra<BigInteger, IntegerMaterial>.Create(presentation: Presentations.CayleyDickson<BigInteger, IntegerMaterial>(
            basisRelabelling: [],
            floors: 2,
            liveAssociator: true,
            material: default
        ));

        if (CoherenceMatchesMeasuredIdentity(
            algebra: coherent,
            name: "cayley-dickson(2, live)"
        ) is { } quaternion) { return quaternion; }

        var octonion = PresentedAlgebra<BigInteger, IntegerMaterial>.Create(presentation: Presentations.CayleyDickson<BigInteger, IntegerMaterial>(
            basisRelabelling: [],
            floors: 3,
            liveAssociator: true,
            material: default
        ));

        if (CoherenceMatchesMeasuredIdentity(
            algebra: octonion,
            name: "cayley-dickson(3, live)"
        ) is { } floor) { return floor; }

        var perturbed = PresentedAlgebra<BigInteger, IntegerMaterial>.Create(presentation: PerturbedCochainPresentation());

        if (CoherenceMatchesMeasuredIdentity(
            algebra: perturbed,
            name: "the perturbed 3-cochain"
        ) is { } broken) { return broken; }

        // Coherence is not faithfulness. The group's product associates, so nothing it computes can carry the declared
        // sign, and the tree normalizer parts company with the compiled cells at exactly the triples where the cochain
        // is not one — measured, not described.
        var twisted = PresentedAlgebra<BigInteger, IntegerMaterial>.Create(presentation: CyclicGroupPresentation(
            flippedTriple: CocycleTriple,
            order: CocycleOrder
        ));
        var twistedCertificate = twisted.Certify(overlapLimit: (1L << 20));

        if (!twisted.Presentation.HasLiveReassociation) { return "the two-element group's 3-cocycle did not register as a live re-association charge"; }

        if (
            !twistedCertificate.IsCoherent ||
            (0 != twistedCertificate.CoherenceWitness.Length)
        ) {
            return $"the 3-cocycle (−1)^(a·b·c) reports coherent={twistedCertificate.IsCoherent} with {twistedCertificate.CoherenceWitness.Length} witness(es), though it satisfies the cocycle condition on every quadruple";
        }

        if (CoherenceMatchesMeasuredIdentity(
            algebra: twisted,
            name: "the 3-cocycle (−1)^(a·b·c)"
        ) is { } cocycle) { return cocycle; }

        if (
            !twistedCertificate.IsAssociative ||
            (0L != twistedCertificate.NonAssociativeTripleCount) ||
            (ClosureOutcome.BasisAssociativityVerified != twistedCertificate.Outcome)
        ) {
            return $"the 3-cocycle's underlying product reports associative={twistedCertificate.IsAssociative} nonassociative triples={twistedCertificate.NonAssociativeTripleCount} {twistedCertificate.Outcome}, so the instance does not separate coherence from faithfulness";
        }

        var sensitive = MovedBrackets(
            algebra: twisted,
            refusals: out var twistedMoves
        );
        var unfaithful = UnfaithfulBrackets(
            algebra: twisted,
            refusals: out var twistedFaith
        );

        if (
            (0L != twistedMoves) ||
            (0L != twistedFaith)
        ) {
            return $"the 3-cocycle refused {(twistedMoves + twistedFaith)} normalization(s), so neither count below measures the declaration";
        }

        if (
            (1 != sensitive) ||
            (1 != unfaithful)
        ) {
            return $"the 3-cocycle moves {sensitive} of 8 bracketings and disagrees with its own nested products on {unfaithful}, where exactly one triple carries a charge of minus one";
        }

        // Coherent means the charge is spelling-independent, and the unit written as its own letter against the unit
        // written as the empty product is the spelling pair that a declaration charging at the unit would separate.
        if (SpellingsOfOneElementAgree(
            algebra: twisted,
            name: "the 3-cocycle (−1)^(a·b·c)"
        ) is { } spelled) { return spelled; }

        // And the faithful case, at the same shape: an associator that IS the product's own leaves the tree agreeing
        // with the nested products everywhere, so the count above is a property of the declaration and not of the walk.
        var faithless = UnfaithfulBrackets(
            algebra: octonion,
            refusals: out var octonionFaith
        );

        if (0L != octonionFaith) { return $"the octonion floor refused {octonionFaith} normalization(s), so its faithfulness count is not a measurement"; }

        return ((0 == faithless)
            ? null
            : $"the octonion floor's declared associator disagrees with its own nested products on {faithless} triple(s)"
        );
    }
    /// <summary>Proves that the presented product at a reflection world IS composition of the lattice's own reflections:
    /// one cell of the compiled table, one composite permutation, and one pair of reflections applied in sequence, all
    /// naming the same element.</summary>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    /// <remarks>The group slice pinned the ORDERS of these worlds and the triviality of their relations on the lattice.
    /// What it did not pin is that <c>Multiply</c> reproduces the action, which is the statement that makes the group
    /// algebra an instance of the kernel rather than a table beside it.
    /// <para>Two kinds of leg run here and they carry different weight. The composite-row chase and the power walk
    /// re-implement the composition rule <see cref="Presentations"/> uses to BUILD this presentation's reduce rules, so
    /// they are faithful carriage: agreement proves the product carries the rule, not that the rule is the action. The
    /// leg that stands outside is <see cref="SymmetryLattice.Reflect"/> composed by hand on the lattice itself, which
    /// runs no step the algebra runs.</para></remarks>
    public static string? ReflectionProductTwinsAction() {
        foreach (var (name, mirrors, order, points) in ReflectionProductWorlds) {
            var system = ReflectionSystem.Create(mirrors: mirrors);

            if (points != system.Points.Length) { return $"{name}: the sub-system acts on {system.Points.Length} points, not {points}"; }

            if (!system.TryEnumerateGroup(
                searchLimit: 4_096L,
                out var permutations,
                out var refusal
            )) {
                return $"{name}: the enumeration refused after reaching {refusal.PointsReached} element(s)";
            }

            var rows = permutations.Span;
            var elementCount = (rows.Length / points);

            if (order != elementCount) { return $"{name}: the action closes on {elementCount} elements, not {order}"; }

            var algebra = PresentedAlgebra<BigInteger, IntegerMaterial>.Create(presentation: Presentations.PermutationGroup<BigInteger, IntegerMaterial>(
                material: default,
                permutations: rows,
                pointCount: points
            ));

            if (order != algebra.Presentation.NormalFormCount) { return $"{name}: the presented basis holds {algebra.Presentation.NormalFormCount} normal forms against an order of {order}"; }

            // A mirror's row IS its reflection, which is what lets the product below be read as an action rather than
            // as a permutation identity.
            foreach (var mirror in system.Mirrors) {
                var mirrorKey = RowKeyOfMirror(
                    elementCount: elementCount,
                    mirror: mirror,
                    points: points,
                    rows: rows,
                    system: system
                );

                if (mirrorKey < 0) { return $"{name}: the reflection in mirror {mirror} is not one of the enumerated elements"; }

                for (var point = 0; (point < points); ++point) {
                    if (system.Points[rows[((mirrorKey * points) + point)]] != SymmetryLattice.Reflect(
                        node: system.Points[point],
                        mirror: mirror
                    )) {
                        return $"{name}: the row of mirror {mirror} moves point {point} somewhere Reflect does not";
                    }
                }
            }

            var composite = new int[points];

            for (var left = 0; (left < elementCount); ++left) {
                var leftBasis = PresentedBasis(
                    algebra: algebra,
                    key: left
                );

                for (var right = 0; (right < elementCount); ++right) {
                    var product = algebra.Multiply(
                        left: leftBasis,
                        right: PresentedBasis(
                            algebra: algebra,
                            key: right
                        )
                    );

                    for (var point = 0; (point < points); ++point) { composite[point] = rows[((right * points) + rows[((left * points) + point)])]; }

                    var target = RowKey(
                        elementCount: elementCount,
                        points: points,
                        row: composite,
                        rows: rows
                    );

                    if (target < 0) { return $"{name}: the composite of elements {left} and {right} is not an element of the enumerated group"; }

                    if (
                        (1 != product.SupportCount) ||
                        (product.Keys[0] != target) ||
                        (BigInteger.One != product.Coefficients[0])
                    ) {
                        return $"{name}: the product of basis elements {left} and {right} is [{ElementText(value: product)}], where composing their actions gives the single element {target}";
                    }
                }
            }

            // The whole point, stated on the lattice: the algebra's product of two mirror basis elements moves every
            // node the way the two reflections do, in that order.
            foreach (var first in system.Mirrors) {
                foreach (var second in system.Mirrors) {
                    var product = algebra.Multiply(
                        left: PresentedBasis(
                            algebra: algebra,
                            key: RowKeyOfMirror(
                                elementCount: elementCount,
                                mirror: first,
                                points: points,
                                rows: rows,
                                system: system
                            )
                        ),
                        right: PresentedBasis(
                            algebra: algebra,
                            key: RowKeyOfMirror(
                                elementCount: elementCount,
                                mirror: second,
                                points: points,
                                rows: rows,
                                system: system
                            )
                        )
                    );
                    var key = ((int)product.Keys[0]);

                    for (var point = 0; (point < points); ++point) {
                        if (system.Points[rows[((key * points) + point)]] != SymmetryLattice.Reflect(
                            node: SymmetryLattice.Reflect(
                                node: system.Points[point],
                                mirror: first
                            ),
                            mirror: second
                        )) {
                            return $"{name}: the product of the reflections in {first} and {second} moves point {point} somewhere the two reflections do not";
                        }
                    }
                }
            }

            // Powers walk the same cycle: the presented Power of the Coxeter element and the repeated action agree at
            // every exponent, and the exponent at which the element returns is the same number for both.
            var element = CoxeterElementKey(
                algebra: algebra,
                elementCount: elementCount,
                points: points,
                rows: rows,
                system: system
            );
            var walk = new int[points];

            for (var point = 0; (point < points); ++point) { walk[point] = point; }

            for (var exponent = 1; (exponent <= (2 * order)); ++exponent) {
                for (var point = 0; (point < points); ++point) { composite[point] = rows[((element * points) + walk[point])]; }

                composite.CopyTo(
                    array: walk,
                    index: 0
                );

                var power = algebra.Power(
                    value: PresentedBasis(
                        algebra: algebra,
                        key: element
                    ),
                    exponent: ((ulong)exponent)
                );
                var target = RowKey(
                    elementCount: elementCount,
                    points: points,
                    row: walk,
                    rows: rows
                );

                if (
                    (1 != power.SupportCount) ||
                    (power.Keys[0] != target) ||
                    (BigInteger.One != power.Coefficients[0])
                ) {
                    return $"{name}: the {exponent}th power of the Coxeter element is [{ElementText(value: power)}], where its {exponent}-fold action is element {target}";
                }
            }
        }

        return null;
    }
    /// <summary>Proves the Stokes adjunction at three materials beyond the integers — GF(2), a prime field and the
    /// rationals — each with the pairing checked against the coefficientwise sum and with a non-degeneracy count that a
    /// collapsed boundary could not satisfy.</summary>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    /// <remarks>The identity is the associativity of one product, so it cannot depend on the material — which is
    /// exactly why running it at one material proves less than it looks. The teeth are the count: the ordered basis
    /// pairs whose Stokes value is nonzero are precisely the declared incidences, so a boundary that returned zero, or
    /// a coboundary that lost its signs, fails the case rather than passing it vacuously.</remarks>
    public static string? StokesMaterialSweep() {
        foreach (var world in ChainWorlds) {
            var (name, topFaces, _) = EulerWorlds[world];
            var (dimensions, incidences) = SimplicialComplex(topFaces: topFaces);

            // GF(2) carries exactly ONE nonzero value, so a weight that varied would only vary the zeros: the second
            // value of an alternating (cell & 1) + 1 reduced to zero and left half the dense-operand leg empty.
            if (StokesHolds(
                dimensions: dimensions,
                incidences: incidences,
                material: default(ParityMaterial),
                name: $"{name} over GF(2)",
                weight: static _ => 1UL
            ) is { } parity) { return parity; }

            if (StokesHolds(
                name: $"{name} over the prime field",
                dimensions: dimensions,
                incidences: incidences,
                material: new PrimeFieldMaterial(field: PrimeField64.Create(modulus: PrimeFieldModulus)),
                weight: static cell => ((ulong)((3 * cell) + 7))
            ) is { } prime) { return prime; }

            if (StokesHolds(
                name: $"{name} over the rationals",
                dimensions: dimensions,
                incidences: incidences,
                material: default(RationalMaterial),
                weight: static cell => RealQuadratic.Rational(
                    denominator: ((cell & 3) + 2),
                    numerator: (cell + 1)
                )
            ) is { } rational) { return rational; }
        }

        return null;
    }
    /// <summary>Proves the elementary-divisor certificate at orders and shapes the swept three-by-three draw does not
    /// reach — square through order eight, rectangular in both orientations, and one matrix whose invariants are a
    /// classical fact rather than a recomputation.</summary>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    /// <remarks>The classical fact is the reflection lattice's own Cartan matrix, built here from
    /// <see cref="ReflectionSystem.BondMatrix"/>: its determinant is one, so its elementary divisors are eight ones and
    /// nothing else. Nothing in this file computes that determinant, and the second kernel meets the group slice's
    /// measured diagram rather than a tabulated one.</remarks>
    public static string? SmithRemultipliesAtScale() {
        // The chains that only start moving at a high diagonal position. A random draw usually needs the divisibility
        // repair at stage zero or one and never again, so a repair that stopped working past the third stage survives
        // every swept matrix; these do not survive it.
        foreach (var (name, order, diagonal, expected) in DeepChainForms) {
            var entries = new BigInteger[(order * order)];

            for (var index = 0; (index < order); ++index) { entries[((index * order) + index)] = diagonal[index]; }

            if (!SmithNormalForm.TryReduce(
                columnCount: order,
                entries: entries,
                form: out var chain,
                magnitudeBits: SmithWideCeiling,
                obstruction: out var stoppedEarly,
                rowCount: order
            )) {
                return $"{name}: refused at stage {stoppedEarly.Stage} with {stoppedEarly.MagnitudeBits} bits";
            }

            if (chain.Rank != expected.Length) { return $"{name}: rank {chain.Rank} where {expected.Length} was written out"; }

            for (var index = 0; (index < expected.Length); ++index) {
                if (chain.Divisors[index] != expected[index]) { return $"{name}: divisor {index} is {chain.Divisors[index]} where {expected[index]} was written out"; }
            }

            if (SmithCertificate(
                columns: order,
                entries: entries,
                form: chain,
                name: name,
                rows: order
            ) is { } unproved) { return unproved; }
        }

        foreach (var (rows, columns, seed, span, minorLimit) in SmithScaleShapes) {
            var name = $"a {rows}-by-{columns} draw at seed {seed}";
            var entries = ScaledMatrix(
                columns: columns,
                rows: rows,
                seed: seed,
                span: span
            );

            if (!SmithNormalForm.TryReduce(
                columnCount: columns,
                entries: entries,
                form: out var form,
                magnitudeBits: SmithWideCeiling,
                obstruction: out var refusal,
                rowCount: rows
            )) {
                return $"{name}: refused at stage {refusal.Stage} with {refusal.MagnitudeBits} bits after {refusal.StepsTaken} step(s)";
            }

            if (
                (form.RowCount != rows) ||
                (form.ColumnCount != columns)
            ) { return $"{name}: the reduction reports {form.RowCount} by {form.ColumnCount}"; }

            if (SmithCertificate(
                columns: columns,
                entries: entries,
                form: form,
                name: name,
                rows: rows
            ) is { } certificate) { return certificate; }

            if (SmithDivisorOracle(
                columns: columns,
                entries: entries,
                form: form,
                maximumSize: minorLimit,
                name: name,
                rows: rows
            ) is { } oracle) { return oracle; }
        }

        var system = ReflectionSystem.Create(mirrors: ReflectionSystem.SimpleMirrors);
        var rank = system.Mirrors.Length;
        var bonds = system.BondMatrix;
        var cartan = new BigInteger[(rank * rank)];

        for (var row = 0; (row < rank); ++row) {
            for (var column = 0; (column < rank); ++column) {
                var bond = bonds[((row * rank) + column)];

                if (
                    (row != column) &&
                    (2 != bond) &&
                    (3 != bond)
                ) { return $"the reflection diagram is not simply laced: mirrors {row} and {column} bond at {bond}"; }

                cartan[((row * rank) + column)] = ((row == column)
                    ? 2
                    : ((3 == bond)
                        ? BigInteger.MinusOne
                        : BigInteger.Zero
                ));
            }
        }

        if (!SmithNormalForm.TryReduce(
            columnCount: rank,
            entries: cartan,
            form: out var cartanForm,
            magnitudeBits: SmithWideCeiling,
            obstruction: out var stopped,
            rowCount: rank
        )) {
            return $"the Cartan matrix refused at stage {stopped.Stage} with {stopped.MagnitudeBits} bits";
        }

        if (rank != cartanForm.Rank) { return $"the Cartan matrix reduces to rank {cartanForm.Rank} on {rank} mirrors"; }

        for (var index = 0; (index < cartanForm.Rank); ++index) {
            if (!cartanForm.Divisors[index].IsOne) { return $"the Cartan matrix's divisor {index} is {cartanForm.Divisors[index]}, where a unimodular lattice carries only ones"; }
        }

        return (SmithCertificate(
            columns: rank,
            entries: cartan,
            form: cartanForm,
            name: "the reflection lattice's Cartan matrix",
            rows: rank
        )
            ?? SmithDivisorOracle(
            columns: rank,
            entries: cartan,
            form: cartanForm,
            maximumSize: 4,
            name: "the reflection lattice's Cartan matrix",
            rows: rank
        ));
    }

    // The reflection worlds the product twin runs at: the smallest with a bond of three, and the smallest with a
    // branchless chain of them. Both point counts and orders are measured facts of the lattice, written out here so a
    // sub-system that stopped closing would fail rather than shrink quietly.
    private static readonly (string Name, int[] Mirrors, int Order, int Points)[] ReflectionProductWorlds = [
        ("the rank-two world [0, 2]", [0, 2], 6, 6),
        ("the rank-three chain [0, 2, 3]", [0, 2, 3], 24, 12),
    ];
    // Diagonal matrices whose elementary divisors are hand-computable from the gcds of their minors, and whose chain
    // stays at one until the fourth and fifth positions — so the step that BUILDS the chain has to keep working past
    // the stages a small matrix ever reaches.
    private static readonly (string Name, int Order, long[] Diagonal, long[] Divisors)[] DeepChainForms = [
        ("a six-by-six whose chain only moves at the fifth divisor", 6, [1, 1, 1, 6, 10, 15], [1, 1, 1, 1, 30, 30]),
        ("a seven-by-seven whose chain only moves at the sixth divisor", 7, [1, 1, 1, 1, 4, 6, 9], [1, 1, 1, 1, 1, 6, 36]),
    ];
    // The shapes the certificate is proved at beyond the swept three-by-three: square through order eight, rectangular
    // both ways, and one wide draw. The last column bounds the gcd-of-minors oracle, which enumerates every subset of
    // that size and so is run where it is cheap rather than everywhere.
    private static readonly (int Rows, int Columns, int Seed, int Span, int MinorLimit)[] SmithScaleShapes = [
        (5, 5, 11, 30, 5),
        (6, 6, 23, 12, 6),
        (8, 8, 37, 6, 3),
        (4, 7, 41, 20, 4),
        (7, 4, 53, 20, 4),
        (6, 9, 67, 9, 3),
    ];

    // The two-element group, and the ordered triple its nontrivial 3-cocycle charges: (1, 1, 1), the only one whose
    // product of letters is odd, so the cochain is (−1)^(a·b·c) exactly.
    private const int CocycleOrder = 2;
    private const int CocycleTriple = 7;
    // The perturbed cochain: the three-element group with (1, 1, 1) flipped. It has to be the three-element group and
    // not the two-element one, because a declaration is refused unless it is NORMALIZED AT THE UNIT — the unit spelled
    // as the empty product carries no letter for a splice to charge with — and over two elements the only triple that
    // avoids the unit is (1, 1, 1), whose flip is the cocycle above. Over three elements flipping (1, 1, 1) leaves the
    // unit untouched and still breaks the quadruple identity, at (1, 1, 1, 2) among others, so the certificate has
    // something to witness.
    private const int PerturbedOrder = 3;
    private const int PerturbedTriple = 13;

    // A cyclic group's algebra with one entry of its re-association table flipped to minus one. The cells are the
    // group's own either way, so what the certificate reports is a property of the DECLARED charges alone; which entry
    // moves is what decides whether those charges are a cocycle.
    private static ChargedPresentation<BigInteger, IntegerMaterial> CyclicGroupPresentation(int order, int flippedTriple) {
        var charges = new BigInteger[((order * order) * order)];

        charges.AsSpan().Fill(value: BigInteger.One);

        charges[flippedTriple] = BigInteger.MinusOne;

        var rules = new List<RewriteRule<BigInteger>> {
            new(
            kind: RuleKind.Reassociate,
            pattern: ReadOnlyMemory<int>.Empty,
            replacement: ReadOnlyMemory<int>.Empty,
            charges: charges
        ),
            new(
            kind: RuleKind.Reduce,
            pattern: ReadOnlyMemory<int>.Empty,
            replacement: RewriteRule<BigInteger>.PackReplacement(terms: [[0]]),
            charges: new[] { BigInteger.One }
        ),
        };

        for (var left = 0; (left < order); ++left) {
            for (var right = 0; (right < order); ++right) {
                rules.Add(item: new(
                    kind: RuleKind.Reduce,
                    pattern: new[] { left, right },
                    replacement: RewriteRule<BigInteger>.PackReplacement(terms: [[((left + right) % order)]]),
                    charges: new[] { BigInteger.One }
                ));
            }
        }

        return ChargedPresentation<BigInteger, IntegerMaterial>.Create(
            generators: SingleColourBasis(count: order),
            rules: System.Runtime.InteropServices.CollectionsMarshal.AsSpan(list: rules),
            material: default
        );
    }
    // The certificate's coherence flag against the same condition recomputed from BEHAVIOUR: the associator of every
    // ordered triple measured through the normalizer, then the five-vertex identity re-evaluated on those measured
    // signs. The declared table is internal and is never read here, so a certificate that compared the wrong two routes
    // would disagree with this even where its own perturbation probe still separated.
    private static string? CoherenceMatchesMeasuredIdentity(string name, PresentedAlgebra<BigInteger, IntegerMaterial> algebra) {
        var certificate = algebra.Certify(overlapLimit: (1L << 22));
        var count = algebra.Presentation.NormalFormCount;
        var associator = new BigInteger[((count * count) * count)];
        var product = new int[(count * count)];

        for (var first = 0; (first < count); ++first) {
            for (var second = 0; (second < count); ++second) {
                var cell = algebra.Multiply(
                    left: PresentedBasis(
                        algebra: algebra,
                        key: first
                    ),
                    right: PresentedBasis(
                        algebra: algebra,
                        key: second
                    )
                );

                if (1 != cell.SupportCount) { return $"{name}: the cell ({first},{second}) carries {cell.SupportCount} term(s), so no ratio of coefficients names its charge"; }

                product[((first * count) + second)] = ((int)cell.Keys[0]);

                for (var third = 0; (third < count); ++third) {
                    if (!TryMeasureAssociator(
                        algebra: algebra,
                        charge: out var charge,
                        first: first,
                        second: second,
                        third: third
                    )) {
                        return $"{name}: the triple ({first},{second},{third}) does not normalize to one term at one key either way, so its charge cannot be measured";
                    }

                    associator[((((first * count) + second) * count) + third)] = charge;
                }
            }
        }

        var holds = true;

        for (var first = 0; ((first < count) && holds); ++first) {
            for (var second = 0; ((second < count) && holds); ++second) {
                for (var third = 0; ((third < count) && holds); ++third) {
                    for (var fourth = 0; ((fourth < count) && holds); ++fourth) {
                        var (nested, flat) = MeasuredCoherenceRoutes(
                            associator: associator,
                            count: count,
                            first: first,
                            fourth: fourth,
                            product: product,
                            second: second,
                            third: third
                        );

                        holds = (nested == flat);
                    }
                }
            }
        }

        if (certificate.IsCoherent != holds) {
            return $"{name}: the certificate reports coherent={certificate.IsCoherent} where the five-vertex identity on the MEASURED charges reports {holds}";
        }

        // Every witness must be a quadruple whose two measured routes really differ, and must carry those two charges:
        // the three-factor route as Nested and the two-factor one as Flat.
        foreach (var witness in certificate.CoherenceWitness) {
            var (nested, flat) = MeasuredCoherenceRoutes(
                associator: associator,
                product: product,
                count: count,
                first: witness.First,
                second: witness.Second,
                third: witness.Third,
                fourth: witness.Fourth
            );

            if (
                (witness.Nested != nested) ||
                (witness.Flat != flat) ||
                (nested == flat)
            ) {
                return $"{name}: the witness ({witness.First},{witness.Second},{witness.Third},{witness.Fourth}) carries {witness.Nested} against {witness.Flat}, where the measured routes charge {nested} against {flat}";
            }
        }

        return null;
    }
    // The two routes of the coherence identity, on measured charges: the three-factor one that rebalances the inner
    // bracket first, and the two-factor one that rebalances the outer bracket first.
    private static (BigInteger Nested, BigInteger Flat) MeasuredCoherenceRoutes(BigInteger[] associator, int[] product, int count, int first, int second, int third, int fourth) {
        BigInteger Charge(int a, int b, int c) => associator[((((a * count) + b) * count) + c)];

        var nested = ((Charge(
            a: first,
            b: second,
            c: third
        ) * Charge(
            a: first,
            b: product[((second * count) + third)],
            c: fourth
        )) * Charge(
            a: second,
            b: third,
            c: fourth
        ));
        var flat = (Charge(
            a: first,
            b: second,
            c: product[((third * count) + fourth)]
        ) * Charge(
            a: product[((first * count) + second)],
            b: third,
            c: fourth
        ));

        return (nested, flat);
    }
    // The associator of one triple, MEASURED: both bracketings normalize to a single term at a single key, and the
    // charge the normalizer applied is the ratio of the two coefficients. Restricted to the sign cochains this file
    // declares, where the left-normed coefficient is a unit and the ratio is a product.
    private static bool TryMeasureAssociator(PresentedAlgebra<BigInteger, IntegerMaterial> algebra, int first, int second, int third, out BigInteger charge) {
        var a = Term.Leaf(symbol: first);
        var b = Term.Leaf(symbol: second);
        var c = Term.Leaf(symbol: third);

        charge = BigInteger.Zero;

        if (
            !algebra.TryNormalize(
            term: BracketPair(
                left: a,
                right: BracketPair(
                    left: b,
                    right: c
                )
            ),
            stepLimit: NormalizationSteps,
            normalForm: out var nested,
            obstruction: out _
        ) ||
            !algebra.TryNormalize(
            term: BracketPair(
                left: BracketPair(
                    left: a,
                    right: b
                ),
                right: c
            ),
            stepLimit: NormalizationSteps,
            normalForm: out var normed,
            obstruction: out _
        )
        ) {
            return false;
        }

        if (
            (1 != nested.SupportCount) ||
            (1 != normed.SupportCount) ||
            (nested.Keys[0] != normed.Keys[0])
        ) { return false; }

        if (!BigInteger.Abs(value: normed.Coefficients[0]).IsOne) { return false; }

        charge = (nested.Coefficients[0] * normed.Coefficients[0]);

        return true;
    }
    // How many ordered triples the declared charges move off the flattener's left-normed answer. Zero is what a uniform
    // charge of one gives, so it is what a dropped declaration gives too — and it is ALSO what a normalization that
    // refused every triple gives, since a refusal leaves both elements at the zero of the algebra and they compare
    // equal. The refusal count is therefore returned beside the answer and asserted by every caller: without it a step
    // limit one too low reads as a declaration that stopped arriving.
    private static int MovedBrackets<TValue, TOps>(PresentedAlgebra<TValue, TOps> algebra, out long refusals)
        where TOps : struct, IMaterialOps<TValue, TOps> {
        var count = algebra.Presentation.NormalFormCount;
        var moved = 0;

        refusals = 0L;

        for (var first = 0; (first < count); ++first) {
            for (var second = 0; (second < count); ++second) {
                for (var third = 0; (third < count); ++third) {
                    var a = Term.Leaf(symbol: first);
                    var b = Term.Leaf(symbol: second);
                    var c = Term.Leaf(symbol: third);

                    if (!algebra.TryNormalize(
                        term: BracketPair(
                            left: a,
                            right: BracketPair(
                                left: b,
                                right: c
                            )
                        ),
                        stepLimit: NormalizationSteps,
                        normalForm: out var nested,
                        obstruction: out _
                    )) { ++refusals; }

                    if (!algebra.TryNormalize(
                        term: BracketPair(
                            left: BracketPair(
                                left: a,
                                right: b
                            ),
                            right: c
                        ),
                        stepLimit: NormalizationSteps,
                        normalForm: out var normed,
                        obstruction: out _
                    )) { ++refusals; }

                    if (!algebra.AreEqual(
                        left: nested,
                        right: normed
                    )) { ++moved; }
                }
            }
        }

        return moved;
    }
    // How many ordered triples the tree normalizer answers differently from the SAME tree evaluated bracket by bracket
    // through the compiled cells. Zero is faithfulness; anything else is a declared cochain that is not this product's
    // associator, which is admitted and computed rather than refused. The refusal count travels beside it for the same
    // reason it does above.
    private static int UnfaithfulBrackets<TValue, TOps>(PresentedAlgebra<TValue, TOps> algebra, out long refusals)
        where TOps : struct, IMaterialOps<TValue, TOps> {
        var count = algebra.Presentation.NormalFormCount;
        var unfaithful = 0;

        refusals = 0L;

        for (var first = 0; (first < count); ++first) {
            for (var second = 0; (second < count); ++second) {
                for (var third = 0; (third < count); ++third) {
                    var nestedTerm = BracketPair(
                        left: Term.Leaf(symbol: first),
                        right: BracketPair(
                            left: Term.Leaf(symbol: second),
                            right: Term.Leaf(symbol: third)
                        )
                    );
                    var product = algebra.Multiply(
                        left: PresentedBasis(
                            algebra: algebra,
                            key: first
                        ),
                        right: algebra.Multiply(
                            left: PresentedBasis(
                                algebra: algebra,
                                key: second
                            ),
                            right: PresentedBasis(
                                algebra: algebra,
                                key: third
                            )
                        )
                    );

                    if (!algebra.TryNormalize(
                        normalForm: out var nested,
                        obstruction: out _,
                        stepLimit: NormalizationSteps,
                        term: nestedTerm
                    )) { ++refusals; }

                    if (!algebra.AreEqual(
                        left: nested,
                        right: product
                    )) { ++unfaithful; }
                }
            }
        }

        return unfaithful;
    }
    // The escape a certified-coherent declaration used to have: the unit written as its own generator letter and the
    // unit written as the empty product are two spellings of ONE element, and the second splices one time fewer, so a
    // cochain charging where the unit sits answered them differently while the certificate reported no witness at all.
    // Admission now refuses such a cochain; this is the behavioural half of that, run at every shipped declaration.
    private static string? SpellingsOfOneElementAgree<TValue, TOps>(string name, PresentedAlgebra<TValue, TOps> algebra)
        where TOps : struct, IMaterialOps<TValue, TOps> {
        if (1 != algebra.Identity.SupportCount) { return $"{name}: the unit is not one basis element, so it has no generator spelling"; }

        var count = algebra.Presentation.NormalFormCount;
        var unitKey = algebra.Identity.Keys[0];

        for (var first = 0; (first < count); ++first) {
            for (var second = 0; (second < count); ++second) {
                var a = Term.Leaf(symbol: first);
                var b = Term.Leaf(symbol: second);
                var unitLetter = Term.Leaf(symbol: ((int)unitKey));
                var empty = Term.Node(
                    children: [],
                    symbol: Term.Product
                );

                // The same three products, written six ways: the unit at each of the three positions, spelled once as
                // its letter and once as the empty product.
                foreach (var (letterTerm, emptyTerm) in new[] {
                    (BracketPair(
                    left: unitLetter,
                    right: BracketPair(
                        left: a,
                        right: b
                    )
                ), BracketPair(
                    left: empty,
                    right: BracketPair(
                        left: a,
                        right: b
                    )
                )),
                    (BracketPair(
                    left: a,
                    right: BracketPair(
                        left: unitLetter,
                        right: b
                    )
                ), BracketPair(
                    left: a,
                    right: BracketPair(
                        left: empty,
                        right: b
                    )
                )),
                    (BracketPair(
                    left: a,
                    right: BracketPair(
                        left: b,
                        right: unitLetter
                    )
                ), BracketPair(
                    left: a,
                    right: BracketPair(
                        left: b,
                        right: empty
                    )
                )),
                }) {
                    if (
                        !algebra.TryNormalize(
                        normalForm: out var letterForm,
                        obstruction: out _,
                        stepLimit: NormalizationSteps,
                        term: letterTerm
                    ) ||
                        !algebra.TryNormalize(
                        normalForm: out var emptyForm,
                        obstruction: out _,
                        stepLimit: NormalizationSteps,
                        term: emptyTerm
                    )
                    ) {
                        return $"{name}: a spelling of the product ({first},{second}) with the unit in it refused to normalize";
                    }

                    if (!algebra.AreEqual(
                        left: letterForm,
                        right: emptyForm
                    )) {
                        return $"{name}: the product ({first},{second}) answers [{ElementText(value: letterForm)}] with the unit spelled as a letter and [{ElementText(value: emptyForm)}] with it spelled as the empty product";
                    }
                }
            }
        }

        return null;
    }
    // The lexicographic index of one permutation row in the enumerated table, by linear scan — the table is small and
    // the scan shares no step with the presentation's own binary search.
    private static int RowKey(ReadOnlySpan<int> rows, ReadOnlySpan<int> row, int points, int elementCount) {
        for (var element = 0; (element < elementCount); ++element) {
            if (rows.Slice(
                length: points,
                start: (element * points)
            ).SequenceEqual(other: row)) { return element; }
        }

        return -1;
    }
    private static int RowKeyOfMirror(ReflectionSystem system, ReadOnlySpan<int> rows, int points, int elementCount, int mirror) {
        var row = new int[points];

        for (var point = 0; (point < points); ++point) {
            var image = SymmetryLattice.Reflect(
                node: system.Points[point],
                mirror: mirror
            );
            var index = system.Points.IndexOf(value: image);

            if (index < 0) { return -1; }

            row[point] = index;
        }

        return RowKey(
            elementCount: elementCount,
            points: points,
            row: row,
            rows: rows
        );
    }
    // The Coxeter element: the mirrors read once, in the order the system carries them, as one element of the group.
    private static int CoxeterElementKey<TValue, TOps>(PresentedAlgebra<TValue, TOps> algebra, ReflectionSystem system, ReadOnlySpan<int> rows, int points, int elementCount)
        where TOps : struct, IMaterialOps<TValue, TOps> {
        var row = new int[points];

        for (var point = 0; (point < points); ++point) {
            var image = system.Points[point];

            foreach (var mirror in system.Mirrors) {
                image = SymmetryLattice.Reflect(
                mirror: mirror,
                node: image
            );
            }

            row[point] = system.Points.IndexOf(value: image);
        }

        return RowKey(
            elementCount: elementCount,
            points: points,
            row: row,
            rows: rows
        );
    }
    // Stokes at one material: the chain-complex condition, the adjunction on every ordered basis pair and at dense
    // operands, the pairing against the coefficientwise sum, and the non-degeneracy count that keeps a collapsed
    // operator from passing.
    private static string? StokesHolds<TValue, TOps>(string name, int[] dimensions, (int Face, int Coface, int Sign)[] incidences, TOps material, Func<int, TValue> weight)
        where TOps : struct, IMaterialOps<TValue, TOps> {
        var calculus = ExteriorCalculus<TValue, TOps>.Create(
            dimensions: dimensions,
            incidences: incidences,
            material: material
        );
        var algebra = calculus.Poset.Algebra;
        var cellCount = calculus.CellCount;
        var comparer = EqualityComparer<TValue>.Default;
        var nonzero = 0;

        if (0 != algebra.Multiply(
            left: calculus.Incidence,
            right: calculus.Incidence
        ).SupportCount) {
            return $"{name}: the incidence element does not square to zero, so the declared numbers are no chain complex";
        }

        for (var left = 0; (left < cellCount); ++left) {
            var cochain = algebra.FromSupport(
                keys: [calculus.CochainKey(cell: left)],
                coefficients: [material.One]
            );
            var raisedCochain = calculus.Coboundary(cochain: cochain);

            for (var right = 0; (right < cellCount); ++right) {
                var chain = algebra.FromSupport(
                    keys: [calculus.ChainKey(cell: right)],
                    coefficients: [material.One]
                );
                var raised = calculus.Pair(
                    chain: chain,
                    cochain: raisedCochain
                );
                var lowered = calculus.Pair(
                    cochain: cochain,
                    chain: calculus.Boundary(chain: chain)
                );

                if (!comparer.Equals(
                    x: raised,
                    y: lowered
                )) { return $"{name}: Stokes fails at cochain {left} and chain {right}: {raised} against {lowered}"; }

                if (!material.IsZero(value: raised)) { ++nonzero; }
            }
        }

        // The ordered basis pairs Stokes does not annihilate are exactly the declared incidences, so an operator that
        // collapsed to zero — or a coboundary that lost the covering relation — fails here rather than passing on an
        // identity between two zeros.
        if (nonzero != incidences.Length) {
            return $"{name}: {nonzero} of {(cellCount * cellCount)} ordered basis pairs pair nontrivially, where the complex declares {incidences.Length} incidence(s)";
        }

        for (var shift = 0; (shift < cellCount); ++shift) {
            var chainValues = new TValue[cellCount];
            var cochainValues = new TValue[cellCount];
            var total = material.Zero;

            for (var cell = 0; (cell < cellCount); ++cell) {
                chainValues[cell] = weight(((cell + shift) % cellCount));
                cochainValues[cell] = weight(((cell + (2 * shift)) % cellCount));
            }

            for (var cell = 0; (cell < cellCount); ++cell) {
                total = material.Add(
                    left: total,
                    right: material.Multiply(
                        left: chainValues[cell],
                        right: cochainValues[cell]
                    )
                );
            }

            var chain = calculus.Chain(values: chainValues);
            var cochain = calculus.Cochain(values: cochainValues);

            if (!comparer.Equals(
                x: calculus.Pair(
                    chain: chain,
                    cochain: cochain
                ),
                y: total
            )) {
                return $"{name}: the pairing is not the sum of the coefficientwise products at shift {shift}";
            }

            if (!comparer.Equals(
                x: calculus.Pair(
                    cochain: calculus.Coboundary(cochain: cochain),
                    chain: chain
                ),
                y: calculus.Pair(
                    cochain: cochain,
                    chain: calculus.Boundary(chain: chain)
                )
            )) {
                return $"{name}: Stokes fails at dense operands, shift {shift}";
            }
        }

        return null;
    }

    // ---- phase 4: boundary composability, and co-arity greater than one ----

    // The tabulated Catalan numbers, transcribed rather than recurred, so the block dimensions answer to a published
    // sequence instead of to arithmetic the subject could share.
    private static readonly int[] TangleCatalanTable = [1, 1, 2, 5, 14, 42, 132, 429];
    // The whole basis size at each width, likewise transcribed: the even-sum Catalan sums the width cap is derived from,
    // 377 at width six against the 512 normal forms a finite basis holds, and 1182 at width seven, which is refused.
    private static readonly int[] TangleBasisTable = [1, 2, 6, 15, 43, 123, 377];
    // The loop-charge canary's floors. MEASURED on the derived tables: 4 loop-creating ordered pairs of 36 at width two,
    // 22 of 225 at width three, and 242 of 1849 at width four. Each floor is two thirds of its own measurement — a third
    // below it — so a loop charge that quietly stopped being applied, reporting zero, fails every row while an honest
    // table clears every one.
    private static readonly (int Width, int Minimum)[] TangleLoopFloors = [(2, 2), (3, 14), (4, 161)];

    /// <summary>Proves the planar basis has the dimensions the Catalan numbers give it, block by block and in total, at
    /// two independent countings, and that the generator boundaries carry the widths those blocks name.</summary>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    /// <remarks>The second counting is the ballot difference <c>C(2p, p) − C(2p, p−1)</c> over
    /// <see cref="BigInteger"/> binomials, which reaches the Catalan number without a Catalan recursion and so catches a
    /// mis-transcribed table as well as a mis-enumerated basis.</remarks>
    public static string? TangleBasisCounts() =>
        TangleBasisCountsThrough(maximumWidth: 5);
    /// <summary>Proves the whole width-two composition table — the six diagrams — against the arc-tracing oracle, and
    /// that the sum of the identity diagrams acts as the unit on every one of them.</summary>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? TangleComposesAtSmokeWidth() =>
        (TangleBasisCountsThrough(maximumWidth: 2) ?? TangleTableMatchesOracle(maximumWidth: 2));
    /// <summary>Proves the derived product satisfies the three algebraic relations of a tangle algebra: a cup-cap
    /// generator squares to the loop charge times itself, an adjacent triple collapses, and a distant pair
    /// commutes.</summary>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    /// <remarks>The relations are asserted on the product the composition table produces rather than declared as rules,
    /// which is why they are evidence: nothing in the catalogue entry mentions them, so a mis-traced arc or a
    /// mis-counted loop breaks one of the three.</remarks>
    public static string? TangleRelationsHold() =>
        (TangleRelations(
            loopCharge: new BigInteger(value: 3),
            material: default(IntegerMaterial)
        )
            ?? (TangleRelations(
            loopCharge: RealQuadratic.Rational(
                denominator: 2,
                numerator: 5
            ),
            material: default(RationalMaterial)
        )
            ?? TangleRelations(
            loopCharge: 4UL,
            material: new PrimeFieldMaterial(field: PrimeField64.Create(modulus: 1_000_003UL))
        )));
    /// <summary>Proves that deriving composability from the generator boundaries reproduces, cell for cell, the
    /// endpoint tests the quiver and the interval poset used to write out by hand.</summary>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    /// <remarks>
    /// <para>
    /// The prediction is made from the ARGUMENT data — an arrow's endpoints, a poset relation's transitive closure taken
    /// by breadth-first search from each element — so it shares no step with the catalogue entry, and the boundary
    /// comparison is separately restated over independently constructed <see cref="Generator"/> values and required to
    /// agree with the table's annihilation pattern.
    /// </para>
    /// <para>
    /// The colour count is read at all three entries: a quiver has one colour per object, a poset one per element, and a
    /// tangle exactly one however wide it gets, because a tangle's boundaries carry their LENGTH rather than their
    /// colours. That is the two halves of one comparison, each exercised by an entry the other does not.
    /// </para>
    /// </remarks>
    public static string? BoundaryCompositionUnmoved() {
        for (var objects = 1; (objects <= 5); ++objects) {
            var arrows = new (int Source, int Target, BigInteger Weight)[(objects * objects)];

            for (var source = 0; (source < objects); ++source) {
                for (var target = 0; (target < objects); ++target) {
                    arrows[((source * objects) + target)] = (source, target, (((source * objects) + target) + 1));
                }
            }

            var presentation = Presentations.Quiver<BigInteger, IntegerMaterial>(
                arrows: arrows,
                material: default,
                objectCount: objects
            );
            var compiled = PresentedAlgebra<BigInteger, IntegerMaterial>.Create(presentation: presentation).Compile();
            var generators = new Generator[(objects * objects)];

            for (var source = 0; (source < objects); ++source) {
                for (var target = 0; (target < objects); ++target) {
                    generators[((source * objects) + target)] = new Generator(
                        degree: 1,
                        inputs: new[] { source },
                        outputs: new[] { target },
                        symbol: ((source * objects) + target)
                    );
                }
            }

            if (presentation.ColourCount != objects) {
                return $"quiver({objects}): the presentation reports {presentation.ColourCount} boundary colours, where the objects give it {objects}";
            }

            for (var left = 0; (left < generators.Length); ++left) {
                for (var right = 0; (right < generators.Length); ++right) {
                    var composes = ((left % objects) == (right / objects));
                    var target = (((left / objects) * objects) + (right % objects));

                    if (BoundariesMeetHere(
                        left: generators[left],
                        right: generators[right]
                    ) != composes) {
                        return $"quiver({objects}): the boundary comparison disagrees with the endpoint test at the ordered pair ({left}, {right})";
                    }

                    if (CellDisagrees(
                        compiled: compiled,
                        left: left,
                        right: right,
                        composes: composes,
                        target: target,
                        charge: BigInteger.One
                    ) is { } detail) {
                        return $"quiver({objects}): {detail}";
                    }
                }
            }
        }

        foreach (var (elements, relations) in TanglePosetOrders) {
            var order = ReachabilityClosure(
                elements: elements,
                relations: relations
            );
            var lowerOf = new List<int>();
            var upperOf = new List<int>();
            var symbolOf = new int[(elements * elements)];

            Array.Fill(
                array: symbolOf,
                value: -1
            );

            for (var lower = 0; (lower < elements); ++lower) {
                for (var upper = 0; (upper < elements); ++upper) {
                    if (!order[((lower * elements) + upper)]) { continue; }

                    symbolOf[((lower * elements) + upper)] = lowerOf.Count;

                    lowerOf.Add(item: lower);
                    upperOf.Add(item: upper);
                }
            }

            var presentation = Presentations.IntervalPoset<BigInteger, IntegerMaterial>(
                elementCount: elements,
                material: default,
                relations: relations
            );
            var compiled = PresentedAlgebra<BigInteger, IntegerMaterial>.Create(presentation: presentation).Compile();

            if (presentation.GeneratorCount != lowerOf.Count) {
                return $"poset({elements}): the presentation carries {presentation.GeneratorCount} intervals, where the closure names {lowerOf.Count}";
            }

            if (presentation.ColourCount != elements) {
                return $"poset({elements}): the presentation reports {presentation.ColourCount} boundary colours, where the elements give it {elements}";
            }

            for (var left = 0; (left < lowerOf.Count); ++left) {
                for (var right = 0; (right < lowerOf.Count); ++right) {
                    var composes = (upperOf[left] == lowerOf[right]);
                    var target = (composes
                        ? symbolOf[((lowerOf[left] * elements) + upperOf[right])]
                        : -1
                    );
                    var leftGenerator = new Generator(
                        symbol: left,
                        inputs: new[] { lowerOf[left] },
                        outputs: new[] { upperOf[left] },
                        degree: 1
                    );
                    var rightGenerator = new Generator(
                        symbol: right,
                        inputs: new[] { lowerOf[right] },
                        outputs: new[] { upperOf[right] },
                        degree: 1
                    );

                    if (BoundariesMeetHere(
                        left: leftGenerator,
                        right: rightGenerator
                    ) != composes) {
                        return $"poset({elements}): the boundary comparison disagrees with the endpoint test at the ordered pair ({left}, {right})";
                    }

                    if (CellDisagrees(
                        compiled: compiled,
                        left: left,
                        right: right,
                        composes: composes,
                        target: target,
                        charge: BigInteger.One
                    ) is { } detail) {
                        return $"poset({elements}): {detail}";
                    }
                }
            }
        }

        // The other half of the same comparison: a tangle is single-coloured however wide it gets, so what its
        // boundaries carry is their length, and co-arity greater than one is what decides its cells.
        for (var width = 1; (width <= 4); ++width) {
            var presentation = Presentations.PlanarTangle<BigInteger, IntegerMaterial>(
                loopCharge: 3,
                material: default,
                maximumWidth: width
            );

            if (1 != presentation.ColourCount) {
                return $"tangle({width}): the presentation reports {presentation.ColourCount} boundary colours, where a single-wire diagram algebra has one";
            }

            var basis = Oracles.PlanarDiagrams(maximumWidth: width);
            var compiled = PresentedAlgebra<BigInteger, IntegerMaterial>.Create(presentation: presentation).Compile();

            for (var left = 0; (left < basis.Count); ++left) {
                for (var right = 0; (right < basis.Count); ++right) {
                    var leftGenerator = new Generator(
                        symbol: left,
                        inputs: new int[basis[left].InputWidth],
                        outputs: new int[basis[left].OutputWidth],
                        degree: 1
                    );
                    var rightGenerator = new Generator(
                        symbol: right,
                        inputs: new int[basis[right].InputWidth],
                        outputs: new int[basis[right].OutputWidth],
                        degree: 1
                    );
                    var composes = (basis[left].OutputWidth == basis[right].InputWidth);

                    if (BoundariesMeetHere(
                        left: leftGenerator,
                        right: rightGenerator
                    ) != composes) {
                        return $"tangle({width}): the boundary comparison disagrees with the wire count at the ordered pair ({left}, {right})";
                    }

                    if ((0 == compiled.TargetCount(
                        leftKey: left,
                        rightKey: right
                    )) == composes) {
                        return $"tangle({width}): the ordered pair ({left}, {right}) {(composes
                            ? "annihilates where its wires meet"
                            : "composes where its wires do not meet")}";
                    }
                }
            }
        }

        return null;
    }
    /// <summary>Proves the tangle entry refuses the widths whose diagrams no finite basis of this library holds, and
    /// refuses them by naming the argument that carried the data.</summary>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    /// <remarks>The other half of the cap — that the LAST admitted width is reachable and carries its 377 diagrams — is
    /// asserted where a width-six presentation is already built and paid for: <c>deep.presented-tangle-sweep</c>, whose
    /// basis walk runs every width through six against the transcribed totals. A default-tier law does not build a
    /// 377-key presentation, whose composition table is one rule per ordered pair, to restate a count that case
    /// already makes; the refusals here cost one throw each.</remarks>
    public static string? TangleLimitsRefuse() =>
        (RefusesDeclaration(
            name: "a negative tangle width",
            build: static () => _ = Presentations.PlanarTangle<BigInteger, IntegerMaterial>(
                loopCharge: 3,
                material: default,
                maximumWidth: -1
            )
        )
            ?? RefusesDeclaration(
            name: "a tangle width whose diagrams outrun a finite basis",
            build: static () => _ = Presentations.PlanarTangle<BigInteger, IntegerMaterial>(
                loopCharge: 3,
                material: default,
                maximumWidth: 7
            )
        ));
    /// <summary>The loop-charge canary: composing two diagrams must actually strand off closed loops, on more ordered
    /// pairs than the measured floor.</summary>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    /// <remarks>Without it a loop charge silently equal to the material's one — or an arc trace that never counted a
    /// loop at all — would leave the dimensions, the relations, the unit and the refusals all green, because every one
    /// of those statements holds just as well at a trivial charge.</remarks>
    public static string? TangleLoopChargeCanary() {
        foreach (var (width, minimum) in TangleLoopFloors) {
            var basis = Oracles.PlanarDiagrams(maximumWidth: width);
            var presentation = Presentations.PlanarTangle<BigInteger, IntegerMaterial>(
                loopCharge: 3,
                material: default,
                maximumWidth: width
            );
            var compiled = PresentedAlgebra<BigInteger, IntegerMaterial>.Create(presentation: presentation).Compile();
            var charged = 0;
            var looped = 0;

            for (var left = 0; (left < basis.Count); ++left) {
                for (var right = 0; (right < basis.Count); ++right) {
                    if (basis[left].OutputWidth != basis[right].InputWidth) { continue; }

                    var (_, _, _, loops) = Oracles.PlanarCompose(
                        basis: basis,
                        left: left,
                        right: right
                    );

                    if (loops.IsZero) { continue; }

                    ++looped;

                    if (compiled.Charge(
                        leftKey: left,
                        rightKey: right
                    ) != BigInteger.Pow(
                        exponent: ((int)loops),
                        value: 3
                    )) { continue; }

                    ++charged;
                }
            }

            if (
                (looped < minimum) ||
                (charged != looped)
            ) {
                return $"tangle({width}): {looped} ordered pair(s) create a closed loop and {charged} of them carry the loop charge, against the floor of {minimum}";
            }
        }

        return null;
    }
    /// <summary>The deep sweep: the whole basis and the whole composition table at the last admissible width, against
    /// the arc-tracing oracle, with the three algebraic relations checked at five and six strands.</summary>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? TangleDeepSweep() =>
        (TangleBasisCountsThrough(maximumWidth: 6) ?? TangleTableMatchesOracle(maximumWidth: 6));

    // The orders the poset half of the boundary claim runs over: a point, a chain, a diamond, a fence, and an
    // antichain, so both the composable and the annihilating case are dense at each.
    private static readonly (int Elements, (int Lower, int Upper)[] Relations)[] TanglePosetOrders = [
        (1, []),
        (4, [(0, 1), (1, 2), (2, 3)]),
        (4, [(0, 1), (0, 2), (1, 3), (2, 3)]),
        (5, [(0, 2), (1, 2), (2, 3), (2, 4)]),
        (3, []),
    ];

    // The boundary comparison, restated over generators this file builds: the wires handed over must BE the wires
    // taken, in number and in colour. The two halves are written out HERE, where the library states them as one span
    // equality that already decides both, so this is an independent restatement rather than a copy of that line — and
    // it is where Arity and Coarity are read and required to agree with the widths the table annihilates on. What the
    // law asserts is that the compiled table annihilates exactly where this says it should, which is a statement about
    // the table rather than about the expression.
    private static bool BoundariesMeetHere(Generator left, Generator right) =>
        ((left.Coarity == right.Arity) && left.Outputs.SequenceEqual(other: right.Inputs));
    private static string? CellDisagrees(CompiledProduct<BigInteger> compiled, int left, int right, bool composes, int target, BigInteger charge) {
        var entries = compiled.TargetCount(
            leftKey: left,
            rightKey: right
        );

        if (!composes) {
            return ((0 == entries)
                ? null
                : $"the ordered pair ({left}, {right}) reduces to {entries} term(s) where its boundaries do not meet"
            );
        }

        if (1 != entries) { return $"the ordered pair ({left}, {right}) reduces to {entries} term(s) where its boundaries meet"; }
        if (compiled.Target(
            leftKey: left,
            rightKey: right
        ) != target) {
            return $"the ordered pair ({left}, {right}) composes to {compiled.Target(
            leftKey: left,
            rightKey: right
        )}, not {target}";
        }
        if (compiled.Charge(
            leftKey: left,
            rightKey: right
        ) != charge) {
            return $"the ordered pair ({left}, {right}) carries the charge {compiled.Charge(
            leftKey: left,
            rightKey: right
        )}, not {charge}";
        }

        return null;
    }
    // The transitive-reflexive closure by breadth-first search from each element, which shares no step with the triple
    // loop the catalogue entry takes.
    private static bool[] ReachabilityClosure(int elements, (int Lower, int Upper)[] relations) {
        var order = new bool[(elements * elements)];

        for (var start = 0; (start < elements); ++start) {
            var frontier = new Queue<int>();

            order[((start * elements) + start)] = true;
            frontier.Enqueue(item: start);

            while (frontier.Count > 0) {
                var current = frontier.Dequeue();

                foreach (var (lower, upper) in relations) {
                    if (
                        (lower != current) ||
                        order[((start * elements) + upper)]
                    ) { continue; }

                    order[((start * elements) + upper)] = true;

                    frontier.Enqueue(item: upper);
                }
            }
        }

        return order;
    }
    private static string? TangleBasisCountsThrough(int maximumWidth) {
        var basis = Oracles.PlanarDiagrams(maximumWidth: maximumWidth);

        for (var width = 0; (width <= maximumWidth); ++width) {
            var presentation = Presentations.PlanarTangle<BigInteger, IntegerMaterial>(
                loopCharge: 3,
                material: default,
                maximumWidth: width
            );

            if (
                !presentation.HasCompiledNormalFormBasis ||
                (presentation.NormalFormCount != TangleBasisTable[width]) ||
                (presentation.GeneratorCount != TangleBasisTable[width])
            ) {
                return $"tangle({width}): {presentation.GeneratorCount} generator(s) and {presentation.NormalFormCount} normal form(s), where the even-sum Catalan total is {TangleBasisTable[width]}";
            }

            // The generators ARE the normal forms, so a key is a one-letter word and nothing about the basis depends on
            // the discovery order.
            for (var key = 0; (key < presentation.NormalFormCount); ++key) {
                var word = presentation.NormalFormWord(key: key);

                if (
                    (1 != word.Length) ||
                    (key != word[0])
                ) {
                    return $"tangle({width}): the normal form at key {key} is not the one-letter word naming its own diagram";
                }
            }
        }

        for (var inputs = 0; (inputs <= maximumWidth); ++inputs) {
            for (var outputs = 0; (outputs <= maximumWidth); ++outputs) {
                var block = basis.Count(predicate: diagram => ((diagram.InputWidth == inputs) && (diagram.OutputWidth == outputs)));
                var points = (inputs + outputs);

                if (0 != (points & 1)) {
                    if (0 != block) { return $"tangle block ({inputs}, {outputs}): {block} diagram(s) over an odd boundary, which no perfect matching covers"; }

                    continue;
                }

                var half = (points / 2);
                var ballot = (Binomial(
                    lower: half,
                    upper: points
                ) - Binomial(
                    lower: (half - 1),
                    upper: points
                ));

                if (
                    (block != TangleCatalanTable[half]) ||
                    (ballot != TangleCatalanTable[half])
                ) {
                    return $"tangle block ({inputs}, {outputs}): {block} diagram(s), where the Catalan table says {TangleCatalanTable[half]} and the ballot difference says {ballot}";
                }
            }
        }

        return null;
    }
    // The composition table against the arc-tracing oracle, exhaustively, plus the unit: every ordered pair whose
    // boundaries meet composes to the diagram the oracle traces, at the loop charge raised to the loops the oracle
    // counts, and the sum of the identity diagrams fixes every basis element from both sides.
    private static string? TangleTableMatchesOracle(int maximumWidth) {
        var basis = Oracles.PlanarDiagrams(maximumWidth: maximumWidth);
        var symbols = Oracles.PlanarSymbols(basis: basis);
        var algebra = PresentedAlgebra<BigInteger, IntegerMaterial>.Create(presentation: Presentations.PlanarTangle<BigInteger, IntegerMaterial>(
            loopCharge: 3,
            material: default,
            maximumWidth: maximumWidth
        ));
        var compiled = algebra.Compile();

        if (compiled.KeyCount != basis.Count) {
            return $"tangle({maximumWidth}): the compiled table carries {compiled.KeyCount} keys against the oracle's {basis.Count} diagrams";
        }

        for (var left = 0; (left < basis.Count); ++left) {
            for (var right = 0; (right < basis.Count); ++right) {
                var composes = (basis[left].OutputWidth == basis[right].InputWidth);

                if (!composes) {
                    if (0 != compiled.TargetCount(
                        leftKey: left,
                        rightKey: right
                    )) {
                        return $"tangle({maximumWidth}): the ordered pair ({left}, {right}) composes where its wires do not meet";
                    }

                    continue;
                }

                var (inputWidth, outputWidth, code, loops) = Oracles.PlanarCompose(
                    basis: basis,
                    left: left,
                    right: right
                );

                if (CellDisagrees(
                    compiled: compiled,
                    left: left,
                    right: right,
                    composes: true,
                    target: symbols[(inputWidth, outputWidth, code)],
                    charge: BigInteger.Pow(
                        exponent: ((int)loops),
                        value: 3
                    )
                ) is { } detail) {
                    return $"tangle({maximumWidth}): {detail}";
                }
            }
        }

        var identity = algebra.Identity;

        if (identity.SupportCount != (maximumWidth + 1)) {
            return $"tangle({maximumWidth}): the unit carries {identity.SupportCount} identity diagram(s), where one per width is {(maximumWidth + 1)}";
        }

        for (var symbol = 0; (symbol < basis.Count); ++symbol) {
            var element = algebra.Generator(symbol: symbol);

            if (
                !algebra.AreEqual(
                left: algebra.Multiply(
                    left: identity,
                    right: element
                ),
                right: element
            ) ||
                !algebra.AreEqual(
                left: algebra.Multiply(
                    left: element,
                    right: identity
                ),
                right: element
            )
            ) {
                return $"tangle({maximumWidth}): the sum of the identity diagrams does not fix the diagram {symbol}";
            }
        }

        return null;
    }
    // The three algebraic relations, at one material. e_i is the diagram that caps two adjacent inputs, cups the two
    // outputs below them, and carries every other wire straight through — the one place a tangle's cups and caps show
    // up as a single basis element with co-arity equal to its arity.
    private static string? TangleRelations<TValue, TOps>(TValue loopCharge, TOps material)
        where TOps : struct, IMaterialOps<TValue, TOps> {
        const int MaximumStrands = 4;

        var basis = Oracles.PlanarDiagrams(maximumWidth: MaximumStrands);
        var algebra = PresentedAlgebra<TValue, TOps>.Create(presentation: Presentations.PlanarTangle<TValue, TOps>(
            loopCharge: loopCharge,
            material: material,
            maximumWidth: MaximumStrands
        ));

        for (var strands = 2; (strands <= MaximumStrands); ++strands) {
            for (var index = 1; (index < strands); ++index) {
                var hook = algebra.Generator(symbol: HookDiagram(
                    basis: basis,
                    index: index,
                    strands: strands
                ));
                var keys = hook.Keys.ToArray();
                var scaled = new TValue[keys.Length];

                for (var entry = 0; (entry < keys.Length); ++entry) {
                    scaled[entry] = material.Multiply(
                    left: hook.Coefficients[entry],
                    right: loopCharge
                );
                }

                if (!algebra.AreEqual(
                    left: algebra.Multiply(
                        left: hook,
                        right: hook
                    ),
                    right: algebra.FromSupport(
                        coefficients: scaled,
                        keys: keys
                    )
                )) {
                    return $"tangle relations ({typeof(TOps).Name}): the hook at strand {index} of {strands} does not square to the loop charge times itself";
                }

                for (var other = 1; (other < strands); ++other) {
                    var separation = Math.Abs(value: (other - index));

                    if (0 == separation) { continue; }

                    var neighbour = algebra.Generator(symbol: HookDiagram(
                        basis: basis,
                        index: other,
                        strands: strands
                    ));

                    if (1 == separation) {
                        if (!algebra.AreEqual(
                            left: algebra.Multiply(
                                left: algebra.Multiply(
                                    left: hook,
                                    right: neighbour
                                ),
                                right: hook
                            ),
                            right: hook
                        )) {
                            return $"tangle relations ({typeof(TOps).Name}): the adjacent triple at strands {index} and {other} of {strands} does not collapse";
                        }

                        continue;
                    }

                    if (!algebra.AreEqual(
                        left: algebra.Multiply(
                            left: hook,
                            right: neighbour
                        ),
                        right: algebra.Multiply(
                            left: neighbour,
                            right: hook
                        )
                    )) {
                        return $"tangle relations ({typeof(TOps).Name}): the distant pair at strands {index} and {other} of {strands} does not commute";
                    }
                }
            }
        }

        return null;
    }
    // The hook diagram at one strand: every wire straight through except the adjacent pair, which is capped above and
    // cupped below.
    private static int HookDiagram(IReadOnlyList<Oracles.PlanarDiagram> basis, int strands, int index) {
        var partner = StraightThroughDiagram(strands: strands);

        partner[(index - 1)] = index;
        partner[index] = (index - 1);
        partner[(((2 * strands) - 1) - index)] = (((2 * strands) - 1) - (index - 1));
        partner[(((2 * strands) - 1) - (index - 1))] = (((2 * strands) - 1) - index);

        return PlanarSymbolOf(
            basis: basis,
            inputWidth: strands,
            outputWidth: strands,
            partner: partner
        );
    }
    // The straight-through matching at one width: input wire j joined to output wire j, which sits at the boundary
    // position mirroring it because the outputs are read in reverse.
    private static int[] StraightThroughDiagram(int strands) {
        var partner = new int[(2 * strands)];

        for (var wire = 0; (wire < strands); ++wire) {
            partner[wire] = (((2 * strands) - 1) - wire);
            partner[(((2 * strands) - 1) - wire)] = wire;
        }

        return partner;
    }
    // A diagram's symbol, located in the oracle basis by its matching, so the catalogue's key scheme is read rather than
    // reconstructed. The order IS the key scheme, which is the one thing the oracle and the entry share.
    private static int PlanarSymbolOf(IReadOnlyList<Oracles.PlanarDiagram> basis, int inputWidth, int outputWidth, int[] partner) {
        for (var symbol = 0; (symbol < basis.Count); ++symbol) {
            if (
                (basis[symbol].InputWidth != inputWidth) ||
                (basis[symbol].OutputWidth != outputWidth)
            ) { continue; }
            if (basis[symbol].Partner.SequenceEqual(second: partner)) { return symbol; }
        }

        throw new InvalidOperationException(message: $"no ({inputWidth}, {outputWidth}) diagram carries that matching");
    }
    private static BigInteger Binomial(int upper, int lower) {
        if (
            (lower < 0) ||
            (lower > upper)
        ) { return BigInteger.Zero; }

        var value = BigInteger.One;

        for (var step = 0; (step < lower); ++step) { value = ((value * (upper - step)) / (step + 1)); }

        return value;
    }
    // A deterministic integer matrix at a given shape: one multiplicative congruential walk, entries centred on zero,
    // so the draw is a function of the shape and the seed and of nothing else.
    private static BigInteger[] ScaledMatrix(int rows, int columns, int seed, int span) {
        var entries = new BigInteger[(rows * columns)];
        var state = ((ulong)((seed * 6_364_136_223_846_793_005L) + 1_442_695_040_888_963_407L));

        for (var index = 0; (index < entries.Length); ++index) {
            state = ((state * 6_364_136_223_846_793_005UL) + 1_442_695_040_888_963_407UL);
            entries[index] = (((int)((state >> 33) % ((ulong)((2 * span) + 1)))) - span);
        }

        return entries;
    }

}
