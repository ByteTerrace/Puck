using System.Numerics;

namespace Puck.Maths.Tests;

internal static partial class Subjects {
    // ---- phase 4: the braiding certificate ----

    // The Clifford signatures the braiding is read at. The first four are non-degenerate, where the commutation charge
    // is a genuine sign on every pair; the last two carry degenerate generators, whose pairs annihilate BOTH ways and
    // so are commutable at the material's one, which is what makes the hexagon identity fail there.
    private static readonly (int Positive, int Negative, int Degenerate)[] BraidingSignatures = [(3, 0, 0), (2, 1, 0), (0, 3, 0), (4, 1, 0), (2, 0, 1), (3, 0, 1)];
    // The nontrivial-braiding canary's floors. MEASURED on the derived charges: 6 ordered basis pairs of 16 carry a
    // charge that is not the material's one at the quaternion floor, 42 of 64 at the octonion floor, and 480 of 1024 at
    // the conformal Clifford signature. Each floor is two thirds of its own measurement — a third below it — so a
    // braiding that collapsed to the trivial one, reporting zero nontrivial pairs, fails every row while an honest
    // derivation clears every one.
    private static readonly (string Name, int Minimum)[] BraidingNontrivialFloors = [("cayley-dickson(2)", 4), ("cayley-dickson(3)", 28), ("clifford(4,1,0)", 320)];

