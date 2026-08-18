using System.Numerics;

namespace Puck.Maths.Tests;

internal static partial class Subjects {
    // ---- structural claims: exhaustive over a presentation's own basis ----

    /// <summary>Proves the eagerly compiled product table equal to the interpreted tree normalizer on their whole
    /// overlap — every ordered pair of normal-form keys of several presentations — and equal in turn to the running
    /// product on basis elements.</summary>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    /// <remarks>The list reaches a presentation whose re-association charges are LIVE, so the two paths are held equal
    /// with the associator applied during normalization and not only where every splice charges one.</remarks>
    public static string? InterpretedEqualsCompiled() {
        // The monogenic tail [m₀ … m₃] states x⁴ = −Σ mⱼ·xʲ, so the tail [−1, 1, 0, −1] below is the relation
        // x⁴ = 1 − x + x³, written out here as the reduction the independent leg applies.
        var quarticRelation = new BigInteger[] { BigInteger.One, BigInteger.MinusOne, BigInteger.Zero, BigInteger.One, };
        var presentations = new (string Name, ChargedPresentation<FixedQ4816, FixedMaterial> Presentation, Func<ChargedPresentation<FixedQ4816, FixedMaterial>, Func<int, int, (long Key, BigInteger Coefficient)[]>> Reference)[] {
            ("clifford(2,1,1)", Presentations.Clifford<FixedQ4816, FixedMaterial>(
            degenerateCount: 1,
            material: default,
            negativeCount: 1,
            positiveCount: 2
        ),
                static presentation => CliffordBasisReference(
            degenerateCount: 1,
            negativeCount: 1,
            positiveCount: 2,
            presentation: presentation
        )),
            ("cayley-dickson(3)", Presentations.CayleyDickson<FixedQ4816, FixedMaterial>(
            floors: 3,
            basisRelabelling: [],
            material: default
        ),
                static _ => static (left, right) => TwistedBasisProduct(
            charge: Oracles.CayleyDicksonCharge(
                floors: 3,
                leftIndex: left,
                rightIndex: right
            ),
            target: left ^ right
        )),
            ("cayley-dickson(3, live associator)", Presentations.CayleyDickson<FixedQ4816, FixedMaterial>(
            basisRelabelling: [],
            floors: 3,
            liveAssociator: true,
            material: default
        ),
                static _ => static (left, right) => TwistedBasisProduct(
            charge: Oracles.CayleyDicksonCharge(
                floors: 3,
                leftIndex: left,
                rightIndex: right
            ),
            target: left ^ right
        )),
            ("monogenic(x^4)", Presentations.Monogenic<FixedQ4816, FixedMaterial>(
            modulus: [Raw(value: -OneRaw), Raw(value: OneRaw), Raw(value: 0L), Raw(value: -OneRaw)],
            material: default
        ),
                _ => (left, right) => ReducedMonomialProduct(
            leftExponent: left,
            relation: quarticRelation,
            rightExponent: right
        )),
        };

        foreach (var (name, presentation, referenceFactory) in presentations) {
            var reference = referenceFactory(presentation);

            var algebra = PresentedAlgebra<FixedQ4816, FixedMaterial>.Create(presentation: presentation);
            var compiled = algebra.Compile();
            var count = presentation.NormalFormCount;

            if (
                !presentation.HasCompiledNormalFormBasis ||
                (compiled.KeyCount != count) ||
                (compiled.CellCount != (count * count))
            ) {
                return $"{name}: the presentation reports no dense form (compiled={presentation.HasCompiledNormalFormBasis} keys={compiled.KeyCount}/{count} cells={compiled.CellCount})";
            }

            if (!algebra.TryNormalize(
                term: Term.Unit,
                stepLimit: (1L << 20),
                normalForm: out var unit,
                obstruction: out _
            )) {
                return $"{name}: the unit term did not normalize";
            }

            if (Difference(
                left: unit,
                right: algebra.Identity
            ) is { } unitDetail) {
                return $"{name}: normalized unit term differs from Identity at {unitDetail}";
            }

            // The bounded attempt is part of the contract: at a step limit of zero a reducible term must REFUSE, and it
            // must report where it stopped rather than throw or silently return a partial normal form.
            var reducible = Term.Node(
                symbol: Term.Product,
                children: [Term.Leaf(symbol: 0), Term.Leaf(symbol: 0), Term.Leaf(symbol: 0), Term.Leaf(symbol: 0)]
            );

            if (algebra.TryNormalize(
                normalForm: out var refused,
                obstruction: out var refusal,
                stepLimit: 0L,
                term: reducible
            )) {
                return $"{name}: a reducible term normalized inside a step limit of zero, to {refused.SupportCount} term(s)";
            }

            if (
                (0L != refusal.StepsTaken) ||
                (0L > refusal.BlockedKey)
            ) {
                return $"{name}: the refusal reports steps={refusal.StepsTaken} blocked={refusal.BlockedKey}";
            }

            for (var left = 0; (left < count); ++left) {
                for (var right = 0; (right < count); ++right) {
                    var graft = Term.Node(
                        symbol: Term.Product,
                        children: [
                        WordTerm(word: presentation.NormalFormWord(key: left)),
                        WordTerm(word: presentation.NormalFormWord(key: right)),
                    ]
                    );

                    if (!algebra.TryNormalize(
                        normalForm: out var interpreted,
                        obstruction: out var obstruction,
                        stepLimit: (1L << 20),
                        term: graft
                    )) {
                        return $"{name}: the graft of keys ({left},{right}) did not normalize (steps={obstruction.StepsTaken} blocked={obstruction.BlockedKey})";
                    }

                    var product = algebra.Multiply(
                        left: BasisElement(
                            algebra: algebra,
                            key: left
                        ),
                        right: BasisElement(
                            algebra: algebra,
                            key: right
                        )
                    );

                    if (Difference(
                        left: interpreted,
                        right: product
                    ) is { } productDetail) {
                        return $"{name}: keys ({left},{right}) interpreted vs compiled product differ at {productDetail}";
                    }

                    var entries = compiled.TargetCount(
                        leftKey: left,
                        rightKey: right
                    );

                    if (entries != interpreted.SupportCount) {
                        return $"{name}: keys ({left},{right}) cell holds {entries} entr(ies), the normalizer {interpreted.SupportCount}";
                    }

                    if (
                        (0 == entries) &&
                        ((-1L != compiled.Target(
                        leftKey: left,
                        rightKey: right
                    )) || (0L != compiled.Charge(
                        leftKey: left,
                        rightKey: right
                    ).Value))
                    ) {
                        return $"{name}: keys ({left},{right}) annihilate but the leading entry is not the empty one";
                    }

                    for (var entry = 0; (entry < entries); ++entry) {
                        if (
                            (compiled.Target(
                            index: entry,
                            leftKey: left,
                            rightKey: right
                        ) != interpreted.Keys[entry]) ||
                            (compiled.Charge(
                            index: entry,
                            leftKey: left,
                            rightKey: right
                        ).Value != interpreted.Coefficients[entry].Value)
                        ) {
                            return $"{name}: keys ({left},{right}) entry {entry} is ({compiled.Target(
                                index: entry,
                                leftKey: left,
                                rightKey: right
                            )},{compiled.Charge(
                                index: entry,
                                leftKey: left,
                                rightKey: right
                            ).Value}), the normalizer says ({interpreted.Keys[entry]},{interpreted.Coefficients[entry].Value})";
                        }
                    }

                    if (
                        (entries > 0) &&
                        ((compiled.Target(
                        leftKey: left,
                        rightKey: right
                    ) != interpreted.Keys[0])
                            || (compiled.Charge(
                        leftKey: left,
                        rightKey: right
                    ).Value != interpreted.Coefficients[0].Value))
                    ) {
                        return $"{name}: keys ({left},{right}) leading entry disagrees with the indexed one";
                    }

                    // The third leg (worklist A10). Interpreted, compiled and running product agreeing with each other
                    // is INTRA-PRESENTED: TryNormalize and Compile() share the rewriter. This is the independent side —
                    // the basis product formed away from the presentation entirely, in exact BigInteger, and compared
                    // key for key and coefficient for coefficient.
                    var expected = reference(
                        left,
                        right
                    );

                    if (expected.Length != interpreted.SupportCount) {
                        return $"{name}: keys ({left},{right}) normalize to {interpreted.SupportCount} term(s), the independent reference to {expected.Length}";
                    }

                    for (var entry = 0; (entry < expected.Length); ++entry) {
                        if (
                            (interpreted.Keys[entry] != expected[entry].Key) ||
                            (interpreted.Coefficients[entry].Value != (expected[entry].Coefficient * OneRaw))
                        ) {
                            return $"{name}: keys ({left},{right}) entry {entry} normalizes to ({interpreted.Keys[entry]},{interpreted.Coefficients[entry].Value}), the independent reference says ({expected[entry].Key},{(expected[entry].Coefficient * OneRaw)})";
                        }
                    }
                }
            }
        }

        return null;
    }

    // The basis product of a Clifford presentation, away from the presentation: the normal-form word gives each key its
    // blade bitmask, the bubble-sort oracle gives the charge, and the target is the mask exclusive-or read back as a key.
    // The word is data the presentation publishes, not arithmetic it performs; every sign here comes from Oracles.
    private static Func<int, int, (long Key, BigInteger Coefficient)[]> CliffordBasisReference(ChargedPresentation<FixedQ4816, FixedMaterial> presentation, int positiveCount, int negativeCount, int degenerateCount) {
        var count = presentation.NormalFormCount;
        var keyToMask = new int[count];
        var maskToKey = new Dictionary<int, long>();

        for (var key = 0; (key < count); ++key) {
            var mask = 0;

            foreach (var symbol in presentation.NormalFormWord(key: key)) { mask |= (1 << symbol); }

            keyToMask[key] = mask;
            maskToKey[mask] = key;
        }

        return (left, right) => TwistedBasisProduct(
            charge: Oracles.CliffordCharge(
                leftBlade: keyToMask[left],
                rightBlade: keyToMask[right],
                positiveCount: positiveCount,
                negativeCount: negativeCount,
                degenerateCount: degenerateCount
            ),
            target: ((int)maskToKey[keyToMask[left] ^ keyToMask[right]])
        );
    }
    // The basis product of a twisted group presentation, away from the presentation: one target key, one charge, and no
    // term at all where the charge annihilates.
    private static (long Key, BigInteger Coefficient)[] TwistedBasisProduct(int charge, int target) =>
        ((0 == charge)
            ? []
            : [(((long)target), new BigInteger(value: charge))]
        );
    // The basis product of a monogenic presentation, away from the presentation: the monomial x^(l+r) reduced through
    // the relation in exact BigInteger, its non-zero coefficients in ascending exponent order.
    private static (long Key, BigInteger Coefficient)[] ReducedMonomialProduct(BigInteger[] relation, int leftExponent, int rightExponent) {
        var reduced = Oracles.MonogenicMonomialProduct(
            leftExponent: leftExponent,
            relation: relation,
            rightExponent: rightExponent
        );
        var terms = new List<(long Key, BigInteger Coefficient)>();

        for (var exponent = 0; (exponent < reduced.Length); ++exponent) {
            if (!reduced[exponent].IsZero) { terms.Add(item: (((long)exponent), reduced[exponent])); }
        }

        return terms.ToArray();
    }

    /// <summary>Proves the compiled charges of several Clifford signatures — including the conformal <c>(4, 1, 0)</c>
    /// world the four-generator <see cref="GeometricAlgebra"/> cannot reach — equal to the shared-nothing bubble-sort
    /// oracle, at both the house scalar and the exact integer material, and proves the conformal signature's own
    /// associativity certificate.</summary>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? CliffordChargesMatchOracle() {
        var signatures = new[] { (4, 1, 0), (3, 0, 0), (3, 0, 1), (2, 2, 0), (1, 1, 1), (0, 0, 3) };

        foreach (var (positiveCount, negativeCount, degenerateCount) in signatures) {
            var binding = CliffordBinding(
                degenerateCount: degenerateCount,
                negativeCount: negativeCount,
                positiveCount: positiveCount
            );
            var integer = PresentedAlgebra<BigInteger, IntegerMaterial>.Create(presentation: Presentations.Clifford<BigInteger, IntegerMaterial>(
                degenerateCount: degenerateCount,
                material: default,
                negativeCount: negativeCount,
                positiveCount: positiveCount
            ));
            var integerCompiled = integer.Compile();
            var compiled = binding.Algebra.Compile();
            var count = compiled.KeyCount;
            var name = $"clifford({positiveCount},{negativeCount},{degenerateCount})";

            if (count != (1 << ((positiveCount + negativeCount) + degenerateCount))) {
                return $"{name}: {count} normal forms, expected {(1 << ((positiveCount + negativeCount) + degenerateCount))}";
            }

            for (var left = 0; (left < count); ++left) {
                for (var right = 0; (right < count); ++right) {
                    var expected = Oracles.CliffordCharge(
                        leftBlade: binding.KeyToLane[left],
                        rightBlade: binding.KeyToLane[right],
                        positiveCount: positiveCount,
                        negativeCount: negativeCount,
                        degenerateCount: degenerateCount
                    );
                    var entries = compiled.TargetCount(
                        leftKey: left,
                        rightKey: right
                    );

                    if (0 == expected) {
                        if (0 != entries) { return $"{name}: keys ({left},{right}) should annihilate but carry {entries} entr(ies)"; }

                        if (0 != integerCompiled.TargetCount(
                            leftKey: left,
                            rightKey: right
                        )) { return $"{name}: keys ({left},{right}) annihilate over the house scalar but not over the integers"; }

                        continue;
                    }

                    if (1 != entries) { return $"{name}: keys ({left},{right}) carry {entries} entr(ies), expected one"; }

                    var targetBlade = binding.KeyToLane[left] ^ binding.KeyToLane[right];
                    var actualBlade = binding.KeyToLane[((int)compiled.Target(
                        leftKey: left,
                        rightKey: right
                    ))];

                    if (actualBlade != targetBlade) { return $"{name}: keys ({left},{right}) target blade {actualBlade}, expected {targetBlade}"; }

                    var charge = compiled.Charge(
                        leftKey: left,
                        rightKey: right
                    ).Value;

                    if (charge != (expected * OneRaw)) { return $"{name}: keys ({left},{right}) charge {charge}, expected {(expected * OneRaw)}"; }

                    if (integerCompiled.Charge(
                        leftKey: left,
                        rightKey: right
                    ) != expected) {
                        return $"{name}: keys ({left},{right}) integer charge {integerCompiled.Charge(
                            leftKey: left,
                            rightKey: right
                        )}, expected {expected}";
                    }
                }
            }
        }

        var conformal = PresentedAlgebra<BigInteger, IntegerMaterial>.Create(presentation: Presentations.Clifford<BigInteger, IntegerMaterial>(
            degenerateCount: 0,
            material: default,
            negativeCount: 1,
            positiveCount: 4
        ));
        var certificate = conformal.Certify(overlapLimit: (1L << 20));

        if (
            !certificate.IsAssociative ||
            !certificate.HasIdentity ||
            (ClosureOutcome.BasisAssociativityVerified != certificate.Outcome) ||
            (0L != certificate.NonAssociativeTripleCount)
        ) {
            return $"conformal (4,1,0): outcome={certificate.Outcome} associative={certificate.IsAssociative} unital={certificate.HasIdentity} nonassociative triples={certificate.NonAssociativeTripleCount}";
        }

        if (
            certificate.IsCommutative ||
            (0 != certificate.AssociatorWitness.Length) ||
            (0 != certificate.ZeroDivisorWitness.Length)
        ) {
            return $"conformal (4,1,0): commutative={certificate.IsCommutative} associators={certificate.AssociatorWitness.Length} zero divisors={certificate.ZeroDivisorWitness.Length}";
        }

        var degenerateAlgebra = PresentedAlgebra<BigInteger, IntegerMaterial>.Create(presentation: Presentations.Clifford<BigInteger, IntegerMaterial>(
            degenerateCount: 1,
            material: default,
            negativeCount: 0,
            positiveCount: 2
        ));
        var degenerate = degenerateAlgebra.Certify(overlapLimit: (1L << 20));
        var degenerateCompiled = degenerateAlgebra.Compile();

        if (0 == degenerate.ZeroDivisorWitness.Length) {
            return "clifford(2,0,1): a degenerate generator squares to zero, so at least one basis pair must be a zero divisor";
        }

        foreach (var witness in degenerate.ZeroDivisorWitness) {
            if (0 != degenerateCompiled.TargetCount(
                leftKey: witness.LeftKey,
                rightKey: witness.RightKey
            )) {
                return $"clifford(2,0,1): the reported zero divisor ({witness.LeftKey},{witness.RightKey}) has a non-empty compiled cell";
            }
        }

        return null;
    }
    /// <summary>
    /// Pins the certification contract at the rewrite-relation counterexample: two declarations send <c>gg</c> to
    /// distinct irreducible words, while declaration priority still induces an associative two-element product.
    /// Certification may report that product's basis associativity, but its public vocabulary must claim no rewrite
    /// confluence.
    /// </summary>
    /// <returns>The counterexample text, or <see langword="null"/> when the contract holds.</returns>
    public static string? CertificationScopesAssociativityNotConfluence() {
        var firstBranch = RewriteRule<BigInteger>.PackReplacement(terms: [[]]);
        var secondBranch = RewriteRule<BigInteger>.PackReplacement(terms: [[0]]);
        var presentation = ChargedPresentation<BigInteger, IntegerMaterial>.Create(
            generators: [new(
                    degree: 1,
                    inputs: new[] { 0 },
                    outputs: new[] { 0 },
                    symbol: 0
                )],
            rules: [
                new(
                    kind: RuleKind.Reduce,
                    pattern: new[] { 0, 0 },
                    replacement: firstBranch,
                    charges: new[] { BigInteger.One }
                ),
                new(
                    kind: RuleKind.Reduce,
                    pattern: new[] { 0, 0 },
                    replacement: secondBranch,
                    charges: new[] { BigInteger.One }
                ),
            ],
            material: default
        );
        var algebra = PresentedAlgebra<BigInteger, IntegerMaterial>.Create(presentation: presentation);
        var generator = algebra.Generator(symbol: 0);
        var certificate = algebra.Certify(overlapLimit: long.MaxValue);

        if (algebra.AreEqual(
            left: algebra.Identity,
            right: generator
        )) {
            return "the two critical-peak branches 1 <- gg -> g are not distinct";
        }

        if (!algebra.AreEqual(
            left: algebra.Multiply(
                left: generator,
                right: generator
            ),
            right: algebra.Identity
        )) {
            return "declaration priority no longer induces the control product g² = 1";
        }

        if (
            (ClosureOutcome.BasisAssociativityVerified != certificate.Outcome) ||
            !certificate.IsAssociative ||
            (0L != certificate.NonAssociativeTripleCount) ||
            (0 != certificate.AssociatorWitness.Length)
        ) {
            return $"the deterministic product reports {certificate.Outcome}, associative={certificate.IsAssociative}, nonassociative triples={certificate.NonAssociativeTripleCount}, witnesses={certificate.AssociatorWitness.Length}";
        }

        if (Enum.IsDefined(
            enumType: typeof(ClosureOutcome),
            value: "Confluent"
        )) {
            return "ClosureOutcome still exposes a Confluent member for a check that never follows competing rewrite routes";
        }

        var parameter = typeof(PresentedAlgebra<BigInteger, IntegerMaterial>)
            .GetMethod(name: nameof(PresentedAlgebra<BigInteger, IntegerMaterial>.Certify))!
            .GetParameters()
            .Single();

        return (("overlapLimit" == parameter.Name)
            ? null
            : $"Certify broke the historical named-argument contract by exposing its basis-tuple budget as '{parameter.Name}'"
        );
    }
    /// <summary>Proves the Cayley–Dickson tower's compiled charges equal to the doubling recursion at floors two
    /// through four, and its certificates equal to the ladder's known law losses: the quaternion floor associates, the
    /// octonion floor does not but is alternative, and the sedenion floor is neither.</summary>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    /// <remarks><see cref="Oracles.CayleyDicksonCharge"/> TRANSCRIBES the recursion the presentation carries, so that
    /// comparison proves faithful carriage and not a correct sign rule; the independent witness beside it is the
    /// shipped nested doubling tower, multiplied out at every floor in the same loop.</remarks>
    public static string? CayleyDicksonChargesAndCertificates() {
        for (var floors = 2; (floors <= 4); ++floors) {
            var algebra = PresentedAlgebra<BigInteger, IntegerMaterial>.Create(presentation: Presentations.CayleyDickson<BigInteger, IntegerMaterial>(
                floors: floors,
                basisRelabelling: [],
                material: default
            ));
            var compiled = algebra.Compile();
            var count = compiled.KeyCount;

            if (count != (1 << floors)) { return $"cayley-dickson({floors}): {count} normal forms, expected {(1 << floors)}"; }

            for (var left = 0; (left < count); ++left) {
                for (var right = 0; (right < count); ++right) {
                    var expected = Oracles.CayleyDicksonCharge(
                        floors: floors,
                        leftIndex: left,
                        rightIndex: right
                    );

                    if (
                        (1 != compiled.TargetCount(
                        leftKey: left,
                        rightKey: right
                    )) ||
                        (compiled.Target(
                        leftKey: left,
                        rightKey: right
                    ) != (left ^ right)) ||
                        (compiled.Charge(
                        leftKey: left,
                        rightKey: right
                    ) != expected)
                    ) {
                        return $"cayley-dickson({floors}): keys ({left},{right}) compiled to ({compiled.Target(
                            leftKey: left,
                            rightKey: right
                        )},{compiled.Charge(
                            leftKey: left,
                            rightKey: right
                        )}), expected ({(left ^ right)},{expected})";
                    }

                    // The oracle above transcribes the recursion the presentation carries, so it proves carriage and
                    // not the recursion. The witness that DOES stand outside both, at every floor rather than at one:
                    // the two units multiplied out through the shipped nested tower.
                    if (DoublingTowerUnitCharge(
                        floors: floors,
                        left: left,
                        right: right
                    ) != expected) {
                        return $"cayley-dickson({floors}): keys ({left},{right}) charge {expected}, where the shipped doubling tower multiplies the two units out to {DoublingTowerUnitCharge(
                            floors: floors,
                            left: left,
                            right: right
                        )}";
                    }
                }
            }

            var certificate = algebra.Certify(overlapLimit: (1L << 22));
            var expectedAssociative = (floors <= 2);
            var expectedAlternative = (floors <= 3);

            if (
                (certificate.IsAssociative != expectedAssociative) ||
                (certificate.IsAlternative != expectedAlternative) ||
                !certificate.HasIdentity
            ) {
                return $"cayley-dickson({floors}): associative={certificate.IsAssociative} (expected {expectedAssociative}) alternative={certificate.IsAlternative} (expected {expectedAlternative}) unital={certificate.HasIdentity}";
            }

            if (expectedAssociative) {
                if (
                    (ClosureOutcome.BasisAssociativityVerified != certificate.Outcome) ||
                    (0 != certificate.AssociatorWitness.Length)
                ) {
                    return $"cayley-dickson({floors}): outcome={certificate.Outcome} with {certificate.AssociatorWitness.Length} associator charge(s) on an associative floor";
                }

                continue;
            }

            if (
                (ClosureOutcome.BasisNonAssociativityDetected != certificate.Outcome) ||
                (0L == certificate.NonAssociativeTripleCount) ||
                (0 == certificate.AssociatorWitness.Length)
            ) {
                return $"cayley-dickson({floors}): outcome={certificate.Outcome} nonassociative triples={certificate.NonAssociativeTripleCount} associators={certificate.AssociatorWitness.Length} on a non-associative floor";
            }

            // Every reported charge is the associator's own leading coefficient, recomputed here from the compiled
            // charges rather than read back from the certificate's own machinery.
            foreach (var charge in certificate.AssociatorWitness) {
                var expected = AssociatorLeadingCharge(
                    compiled: compiled,
                    left: charge.Left,
                    middle: charge.Middle,
                    right: charge.Right
                );

                if (charge.Charge != expected) {
                    return $"cayley-dickson({floors}): associator ({charge.Left},{charge.Middle},{charge.Right}) charge {charge.Charge}, recomputed {expected}";
                }
            }

            // A bounded search that ran out reports THAT, distinctly from having found no ambiguity, and issues no flag
            // it did not prove.
            var truncated = algebra.Certify(overlapLimit: 4L);

            if (
                (ClosureOutcome.SearchLimitReached != truncated.Outcome) ||
                truncated.IsAssociative ||
                truncated.IsCommutative ||
                truncated.IsAlternative
            ) {
                return $"cayley-dickson({floors}): a four-overlap budget reported outcome={truncated.Outcome} associative={truncated.IsAssociative} commutative={truncated.IsCommutative} alternative={truncated.IsAlternative}";
            }
        }

        return null;
    }
    /// <summary>Proves that a declared re-association 3-cochain is applied DURING normalization, and that what it
    /// charges depends only on the bracketing and never on the route that rebalanced it.</summary>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    /// <remarks>
    /// The oracle is the bracketing's own nested products, which re-associate nothing at all: a term tree normalizes
    /// through the flat word rewriter plus the splice charges, and the same tree evaluated bracket by bracket through
    /// <c>Multiply</c> shares no step with it. Agreement on every bracketing of every ordered basis triple and on all
    /// five bracketings of every quadruple is therefore route-independence in operational form, and the certificate's
    /// own quadruple identity is the same statement about the charges alone.
    /// </remarks>
    public static string? ReassociationRouteCoherent() {
        for (var floors = 2; (floors <= 4); ++floors) {
            var live = PresentedAlgebra<BigInteger, IntegerMaterial>.Create(presentation: Presentations.CayleyDickson<BigInteger, IntegerMaterial>(
                basisRelabelling: [],
                floors: floors,
                liveAssociator: true,
                material: default
            ));
            var flat = PresentedAlgebra<BigInteger, IntegerMaterial>.Create(presentation: Presentations.CayleyDickson<BigInteger, IntegerMaterial>(
                floors: floors,
                basisRelabelling: [],
                material: default
            ));

            if (
                !live.Presentation.HasLiveReassociation ||
                flat.Presentation.HasLiveReassociation
            ) {
                return $"cayley-dickson({floors}): live regime reports {live.Presentation.HasLiveReassociation} and the uniform one {flat.Presentation.HasLiveReassociation}";
            }

            var liveCompiled = live.Compile();
            var flatCompiled = flat.Compile();
            var count = liveCompiled.KeyCount;

            // Declaring the associator moves no cell. It is read when a bracket is spliced away, never when a pair
            // multiplies, so the compiled product of a floor is the same table either way.
            for (var left = 0; (left < count); ++left) {
                for (var right = 0; (right < count); ++right) {
                    if (
                        (liveCompiled.TargetCount(
                        leftKey: left,
                        rightKey: right
                    ) != flatCompiled.TargetCount(
                        leftKey: left,
                        rightKey: right
                    )) ||
                        (liveCompiled.Target(
                        leftKey: left,
                        rightKey: right
                    ) != flatCompiled.Target(
                        leftKey: left,
                        rightKey: right
                    )) ||
                        (liveCompiled.Charge(
                        leftKey: left,
                        rightKey: right
                    ) != flatCompiled.Charge(
                        leftKey: left,
                        rightKey: right
                    ))
                    ) {
                        return $"cayley-dickson({floors}): declaring the associator moved cell ({left},{right}) to ({liveCompiled.Target(
                            leftKey: left,
                            rightKey: right
                        )},{liveCompiled.Charge(
                            leftKey: left,
                            rightKey: right
                        )}) from ({flatCompiled.Target(
                            leftKey: left,
                            rightKey: right
                        )},{flatCompiled.Charge(
                            leftKey: left,
                            rightKey: right
                        )})";
                    }
                }
            }

            var liveCertificate = live.Certify(overlapLimit: (1L << 22));
            var flatCertificate = flat.Certify(overlapLimit: (1L << 22));

            if (
                (liveCertificate.IsAssociative != flatCertificate.IsAssociative) ||
                (liveCertificate.IsAlternative != flatCertificate.IsAlternative) ||
                (liveCertificate.IsCommutative != flatCertificate.IsCommutative) ||
                (liveCertificate.NonAssociativeTripleCount != flatCertificate.NonAssociativeTripleCount) ||
                (liveCertificate.Outcome != flatCertificate.Outcome)
            ) {
                return $"cayley-dickson({floors}): declaring the associator moved a law flag — live(assoc={liveCertificate.IsAssociative} alt={liveCertificate.IsAlternative} comm={liveCertificate.IsCommutative} amb={liveCertificate.NonAssociativeTripleCount} {liveCertificate.Outcome}) against uniform(assoc={flatCertificate.IsAssociative} alt={flatCertificate.IsAlternative} comm={flatCertificate.IsCommutative} amb={flatCertificate.NonAssociativeTripleCount} {flatCertificate.Outcome})";
            }

            // The tower's associator is the coboundary of the SAME 2-cochain its products carry, and a coboundary is a
            // cocycle, so every floor is coherent — the sedenion floor's law loss is a failure of alternativity and of
            // division, never of coherence.
            if (
                !liveCertificate.IsCoherent ||
                (0 != liveCertificate.CoherenceWitness.Length)
            ) {
                return $"cayley-dickson({floors}): the declared associator reports coherent={liveCertificate.IsCoherent} with {liveCertificate.CoherenceWitness.Length} witness(es)";
            }

            if (
                !flatCertificate.IsCoherent ||
                (0 != flatCertificate.CoherenceWitness.Length)
            ) {
                return $"cayley-dickson({floors}): the uniform charge of one reports coherent={flatCertificate.IsCoherent} with {flatCertificate.CoherenceWitness.Length} witness(es)";
            }

            var sensitive = 0;

            for (var first = 0; (first < count); ++first) {
                for (var second = 0; (second < count); ++second) {
                    for (var third = 0; (third < count); ++third) {
                        var leftTree = BracketPair(
                            left: Term.Leaf(symbol: first),
                            right: BracketPair(
                                left: Term.Leaf(symbol: second),
                                right: Term.Leaf(symbol: third)
                            )
                        );
                        var rightTree = BracketPair(
                            left: BracketPair(
                                left: Term.Leaf(symbol: first),
                                right: Term.Leaf(symbol: second)
                            ),
                            right: Term.Leaf(symbol: third)
                        );

                        if (NormalizesTo(
                            algebra: live,
                            term: leftTree,
                            expected: live.Multiply(
                                left: PresentedBasis(
                                    algebra: live,
                                    key: first
                                ),
                                right: live.Multiply(
                                    left: PresentedBasis(
                                        algebra: live,
                                        key: second
                                    ),
                                    right: PresentedBasis(
                                        algebra: live,
                                        key: third
                                    )
                                )
                            )
                        ) is { } nestedDetail) {
                            return $"cayley-dickson({floors}) live: the right-nested triple ({first},{second},{third}) {nestedDetail}";
                        }

                        if (NormalizesTo(
                            algebra: live,
                            term: rightTree,
                            expected: live.Multiply(
                                left: live.Multiply(
                                    left: PresentedBasis(
                                        algebra: live,
                                        key: first
                                    ),
                                    right: PresentedBasis(
                                        algebra: live,
                                        key: second
                                    )
                                ),
                                right: PresentedBasis(
                                    algebra: live,
                                    key: third
                                )
                            )
                        ) is { } flatDetail) {
                            return $"cayley-dickson({floors}) live: the left-normed triple ({first},{second},{third}) {flatDetail}";
                        }

                        _ = live.TryNormalize(
                            normalForm: out var nested,
                            obstruction: out _,
                            stepLimit: NormalizationSteps,
                            term: leftTree
                        );
                        _ = live.TryNormalize(
                            normalForm: out var normed,
                            obstruction: out _,
                            stepLimit: NormalizationSteps,
                            term: rightTree
                        );

                        if (!live.AreEqual(
                            left: nested,
                            right: normed
                        )) { ++sensitive; }

                        // The uniform floor is bracket-inert and stays exactly where phase 1 left it: both bracketings
                        // flatten to the same word and answer with the left-normed product.
                        _ = flat.TryNormalize(
                            normalForm: out var uniformNested,
                            obstruction: out _,
                            stepLimit: NormalizationSteps,
                            term: leftTree
                        );

                        if (!flat.AreEqual(
                            left: uniformNested,
                            right: flat.Multiply(
                                left: flat.Multiply(
                                    left: PresentedBasis(
                                        algebra: flat,
                                        key: first
                                    ),
                                    right: PresentedBasis(
                                        algebra: flat,
                                        key: second
                                    )
                                ),
                                right: PresentedBasis(
                                    algebra: flat,
                                    key: third
                                )
                            )
                        )) {
                            return $"cayley-dickson({floors}) uniform: the right-nested triple ({first},{second},{third}) stopped answering with the left-normed product";
                        }
                    }
                }
            }

            // A bracketing matters exactly where the associator does not vanish, so an associator table of ones would
            // report zero here and fail rather than pass quietly.
            if (sensitive != liveCertificate.NonAssociativeTripleCount) {
                return $"cayley-dickson({floors}): {sensitive} bracket-sensitive triple(s) against {liveCertificate.NonAssociativeTripleCount} nonzero associator(s)";
            }

            if (floors > 3) { continue; }

            for (var first = 0; (first < count); ++first) {
                for (var second = 0; (second < count); ++second) {
                    for (var third = 0; (third < count); ++third) {
                        for (var fourth = 0; (fourth < count); ++fourth) {
                            if (QuadrupleBracketingsAgree(
                                algebra: live,
                                first: first,
                                fourth: fourth,
                                second: second,
                                third: third
                            ) is { } detail) {
                                return $"cayley-dickson({floors}) live: the quadruple ({first},{second},{third},{fourth}) {detail}";
                            }
                        }
                    }
                }
            }
        }

        // A pair carries its factors' brackets. Tensoring the live octonion floor with the one-generator floor pairs
        // eight keys against one, so the paired presentation IS that floor again — and its brackets must still be
        // charged, which a tensor that dropped the cochain would silently stop doing.
        var paired = PresentedAlgebra<BigInteger, IntegerMaterial>.Create(presentation: Presentations.Tensor(
            left: Presentations.CayleyDickson<BigInteger, IntegerMaterial>(
                basisRelabelling: [],
                floors: 3,
                liveAssociator: true,
                material: default
            ),
            right: Presentations.CayleyDickson<BigInteger, IntegerMaterial>(
                floors: 0,
                basisRelabelling: [],
                material: default
            )
        ));
        var pairedCertificate = paired.Certify(overlapLimit: (1L << 22));
        var pairedSensitive = 0;

        if (
            !paired.Presentation.HasLiveReassociation ||
            !pairedCertificate.IsCoherent
        ) {
            return $"the paired presentation reports live={paired.Presentation.HasLiveReassociation} coherent={pairedCertificate.IsCoherent}";
        }

        for (var first = 0; (first < paired.Presentation.NormalFormCount); ++first) {
            for (var second = 0; (second < paired.Presentation.NormalFormCount); ++second) {
                for (var third = 0; (third < paired.Presentation.NormalFormCount); ++third) {
                    var nestedTerm = BracketPair(
                        left: Term.Leaf(symbol: first),
                        right: BracketPair(
                            left: Term.Leaf(symbol: second),
                            right: Term.Leaf(symbol: third)
                        )
                    );

                    if (NormalizesTo(
                        algebra: paired,
                        term: nestedTerm,
                        expected: paired.Multiply(
                            left: PresentedBasis(
                                algebra: paired,
                                key: first
                            ),
                            right: paired.Multiply(
                                left: PresentedBasis(
                                    algebra: paired,
                                    key: second
                                ),
                                right: PresentedBasis(
                                    algebra: paired,
                                    key: third
                                )
                            )
                        )
                    ) is { } pairDetail) {
                        return $"paired octonion floor: the right-nested triple ({first},{second},{third}) {pairDetail}";
                    }

                    _ = paired.TryNormalize(
                        normalForm: out var pairNested,
                        obstruction: out _,
                        stepLimit: NormalizationSteps,
                        term: nestedTerm
                    );
                    _ = paired.TryNormalize(
                        term: BracketPair(
                            left: BracketPair(
                                left: Term.Leaf(symbol: first),
                                right: Term.Leaf(symbol: second)
                            ),
                            right: Term.Leaf(symbol: third)
                        ),
                        stepLimit: NormalizationSteps,
                        normalForm: out var pairNormed,
                        obstruction: out _
                    );

                    if (!paired.AreEqual(
                        left: pairNested,
                        right: pairNormed
                    )) { ++pairedSensitive; }
                }
            }
        }

        if (pairedSensitive != pairedCertificate.NonAssociativeTripleCount) {
            return $"paired octonion floor: {pairedSensitive} bracket-sensitive triple(s) against {pairedCertificate.NonAssociativeTripleCount} nonzero associator(s)";
        }

        // A 3-cochain that is NOT a cocycle is carried, computed and witnessed rather than refused: the object works,
        // and the certificate says what it lost.
        var perturbed = PresentedAlgebra<BigInteger, IntegerMaterial>.Create(presentation: PerturbedCochainPresentation());
        var perturbedCertificate = perturbed.Certify(overlapLimit: (1L << 20));

        if (
            perturbedCertificate.IsCoherent ||
            (0 == perturbedCertificate.CoherenceWitness.Length)
        ) {
            return $"the perturbed 3-cochain reports coherent={perturbedCertificate.IsCoherent} with {perturbedCertificate.CoherenceWitness.Length} witness(es)";
        }

        foreach (var witness in perturbedCertificate.CoherenceWitness) {
            if (witness.Nested == witness.Flat) {
                return $"the coherence witness ({witness.First},{witness.Second},{witness.Third},{witness.Fourth}) charges both routes {witness.Nested}";
            }
        }

        // The declaration is refused where a triple of keys cannot name a bracket at all, and the refusal is an
        // impossibility argument rather than a budget.
        return (RefusesDeclaration(
            name: "a 3-cochain on a presentation whose normal forms outnumber its generators",
            build: static () => {
                var charges = new BigInteger[8];

                charges.AsSpan().Fill(value: BigInteger.One);

                _ = ChargedPresentation<BigInteger, IntegerMaterial>.Create(
                    generators: SingleColourBasis(count: 2),
                    rules: [
                        new(
                        kind: RuleKind.Reassociate,
                        pattern: ReadOnlyMemory<int>.Empty,
                        replacement: ReadOnlyMemory<int>.Empty,
                        charges: charges
                    ),
                    new(
                        kind: RuleKind.Reduce,
                        pattern: new[] { 0, 0 },
                        replacement: RewriteRule<BigInteger>.PackReplacement(terms: [[]]),
                        charges: new[] { BigInteger.One }
                    ),
                    new(
                        kind: RuleKind.Reduce,
                        pattern: new[] { 1, 1 },
                        replacement: RewriteRule<BigInteger>.PackReplacement(terms: [[]]),
                        charges: new[] { BigInteger.One }
                    ),
                    new(
                        kind: RuleKind.Swap,
                        pattern: new[] { 1, 0 },
                        replacement: RewriteRule<BigInteger>.PackReplacement(terms: [[0, 1]]),
                        charges: new[] { BigInteger.MinusOne }
                    ),
                    ],
                    material: default
                );
            }
        ) ?? (RefusesDeclaration(
            name: "a re-association rule carrying neither one charge nor one per generator triple",
            build: static () => {
                _ = ChargedPresentation<BigInteger, IntegerMaterial>.Create(
                    generators: SingleColourBasis(count: 1),
                    rules: [new(
                        kind: RuleKind.Reassociate,
                        pattern: ReadOnlyMemory<int>.Empty,
                        replacement: ReadOnlyMemory<int>.Empty,
                        charges: new[] { BigInteger.One, BigInteger.One }
                    )],
                    material: default
                );
            }
        )
            // Normalization at the unit, in both shapes. The unit spelled as the empty product carries no letter for a
            // splice to charge with, so a charge sitting where the unit sits is one no spelling of the term can pay and
            // two spellings of one element would answer differently. Both are refused at construction rather than
            // certified afterwards, which is what a coherent-but-unnormalized coboundary used to escape through.
            ?? (RefusesDeclaration(
            name: "a 3-cochain charging a triple that names the unit",
            build: static () => _ = CyclicGroupPresentation(
                flippedTriple: 6,
                order: 2
            )
        )
            ?? RefusesDeclaration(
            name: "a uniform re-association charge that is not the material's one",
            build: static () => _ = ChargedPresentation<BigInteger, IntegerMaterial>.Create(
                generators: SingleColourBasis(count: 1),
                rules: [
                    new(
                        kind: RuleKind.Reassociate,
                        pattern: ReadOnlyMemory<int>.Empty,
                        replacement: ReadOnlyMemory<int>.Empty,
                        charges: new[] { BigInteger.MinusOne }
                    ),
                    new(
                        kind: RuleKind.Reduce,
                        pattern: new[] { 0, 0, 0 },
                        replacement: RewriteRule<BigInteger>.PackReplacement(terms: [[0, 0]]),
                        charges: new[] { BigInteger.One }
                    ),
                ],
                material: default
            )
        ))));
    }
    /// <summary>Proves that a uniform re-association charge of one leaves a term's brackets inert at every presentation
    /// the catalogue ships, which is the phase-1 behaviour every existing gate pins.</summary>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? ReassociationBracketsInert() =>
        (BracketsAreInert(
            name: "clifford(3,0,1)",
            presentation: Presentations.Clifford<FixedQ4816, FixedMaterial>(
                degenerateCount: 1,
                material: default,
                negativeCount: 0,
                positiveCount: 3
            )
        )
            ?? (BracketsAreInert(
            name: "cayley-dickson(3)",
            presentation: Presentations.CayleyDickson<FixedQ4816, FixedMaterial>(
                floors: 3,
                basisRelabelling: [],
                material: default
            )
        )
            ?? (BracketsAreInert(
            name: "monogenic(x^3)",
            presentation: Presentations.Monogenic<FixedQ4816, FixedMaterial>(
                modulus: [Raw(value: -OneRaw), Raw(value: OneRaw), Raw(value: 0L)],
                material: default
            )
        )
            ?? (BracketsAreInert(
            name: "quiver(3)",
            presentation: CodiscreteQuiver<BigInteger, CountingMaterial>(
                material: default,
                order: 3
            )
        )
            ?? BracketsAreInert(
            name: "free-monoid(2)",
            presentation: Presentations.FreeMonoid<bool, BooleanMaterial>(
                letterCount: 2,
                material: default,
                windowDegree: 4
            )
        )))));
    /// <summary>Proves the exact orders of the reflection worlds a bounded enumeration reaches, two ways: as the size of
    /// the presented basis, and as the size of the group the lattice action closes on.</summary>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    /// <remarks>
    /// The two counts share no step. One is the set of words the rewriting system cannot shorten or lower; the other is
    /// the set of permutations the mirrors generate, closed by breadth-first search over the lattice's own reflection
    /// map. Where the diagram is a chain of bonds of three the classical order is <c>(rank + 1)!</c> — the symmetric
    /// group on one more letter than the rank — and that factorial is computed here rather than tabulated, so the six,
    /// the twenty-four and the hundred and twenty are each asserted against a third statement of themselves.
    /// </remarks>
    public static string? GroupOrdersExact() {
        // The dihedral family. At rank two the involution and braid rules ARE a complete rewriting system, so the
        // normal-form count is the group's order exactly, and every certificate the presentation can carry holds.
        foreach (var bond in DihedralBonds) {
            var order = (2 * bond);
            var presentation = Presentations.Coxeter<BigInteger, IntegerMaterial>(
                bonds: [1, bond, bond, 1],
                material: default,
                rank: 2
            );
            var algebra = PresentedAlgebra<BigInteger, IntegerMaterial>.Create(presentation: presentation);

            if (
                !presentation.HasCompiledNormalFormBasis ||
                (order != presentation.NormalFormCount)
            ) {
                return $"dihedral({bond}): {presentation.NormalFormCount} normal form(s) against an order of {order}, compiled={presentation.HasCompiledNormalFormBasis}";
            }

            var certificate = algebra.Certify(overlapLimit: (1L << 22));

            if (
                (ClosureOutcome.BasisAssociativityVerified != certificate.Outcome) ||
                !certificate.IsAssociative ||
                !certificate.HasIdentity ||
                !certificate.IsCoherent ||
                (certificate.IsCommutative != (2 == bond)) ||
                (0L != certificate.NonAssociativeTripleCount) ||
                (0 != certificate.ZeroDivisorWitness.Length)
            ) {
                return $"dihedral({bond}): outcome={certificate.Outcome} associative={certificate.IsAssociative} unital={certificate.HasIdentity} commutative={certificate.IsCommutative} coherent={certificate.IsCoherent} nonassociative triples={certificate.NonAssociativeTripleCount} divisors={certificate.ZeroDivisorWitness.Length}";
            }

            if (GroupRegimeIsWhole(
                algebra: algebra,
                enumerateOrbit: true,
                name: $"dihedral({bond})",
                order: order
            ) is { } detail) { return detail; }

            // A reflection is its own inverse, so every witness here is the generator itself at charge one — the
            // involution rule read back out of the product rather than out of the rule list.
            if (!PresentedGroup<BigInteger, IntegerMaterial>.TryCertify(
                algebra: algebra,
                group: out var group,
                obstruction: out _
            )) {
                return $"dihedral({bond}): the group regime refused after certifying once";
            }

            for (var symbol = 0; (symbol < 2); ++symbol) {
                var witness = group.UnitWitnesses[symbol];

                if (
                    (witness.Symbol != symbol) ||
                    (witness.InverseKey != algebra.Generator(symbol: symbol).Keys[0]) ||
                    (BigInteger.One != witness.InverseCharge)
                ) {
                    return $"dihedral({bond}): generator {symbol} is an involution, but its witness is ({witness.Symbol},{witness.InverseKey},{witness.InverseCharge})";
                }
            }
        }

        // Completeness reaches past rank two wherever the DIAGRAM does not. A product of pieces of rank at most two is
        // decided by the same two rules, so its normal-form count is the product of its factors' orders.
        foreach (var (name, rank, bonds, order) in ReducibleCoxeterWorlds) {
            var presentation = Presentations.Coxeter<BigInteger, IntegerMaterial>(
                bonds: bonds,
                material: default,
                rank: rank
            );
            var algebra = PresentedAlgebra<BigInteger, IntegerMaterial>.Create(presentation: presentation);

            if (
                !presentation.HasCompiledNormalFormBasis ||
                (order != presentation.NormalFormCount)
            ) {
                return $"{name}: {presentation.NormalFormCount} normal form(s) against an order of {order}, compiled={presentation.HasCompiledNormalFormBasis}";
            }

            if (GroupRegimeIsWhole(
                algebra: algebra,
                enumerateOrbit: true,
                name: name,
                order: order
            ) is { } detail) { return detail; }
        }

        // The reflection worlds the lattice itself carries. Each order is a theorem: two for one mirror, four for the
        // pair that commutes, and the factorials for the chains.
        foreach (var (mirrors, order) in EnumerableReflectionWorlds) {
            var system = ReflectionSystem.Create(mirrors: mirrors);
            var name = $"mirrors[{string.Join(
                separator: ',',
                values: mirrors
            )}]";

            if (!system.TryEnumerateGroup(
                obstruction: out var refusal,
                permutations: out var permutations,
                searchLimit: 1024L
            )) {
                return $"{name}: the enumeration refused with {refusal.Outcome} after {refusal.PointsReached} element(s)";
            }

            var pointCount = system.Points.Length;
            var enumerated = (permutations.Length / pointCount);

            if (enumerated != order) { return $"{name}: the action closes on {enumerated} element(s) against an order of {order}"; }

            if (
                IsChainDiagram(
                bonds: system.BondMatrix,
                rank: mirrors.Length
            ) &&
                (Factorial(value: (mirrors.Length + 1)) != order)
            ) {
                return $"{name}: a chain of rank {mirrors.Length} has order {order}, and the symmetric group on {(mirrors.Length + 1)} letters has {Factorial(value: (mirrors.Length + 1))}";
            }

            // The presented basis is built from the SAME rows the action closure was counted from, and every rule
            // pattern has length two or zero, so a normal-form count equal to the row count is forced. It pins that the
            // discovery ran and the table is closed; it is NOT a third independent reading of the order. The two
            // independent ones are the action closure above and the factorial below.
            var presentation = Presentations.PermutationGroup<BigInteger, IntegerMaterial>(
                pointCount: pointCount,
                permutations: permutations.Span,
                material: default
            );
            var algebra = PresentedAlgebra<BigInteger, IntegerMaterial>.Create(presentation: presentation);

            if (
                !presentation.HasCompiledNormalFormBasis ||
                (order != presentation.NormalFormCount)
            ) {
                return $"{name}: the presented basis holds {presentation.NormalFormCount} element(s) against an order of {order}, compiled={presentation.HasCompiledNormalFormBasis}";
            }

            if (GroupRegimeIsWhole(
                algebra: algebra,
                enumerateOrbit: (order <= 24),
                name: name,
                order: order
            ) is { } detail) { return detail; }

            if (order > 24) { continue; }

            var certificate = algebra.Certify(overlapLimit: (1L << 22));

            if (
                (ClosureOutcome.BasisAssociativityVerified != certificate.Outcome) ||
                !certificate.IsAssociative ||
                !certificate.HasIdentity ||
                !certificate.IsCoherent ||
                (0 != certificate.ZeroDivisorWitness.Length)
            ) {
                return $"{name}: outcome={certificate.Outcome} associative={certificate.IsAssociative} unital={certificate.HasIdentity} coherent={certificate.IsCoherent} divisors={certificate.ZeroDivisorWitness.Length}";
            }

            // Where the word presentation is complete, the two presentations of the one group are the same algebra: the
            // map that sends a word to the permutation it acts as is a bijection and carries the product to the product.
            if (
                (mirrors.Length <= 2) &&
                (WordAndTableAgree(
                name: name,
                system: system,
                permutations: permutations.Span,
                table: algebra
            ) is { } mismatch)
            ) {
                return mismatch;
            }
        }

        // The regime is not reflection-only, and the witness charge is COMPUTED rather than assumed. A Clifford basis
        // whose generators square to minus one inverts each of them at minus one, and a monogenic basis inverts its
        // generator into a different basis element entirely — which is what makes the witness a search rather than a
        // reflection's fixed point.
        var clifford = PresentedAlgebra<BigInteger, IntegerMaterial>.Create(presentation: Presentations.Clifford<BigInteger, IntegerMaterial>(
            degenerateCount: 0,
            material: default,
            negativeCount: 3,
            positiveCount: 0
        ));

        if (GroupRegimeIsWhole(
            name: "clifford(0,3,0)",
            algebra: clifford,
            order: clifford.Presentation.NormalFormCount,
            enumerateOrbit: true
        ) is { } bladeDetail) { return bladeDetail; }

        if (!PresentedGroup<BigInteger, IntegerMaterial>.TryCertify(
            algebra: clifford,
            group: out var bladeGroup,
            obstruction: out _
        )) {
            return "clifford(0,3,0): the group regime refused after certifying once";
        }

        for (var symbol = 0; (symbol < bladeGroup.UnitWitnesses.Length); ++symbol) {
            var witness = bladeGroup.UnitWitnesses[symbol];

            if (
                (witness.InverseKey != clifford.Generator(symbol: symbol).Keys[0]) ||
                (BigInteger.MinusOne != witness.InverseCharge)
            ) {
                return $"clifford(0,3,0): generator {symbol} squares to minus one, but its witness is ({witness.InverseKey},{witness.InverseCharge})";
            }
        }

        var monogenic = PresentedAlgebra<BigInteger, IntegerMaterial>.Create(presentation: Presentations.Monogenic<BigInteger, IntegerMaterial>(
            modulus: [BigInteger.One, BigInteger.Zero, BigInteger.Zero],
            material: default
        ));

        if (GroupRegimeIsWhole(
            name: "monogenic(x^3+1)",
            algebra: monogenic,
            order: monogenic.Presentation.NormalFormCount,
            enumerateOrbit: true
        ) is { } powerDetail) { return powerDetail; }

        if (!PresentedGroup<BigInteger, IntegerMaterial>.TryCertify(
            algebra: monogenic,
            group: out var powerGroup,
            obstruction: out _
        )) {
            return "monogenic(x^3+1): the group regime refused after certifying once";
        }

        var cube = powerGroup.UnitWitnesses[0];

        if (
            (2L != cube.InverseKey) ||
            (BigInteger.MinusOne != cube.InverseCharge)
        ) {
            return $"monogenic(x^3+1): x inverts to minus x squared, but its witness is ({cube.InverseKey},{cube.InverseCharge})";
        }

        return null;
    }
    /// <summary>Proves that words of the reflection presentation act on the lattice exactly where the lattice says, and
    /// that every relation the presentation declares is a relation the action satisfies.</summary>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    /// <remarks>
    /// The whole symmetry group is far past any enumeration, so this is the gate that stands in for one: the
    /// presentation is SOUND for the action — every involution and every braid word moves no node at all — and the word
    /// that reads the mirrors once is the lattice's own cycle, of order thirty on nodes and fifteen on rays, which are
    /// the two periods the animation surface and the ray factorisation already carry.
    /// </remarks>
    public static string? ReflectionActionMatchesLattice() {
        var system = ReflectionSystem.Create(mirrors: ReflectionSystem.SimpleMirrors);
        var bonds = system.BondMatrix;
        var rank = system.Mirrors.Length;
        var nodes = SymmetryLattice.NodeCount;

        if (
            (8 != rank) ||
            (nodes != system.Points.Length)
        ) {
            return $"the simple mirrors present rank {rank} closing on {system.Points.Length} point(s), against 8 and {nodes}";
        }

        // The diagram: a tree on eight mirrors — seven bonded pairs, every bond two or three, one branch of degree three
        // and no cycle. That shape is what makes these eight a simple system, and it is measured, never declared.
        var degrees = new int[rank];
        var edges = 0;

        for (var high = 1; (high < rank); ++high) {
            for (var low = 0; (low < high); ++low) {
                var bond = bonds[((high * rank) + low)];

                if (
                    (2 != bond) &&
                    (3 != bond)
                ) { return $"the bond between mirrors {low} and {high} is {bond}, and a simply-laced diagram carries only two and three"; }

                if (2 == bond) { continue; }

                ++degrees[high];
                ++degrees[low];
                ++edges;
            }
        }

        if (
            (7 != edges) ||
            (3 != MaximumOf(values: degrees))
        ) {
            return $"the diagram carries {edges} edge(s) with a largest degree of {MaximumOf(values: degrees)}, against a tree on eight mirrors branching once";
        }

        // Soundness: every relation the presentation declares is one the action satisfies, on every node.
        for (var symbol = 0; (symbol < rank); ++symbol) {
            for (var node = 0; (node < nodes); ++node) {
                if (node != system.Apply(
                    node: node,
                    word: [symbol, symbol]
                )) { return $"the involution of mirror {symbol} moves node {node}"; }
            }
        }

        Span<int> ascending = stackalloc int[3];
        Span<int> descending = stackalloc int[3];

        for (var high = 1; (high < rank); ++high) {
            for (var low = 0; (low < high); ++low) {
                var bond = bonds[((high * rank) + low)];

                for (var step = 0; (step < bond); ++step) {
                    ascending[step] = ((0 == (step & 1))
                        ? low
                        : high
                    );
                    descending[step] = ((0 == (step & 1))
                        ? high
                        : low
                    );
                }

                for (var node = 0; (node < nodes); ++node) {
                    if (system.Apply(
                        word: ascending[..bond],
                        node: node
                    ) != system.Apply(
                        word: descending[..bond],
                        node: node
                    )) {
                        return $"the braid relation of mirrors ({low},{high}) at bond {bond} splits node {node}";
                    }
                }
            }
        }

        // The word that reads every mirror once, descending, IS the lattice's cycle.
        Span<int> coxeter = stackalloc int[rank];

        for (var index = 0; (index < rank); ++index) { coxeter[index] = ((rank - 1) - index); }

        var image = new int[nodes];
        var nodeOrder = 0;
        var rayOrder = 0;

        for (var node = 0; (node < nodes); ++node) {
            image[node] = system.Apply(
                node: node,
                word: coxeter
            );

            if (image[node] != SymmetryLattice.Cycle(node: node)) { return $"the mirror word carries node {node} to {image[node]} where the lattice's cycle carries it to {SymmetryLattice.Cycle(node: node)}"; }
        }

        for (var power = 1; (power <= nodes); ++power) {
            var fixesNodes = true;
            var fixesRays = true;

            for (var node = 0; (node < nodes); ++node) {
                if (image[node] != node) { fixesNodes = false; }
                if (SymmetryLattice.CanonicalRay(node: image[node]) != SymmetryLattice.CanonicalRay(node: node)) { fixesRays = false; }
            }

            if (
                fixesNodes &&
                (0 == nodeOrder)
            ) { nodeOrder = power; }
            if (
                fixesRays &&
                (0 == rayOrder)
            ) { rayOrder = power; }

            if (
                (0 != nodeOrder) &&
                (0 != rayOrder)
            ) { break; }

            for (var node = 0; (node < nodes); ++node) {
                image[node] = system.Apply(
                word: coxeter,
                node: image[node]
            );
            }
        }

        if (
            (CyclicRotation.Period != nodeOrder) ||
            (SymmetryLattice.RayCycleOrder != rayOrder)
        ) {
            return $"the mirror word has order {nodeOrder} on nodes and {rayOrder} on rays, against the lattice's {CyclicRotation.Period} and {SymmetryLattice.RayCycleOrder}";
        }

        // The two periods the rest of the library reads off this element: every rotation plane returns to the identity
        // exactly at the node order and never inside it, and the ray cycle's factorisation has the ray order's degree.
        for (var plane = 0; (plane < CyclicRotation.PlaneCount); ++plane) {
            for (var tick = 0; (tick <= (3 * nodeOrder)); ++tick) {
                if ((0 == CyclicRotation.Step(
                    plane: plane,
                    tick: tick
                )) != (0 == (tick % nodeOrder))) {
                    return $"rotation plane {plane} reads step {CyclicRotation.Step(
                        plane: plane,
                        tick: tick
                    )} at tick {tick}, which does not resync with the word's order of {nodeOrder}";
                }
            }
        }

        var factorDegree = 0;

        for (var index = 0; (index < SymmetryLattice.RayCycleFactorCount); ++index) { factorDegree += SymmetryLattice.RayCycleFactor(index: index).Degree; }

        if (factorDegree != rayOrder) { return $"the ray cycle factors carry total degree {factorDegree} against a ray order of {rayOrder}"; }

        // Every ray orbit is one full turn of the ray cycle, so the rays split into exactly RayCount / RayCycleOrder of
        // them — the eight rings the projection lays out, counted through the action rather than through the lattice.
        var orbits = 0;
        var visited = new bool[nodes];

        for (var node = 0; (node < nodes); ++node) {
            var ray = SymmetryLattice.CanonicalRay(node: node);

            if (visited[ray]) { continue; }

            var size = 0;
            var cursor = ray;

            do {
                visited[cursor] = true;
                cursor = SymmetryLattice.CanonicalRay(node: system.Apply(
                    node: cursor,
                    word: coxeter
                ));
                ++size;
            } while (cursor != ray);

            ++orbits;

            if (size != rayOrder) { return $"the ray orbit of {ray} holds {size} ray(s) against the ray order of {rayOrder}"; }
        }

        if (orbits != (SymmetryLattice.RayCount / rayOrder)) {
            return $"the rays split into {orbits} orbit(s) against {(SymmetryLattice.RayCount / rayOrder)}";
        }

        // A word acts by a permutation, and by one that preserves the lattice's exact incidence: orthogonality is a
        // statement about the roots the reflections move, so no word may change it.
        foreach (var word in new[] { new[] { 0 }, new[] { 3, 4 }, new[] { 7, 6, 5, 4, 3, 2, 1, 0 } }) {
            var reached = new bool[nodes];

            for (var node = 0; (node < nodes); ++node) {
                var moved = system.Apply(
                    node: node,
                    word: word
                );

                if (reached[moved]) {
                    return $"the word [{string.Join(
                    separator: ',',
                    values: word
                )}] sends two nodes to {moved}, so it does not act by a permutation";
                }

                reached[moved] = true;
            }

            for (var first = 0; (first < nodes); ++first) {
                for (var second = 0; (second < nodes); ++second) {
                    if (SymmetryLattice.AreOrthogonal(
                        first: first,
                        second: second
                    )
                        != SymmetryLattice.AreOrthogonal(
                        first: system.Apply(
                            node: first,
                            word: word
                        ),
                        second: system.Apply(
                            node: second,
                            word: word
                        )
                    )) {
                        return $"the word [{string.Join(
                            separator: ',',
                            values: word
                        )}] changes the incidence of ({first},{second})";
                    }
                }
            }
        }

        // The action is transitive on the nodes, which is why one orbit enumeration answers for all of them.
        Span<int> orbit = stackalloc int[nodes];

        foreach (var seed in new[] { 0, 17, 239 }) {
            if (
                !system.TryEnumerateOrbit(
                count: out var count,
                obstruction: out var obstruction,
                orbit: orbit,
                seed: seed
            ) ||
                (nodes != count)
            ) {
                return $"the orbit of node {seed} reached {count} node(s) with {obstruction.Outcome}, against {nodes}";
            }

            for (var index = 1; (index < count); ++index) {
                if (orbit[index] <= orbit[(index - 1)]) { return $"the orbit of node {seed} is not ascending at {index}"; }
            }
        }

        return null;
    }
    /// <summary>Proves that the group regime refuses rather than answering wrongly or running forever: the whole lattice
    /// symmetry is not enumerable, an incomplete rewriting system is not a group, and a presentation whose generators do
    /// not invert is not certified.</summary>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    /// <remarks>The load-bearing refusal is that invertible generators do not substitute for associativity: an
    /// infinite word presentation with no finite certificate is turned away, as is a concrete finite nonassociative
    /// product, while finite group tables and complete finite Coxeter presentations remain admitted.</remarks>
    public static string? GroupLimitsRefuse() {
        var system = ReflectionSystem.Create(mirrors: ReflectionSystem.SimpleMirrors);
        var nodes = SymmetryLattice.NodeCount;

        foreach (var limit in new[] { 64L, 512L }) {
            if (system.TryEnumerateGroup(
                obstruction: out var obstruction,
                permutations: out var permutations,
                searchLimit: limit
            )) {
                return $"the whole symmetry group enumerated {(permutations.Length / system.Points.Length)} element(s) inside a budget of {limit}";
            }

            if (
                (ClosureOutcome.SearchLimitReached != obstruction.Outcome) ||
                (limit != obstruction.PointsReached) ||
                (obstruction.BlockedSymbol < 0) ||
                (-1L != obstruction.BlockedKey)
            ) {
                return $"the refused enumeration reports outcome={obstruction.Outcome} symbol={obstruction.BlockedSymbol} key={obstruction.BlockedKey} reached={obstruction.PointsReached} at a budget of {limit}";
            }
        }

        // The caller's buffer is the budget for an orbit, and a refusal still reports the size it would have needed.
        Span<int> cramped = stackalloc int[(nodes - 1)];

        if (
            system.TryEnumerateOrbit(
            count: out var reached,
            obstruction: out var orbitRefusal,
            orbit: cramped,
            seed: 0
        ) ||
            (nodes != reached) ||
            (ClosureOutcome.SearchLimitReached != orbitRefusal.Outcome) ||
            (nodes != orbitRefusal.PointsReached)
        ) {
            return $"an orbit that does not fit its buffer reports reached={reached} outcome={orbitRefusal.Outcome} points={orbitRefusal.PointsReached}";
        }

        // The word presentation of the whole symmetry group has no finite basis. Its generators are involutions, but
        // that no longer suffices for a PresentedGroup: without a finite associativity certificate the group regime
        // refuses before inverse search.
        var presentation = Presentations.Coxeter<BigInteger, IntegerMaterial>(
            rank: system.Mirrors.Length,
            bonds: system.BondMatrix,
            material: default
        );
        var algebra = PresentedAlgebra<BigInteger, IntegerMaterial>.Create(presentation: presentation);
        var certificate = algebra.Certify(overlapLimit: (1L << 20));

        if (
            presentation.HasFiniteNormalForms ||
            (0 != presentation.NormalFormCount)
        ) {
            return $"the whole symmetry group's word presentation reports finite={presentation.HasFiniteNormalForms} with {presentation.NormalFormCount} normal form(s)";
        }

        if (
            (ClosureOutcome.SearchLimitReached != certificate.Outcome) ||
            certificate.IsAssociative ||
            certificate.IsCommutative ||
            certificate.IsAlternative ||
            certificate.IsCoherent ||
            certificate.HasIdentity
        ) {
            return $"a presentation with no basis issued outcome={certificate.Outcome} associative={certificate.IsAssociative} unital={certificate.HasIdentity} coherent={certificate.IsCoherent}";
        }

        if (
            PresentedGroup<BigInteger, IntegerMaterial>.TryCertify(
            algebra: algebra,
            group: out _,
            obstruction: out var groupObstruction
        ) ||
            (ClosureOutcome.SearchLimitReached != groupObstruction.Outcome) ||
            (-1 != groupObstruction.BlockedSymbol) ||
            (-1L != groupObstruction.BlockedKey) ||
            (0L != groupObstruction.PointsReached)
        ) {
            return $"the unverified whole symmetry group certified, or refused as outcome={groupObstruction.Outcome} symbol={groupObstruction.BlockedSymbol} key={groupObstruction.BlockedKey} reached={groupObstruction.PointsReached}";
        }

        // The incompleteness this system carries, witnessed rather than described. Three mirrors in a chain generate a
        // group of order 24, but the involution and braid rules alone do not decide its word problem: the alternating
        // word of a Coxeter element repeats forever without exposing a redex, so the presentation has no finite basis
        // and its fourth power — the identity of the group, proved so by the action — is its OWN normal form.
        var chain = ReflectionSystem.Create(mirrors: [0, 2, 3]);
        var chainPresentation = Presentations.Coxeter<BigInteger, IntegerMaterial>(
            rank: 3,
            bonds: chain.BondMatrix,
            material: default
        );
        var chainAlgebra = PresentedAlgebra<BigInteger, IntegerMaterial>.Create(presentation: chainPresentation);
        var word = new int[12];
        var element = chainAlgebra.Identity;

        if (chainPresentation.HasFiniteNormalForms) { return "a rank-three chain reported a finite basis, which its incomplete rewriting system cannot have"; }

        for (var index = 0; (index < word.Length); ++index) { word[index] = CoxeterElementWord[(index % 3)]; }

        foreach (var letter in word) {
            element = chainAlgebra.Multiply(
            left: element,
            right: chainAlgebra.Generator(symbol: letter)
        );
        }

        for (var node = 0; (node < nodes); ++node) {
            if (node != chain.Apply(
                node: node,
                word: word
            )) { return $"the fourth power of a rank-three Coxeter element moves node {node}, so it is not the identity of the group"; }
        }

        if (
            (1 != element.SupportCount) ||
            (element.Keys[0] == chainAlgebra.Identity.Keys[0]) ||
            (element.Keys[0] != PackedWord(
            generatorCount: 3,
            word: word
        ))
        ) {
            return $"the identity word normalized to {element.SupportCount} term(s) at key {((0 == element.SupportCount)
                ? -1L
                : element.Keys[0])}, and its own packing is {PackedWord(
                generatorCount: 3,
                word: word
            )}";
        }

        if (
            PresentedGroup<BigInteger, IntegerMaterial>.TryCertify(
            algebra: chainAlgebra,
            group: out _,
            obstruction: out var chainObstruction
        ) ||
            (ClosureOutcome.SearchLimitReached != chainObstruction.Outcome)
        ) {
            return $"a rank-three chain without a finite associativity certificate was admitted, or refused as {chainObstruction.Outcome}";
        }

        // A generator that is not a unit, and a unit that is not one basis element, are both refused with the witness
        // that blocked: a degenerate Clifford generator squares to zero, and a quiver's unit is a sum of idempotents.
        if (
            PresentedGroup<BigInteger, IntegerMaterial>.TryCertify(
            algebra: PresentedAlgebra<BigInteger, IntegerMaterial>.Create(presentation: Presentations.Clifford<BigInteger, IntegerMaterial>(
                degenerateCount: 1,
                material: default,
                negativeCount: 0,
                positiveCount: 0
            )),
            group: out _,
            obstruction: out var degenerate
        ) ||
            (ClosureOutcome.AmbiguityWitness != degenerate.Outcome) ||
            (0 != degenerate.BlockedSymbol) ||
            (-1L != degenerate.BlockedKey)
        ) {
            return $"a degenerate generator certified, or reported outcome={degenerate.Outcome} symbol={degenerate.BlockedSymbol} key={degenerate.BlockedKey}";
        }

        if (
            PresentedGroup<BigInteger, IntegerMaterial>.TryCertify(
            algebra: PresentedAlgebra<BigInteger, IntegerMaterial>.Create(presentation: CodiscreteQuiver<BigInteger, IntegerMaterial>(
                material: default,
                order: 3
            )),
            group: out _,
            obstruction: out var quiver
        ) ||
            (ClosureOutcome.AmbiguityWitness != quiver.Outcome) ||
            (-1 != quiver.BlockedSymbol) ||
            (3L != quiver.PointsReached)
        ) {
            return $"a quiver certified, or reported outcome={quiver.Outcome} symbol={quiver.BlockedSymbol} idempotents={quiver.PointsReached}";
        }

        // A generator carrying the material's zero is not a unit either. A quiver on ONE object has a single idempotent
        // for its unit, so it gets past the unit check and is stopped at the arrow instead, with the arrow named.
        if (
            PresentedGroup<BigInteger, IntegerMaterial>.TryCertify(
            algebra: PresentedAlgebra<BigInteger, IntegerMaterial>.Create(presentation: Presentations.Quiver<BigInteger, IntegerMaterial>(
                objectCount: 1,
                arrows: [(0, 0, BigInteger.Zero)],
                material: default
            )),
            group: out _,
            obstruction: out var weightless
        ) ||
            (ClosureOutcome.AmbiguityWitness != weightless.Outcome) ||
            (0 != weightless.BlockedSymbol) ||
            (0L != weightless.PointsReached)
        ) {
            return $"a weightless arrow certified, or reported outcome={weightless.Outcome} symbol={weightless.BlockedSymbol} searched={weightless.PointsReached}";
        }

        // The infinite bond has no finite basis either, so its involutive generators cannot bypass the same
        // associativity-certificate requirement.
        var free = PresentedAlgebra<BigInteger, IntegerMaterial>.Create(presentation: Presentations.Coxeter<BigInteger, IntegerMaterial>(
            bonds: [1, 0, 0, 1],
            material: default,
            rank: 2
        ));

        if (free.Presentation.HasFiniteNormalForms) { return "two mirrors with no relation between them reported a finite basis"; }

        if (
            PresentedGroup<BigInteger, IntegerMaterial>.TryCertify(
            algebra: free,
            group: out _,
            obstruction: out var freeObstruction
        ) ||
            (ClosureOutcome.SearchLimitReached != freeObstruction.Outcome)
        ) {
            return $"two free mirrors without a finite associativity certificate were admitted, or refused as {freeObstruction.Outcome}";
        }

        // The construction-time refusals: data that names no group, and a group past the basis cap.
        return (RefusesDeclaration(
            name: "a bond matrix that is not symmetric",
            build: static () => _ = Presentations.Coxeter<BigInteger, IntegerMaterial>(
                bonds: [1, 3, 4, 1],
                material: default,
                rank: 2
            )
        )
            ?? (RefusesDeclaration(
            name: "a bond of one between two generators",
            build: static () => _ = Presentations.Coxeter<BigInteger, IntegerMaterial>(
                bonds: [1, 1, 1, 1],
                material: default,
                rank: 2
            )
        )
            ?? (RefusesDeclaration(
            name: "a mirror list naming one reflection twice",
            build: static () => _ = ReflectionSystem.Create(mirrors: [3, SymmetryLattice.Antipode(node: 3)])
        )
            ?? (RefusesDeclaration(
            name: "a permutation table that is not closed under composition",
            build: static () => _ = Presentations.PermutationGroup<BigInteger, IntegerMaterial>(
                material: default,
                permutations: [0, 1, 2, 1, 2, 0],
                pointCount: 3
            )
        )
            ?? (RefusesDeclaration(
            name: "a permutation table without the identity",
            build: static () => _ = Presentations.PermutationGroup<BigInteger, IntegerMaterial>(
                material: default,
                permutations: [1, 0],
                pointCount: 2
            )
        )
            ?? (RefusesDeclaration(
            name: "a row that is not a permutation",
            build: static () => _ = Presentations.PermutationGroup<BigInteger, IntegerMaterial>(
                material: default,
                permutations: [0, 1, 1, 1],
                pointCount: 2
            )
        )
            ?? RefusesDeclaration(
            name: "a reflection group past the basis cap",
            build: static () => {
                var wide = ReflectionSystem.Create(mirrors: [0, 2, 3, 4, 5]);

                _ = wide.TryEnumerateGroup(
                    obstruction: out _,
                    permutations: out var permutations,
                    searchLimit: 1024L
                );
                _ = Presentations.PermutationGroup<BigInteger, IntegerMaterial>(
                    pointCount: wide.Points.Length,
                    permutations: permutations.Span,
                    material: default
                );
            }
        )))))));
    }
    /// <summary>Proves that the guarded sum over all lengths REFUSES a cyclic counting quiver — returning the refusal
    /// and its obstruction rather than throwing or inventing a certificate — while the exact finite truncation stays
    /// available and correct.</summary>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? UnguardedStarRefuses() {
        var algebra = PresentedAlgebra<BigInteger, CountingMaterial>.Create(presentation: CodiscreteQuiver<BigInteger, CountingMaterial>(
            material: default,
            order: 2
        ));
        var cycle = algebra.FromSupport(
            keys: [1L, 2L],
            coefficients: [BigInteger.One, BigInteger.One]
        );

        if (algebra.TrySumOverAllLengths(
            obstruction: out var obstruction,
            total: out var total,
            value: cycle
        )) {
            return $"a two-cycle over the counting material issued a certificate and a total of {total.SupportCount} term(s); it must refuse";
        }

        if (0 != total.SupportCount) { return $"the refused total is not the zero element ({total.SupportCount} term(s))"; }

        if (
            (ClosureCertificate.Nilpotent != obstruction.Attempted) ||
            (0L >= obstruction.StepsTaken)
        ) {
            return $"the obstruction reports attempted={obstruction.Attempted} steps={obstruction.StepsTaken} key={obstruction.SupportKey}";
        }

        // The honest partial is still there, and it is the exact matrix 1 + A + A² + A³ + A⁴ of the two-cycle: the walk
        // counts of a two-cycle alternate between the diagonal and the off-diagonal, so the truncation to length four
        // holds three on the diagonal and two off it.
        var truncated = algebra.TruncatedSum(
            bound: 4,
            value: cycle
        );
        var expected = new BigInteger[] { 3, 2, 2, 3 };

        for (var key = 0; (key < 4); ++key) {
            if (truncated[key] != expected[key]) { return $"the truncated sum holds {truncated[key]} at key {key}, expected {expected[key]}"; }
        }

        var nilpotent = algebra.FromSupport(
            keys: [1L],
            coefficients: [BigInteger.One]
        );

        if (!algebra.TrySumOverAllLengths(
            obstruction: out _,
            total: out var nilpotentTotal,
            value: nilpotent
        )) {
            return "a nilpotent arrow must still be summable over all lengths";
        }

        // The quiver's unit is the DIAGONAL SUM, so the total of a single nilpotent arrow is both idempotents plus that
        // arrow: keys 0 and 3 from the unit, key 1 from the arrow.
        if (
            (3 != nilpotentTotal.SupportCount) ||
            (BigInteger.One != nilpotentTotal[0L]) ||
            (BigInteger.One != nilpotentTotal[1L]) ||
            (BigInteger.One != nilpotentTotal[3L])
        ) {
            return $"the nilpotent total holds {nilpotentTotal.SupportCount} term(s), expected the two idempotents plus the single arrow";
        }

        return null;
    }
}