    /// <summary>Proves every derived commutation charge equals the one the doubling recursion and the bubble-sort
    /// charge oracle give it, at the Cayley–Dickson floors and at six Clifford signatures, and — at the octonion floor —
    /// the one the hand-written nested <see cref="DoublingAlgebra{TInner}"/> tower reaches by multiplying the two
    /// orderings out.</summary>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    /// <remarks>The charge is DERIVED from the compiled cells, so the oracle is the ratio of the two cells' own signs
    /// recomputed from a construction that reads no cell: the doubling recursion for the tower, an explicit
    /// generator-list bubble sort for the signatures. Where both orderings annihilate, no ratio exists and the
    /// material's one is the charge every candidate verifies, which the oracle states rather than skips.
    /// <para>The tower's recursion is a TRANSCRIPTION of the one the presentation carries — faithful carriage, not an
    /// independent sign rule — so the shipped nested doubling tower multiplies both orderings out beside it, at every
    /// floor the tower ships rather than at the octonion floor alone. The bubble-sort oracle needs no such witness: it
    /// counts transpositions of an explicit generator list and shares nothing with the presentation.</para></remarks>
    public static string? BraidingDerivedVsDoubling() {
        for (var floors = 1; (floors <= 4); ++floors) {
            var algebra = PresentedAlgebra<BigInteger, IntegerMaterial>.Create(presentation: Presentations.CayleyDickson<BigInteger, IntegerMaterial>(
                floors: floors,
                basisRelabelling: [],
                material: default
            ));
            var certificate = algebra.Certify(overlapLimit: (1L << 22));
            var dimension = (1 << floors);

            for (var left = 0; (left < dimension); ++left) {
                for (var right = 0; (right < dimension); ++right) {
                    var expected = new BigInteger(value: (Oracles.CayleyDicksonCharge(
                        floors: floors,
                        leftIndex: left,
                        rightIndex: right
                    ) * Oracles.CayleyDicksonCharge(
                        floors: floors,
                        leftIndex: right,
                        rightIndex: left
                    )));
                    var derived = certificate.BraidingCharge(
                        leftKey: left,
                        rightKey: right
                    );

                    if (derived != expected) {
                        return $"cayley-dickson({floors}): the pair ({left},{right}) commutes at {derived}, where the doubling recursion's two signs give it {expected}";
                    }

                    // The witness the transcribed charge oracle needs beside it, at EVERY floor the tower ships: both
                    // orderings multiplied out through the shipped nested tower, which shares no code with either side.
                    var tower = new BigInteger(value: (DoublingTowerUnitCharge(
                        floors: floors,
                        left: left,
                        right: right
                    ) * DoublingTowerUnitCharge(
                        floors: floors,
                        left: right,
                        right: left
                    )));

                    if (tower != expected) {
                        return $"cayley-dickson({floors}): the pair ({left},{right}) commutes at {expected} against the doubling recursion, where the nested tower multiplies out to {tower}";
                    }
                }
            }
        }

        foreach (var (positive, negative, degenerate) in BraidingSignatures) {
            var binding = CliffordBinding(
                degenerateCount: degenerate,
                negativeCount: negative,
                positiveCount: positive
            );
            var algebra = PresentedAlgebra<BigInteger, IntegerMaterial>.Create(presentation: Presentations.Clifford<BigInteger, IntegerMaterial>(
                degenerateCount: degenerate,
                material: default,
                negativeCount: negative,
                positiveCount: positive
            ));
            var certificate = algebra.Certify(overlapLimit: (1L << 22));
            var name = $"clifford({positive},{negative},{degenerate})";

            for (var left = 0; (left < binding.KeyToLane.Length); ++left) {
                for (var right = 0; (right < binding.KeyToLane.Length); ++right) {
                    var forward = Oracles.CliffordCharge(
                        leftBlade: binding.KeyToLane[left],
                        rightBlade: binding.KeyToLane[right],
                        positiveCount: positive,
                        negativeCount: negative,
                        degenerateCount: degenerate
                    );
                    var reverse = Oracles.CliffordCharge(
                        leftBlade: binding.KeyToLane[right],
                        rightBlade: binding.KeyToLane[left],
                        positiveCount: positive,
                        negativeCount: negative,
                        degenerateCount: degenerate
                    );

                    // A pair that annihilates both ways constrains no charge — every candidate relates the two zeros —
                    // so the derivation issues none, and the oracle's own product of the two signs, zero exactly
                    // there, IS the expected readout at every pair of every signature.
                    var expected = new BigInteger(value: (forward * reverse));
                    var derived = certificate.BraidingCharge(
                        leftKey: left,
                        rightKey: right
                    );

                    if (derived != expected) {
                        return $"{name}: the pair ({left},{right}) commutes at {derived}, where the bubble-sort oracle's two signs give it {expected}";
                    }
                }
            }
        }

        return null;
    }
    /// <summary>Proves the hexagon identities are MEASURED rather than assumed: the certificate's braiding flags agree
    /// with the same identities recomputed over its own charge readout, every witness is a genuine disagreement, and the
    /// two flags separate at an instance that is braided without being symmetric.</summary>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    /// <remarks>The separating instance is a quantum torus over a prime field: two generators whose swap charge is a
    /// primitive cube root of one. Its charges are a bicharacter, so it braids; a cube root of one is not its own
    /// mirror, so it is not symmetric. Every graded regime in the catalogue — every NONDEGENERATE Clifford signature,
    /// every Cayley–Dickson floor through the quaternions — is symmetric because a sign IS its own mirror, so without
    /// this instance the two flags would never be observed apart. A degenerate signature reports neither flag for a
    /// third reason again: its annihilating pairs constrain no charge, so the derivation is incomplete and there is
    /// nothing for a hexagon to fail — which is why it carries no witness here.</remarks>
    public static string? BraidingHexagonWitnessed() =>
        (HexagonsMatchReadout(
            name: "clifford(3,0,0)",
            algebra: PresentedAlgebra<BigInteger, IntegerMaterial>.Create(presentation: Presentations.Clifford<BigInteger, IntegerMaterial>(
                degenerateCount: 0,
                material: default,
                negativeCount: 0,
                positiveCount: 3
            )),
            braided: true,
            symmetric: true
        )
            ?? (HexagonsMatchReadout(
            name: "clifford(4,1,0)",
            algebra: PresentedAlgebra<BigInteger, IntegerMaterial>.Create(presentation: Presentations.Clifford<BigInteger, IntegerMaterial>(
                degenerateCount: 0,
                material: default,
                negativeCount: 1,
                positiveCount: 4
            )),
            braided: true,
            symmetric: true
        )
            ?? (HexagonsMatchReadout(
            name: "clifford(2,0,1)",
            algebra: PresentedAlgebra<BigInteger, IntegerMaterial>.Create(presentation: Presentations.Clifford<BigInteger, IntegerMaterial>(
                degenerateCount: 1,
                material: default,
                negativeCount: 0,
                positiveCount: 2
            )),
            braided: false,
            symmetric: false
        )
            ?? (HexagonsMatchReadout(
            name: "cayley-dickson(2)",
            algebra: PresentedAlgebra<BigInteger, IntegerMaterial>.Create(presentation: Presentations.CayleyDickson<BigInteger, IntegerMaterial>(
                floors: 2,
                basisRelabelling: [],
                material: default
            )),
            braided: true,
            symmetric: true
        )
            ?? (HexagonsMatchReadout(
            name: "cayley-dickson(3)",
            algebra: PresentedAlgebra<BigInteger, IntegerMaterial>.Create(presentation: Presentations.CayleyDickson<BigInteger, IntegerMaterial>(
                floors: 3,
                basisRelabelling: [],
                material: default
            )),
            braided: false,
            symmetric: false
        )
            ?? (HexagonsMatchReadout(
            name: "quantum-torus(3)",
            algebra: PresentedAlgebra<ulong, PrimeFieldMaterial>.Create(presentation: QuantumTorusPresentation(
                modulus: 7UL,
                order: 3,
                swapCharge: 2UL
            )),
            braided: true,
            symmetric: false
        )
            ?? (HexagonsMatchReadout(
            name: "quantum-torus(4)",
            algebra: PresentedAlgebra<ulong, PrimeFieldMaterial>.Create(presentation: QuantumTorusPresentation(
                modulus: 5UL,
                order: 4,
                swapCharge: 2UL
            )),
            braided: true,
            symmetric: false
        )
            ?? QuantumTorusSeparatesTheFlags())))))));
    /// <summary>Proves the braiding shrinks its GUARANTEE and never its attempt: where the material cannot name the
    /// coefficient a pair would need, no charge is issued, no flag is issued, and no witness is invented — and the same
    /// presentation over a field material derives the coefficient and issues both.</summary>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    /// <remarks><see cref="ClosureOutcome.SearchLimitReached"/> stays distinct from the missing flag: a truncated
    /// certificate reports the budget, while a complete one over a noncommutative group algebra reports
    /// <see cref="ClosureOutcome.BasisAssociativityVerified"/> and still issues no braiding, because its two orderings land on different
    /// basis keys and no scalar relates them.</remarks>
    public static string? BraidingLimitsIssueNoFlag() {
        var permutation = PresentedAlgebra<BigInteger, IntegerMaterial>.Create(presentation: Presentations.PermutationGroup<BigInteger, IntegerMaterial>(
            material: default,
            permutations: [0, 1, 2, 1, 0, 2, 0, 2, 1, 2, 1, 0, 1, 2, 0, 2, 0, 1],
            pointCount: 3
        ));
        var permutationCertificate = permutation.Certify(overlapLimit: (1L << 22));

        if (
            permutationCertificate.IsBraided ||
            permutationCertificate.IsSymmetric ||
            (0 != permutationCertificate.BraidingWitness.Length) ||
            (ClosureOutcome.BasisAssociativityVerified != permutationCertificate.Outcome)
        ) {
            return $"permutation(3): braided={permutationCertificate.IsBraided} symmetric={permutationCertificate.IsSymmetric} witnesses={permutationCertificate.BraidingWitness.Length} outcome={permutationCertificate.Outcome}, where a noncommutative group algebra names no commutation charge and reports so without a witness";
        }

        if (0 == UnchargedPairs(
            certificate: permutationCertificate,
            keys: permutation.MaximumSupportCount,
            material: default(IntegerMaterial)
        )) {
            return "permutation(3): every ordered pair carries a commutation charge, where the two orderings of a noncommuting pair land on different basis keys and no scalar can relate them";
        }

        // The same statement one derivation deeper: a swap charge of two makes the mirror pair need one half, which the
        // counting and integer materials cannot name and a field material can. It is the SAME presentation shape at
        // three materials, so what separates them is the coefficient search and nothing else.
        var counting = PresentedAlgebra<BigInteger, CountingMaterial>.Create(presentation: HalvedSwapPresentation<BigInteger, CountingMaterial>(
            one: BigInteger.One,
            swapCharge: 2,
            material: default
        ));
        var integer = PresentedAlgebra<BigInteger, IntegerMaterial>.Create(presentation: HalvedSwapPresentation<BigInteger, IntegerMaterial>(
            one: BigInteger.One,
            swapCharge: 2,
            material: default
        ));
        var rational = PresentedAlgebra<QuadraticSurd, RationalMaterial>.Create(presentation: HalvedSwapPresentation<QuadraticSurd, RationalMaterial>(
            one: QuadraticSurd.Rational(
                denominator: 1,
                numerator: 1
            ),
            swapCharge: QuadraticSurd.Rational(
                denominator: 1,
                numerator: 2
            ),
            material: default
        ));
        var countingCertificate = counting.Certify(overlapLimit: (1L << 22));
        var integerCertificate = integer.Certify(overlapLimit: (1L << 22));
        var rationalCertificate = rational.Certify(overlapLimit: (1L << 22));

        // The window annihilates every product past degree two, and a pair that annihilates BOTH ways constrains no
        // coefficient at any material, since every charge relates the two zeros. That is the FLOOR each uncharged
        // count sits on, so the separation is stated against it rather than against nothing: the two materials that
        // cannot name the half sit strictly above the floor, and the field material below sits exactly on it.
        var countingFloor = AnnihilatingPairs(
            algebra: counting,
            keys: counting.MaximumSupportCount
        );
        var integerFloor = AnnihilatingPairs(
            algebra: integer,
            keys: integer.MaximumSupportCount
        );
        var rationalFloor = AnnihilatingPairs(
            algebra: rational,
            keys: rational.MaximumSupportCount
        );

        if (
            (UnchargedPairs(
            certificate: countingCertificate,
            keys: counting.MaximumSupportCount,
            material: default(CountingMaterial)
        ) <= countingFloor) ||
            (UnchargedPairs(
            certificate: integerCertificate,
            keys: integer.MaximumSupportCount,
            material: default(IntegerMaterial)
        ) <= integerFloor)
        ) {
            return "halved swap: an unsigned or integer material named the half the mirror pair needs, which neither carries";
        }

        if (
            countingCertificate.IsBraided ||
            integerCertificate.IsBraided ||
            (0 != countingCertificate.BraidingWitness.Length) ||
            (0 != integerCertificate.BraidingWitness.Length)
        ) {
            return $"halved swap: braided={countingCertificate.IsBraided}/{integerCertificate.IsBraided} with {countingCertificate.BraidingWitness.Length}/{integerCertificate.BraidingWitness.Length} witnesses, where a missing coefficient issues no flag and invents nothing";
        }

        if (rationalFloor != UnchargedPairs(
            certificate: rationalCertificate,
            keys: rational.MaximumSupportCount,
            material: default(RationalMaterial)
        )) {
            return "halved swap: the field material's uncharged pairs are not exactly the ones that annihilate both ways, where inverting the cell's own charge names every coefficient a pair constrains";
        }

        var half = QuadraticSurd.Rational(
            denominator: 2,
            numerator: 1
        );

        if (rationalCertificate.BraidingCharge(
            leftKey: HalvedSwapLetter(
                algebra: rational,
                symbol: 0
            ),
            rightKey: HalvedSwapLetter(
                algebra: rational,
                symbol: 1
            )
        ) != half) {
            return "halved swap: the field material derived a coefficient other than one half for the mirror of a swap charge of two";
        }

        // The budget, kept distinct from the missing flag: a truncated certificate reports the search rather than a
        // braiding it never examined.
        var truncated = PresentedAlgebra<BigInteger, IntegerMaterial>.Create(presentation: Presentations.Clifford<BigInteger, IntegerMaterial>(
            degenerateCount: 0,
            material: default,
            negativeCount: 0,
            positiveCount: 3
        )).Certify(overlapLimit: 4L);

        if (
            truncated.IsBraided ||
            truncated.IsSymmetric ||
            (0 != truncated.BraidingWitness.Length) ||
            (ClosureOutcome.SearchLimitReached != truncated.Outcome)
        ) {
            return $"clifford(3,0,0) at a budget of four: braided={truncated.IsBraided} symmetric={truncated.IsSymmetric} witnesses={truncated.BraidingWitness.Length} outcome={truncated.Outcome}";
        }

        // The readout refuses a key it never certified, including EVERY key of a presentation with no finite basis.
        var unbounded = PresentedAlgebra<BigInteger, IntegerMaterial>.Create(presentation: Presentations.Coxeter<BigInteger, IntegerMaterial>(
            bonds: [1, 3, 3, 3, 1, 3, 3, 3, 1],
            material: default,
            rank: 3
        )).Certify(overlapLimit: (1L << 20));

        if (
            unbounded.IsBraided ||
            unbounded.IsSymmetric ||
            (ClosureOutcome.SearchLimitReached != unbounded.Outcome)
        ) {
            return $"coxeter(3): braided={unbounded.IsBraided} symmetric={unbounded.IsSymmetric} outcome={unbounded.Outcome}, where a presentation with no finite basis proves nothing";
        }

        return (RefusesDeclaration(
            name: "a braiding charge outside the certified basis",
            build: () => _ = permutationCertificate.BraidingCharge(
                leftKey: 6L,
                rightKey: 0L
            )
        )
            ?? (RefusesDeclaration(
            name: "a negative braiding key",
            build: () => _ = permutationCertificate.BraidingCharge(
                leftKey: 0L,
                rightKey: -1L
            )
        )
            ?? RefusesDeclaration(
            name: "a braiding charge of a presentation with no finite basis",
            build: () => _ = unbounded.BraidingCharge(
                leftKey: 0L,
                rightKey: 0L
            )
        )));
    }
    /// <summary>The nontrivial-braiding canary: the derived charges must actually be nontrivial on more ordered basis
    /// pairs than the measured floor, and each of those pairs must re-multiply to the charge it carries.</summary>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    /// <remarks>Without it a braiding that collapsed to the material's one everywhere would pass every twin: the
    /// hexagon identities hold vacuously at the trivial braiding, the symmetric flag holds, and every refusal case is
    /// untouched. Only a floor on how many pairs genuinely anticommute catches it.</remarks>
    public static string? BraidingNontrivialCanary() {
        var subjects = new PresentedAlgebra<BigInteger, IntegerMaterial>[] {
            PresentedAlgebra<BigInteger, IntegerMaterial>.Create(presentation: Presentations.CayleyDickson<BigInteger, IntegerMaterial>(
            floors: 2,
            basisRelabelling: [],
            material: default
        )),
            PresentedAlgebra<BigInteger, IntegerMaterial>.Create(presentation: Presentations.CayleyDickson<BigInteger, IntegerMaterial>(
            floors: 3,
            basisRelabelling: [],
            material: default
        )),
            PresentedAlgebra<BigInteger, IntegerMaterial>.Create(presentation: Presentations.Clifford<BigInteger, IntegerMaterial>(
            degenerateCount: 0,
            material: default,
            negativeCount: 1,
            positiveCount: 4
        )),
        };

        for (var index = 0; (index < BraidingNontrivialFloors.Length); ++index) {
            var (name, minimum) = BraidingNontrivialFloors[index];
            var algebra = subjects[index];
            var certificate = algebra.Certify(overlapLimit: (1L << 22));
            var keys = algebra.MaximumSupportCount;
            var nontrivial = 0;

            for (var left = 0; (left < keys); ++left) {
                for (var right = 0; (right < keys); ++right) {
                    var charge = certificate.BraidingCharge(
                        leftKey: left,
                        rightKey: right
                    );

                    if (charge.IsOne) { continue; }

                    ++nontrivial;

                    // The teeth: the charge is re-multiplied through the algebra's own product rather than read back,
                    // so a table that carried a nontrivial value it could not justify fails here.
                    var forward = algebra.Multiply(
                        left: PresentedBasis(
                            algebra: algebra,
                            key: left
                        ),
                        right: PresentedBasis(
                            algebra: algebra,
                            key: right
                        )
                    );
                    var reverse = algebra.Multiply(
                        left: PresentedBasis(
                            algebra: algebra,
                            key: right
                        ),
                        right: PresentedBasis(
                            algebra: algebra,
                            key: left
                        )
                    );
                    var scaled = algebra.FromSupport(
                        keys: reverse.Keys.ToArray(),
                        coefficients: [.. reverse.Coefficients.ToArray().Select(selector: value => (charge * value))]
                    );

                    if (!algebra.AreEqual(
                        left: forward,
                        right: scaled
                    )) {
                        return $"{name}: the pair ({left},{right}) carries the commutation charge {charge}, which does not re-multiply the two orderings into each other";
                    }
                }
            }

            if (nontrivial < minimum) {
                return $"{name}: {nontrivial} ordered basis pair(s) commute at a charge that is not one, against the measured floor of {minimum}";
            }
        }

        return null;
    }

    // The quantum torus: two generators, both of the given order, whose swap charge is a primitive root of one of that
    // order in the field. Its cells are a twisted group algebra of the square of a cyclic group, so its commutation
    // charges are the skew pairing q^(bc − ad) — a bicharacter that is NOT its own mirror, which is the whole reason
    // this instance exists. Every catalogue entry's braiding is a sign, and a sign cannot separate the two flags.
    private static ChargedPresentation<ulong, PrimeFieldMaterial> QuantumTorusPresentation(int order, ulong modulus, ulong swapCharge) {
        var first = new int[order];
        var second = new int[order];

        for (var index = 0; (index < order); ++index) { second[index] = 1; }

        return ChargedPresentation<ulong, PrimeFieldMaterial>.Create(
            generators: SingleColourBasis(count: 2),
            rules: [
                new(
                    kind: RuleKind.Reassociate,
                    pattern: ReadOnlyMemory<int>.Empty,
                    replacement: RewriteRule<ulong>.PackReplacement(terms: [[]]),
                    charges: new[] { 1UL }
                ),
                new(
                    kind: RuleKind.Swap,
                    pattern: new[] { 1, 0 },
                    replacement: RewriteRule<ulong>.PackReplacement(terms: [[0, 1]]),
                    charges: new[] { swapCharge }
                ),
                new(
                    kind: RuleKind.Reduce,
                    pattern: first,
                    replacement: RewriteRule<ulong>.PackReplacement(terms: [[]]),
                    charges: new[] { 1UL }
                ),
                new(
                    kind: RuleKind.Reduce,
                    pattern: second,
                    replacement: RewriteRule<ulong>.PackReplacement(terms: [[]]),
                    charges: new[] { 1UL }
                ),
            ],
            material: PrimeFieldMaterial.Create(modulus: modulus)
        );
    }
    // Two letters that swap at a charge of two, windowed at degree two so the basis is finite. The mirror of that pair
    // needs one HALF, which is the smallest statement that separates the field derivation from the two signs.
    private static ChargedPresentation<TValue, TOps> HalvedSwapPresentation<TValue, TOps>(TValue one, TValue swapCharge, TOps material)
        where TOps : struct, IMaterialOps<TValue, TOps> =>
        ChargedPresentation<TValue, TOps>.Create(
            generators: SingleColourBasis(count: 2),
            rules: [
                new(
                    kind: RuleKind.Reassociate,
                    pattern: ReadOnlyMemory<int>.Empty,
                    replacement: RewriteRule<TValue>.PackReplacement(terms: [[]]),
                    charges: new[] { one }
                ),
                new(
                    kind: RuleKind.Swap,
                    pattern: new[] { 1, 0 },
                    replacement: RewriteRule<TValue>.PackReplacement(terms: [[0, 1]]),
                    charges: new[] { swapCharge }
                ),
            ],
            material: material,
            windowDegree: 2
        );
    // The key of a one-letter normal form, read off the presentation rather than assumed to be the symbol.
    private static long HalvedSwapLetter<TValue, TOps>(PresentedAlgebra<TValue, TOps> algebra, int symbol)
        where TOps : struct, IMaterialOps<TValue, TOps> {
        for (var key = 0L; (key < algebra.MaximumSupportCount); ++key) {
            var word = algebra.Presentation.NormalFormWord(key: key);

            if (
                (1 == word.Length) &&
                (symbol == word[0])
            ) { return key; }
        }

        throw new InvalidOperationException(message: $"the presentation carries no one-letter normal form at symbol {symbol}");
    }
    // The ordered basis pairs at which no commutation charge could be derived, counted through the certificate's own
    // readout, whose zero is the one value no candidate can produce.
    // The ordered basis pairs whose two products BOTH vanish. Those constrain no commutation charge at ANY material,
    // since every coefficient relates two zeros, so they are the floor an uncharged count sits on and the thing a
    // material's own inability to name a coefficient has to be measured against.
    private static int AnnihilatingPairs<TValue, TOps>(PresentedAlgebra<TValue, TOps> algebra, int keys)
        where TOps : struct, IMaterialOps<TValue, TOps> {
        var annihilating = 0;

        for (var left = 0; (left < keys); ++left) {
            for (var right = 0; (right < keys); ++right) {
                if (
                    (0 == algebra.Multiply(
                    left: PresentedBasis(
                        algebra: algebra,
                        key: left
                    ),
                    right: PresentedBasis(
                        algebra: algebra,
                        key: right
                    )
                ).SupportCount) &&
                    (0 == algebra.Multiply(
                    left: PresentedBasis(
                        algebra: algebra,
                        key: right
                    ),
                    right: PresentedBasis(
                        algebra: algebra,
                        key: left
                    )
                ).SupportCount)
                ) {
                    ++annihilating;
                }
            }
        }

        return annihilating;
    }
    private static int UnchargedPairs<TValue, TOps>(in PresentationCertificate<TValue> certificate, int keys, TOps material)
        where TOps : struct, IMaterialOps<TValue, TOps> {
        var uncharged = 0;

        for (var left = 0; (left < keys); ++left) {
            for (var right = 0; (right < keys); ++right) {
                if (material.IsZero(value: certificate.BraidingCharge(
                    leftKey: left,
                    rightKey: right
                ))) { ++uncharged; }
            }
        }

        return uncharged;
    }
    // The certificate's braiding flags against the SAME conditions recomputed here from its charge readout and the
    // algebra's own products: completeness, both hexagons, and the mirror comparison. The subject's loop is never read,
    // so a certificate that folded the wrong pair, checked one hexagon twice, or skipped the triples whose folded
    // product is not a basis element disagrees with this.
    private static string? HexagonsMatchReadout<TValue, TOps>(string name, PresentedAlgebra<TValue, TOps> algebra, bool braided, bool symmetric)
        where TOps : struct, IMaterialOps<TValue, TOps> {
        var certificate = algebra.Certify(overlapLimit: (1L << 22));
        var comparer = EqualityComparer<TValue>.Default;
        var keys = algebra.MaximumSupportCount;
        var material = algebra.Presentation.Material;
        var charge = new TValue[(keys * keys)];
        var folded = new int[(keys * keys)];
        var complete = true;

        for (var left = 0; (left < keys); ++left) {
            for (var right = 0; (right < keys); ++right) {
                var cell = algebra.Multiply(
                    left: PresentedBasis(
                        algebra: algebra,
                        key: left
                    ),
                    right: PresentedBasis(
                        algebra: algebra,
                        key: right
                    )
                );

                charge[((left * keys) + right)] = certificate.BraidingCharge(
                    leftKey: left,
                    rightKey: right
                );
                complete &= !material.IsZero(value: charge[((left * keys) + right)]);
                folded[((left * keys) + right)] = ((1 == cell.SupportCount)
                    ? ((int)cell.Keys[0])
                    : -1
                );
            }
        }

        // Every failure of EITHER identity is recorded, in the certificate's own emission order — leading then trailing
        // at each triple — and the two lists are then compared entry for entry rather than by whether both are empty.
        // That is what pins the two identities separately: a certificate that dropped either pass emits exactly half
        // these witnesses at every shipped instance, and a count-agnostic comparison cannot see it because the leading
        // and trailing failure sets coincide wherever anything fails at all.
        var expected = new List<(int Left, int Middle, int Right, TValue Nested, TValue Flat)>();

        for (var left = 0; (left < keys); ++left) {
            for (var middle = 0; (middle < keys); ++middle) {
                for (var right = 0; (right < keys); ++right) {
                    if (TryHexagonWitness(
                        charge: charge,
                        material: material,
                        comparer: comparer,
                        keys: keys,
                        folded: folded[((left * keys) + middle)],
                        head: left,
                        tail: middle,
                        held: right,
                        leading: true,
                        nested: out var leadingNested,
                        flat: out var leadingFlat
                    )) {
                        expected.Add(item: (left, middle, right, leadingNested, leadingFlat));
                    }

                    if (TryHexagonWitness(
                        charge: charge,
                        material: material,
                        comparer: comparer,
                        keys: keys,
                        folded: folded[((middle * keys) + right)],
                        head: middle,
                        tail: right,
                        held: left,
                        leading: false,
                        nested: out var trailingNested,
                        flat: out var trailingFlat
                    )) {
                        expected.Add(item: (left, middle, right, trailingNested, trailingFlat));
                    }
                }
            }
        }

        var recomputed = (complete && (0 == expected.Count));

        if (
            (certificate.IsBraided != recomputed) ||
            (certificate.IsBraided != braided)
        ) {
            return $"{name}: the certificate reports braided={certificate.IsBraided}, the recomputed hexagons report {recomputed}, and the claim expects {braided}";
        }

        if (expected.Count != certificate.BraidingWitness.Length) {
            return $"{name}: the certificate carries {certificate.BraidingWitness.Length} witness(es) against {expected.Count} recomputed hexagon failure(s)";
        }

        for (var index = 0; (index < expected.Count); ++index) {
            var witness = certificate.BraidingWitness[index];

            if (comparer.Equals(
                x: witness.Nested,
                y: witness.Flat
            )) {
                return $"{name}: the witness ({witness.Left},{witness.Middle},{witness.Right}) carries two charges that agree, so it witnesses nothing";
            }

            if (
                (witness.Left != expected[index].Left) ||
                (witness.Middle != expected[index].Middle) ||
                (witness.Right != expected[index].Right) ||
                !comparer.Equals(
                x: witness.Nested,
                y: expected[index].Nested
            ) ||
                !comparer.Equals(
                x: witness.Flat,
                y: expected[index].Flat
            )
            ) {
                return $"{name}: witness {index} is ({witness.Left},{witness.Middle},{witness.Right}) carrying {witness.Nested} against {witness.Flat}, where the recomputed hexagons give ({expected[index].Left},{expected[index].Middle},{expected[index].Right}) carrying {expected[index].Nested} against {expected[index].Flat}";
            }
        }

        if (certificate.IsSymmetric != symmetric) {
            return $"{name}: the certificate reports symmetric={certificate.IsSymmetric}, where the claim expects {symmetric}";
        }

        var mirrored = true;

        for (var left = 0; (left < keys); ++left) {
            for (var right = 0; (right < keys); ++right) {
                mirrored &= comparer.Equals(
                    x: charge[((left * keys) + right)],
                    y: charge[((right * keys) + left)]
                );
            }
        }

        return ((certificate.IsSymmetric == (recomputed && mirrored))
            ? null
            : $"{name}: symmetric={certificate.IsSymmetric}, where the readout's own mirror comparison reports {(recomputed && mirrored)}"
        );
    }
    // One hexagon at one ordered triple, restated: the charge of the pair the two folded arguments reach against the
    // product of the charges those two carry against the held one. The leading hexagon folds on the left of the held
    // argument and the trailing one on its right, which is the only difference between them. Returns whether the
    // identity FAILS, and hands back the two charges that disagree so the caller can pin which route produced which
    // value rather than only that some route produced a witness.
    private static bool TryHexagonWitness<TValue, TOps>(TValue[] charge, TOps material, EqualityComparer<TValue> comparer, int keys, int folded, int head, int tail, int held, bool leading, out TValue nested, out TValue flat)
        where TOps : struct, IMaterialOps<TValue, TOps> {
        nested = material.Zero;
        flat = material.Zero;

        if (folded < 0) { return false; }

        var foldedSlot = (leading
            ? ((folded * keys) + held)
            : ((held * keys) + folded)
        );
        var headSlot = (leading
            ? ((head * keys) + held)
            : ((held * keys) + head)
        );
        var tailSlot = (leading
            ? ((tail * keys) + held)
            : ((held * keys) + tail)
        );

        if (
            material.IsZero(value: charge[foldedSlot]) ||
            material.IsZero(value: charge[headSlot]) ||
            material.IsZero(value: charge[tailSlot])
        ) { return false; }

        nested = charge[foldedSlot];
        flat = material.Multiply(
            left: charge[headSlot],
            right: charge[tailSlot]
        );

        return !comparer.Equals(
            x: nested,
            y: flat
        );
    }
    // The quantum torus's two flags, separated by hand rather than by a count: the two generators commute at a charge
    // whose mirror is a different charge, and both are cube roots of the material's one.
    private static string? QuantumTorusSeparatesTheFlags() {
        var algebra = PresentedAlgebra<ulong, PrimeFieldMaterial>.Create(presentation: QuantumTorusPresentation(
            modulus: 7UL,
            order: 3,
            swapCharge: 2UL
        ));
        var certificate = algebra.Certify(overlapLimit: (1L << 22));
        var material = algebra.Presentation.Material;
        var first = HalvedSwapLetter(
            algebra: algebra,
            symbol: 0
        );
        var second = HalvedSwapLetter(
            algebra: algebra,
            symbol: 1
        );
        var forward = certificate.BraidingCharge(
            leftKey: first,
            rightKey: second
        );
        var reverse = certificate.BraidingCharge(
            leftKey: second,
            rightKey: first
        );

        if (
            (4UL != forward) ||
            (2UL != reverse)
        ) {
            return $"quantum-torus(3): the two generators commute at {forward} and {reverse}, where a swap charge of two over the seven-element field gives them four and two";
        }

        return (((material.One == material.Multiply(
            left: forward,
            right: reverse
        ))
                && (material.One == material.Multiply(
            left: material.Multiply(
                left: forward,
                right: forward
            ),
            right: forward
        )))
            ? null
            : "quantum-torus(3): the two commutation charges are not inverse cube roots of one, so the instance does not separate a braiding from a symmetry"
        );
    }

    // ---- phase 4: presentation morphisms and substitution systems ----

    // The surds the period substitution is read at: the two shortest geodesics, then periods of length two, four, five
    // and six. The last two are the point of MapWord — their composed letter images run 52 and 411 letters, past what a
    // mixed-radix long holds, so neither can be an element and only the chain form reaches them.
    private static readonly (long P, long Q, long D, long R)[] SubstitutionSurds = [
        (1L, 1L, 5L, 2L),    // the golden ratio, period [1]
        (1L, 1L, 2L, 1L),    // the silver ratio, period [2]
        (0L, 1L, 3L, 1L),    // √3, period [1, 2]
        (0L, 1L, 7L, 1L),    // √7, period [1, 1, 1, 4]
        (0L, 1L, 13L, 1L),   // √13, period [1, 1, 1, 1, 6]
        (0L, 1L, 19L, 1L),   // √19, period [2, 1, 3, 1, 2, 8]
    ];

    // The tiles compared against the streamed quasicrystal. Every surd's fixed point reaches this length in a handful
    // of substitution passes, and the shortest period needs the most of them.
    private const int SubstitutionTiles = 400;

    /// <summary>Proves that a morphism admitted by <see cref="PresentedFunctor{TValue, TOps}.TryCreate"/> really is
    /// one: its map carries products to products and sums to sums, at five source-and-target pairs spanning a Clifford
    /// signature, a doubling floor, a group algebra's sign character, a weighted quiver and a free monoid over the
    /// planar tangle algebra.</summary>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    /// <remarks>The admission reads the source's rules and its compiled cells; the check here reads neither. It
    /// multiplies and adds through the algebras' own public products on elements that are not basis elements, so
    /// bilinearity — the step that carries a basis statement to every element — is measured rather than assumed.</remarks>
    public static string? FunctorPreservesRelations() {
        var clifford = PresentedAlgebra<BigInteger, IntegerMaterial>.Create(presentation: Presentations.Clifford<BigInteger, IntegerMaterial>(
            degenerateCount: 0,
            material: default,
            negativeCount: 0,
            positiveCount: 2
        ));
        var cliffordWide = PresentedAlgebra<BigInteger, IntegerMaterial>.Create(presentation: Presentations.Clifford<BigInteger, IntegerMaterial>(
            degenerateCount: 0,
            material: default,
            negativeCount: 0,
            positiveCount: 3
        ));

        if (MorphismHolds(
            name: "clifford(2,0,0) -> clifford(3,0,0)",
            source: clifford,
            target: cliffordWide,
            images: [cliffordWide.Generator(symbol: 0), cliffordWide.Generator(symbol: 2)],
            draws: MorphismDraws(
                algebra: clifford,
                seed: 0x4F01UL
            )
        ) is { } signature) {
            return signature;
        }

        // A generator squaring to minus the unit, landing on the doubling tower's own square root of minus one.
        var negative = PresentedAlgebra<BigInteger, IntegerMaterial>.Create(presentation: Presentations.Clifford<BigInteger, IntegerMaterial>(
            degenerateCount: 0,
            material: default,
            negativeCount: 1,
            positiveCount: 0
        ));
        var complex = PresentedAlgebra<BigInteger, IntegerMaterial>.Create(presentation: Presentations.CayleyDickson<BigInteger, IntegerMaterial>(
            floors: 1,
            basisRelabelling: [],
            material: default
        ));

        if (MorphismHolds(
            name: "clifford(0,1,0) -> cayley-dickson(1)",
            source: negative,
            target: complex,
            images: [complex.Generator(symbol: 1)],
            draws: MorphismDraws(
                algebra: negative,
                seed: 0x4F02UL
            )
        ) is { } doubling) {
            return doubling;
        }

        if (SignCharacterIsAMorphism() is { } character) { return character; }
        if (WeightedQuiverCarriesItsWeights() is { } weights) { return weights; }

        // A source with no finite basis: the free monoid, whose only rule charges brackets, so every assignment is a
        // morphism by the universal property and the whole content is that the map multiplies.
        var free = PresentedAlgebra<BigInteger, IntegerMaterial>.Create(presentation: Presentations.FreeMonoid<BigInteger, IntegerMaterial>(
            letterCount: 2,
            material: default
        ));
        var tangle = PresentedAlgebra<BigInteger, IntegerMaterial>.Create(presentation: Presentations.PlanarTangle<BigInteger, IntegerMaterial>(
            loopCharge: 3,
            material: default,
            maximumWidth: 2
        ));

        if (free.Presentation.HasFiniteNormalForms) { return "the free monoid on two letters reports a finite basis, so the sparse source is not being exercised"; }

        return MorphismHolds(
            name: "free-monoid(2) -> planar-tangle(2)",
            source: free,
            target: tangle,
            images: [tangle.Generator(symbol: 3), tangle.Generator(symbol: 4)],
            draws: FreeWordDraws(
                algebra: free,
                letterCount: 2,
                seed: 0x4F03UL
            )
        );
    }
    /// <summary>Proves that a non-morphism is refused, and refused by naming a relation that really fails on the
    /// images — the rewrite rule where one carries the failure, and the ordered basis pair where the failure is an
    /// annihilation a degree window states and no rule mentions.</summary>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    /// <remarks>Each refusal is re-derived here from the obstruction's own public data: the named rule's pattern and
    /// charged replacement are folded through the images by hand, and the named pair's two products are formed the same
    /// way, so an obstruction that pointed at an innocent relation would fail this claim rather than pass it.</remarks>
    public static string? FunctorRefusesWitness() {
        var mixed = PresentedAlgebra<BigInteger, IntegerMaterial>.Create(presentation: Presentations.Clifford<BigInteger, IntegerMaterial>(
            degenerateCount: 0,
            material: default,
            negativeCount: 1,
            positiveCount: 1
        ));
        var positive = PresentedAlgebra<BigInteger, IntegerMaterial>.Create(presentation: Presentations.Clifford<BigInteger, IntegerMaterial>(
            degenerateCount: 0,
            material: default,
            negativeCount: 0,
            positiveCount: 2
        ));

        // A generator that squares to minus the unit has nowhere to go in a signature whose generators both square to
        // it, and swapping the two generators of one mixed signature moves the square the other way.
        if (RefusalNamesARule(
            name: "clifford(1,1,0) -> clifford(2,0,0)",
            source: mixed,
            target: positive,
            images: [positive.Generator(symbol: 0), positive.Generator(symbol: 1)],
            expectedKind: RuleKind.Reduce,
            expectedPattern: [1, 1]
        ) is { } across) {
            return across;
        }

        if (RefusalNamesARule(
            name: "clifford(1,1,0) swapped",
            source: mixed,
            target: mixed,
            images: [mixed.Generator(symbol: 1), mixed.Generator(symbol: 0)],
            expectedKind: RuleKind.Reduce,
            expectedPattern: [0, 0]
        ) is { } swapped) {
            return swapped;
        }

        // The unit is a relation like any other: a quiver states it as the empty-pattern rule, so collapsing both
        // objects onto one is refused by that rule and by nothing else.
        (int Source, int Target, BigInteger Weight)[] arrows = [(0, 0, 2), (0, 1, 3), (1, 0, 5), (1, 1, 7)];
        var quiver = PresentedAlgebra<BigInteger, IntegerMaterial>.Create(presentation: Presentations.Quiver<BigInteger, IntegerMaterial>(
            arrows: arrows,
            material: default,
            objectCount: 2
        ));
        var collapsed = new PresentedAlgebra<BigInteger, IntegerMaterial>.Element[quiver.Presentation.GeneratorCount];
        var weighted = new PresentedAlgebra<BigInteger, IntegerMaterial>.Element[collapsed.Length];

        for (var symbol = 0; (symbol < collapsed.Length); ++symbol) {
            collapsed[symbol] = quiver.FromSupport(
                keys: [0L],
                coefficients: [BigInteger.One]
            );
            weighted[symbol] = quiver.Generator(symbol: symbol);
        }

        if (RefusalNamesARule(
            expectedKind: RuleKind.Reduce,
            expectedPattern: [],
            images: collapsed,
            name: "quiver(2) collapsed",
            source: quiver,
            target: quiver
        ) is { } collapse) {
            return collapse;
        }

        // The weights are not multiplicative along composition, so the arrows' own weighted elements are not a
        // morphism — which is exactly why the images are assigned to symbols and the weight rides through by linearity.
        if (RefusalNamesARule(
            expectedKind: RuleKind.Reduce,
            expectedPattern: [],
            images: weighted,
            name: "quiver(2) weighted",
            source: quiver,
            target: quiver
        ) is { } scaled) {
            return scaled;
        }

        if (WindowRefusalNamesAPair() is { } window) { return window; }

        // The source whose relations cannot be enumerated at all: a degree window with no finite basis names
        // infinitely many annihilations and no rule carries one, so it is refused at construction.
        var unbounded = PresentedAlgebra<BigInteger, IntegerMaterial>.Create(presentation: Presentations.FreeMonoid<BigInteger, IntegerMaterial>(
            letterCount: 20,
            material: default,
            windowDegree: 3
        ));

        if (
            !unbounded.Presentation.HasFiniteNormalForms ||
            unbounded.Presentation.HasCompiledNormalFormBasis
        ) {
            return $"the finite but capacity-obstructed free monoid reports finite={unbounded.Presentation.HasFiniteNormalForms}, compiled={unbounded.Presentation.HasCompiledNormalFormBasis}";
        }

        var unboundedImages = new PresentedAlgebra<BigInteger, IntegerMaterial>.Element[unbounded.Presentation.GeneratorCount];

        for (var symbol = 0; (symbol < unboundedImages.Length); ++symbol) { unboundedImages[symbol] = unbounded.Generator(symbol: symbol); }

        if (RefusedParameter(action: () => _ = PresentedFunctor<BigInteger, IntegerMaterial>.TryCreate(
            functor: out _,
            images: unboundedImages,
            obstruction: out _,
            source: unbounded,
            target: unbounded
        )) is not "source") {
            return "a windowed source with no finite basis was not refused by naming the source";
        }

        if (RefusedParameter(action: () => _ = PresentedFunctor<BigInteger, IntegerMaterial>.TryCreate(
            source: mixed,
            target: positive,
            images: [positive.Generator(symbol: 1)],
            functor: out _,
            obstruction: out _
        )) is not "images") {
            return "an image count that is not the source generator count was not refused by naming the images";
        }

        if (RefusedParameter(action: () => _ = PresentedFunctor<BigInteger, IntegerMaterial>.TryCreate(
            source: mixed,
            target: positive,
            images: [mixed.Generator(symbol: 0), mixed.Generator(symbol: 1)],
            functor: out _,
            obstruction: out _
        )) is not "images") {
            return "an image belonging to another algebra was not refused by naming the images";
        }

        return null;
    }
    /// <summary>Proves the fixed point of a continued-fraction period's composed substitution equal, tile for tile, to
    /// the quasicrystal word <see cref="QuadraticQuasicrystal.Word(long, long, long, long, Span{bool})"/> streams for
    /// the same surd, at six surds whose periods run from one term to six.</summary>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    /// <remarks>
    /// The composition is <see cref="PresentedFunctor{TValue, TOps}.MapWord"/> applied through the period's factors,
    /// innermost outward, so no composed image is ever formed as an element — at √13 and √19 none could be, their
    /// letter images running 52 and 411 symbols against the low thirties a mixed-radix key holds.
    /// <para>What the shipped streamer shares with this claim, stated rather than denied: both sides read the same
    /// period from <see cref="ContinuedFraction.Expand"/>, and the substitution recipe <c>τ_k: long → long^k short,
    /// short → long</c> is AUTHORED TWICE — once in <see cref="SubstitutionFactor"/> and once in the streamer. So that
    /// half is faithful carriage of one recipe, not two independent derivations of the word.</para>
    /// <para>The independent witness is <see cref="Oracles.SturmianMechanicalWord"/>: the same word read as the first
    /// differences of a slope, with no substitution applied and the period expanded by the oracle's own surd walk. It
    /// shares nothing with either side.</para>
    /// </remarks>
    public static string? SubstitutionTwinsQuasicrystal() {
        var algebra = PresentedAlgebra<bool, BooleanMaterial>.Create(presentation: Presentations.FreeMonoid<bool, BooleanMaterial>(
            letterCount: 2,
            material: default
        ));

        foreach (var (p, q, d, r) in SubstitutionSurds) {
            var period = SubstitutionPeriod(
                d: d,
                p: p,
                q: q,
                r: r
            );
            var factors = new PresentedFunctor<bool, BooleanMaterial>[period.Length];

            for (var index = 0; (index < period.Length); ++index) {
                if (SubstitutionFactor(
                    algebra: algebra,
                    partialQuotient: period[index],
                    factor: out var built
                ) is { } refused) { return $"√{d}: {refused}"; }

                factors[index] = built!;
            }

            var streamed = new bool[SubstitutionTiles];
            var mechanical = new bool[SubstitutionTiles];

            QuadraticQuasicrystal.Word(
                d: d,
                p: p,
                q: q,
                r: r,
                tiles: streamed
            );
            Oracles.SturmianMechanicalWord(
                d: d,
                p: p,
                q: q,
                r: r,
                tiles: mechanical
            );

            // The witness: the same word with no substitution anywhere in it, and a period this oracle expanded for
            // itself. It stands against both sides, which share the recipe and the expansion.
            for (var index = 0; (index < SubstitutionTiles); ++index) {
                if (streamed[index] != mechanical[index]) {
                    return $"√{d}: the streamed quasicrystal reads {(streamed[index]
                        ? "long"
                        : "short")} at position {index}, where the mechanical word of its slope reads {(mechanical[index]
                        ? "long"
                        : "short")}";
                }
            }

            // One substitution pass IS the period's factors applied innermost outward. Every intermediate is truncated
            // to the compared length, which loses nothing: a substitution sends a prefix to a prefix, and no factor
            // shortens a word, so the truncated pass still reaches the compared length.
            var current = ((int[])[SubstitutionLong]);
            var passes = 0;

            while (current.Length < SubstitutionTiles) {
                for (var index = (period.Length - 1); (index >= 0); --index) {
                    var length = factors[index].MapWord(
                        image: [],
                        word: current
                    );
                    var next = new int[Math.Min(
                        val1: length,
                        val2: SubstitutionTiles
                    )];

                    _ = factors[index].MapWord(
                        image: next,
                        word: current
                    );
                    current = next;
                }

                if (++passes > 32) { return $"√{d}: the substitution did not reach {SubstitutionTiles} tiles in 32 passes, so it does not grow"; }
            }

            for (var index = 0; (index < SubstitutionTiles); ++index) {
                if ((SubstitutionLong == current[index]) != streamed[index]) {
                    return $"√{d}: the substitution's fixed point and the streamed quasicrystal carry different tiles at position {index}, where the substitution reads letter {current[index]} and the stream reads {(streamed[index]
                        ? 1
                        : 0)} long tile(s)";
                }
            }

            // Where the composed images still have keys, the single composed morphism must agree with the chain that
            // built them — the same substitution stated as one functor rather than as a fold of six.
            var composedLong = SubstitutionComposedImage(
                factors: factors,
                seed: SubstitutionLong
            );
            var composedShort = SubstitutionComposedImage(
                factors: factors,
                seed: SubstitutionShort
            );

            if (
                (composedLong.Length > 32) ||
                (composedShort.Length > 32)
            ) { continue; }

            if (!PresentedFunctor<bool, BooleanMaterial>.TryCreate(
                source: algebra,
                target: algebra,
                images: [MorphismWordElement(
                        algebra: algebra,
                        word: composedLong
                    ), MorphismWordElement(
                        algebra: algebra,
                        word: composedShort
                    )],
                functor: out var composed,
                obstruction: out _
            )) {
                return $"√{d}: the composed period substitution is not a morphism of the free monoid, which has no relation to break";
            }

            var seeded = ((int[])[SubstitutionLong]);

            while (seeded.Length < SubstitutionTiles) {
                var length = composed!.MapWord(
                    image: [],
                    word: seeded
                );
                var next = new int[Math.Min(
                    val1: length,
                    val2: SubstitutionTiles
                )];

                _ = composed.MapWord(
                    image: next,
                    word: seeded
                );
                seeded = next;
            }

            for (var index = 0; (index < SubstitutionTiles); ++index) {
                if (seeded[index] != current[index]) {
                    return $"√{d}: the composed morphism and the chain of its factors disagree at tile {index}";
                }
            }
        }

        return null;
    }
    /// <summary>Proves the abelianization of a period's composed substitution equal to
    /// <see cref="QuadraticInflation"/>'s four entries, in the transposed orientation, and equal again to the transfer
    /// element the same period evaluates to.</summary>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    /// <remarks>
    /// <b>The orientation is the point, and it is recorded rather than assumed.</b> Counting occurrences gives the
    /// matrix whose ROW <c>i</c> is the letter census of the image of letter <c>i</c>; the inflation lens folds the
    /// period as convergent matrices, whose COLUMN <c>i</c> is that same census. So the abelianization is the
    /// transpose of <c>[[A, B], [C, D]]</c>, and the claim proves both that it matches transposed and that at every
    /// period whose matrix is not symmetric it does NOT match directly — without which a symmetric single-term period
    /// would hide the orientation entirely.
    /// <para>What the three routes SHARE, stated rather than denied: all of them read the same period from
    /// <see cref="ContinuedFraction.Expand"/>, and <see cref="ConvergentTransfer{TValue, TOps}"/> is
    /// <see cref="PresentedAlgebra{TValue, TOps}"/> on a two-object quiver, so the transfer route is the presented
    /// object a second time rather than a third construction. <see cref="Oracles.SubstitutionIncidence"/> is the leg
    /// that stands outside all of them.</para>
    /// </remarks>
    public static string? SubstitutionMatrixVsInflation() {
        var algebra = PresentedAlgebra<BigInteger, CountingMaterial>.Create(presentation: Presentations.FreeMonoid<BigInteger, CountingMaterial>(
            letterCount: 2,
            material: default
        ));
        var transfer = ConvergentTransfer<BigInteger, IntegerMaterial>.Create(material: default);
        var asymmetric = 0;

        foreach (var (p, q, d, r) in SubstitutionSurds) {
            var period = SubstitutionPeriod(
                d: d,
                p: p,
                q: q,
                r: r
            );
            var factors = new PresentedFunctor<BigInteger, CountingMaterial>[period.Length];

            for (var index = 0; (index < period.Length); ++index) {
                if (SubstitutionFactor(
                    algebra: algebra,
                    partialQuotient: period[index],
                    factor: out var built
                ) is { } refused) { return $"√{d}: {refused}"; }

                factors[index] = built!;
            }

            var inflation = QuadraticInflation.FromQuadraticIrrational(
                d: d,
                p: p,
                q: q,
                r: r
            );
            var longImage = SubstitutionComposedImage(
                factors: factors,
                seed: SubstitutionLong
            );
            var shortImage = SubstitutionComposedImage(
                factors: factors,
                seed: SubstitutionShort
            );
            var census = new long[4];

            foreach (var letter in longImage) { ++census[letter]; }
            foreach (var letter in shortImage) { ++census[(2 + letter)]; }

            if (
                (census[0] != inflation.A) ||
                (census[1] != inflation.C) ||
                (census[2] != inflation.B) ||
                (census[3] != inflation.D)
            ) {
                return $"√{d}: the abelianization counts [[{census[0]},{census[1]}],[{census[2]},{census[3]}]], where the transpose of the inflation matrix is [[{inflation.A},{inflation.C}],[{inflation.B},{inflation.D}]]";
            }

            if (inflation.B != inflation.C) {
                ++asymmetric;

                if (
                    (census[1] == inflation.B) &&
                    (census[2] == inflation.C)
                ) {
                    return $"√{d}: the abelianization matches the inflation matrix directly as well as transposed, so the orientation is not pinned";
                }
            }

            // The witness the two routes above need: the continuant product ∏ [[bᵢ, 1], [1, 0]] formed in BigInteger
            // from the period alone, which is neither a census of letter images nor a shipped inflation lens. It pins
            // the ORIENTATION from outside, which is the whole content of this claim.
            var incidence = Oracles.SubstitutionIncidence(period: period);

            if (
                (incidence.A != inflation.A) ||
                (incidence.B != inflation.B) ||
                (incidence.C != inflation.C) ||
                (incidence.D != inflation.D)
            ) {
                return $"√{d}: the inflation lens reads [[{inflation.A},{inflation.B}],[{inflation.C},{inflation.D}]], where the continuant product of the same period is [[{incidence.A},{incidence.B}],[{incidence.C},{incidence.D}]]";
            }

            // A third route to the same matrix, and what it does and does not share: the period's partial quotients
            // evaluated through the presented quiver that IS the two-by-two matrices. It reaches the matrix by another
            // fold, but it reads the same period, and ConvergentTransfer is PresentedAlgebra on a Quiver(2) — so this
            // is the presented object twice, not two independent constructions.
            var quotients = new BigInteger[period.Length];

            for (var index = 0; (index < period.Length); ++index) { quotients[index] = period[index]; }

            var evaluated = transfer.Evaluate(partialQuotients: quotients);

            if (
                (transfer.Entry(
                column: 0,
                row: 0,
                value: evaluated
            ) != inflation.A) ||
                (transfer.Entry(
                column: 1,
                row: 0,
                value: evaluated
            ) != inflation.B) ||
                (transfer.Entry(
                column: 0,
                row: 1,
                value: evaluated
            ) != inflation.C) ||
                (transfer.Entry(
                column: 1,
                row: 1,
                value: evaluated
            ) != inflation.D)
            ) {
                return $"√{d}: the transfer element of the period is not the inflation matrix";
            }
        }

        return ((asymmetric >= 4)
            ? null
            : $"only {asymmetric} of the surds carry an asymmetric substitution matrix, so the transposed orientation is not separated from the direct one"
        );
    }
    /// <summary>Proves the convergent transfer to be an instance of a presentation morphism: the functor out of the
    /// free monoid on one letter per partial quotient, carrying each letter to that quotient's digit element, maps the
    /// word to exactly the element <see cref="ConvergentTransfer{TValue, TOps}.Evaluate"/> folds and the entries
    /// <see cref="ConvergentTransfer{TValue, TOps}.Run"/> reads out as a module.</summary>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    /// <remarks>The free monoid has no relation to break, so the admission is the universal property; the content is
    /// that three shipped evaluators of one word — the morphism's fold, the transfer's own, and the machine's run —
    /// reach the same value. The digit element carries three terms, so it names no word: the same instance shows
    /// <see cref="PresentedFunctor{TValue, TOps}.MapWord"/> refusing where <see cref="PresentedFunctor{TValue, TOps}.Map"/>
    /// answers.</remarks>
    public static string? FunctorTwinsTransfer() {
        const int Letters = 5;

        var free = PresentedAlgebra<BigInteger, IntegerMaterial>.Create(presentation: Presentations.FreeMonoid<BigInteger, IntegerMaterial>(
            letterCount: Letters,
            material: default
        ));
        var transfer = ConvergentTransfer<BigInteger, IntegerMaterial>.Create(material: default);
        var word = free.Identity;

        for (var symbol = 0; (symbol < Letters); ++symbol) {
            word = free.Multiply(
            left: word,
            right: free.Generator(symbol: symbol)
        );
        }

        BigInteger[][] draws = [[3, 1, 4, 1, 5], [1, 1, 1, 1, 1], [2, 7, 1, 8, 2]];

        foreach (var quotients in draws) {
            var images = new PresentedAlgebra<BigInteger, IntegerMaterial>.Element[Letters];

            for (var symbol = 0; (symbol < Letters); ++symbol) { images[symbol] = transfer.Digit(partialQuotient: quotients[symbol]); }

            if (!PresentedFunctor<BigInteger, IntegerMaterial>.TryCreate(
                source: free,
                target: transfer.Algebra,
                images: images,
                functor: out var functor,
                obstruction: out var obstruction
            )) {
                return $"the transfer morphism of [{string.Join(
                    separator: ',',
                    values: quotients
                )}] was refused at rule {obstruction.RuleIndex} and pair ({obstruction.LeftKey},{obstruction.RightKey}), where a free source has no relation to break";
            }

            if (
                (functor!.ImageCount != Letters) ||
                !ReferenceEquals(
                objA: functor.Source,
                objB: free
            ) ||
                !ReferenceEquals(
                objA: functor.Target,
                objB: transfer.Algebra
            )
            ) {
                return $"the transfer morphism reports {functor.ImageCount} image(s) over the wrong pair of algebras";
            }

            if (functor.IsWordMorphism) { return "the digit element carries three terms, so the transfer morphism must not claim to send words to words"; }

            var mapped = functor.Map(value: word);
            var evaluated = transfer.Evaluate(partialQuotients: quotients);

            if (!transfer.Algebra.AreEqual(
                left: mapped,
                right: evaluated
            )) {
                return $"the morphism maps the word of [{string.Join(
                    separator: ',',
                    values: quotients
                )}] to an element the transfer's own fold does not reach";
            }

            for (var row = 0; (row < 2); ++row) {
                for (var column = 0; (column < 2); ++column) {
                    if (transfer.Entry(
                        column: column,
                        row: row,
                        value: mapped
                    ) != transfer.Run(
                        column: column,
                        partialQuotients: quotients,
                        row: row
                    )) {
                        return $"the morphism and the module run disagree at ({row},{column}) on [{string.Join(
                            separator: ',',
                            values: quotients
                        )}]";
                    }
                }
            }

            for (var symbol = 0; (symbol < Letters); ++symbol) {
                if (!transfer.Algebra.AreEqual(
                    left: functor.Image(symbol: symbol),
                    right: images[symbol]
                )) {
                    return $"the morphism's image of letter {symbol} is not the digit element it was given";
                }
            }

            if (!Throws<InvalidOperationException>(action: () => _ = functor.MapWord(
                image: [],
                word: [0]
            ))) {
                return "a morphism whose images are not single basis elements answered MapWord instead of refusing";
            }
        }

        return null;
    }

    // The two letters of a two-letter substitution: the long tile and the short one, in the order the streamed
    // quasicrystal reads them — true for long, false for short.
    private const int SubstitutionLong = 0;
    private const int SubstitutionShort = 1;

    // One factor of a period: the substitution sending the long tile to that many long tiles followed by a short one,
    // and the short tile to a long one. Its images are two words, so the factor is a word morphism.
    private static string? SubstitutionFactor<TValue, TOps>(PresentedAlgebra<TValue, TOps> algebra, long partialQuotient, out PresentedFunctor<TValue, TOps>? factor)
        where TOps : struct, IMaterialOps<TValue, TOps> {
        var longImage = new int[(partialQuotient + 1L)];

        longImage[partialQuotient] = SubstitutionShort;

        if (!PresentedFunctor<TValue, TOps>.TryCreate(
            source: algebra,
            target: algebra,
            images: [MorphismWordElement(
                    algebra: algebra,
                    word: longImage
                ), MorphismWordElement(
                    algebra: algebra,
                    word: [SubstitutionLong]
                )],
            functor: out factor,
            obstruction: out var obstruction
        )) {
            return $"the factor at partial quotient {partialQuotient} was refused at rule {obstruction.RuleIndex}";
        }

        return (factor!.IsWordMorphism
            ? null
            : $"the factor at partial quotient {partialQuotient} does not send words to words"
        );
    }
    // The composed image of one letter under a whole period: the factors applied innermost outward, each through
    // MapWord, so the growing word is never an element.
    private static int[] SubstitutionComposedImage<TValue, TOps>(PresentedFunctor<TValue, TOps>[] factors, int seed)
        where TOps : struct, IMaterialOps<TValue, TOps> {
        var current = ((int[])[seed]);

        for (var index = (factors.Length - 1); (index >= 0); --index) {
            var next = new int[factors[index].MapWord(
                image: [],
                word: current
            )];

            _ = factors[index].MapWord(
                image: next,
                word: current
            );
            current = next;
        }

        return current;
    }
    // The period block of a surd's continued fraction, which is what the substitution and the inflation lens both read.
    private static long[] SubstitutionPeriod(long p, long q, long d, long r) {
        Span<long> terms = stackalloc long[128];

        _ = ContinuedFraction.Expand(
            d: d,
            p: p,
            periodLength: out var periodLength,
            periodStart: out var periodStart,
            q: q,
            r: r,
            terms: terms
        );

        return terms.Slice(
            length: periodLength,
            start: periodStart
        ).ToArray();
    }
    // The basis element one word names, built as the ordered product of its letters — the only route a caller has to a
    // word's key, and the reason a word past the low thirties cannot be an image.
    private static PresentedAlgebra<TValue, TOps>.Element MorphismWordElement<TValue, TOps>(PresentedAlgebra<TValue, TOps> algebra, ReadOnlySpan<int> word)
        where TOps : struct, IMaterialOps<TValue, TOps> {
        var result = algebra.Identity;

        foreach (var letter in word) {
            result = algebra.Multiply(
            left: result,
            right: algebra.Generator(symbol: letter)
        );
        }

        return result;
    }
    // The group algebra's sign character: every element to plus or minus the unit of a one-dimensional algebra. It is
    // a morphism no word can express, and it is not injective, so it exercises the half of the surface a relabelling
    // never reaches.
    private static string? SignCharacterIsAMorphism() {
        int[][] rows = [[0, 1, 2], [0, 2, 1], [1, 0, 2], [1, 2, 0], [2, 0, 1], [2, 1, 0]];
        var flat = new int[(rows.Length * 3)];
        var signs = new BigInteger[rows.Length];

        for (var row = 0; (row < rows.Length); ++row) {
            var inversions = 0;

            for (var first = 0; (first < 3); ++first) {
                flat[((row * 3) + first)] = rows[row][first];

                for (var second = (first + 1); (second < 3); ++second) {
                    if (rows[row][first] > rows[row][second]) { ++inversions; }
                }
            }

            signs[row] = ((0 == (inversions & 1))
                ? BigInteger.One
                : BigInteger.MinusOne
            );
        }

        var source = PresentedAlgebra<BigInteger, IntegerMaterial>.Create(presentation: Presentations.PermutationGroup<BigInteger, IntegerMaterial>(
            material: default,
            permutations: flat,
            pointCount: 3
        ));
        var target = PresentedAlgebra<BigInteger, IntegerMaterial>.Create(presentation: Presentations.FreeMonoid<BigInteger, IntegerMaterial>(
            letterCount: 0,
            material: default
        ));
        var images = new PresentedAlgebra<BigInteger, IntegerMaterial>.Element[rows.Length];

        for (var row = 0; (row < rows.Length); ++row) {
            images[row] = target.FromSupport(
            keys: [0L],
            coefficients: [signs[row]]
        );
        }

        if (MorphismHolds(
            name: "permutation(3) -> free-monoid(0)",
            source: source,
            target: target,
            images: images,
            draws: MorphismDraws(
                algebra: source,
                seed: 0x4F04UL
            )
        ) is { } refused) {
            return refused;
        }

        if (!PresentedFunctor<BigInteger, IntegerMaterial>.TryCreate(
            functor: out var functor,
            images: images,
            obstruction: out _,
            source: source,
            target: target
        )) {
            return "permutation(3): the sign character was refused";
        }

        // The generators are the group's own elements in lexicographic order, so the character is read back generator
        // by generator against the parity of that permutation.
        for (var row = 0; (row < rows.Length); ++row) {
            var mapped = functor!.Map(value: source.Generator(symbol: row));

            if (
                (1 != mapped.SupportCount) ||
                (mapped.Coefficients[0] != signs[row])
            ) {
                return $"permutation(3): the character sends the element at symbol {row} to a value other than its parity {signs[row]}";
            }
        }

        // Breaking the character at one element must break the morphism, and break it at a rewrite rule.
        var broken = ((PresentedAlgebra<BigInteger, IntegerMaterial>.Element[])[.. images]);

        broken[1] = target.FromSupport(
            coefficients: [2],
            keys: [0L]
        );

        return RefusalNamesARule(
            expectedKind: RuleKind.Reduce,
            expectedPattern: [1, 1],
            images: broken,
            name: "permutation(3) broken character",
            source: source,
            target: target
        );
    }
    // A quiver whose arrows carry weights: the identity on the BASIS is a morphism, and the weight a generator carries
    // rides through it by linearity rather than being divided out.
    private static string? WeightedQuiverCarriesItsWeights() {
        (int Source, int Target, BigInteger Weight)[] arrows = [(0, 0, 2), (0, 1, 3), (1, 0, 5), (1, 1, 7)];
        var quiver = PresentedAlgebra<BigInteger, IntegerMaterial>.Create(presentation: Presentations.Quiver<BigInteger, IntegerMaterial>(
            arrows: arrows,
            material: default,
            objectCount: 2
        ));
        var basis = new PresentedAlgebra<BigInteger, IntegerMaterial>.Element[quiver.Presentation.NormalFormCount];

        for (var key = 0; (key < basis.Length); ++key) {
            basis[key] = quiver.FromSupport(
            keys: [key],
            coefficients: [BigInteger.One]
        );
        }

        if (MorphismHolds(
            name: "weighted quiver(2)",
            source: quiver,
            target: quiver,
            images: basis,
            draws: MorphismDraws(
                algebra: quiver,
                seed: 0x4F05UL
            )
        ) is { } broken) {
            return broken;
        }

        if (!PresentedFunctor<BigInteger, IntegerMaterial>.TryCreate(
            functor: out var functor,
            images: basis,
            obstruction: out _,
            source: quiver,
            target: quiver
        )) {
            return "the identity on a weighted quiver's basis was refused";
        }

        for (var symbol = 0; (symbol < basis.Length); ++symbol) {
            var mapped = functor!.Map(value: quiver.Generator(symbol: symbol));

            if (
                (1 != mapped.SupportCount) ||
                (mapped.Keys[0] != symbol) ||
                (mapped.Coefficients[0] != arrows[symbol].Weight)
            ) {
                return $"weighted quiver(2): the image of the arrow at symbol {symbol} does not carry that arrow's weight {arrows[symbol].Weight}";
            }
        }

        return null;
    }
    // The relation a degree window states and no rule carries: two basis elements whose combined degree passes the
    // window annihilate, so images that do not annihilate there are refused by the PAIR rather than by a rule.
    private static string? WindowRefusalNamesAPair() {
        var windowed = PresentedAlgebra<BigInteger, IntegerMaterial>.Create(presentation: Presentations.FreeMonoid<BigInteger, IntegerMaterial>(
            letterCount: 2,
            material: default,
            windowDegree: 2
        ));
        var images = ((PresentedAlgebra<BigInteger, IntegerMaterial>.Element[])[windowed.Identity, windowed.Identity]);

        if (PresentedFunctor<BigInteger, IntegerMaterial>.TryCreate(
            functor: out _,
            images: images,
            obstruction: out var obstruction,
            source: windowed,
            target: windowed
        )) {
            return "sending both letters of a windowed free monoid to the unit was admitted, though the window annihilates a product the unit does not";
        }

        if (
            (-1 != obstruction.RuleIndex) ||
            (obstruction.LeftKey < 0L) ||
            (obstruction.RightKey < 0L)
        ) {
            return $"the window's annihilation was reported as rule {obstruction.RuleIndex} and pair ({obstruction.LeftKey},{obstruction.RightKey}), where no rule carries it";
        }

        // Re-derived independently: the product of the two images against the image of the source's own product, both
        // folded by hand from the images and the normal forms the two keys name.
        var left = windowed.FromSupport(
            keys: [obstruction.LeftKey],
            coefficients: [BigInteger.One]
        );
        var right = windowed.FromSupport(
            keys: [obstruction.RightKey],
            coefficients: [BigInteger.One]
        );
        var product = windowed.Multiply(
            left: left,
            right: right
        );
        var mapped = windowed.Multiply(
            left: FoldImages(
                target: windowed,
                images: images,
                word: windowed.Presentation.NormalFormWord(key: obstruction.LeftKey)
            ),
            right: FoldImages(
                target: windowed,
                images: images,
                word: windowed.Presentation.NormalFormWord(key: obstruction.RightKey)
            )
        );
        var expected = windowed.Zero;

        for (var index = 0; (index < product.SupportCount); ++index) {
            var folded = FoldImages(
                target: windowed,
                images: images,
                word: windowed.Presentation.NormalFormWord(key: product.Keys[index])
            );
            var scaled = new BigInteger[folded.SupportCount];

            for (var entry = 0; (entry < scaled.Length); ++entry) { scaled[entry] = (product.Coefficients[index] * folded.Coefficients[entry]); }

            expected = windowed.Add(
                left: expected,
                right: windowed.FromSupport(
                    keys: folded.Keys,
                    coefficients: scaled
                )
            );
        }

        return (!windowed.AreEqual(
            left: mapped,
            right: expected
        )
            ? null
            : $"the pair ({obstruction.LeftKey},{obstruction.RightKey}) the refusal names is one the images do preserve"
        );
    }
    // A morphism admitted, then checked to BE one on elements the admission never looked at: the map carries products
    // to products and sums to sums, fixes the unit and the zero, and hands back the images it was given.
    private static string? MorphismHolds<TValue, TOps>(
        string name,
        PresentedAlgebra<TValue, TOps> source,
        PresentedAlgebra<TValue, TOps> target,
        PresentedAlgebra<TValue, TOps>.Element[] images,
        PresentedAlgebra<TValue, TOps>.Element[] draws
    ) where TOps : struct, IMaterialOps<TValue, TOps> {
        if (!PresentedFunctor<TValue, TOps>.TryCreate(
            functor: out var functor,
            images: images,
            obstruction: out var obstruction,
            source: source,
            target: target
        )) {
            return $"{name}: refused at rule {obstruction.RuleIndex} (pattern [{string.Join(
                separator: ',',
                values: obstruction.Rule.Pattern.ToArray()
            )}]) and pair ({obstruction.LeftKey},{obstruction.RightKey})";
        }

        if (functor!.ImageCount != images.Length) { return $"{name}: the morphism reports {functor.ImageCount} image(s) against {images.Length} given"; }
        if (
            !ReferenceEquals(
            objA: functor.Source,
            objB: source
        ) ||
            !ReferenceEquals(
            objA: functor.Target,
            objB: target
        )
        ) { return $"{name}: the morphism does not report the algebras it was built over"; }

        for (var symbol = 0; (symbol < images.Length); ++symbol) {
            if (!target.AreEqual(
                left: functor.Image(symbol: symbol),
                right: images[symbol]
            )) { return $"{name}: the image of symbol {symbol} is not the element it was given"; }
        }

        if (!target.AreEqual(
            left: functor.Map(value: source.Zero),
            right: target.Zero
        )) { return $"{name}: the zero does not map to the zero"; }
        if (!target.AreEqual(
            left: functor.Map(value: source.Identity),
            right: target.Identity
        )) { return $"{name}: the unit does not map to the unit"; }

        foreach (var left in draws) {
            foreach (var right in draws) {
                if (!target.AreEqual(
                    left: functor.Map(value: source.Multiply(
                        left: left,
                        right: right
                    )),
                    right: target.Multiply(
                        left: functor.Map(value: left),
                        right: functor.Map(value: right)
                    )
                )) {
                    return $"{name}: the map of a product is not the product of the maps";
                }

                if (!target.AreEqual(
                    left: functor.Map(value: source.Add(
                        left: left,
                        right: right
                    )),
                    right: target.Add(
                        left: functor.Map(value: left),
                        right: functor.Map(value: right)
                    )
                )) {
                    return $"{name}: the map of a sum is not the sum of the maps";
                }
            }
        }

        return null;
    }
    // A refusal that must name a rewrite rule of the given shape, re-derived here by folding that rule's own pattern
    // and charged replacement through the images.
    private static string? RefusalNamesARule<TValue, TOps>(
        string name,
        PresentedAlgebra<TValue, TOps> source,
        PresentedAlgebra<TValue, TOps> target,
        PresentedAlgebra<TValue, TOps>.Element[] images,
        RuleKind expectedKind,
        int[] expectedPattern
    ) where TOps : struct, IMaterialOps<TValue, TOps> {
        if (PresentedFunctor<TValue, TOps>.TryCreate(
            functor: out _,
            images: images,
            obstruction: out var obstruction,
            source: source,
            target: target
        )) {
            return $"{name}: admitted, where the images break a relation of the source";
        }

        if (obstruction.RuleIndex < 0) { return $"{name}: refused at pair ({obstruction.LeftKey},{obstruction.RightKey}) rather than at the rule that carries the relation"; }
        if (
            (-1L != obstruction.LeftKey) ||
            (-1L != obstruction.RightKey)
        ) { return $"{name}: a rule refusal also named the pair ({obstruction.LeftKey},{obstruction.RightKey})"; }
        if (obstruction.Rule.Kind != expectedKind) { return $"{name}: the refusal names a {obstruction.Rule.Kind} rule, where the relation that breaks is a {expectedKind} one"; }

        if (!obstruction.Rule.Pattern.SequenceEqual(other: expectedPattern)) {
            return $"{name}: the refusal names the pattern [{string.Join(
                separator: ',',
                values: obstruction.Rule.Pattern.ToArray()
            )}], where the relation that breaks is [{string.Join(
                separator: ',',
                values: expectedPattern
            )}]";
        }

        // The rule the refusal names, evaluated on the images by hand: the pattern's product against the charged
        // combination of its replacement terms.
        var replacement = obstruction.Rule.Replacement;
        var offset = 0;
        var total = target.Zero;

        for (var term = 0; (term < obstruction.Rule.TermCount); ++term) {
            var length = replacement[offset++];
            var folded = FoldImages(
                target: target,
                images: images,
                word: replacement.Slice(
                    length: length,
                    start: offset
                )
            );
            var scaled = new TValue[folded.SupportCount];

            for (var index = 0; (index < scaled.Length); ++index) {
                scaled[index] = target.Presentation.Material.Multiply(
                left: obstruction.Rule.Charges[term],
                right: folded.Coefficients[index]
            );
            }

            total = target.Add(
                left: total,
                right: target.FromSupport(
                    keys: folded.Keys,
                    coefficients: scaled
                )
            );
            offset += length;
        }

        return (!target.AreEqual(
            left: FoldImages(
                target: target,
                images: images,
                word: obstruction.Rule.Pattern
            ),
            right: total
        )
            ? null
            : $"{name}: the rule the refusal names holds on the images, so it is not the relation that blocked"
        );
    }
    // The ordered product of a word's images, seeded at the target's unit — the morphism's own fold, restated here so a
    // refusal can be checked without the morphism that was refused.
    private static PresentedAlgebra<TValue, TOps>.Element FoldImages<TValue, TOps>(
        PresentedAlgebra<TValue, TOps> target,
        PresentedAlgebra<TValue, TOps>.Element[] images,
        ReadOnlySpan<int> word
    ) where TOps : struct, IMaterialOps<TValue, TOps> {
        var result = target.Identity;

        foreach (var letter in word) {
            result = target.Multiply(
            left: result,
            right: images[letter]
        );
        }

        return result;
    }
    // Elements to test a morphism on: every basis element, then multi-term combinations, so the map is checked where
    // bilinearity is the whole content rather than only at the basis the admission examined.
    private static PresentedAlgebra<TValue, TOps>.Element[] MorphismDraws<TValue, TOps>(PresentedAlgebra<TValue, TOps> algebra, ulong seed)
        where TOps : struct, IMaterialOps<TValue, TOps> {
        var keys = algebra.Presentation.NormalFormCount;
        var draws = new PresentedAlgebra<TValue, TOps>.Element[(keys + 3)];
        var material = algebra.Presentation.Material;
        var rng = Pcg32XshRr.Create(
            state: seed,
            stream: 11UL
        );

        for (var key = 0; (key < keys); ++key) {
            draws[key] = algebra.FromSupport(
            keys: [key],
            coefficients: [material.One]
        );
        }

        for (var extra = 0; (extra < 3); ++extra) {
            var combination = algebra.Zero;

            for (var key = 0; (key < keys); ++key) {
                if (0U == rng.NextUInt32(
                    maximum: 1U,
                    minimum: 0U
                )) { continue; }

                var weight = material.One;

                for (var repeat = rng.NextUInt32(
                    maximum: 3U,
                    minimum: 0U
                ); (0U != repeat); --repeat) {
                    weight = material.Add(
                    left: weight,
                    right: material.One
                );
                }

                combination = algebra.Add(
                    left: combination,
                    right: algebra.FromSupport(
                        coefficients: [weight],
                        keys: [key]
                    )
                );
            }

            draws[(keys + extra)] = combination;
        }

        return draws;
    }
    // Elements of a free monoid to test a morphism on: drawn words, which is what a source with no finite basis has
    // instead of a basis to enumerate.
    private static PresentedAlgebra<TValue, TOps>.Element[] FreeWordDraws<TValue, TOps>(PresentedAlgebra<TValue, TOps> algebra, int letterCount, ulong seed)
        where TOps : struct, IMaterialOps<TValue, TOps> {
        var draws = new PresentedAlgebra<TValue, TOps>.Element[8];
        var rng = Pcg32XshRr.Create(
            state: seed,
            stream: 13UL
        );

        for (var index = 0; (index < draws.Length); ++index) {
            var word = new int[rng.NextUInt32(
                maximum: 5U,
                minimum: 0U
            )];

            for (var letter = 0; (letter < word.Length); ++letter) {
                word[letter] = ((int)rng.NextUInt32(
                maximum: ((uint)(letterCount - 1)),
                minimum: 0U
            ));
            }

            draws[index] = MorphismWordElement(
                algebra: algebra,
                word: word
            );
        }

        return draws;
    }

}
