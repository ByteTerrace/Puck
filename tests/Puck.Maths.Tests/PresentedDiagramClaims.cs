using System.Globalization;
using System.Numerics;

namespace Puck.Maths.Tests;

/// <summary>
/// Three claims over presented-algebra diagrams, braidings and functors.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><see cref="BraidingCertificateSelfConsistentAtEightInstances"/> — re-multiplies the braiding certificate's
/// charge at EVERY ordered pair of EIGHT catalogue instances; the closest existing case,
/// <c>presented.braiding-nontrivial-canary</c>, only re-multiplies the NONTRIVIAL pairs of three of those eight.</item>
/// <item><see cref="QuantumTorusChargeMatchesSkewPairing"/> — checks the derived charge against the skew-pairing
/// formula q^(bc−ad) at every pair of three (order, modulus, swap-charge) configurations; no existing case reaches
/// all three configurations exhaustively, and one configuration — order 3, modulus 13, swap 3 — is not built anywhere
/// else in the suite.</item>
/// <item><see cref="FunctorTwinsTransferAtVariedLength"/> — draws 60 quotient words of varying length through the
/// same three-evaluator agreement <c>smoke.presented-functor-twin</c> already pins at exactly three FIXED five-letter
/// words; this widens the length and quotient range that smoke sentinel never reaches.</item>
/// </list>
/// </remarks>
internal static class PresentedDiagramClaims {
    // ---- shared construction helpers, local to this file (not calls into Subjects.cs or Oracles.cs) ----

    private static PresentedAlgebra<BigInteger, IntegerMaterial>.Element IntegerBasisElement(PresentedAlgebra<BigInteger, IntegerMaterial> algebra, int key) =>
        algebra.FromSupport(keys: [key], coefficients: [BigInteger.One]);

    private static Generator[] SingleColourBasis(int count) {
        var generators = new Generator[count];

        for (var symbol = 0; (symbol < count); ++symbol) {
            generators[symbol] = new Generator(symbol: symbol, inputs: new int[] { 0 }, outputs: new int[] { 0 }, degree: 1);
        }

        return generators;
    }

    // ---- the braiding certificate, self-consistent at every pair of eight catalogue instances ----

    /// <summary>Proves the braiding certificate's derived charge self-consistent, EXHAUSTIVELY, at eight catalogue
    /// instances: at every ordered basis pair, the charge — when used to scale the reverse product — reconstructs
    /// the forward product, and a pair annihilates both ways if and only if it carries no charge at all.</summary>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    /// <remarks>
    /// <c>presented.braiding-nontrivial-canary</c> already re-multiplies every pair whose charge is not one, at
    /// three of these eight instances (cayley-dickson(2), cayley-dickson(3), clifford(4,1,0)). This claim widens
    /// that self-consistency statement two ways at once: EVERY ordered pair (not only the nontrivial ones — so an
    /// invented charge at a pair that annihilates both ways, or a missing one where both orderings are actually
    /// nonzero, is caught too), and all eight catalogue instances, including the three no other Default-tier case
    /// exhaustively re-multiplies — cayley-dickson(1), cayley-dickson(4), clifford(2,1,0) —
    /// and clifford(2,0,1), the one DEGENERATE signature in the set, whose annihilating pairs constrain no charge at
    /// all, so this claim also exercises the zero-charge branch on the one instance that carries nothing but it.
    /// </remarks>
    public static string? BraidingCertificateSelfConsistentAtEightInstances() {
        (string Name, PresentedAlgebra<BigInteger, IntegerMaterial> Algebra)[] instances = [
            ("cayley-dickson(1)", PresentedAlgebra<BigInteger, IntegerMaterial>.Create(presentation: Presentations.CayleyDickson<BigInteger, IntegerMaterial>(floors: 1, basisRelabelling: [], material: default))),
            ("cayley-dickson(2)", PresentedAlgebra<BigInteger, IntegerMaterial>.Create(presentation: Presentations.CayleyDickson<BigInteger, IntegerMaterial>(floors: 2, basisRelabelling: [], material: default))),
            ("cayley-dickson(3)", PresentedAlgebra<BigInteger, IntegerMaterial>.Create(presentation: Presentations.CayleyDickson<BigInteger, IntegerMaterial>(floors: 3, basisRelabelling: [], material: default))),
            ("cayley-dickson(4)", PresentedAlgebra<BigInteger, IntegerMaterial>.Create(presentation: Presentations.CayleyDickson<BigInteger, IntegerMaterial>(floors: 4, basisRelabelling: [], material: default))),
            ("clifford(3,0,0)", PresentedAlgebra<BigInteger, IntegerMaterial>.Create(presentation: Presentations.Clifford<BigInteger, IntegerMaterial>(positiveCount: 3, negativeCount: 0, degenerateCount: 0, material: default))),
            ("clifford(2,1,0)", PresentedAlgebra<BigInteger, IntegerMaterial>.Create(presentation: Presentations.Clifford<BigInteger, IntegerMaterial>(positiveCount: 2, negativeCount: 1, degenerateCount: 0, material: default))),
            ("clifford(4,1,0)", PresentedAlgebra<BigInteger, IntegerMaterial>.Create(presentation: Presentations.Clifford<BigInteger, IntegerMaterial>(positiveCount: 4, negativeCount: 1, degenerateCount: 0, material: default))),
            ("clifford(2,0,1)", PresentedAlgebra<BigInteger, IntegerMaterial>.Create(presentation: Presentations.Clifford<BigInteger, IntegerMaterial>(positiveCount: 2, negativeCount: 0, degenerateCount: 1, material: default))),
        ];

        foreach (var (name, algebra) in instances) {
            var certificate = algebra.Certify(overlapLimit: (1L << 22));
            var keys = algebra.MaximumSupportCount;

            for (var left = 0; (left < keys); ++left) {
                for (var right = 0; (right < keys); ++right) {
                    var charge = certificate.BraidingCharge(leftKey: left, rightKey: right);
                    var forward = algebra.Multiply(left: IntegerBasisElement(algebra: algebra, key: left), right: IntegerBasisElement(algebra: algebra, key: right));
                    var reverse = algebra.Multiply(left: IntegerBasisElement(algebra: algebra, key: right), right: IntegerBasisElement(algebra: algebra, key: left));

                    if (charge.IsZero) {
                        if ((0 != forward.SupportCount) || (0 != reverse.SupportCount)) {
                            return $"{name}: the pair ({left},{right}) carries no braiding charge, where the two orderings' products are not both zero";
                        }

                        continue;
                    }

                    if ((0 == forward.SupportCount) && (0 == reverse.SupportCount)) {
                        return $"{name}: the pair ({left},{right}) invented the braiding charge {charge} at a pair that annihilates both ways";
                    }

                    var scaled = algebra.FromSupport(
                        keys: reverse.Keys.ToArray(),
                        coefficients: [.. reverse.Coefficients.ToArray().Select(selector: value => (charge * value))]
                    );

                    if (!algebra.AreEqual(left: forward, right: scaled)) {
                        return $"{name}: the pair ({left},{right}) carries the charge {charge}, which does not re-multiply the two orderings into each other";
                    }
                }
            }
        }

        return null;
    }

    // ---- the quantum torus, against the skew pairing, at three configurations ----

    /// <summary>Proves the braiding certificate's derived charge, at EVERY ordered basis pair of a quantum torus,
    /// equal to the skew pairing q^(bc − ad) computed here from the normal-form exponents by repeated modular
    /// multiplication — at three (order, modulus, swap-charge) configurations.</summary>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    /// <remarks>
    /// The presentation construction (<see cref="QuantumTorus"/>) is transcribed from the same construction
    /// <c>presented.braiding-hexagon-witnessed</c>'s <c>QuantumTorusPresentation</c> already builds and runs — that
    /// existing case checks only the IsBraided/IsSymmetric/witness flags at two of these three configurations, and
    /// <c>presented.braiding-hexagon-witnessed</c>'s quantum-torus-separates-the-flags leg checks the derived charge
    /// at exactly ONE pair of ONE configuration. Neither reaches the third configuration (order 3, modulus 13, swap
    /// 3) at all, and neither checks the skew-pairing formula exhaustively at every pair of any configuration. The
    /// skew pairing itself shares no code with the certificate: it is plain modular exponentiation over the
    /// normal-form word's own letter counts, never touching <see cref="PresentationCertificate{TValue}"/>.
    /// </remarks>
    public static string? QuantumTorusChargeMatchesSkewPairing() {
        foreach (var (order, modulus, swapCharge) in ((int Order, ulong Modulus, ulong SwapCharge)[])[(3, 7UL, 2UL), (3, 13UL, 3UL), (4, 5UL, 2UL)]) {
            var algebra = PresentedAlgebra<ulong, PrimeFieldMaterial>.Create(presentation: QuantumTorus(order: order, modulus: modulus, swapCharge: swapCharge));
            var certificate = algebra.Certify(overlapLimit: (1L << 22));
            var keys = algebra.MaximumSupportCount;

            if (keys != (order * order)) {
                return $"quantum-torus(order {order}, modulus {modulus}): the presentation carries {keys} normal form(s), where the order gives it {(order * order)}";
            }

            for (var left = 0; (left < keys); ++left) {
                for (var right = 0; (right < keys); ++right) {
                    var (leftLow, leftHigh) = TorusExponents(presentation: algebra.Presentation, key: left);
                    var (rightLow, rightHigh) = TorusExponents(presentation: algebra.Presentation, key: right);
                    var exponent = ((((leftHigh * rightLow) - (rightHigh * leftLow)) % order) + order) % order;
                    var expected = 1UL;

                    for (var step = 0; (step < exponent); ++step) { expected = ((expected * swapCharge) % modulus); }

                    var derived = certificate.BraidingCharge(leftKey: left, rightKey: right);

                    if (derived != expected) {
                        return $"quantum-torus(order {order}, modulus {modulus}): the pair ({left},{right}) carries {derived}, where the skew pairing q^(bc-ad) gives it {expected}";
                    }
                }
            }
        }

        return null;
    }

    // Transcribed from the construction presented.braiding-hexagon-witnessed's own QuantumTorusPresentation already
    // exercises: two generators of the given order, both swapping at the given charge, over a prime field of the
    // given modulus. This is SUBJECT construction (the same public ChargedPresentation.Create surface every
    // Presentations.* factory in Puck.Maths itself calls), not oracle arithmetic, so re-authoring it here shares no
    // evidence with the skew-pairing check above.
    private static ChargedPresentation<ulong, PrimeFieldMaterial> QuantumTorus(int order, ulong modulus, ulong swapCharge) {
        var first = new int[order];
        var second = new int[order];

        for (var index = 0; (index < order); ++index) { second[index] = 1; }

        return ChargedPresentation<ulong, PrimeFieldMaterial>.Create(
            generators: SingleColourBasis(count: 2),
            rules: [
                new(kind: RuleKind.Reassociate, pattern: ReadOnlyMemory<int>.Empty, replacement: RewriteRule<ulong>.PackReplacement(terms: [[]]), charges: new[] { 1UL }),
                new(kind: RuleKind.Swap, pattern: new[] { 1, 0 }, replacement: RewriteRule<ulong>.PackReplacement(terms: [[0, 1]]), charges: new[] { swapCharge }),
                new(kind: RuleKind.Reduce, pattern: first, replacement: RewriteRule<ulong>.PackReplacement(terms: [[]]), charges: new[] { 1UL }),
                new(kind: RuleKind.Reduce, pattern: second, replacement: RewriteRule<ulong>.PackReplacement(terms: [[]]), charges: new[] { 1UL }),
            ],
            material: PrimeFieldMaterial.Create(modulus: modulus)
        );
    }

    // The two exponents of a quantum-torus normal form, counted off its word rather than assumed from its key.
    private static (int Low, int High) TorusExponents(ChargedPresentation<ulong, PrimeFieldMaterial> presentation, long key) {
        var low = 0;
        var high = 0;

        foreach (var symbol in presentation.NormalFormWord(key: key)) {
            if (0 == symbol) { ++low; } else { ++high; }
        }

        return (low, high);
    }

    // ---- the functor/transfer three-way agreement, at varied word length ----

    /// <summary>Proves three shipped evaluators of one word — <see cref="PresentedFunctor{TValue, TOps}.Map"/>,
    /// <see cref="ConvergentTransfer{TValue, TOps}.Evaluate"/> and <see cref="ConvergentTransfer{TValue, TOps}.Run"/>
    /// — agree at 60 words of varying length, extending <c>smoke.presented-functor-twin</c>'s three FIXED
    /// five-letter words to a deterministically varied length (one through eight letters) and partial-quotient range
    /// the smoke sentinel never reaches.</summary>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    /// <remarks>
    /// The length and each partial quotient are derived from the draw index by fixed-point multiplicative mixing —
    /// no <see cref="Random"/>, seeded or otherwise — so the sixty words are reproducible from the draw index alone,
    /// per the suite's determinism rules for claim bodies.
    /// </remarks>
    public static string? FunctorTwinsTransferAtVariedLength() {
        for (var draw = 0; (draw < 60); ++draw) {
            var lengthMix = unchecked((uint)(((uint)draw * 2654435761U) ^ 0x9E3779B9U));
            var letters = (1 + (int)(lengthMix % 8U));
            var free = PresentedAlgebra<BigInteger, IntegerMaterial>.Create(presentation: Presentations.FreeMonoid<BigInteger, IntegerMaterial>(letterCount: letters, material: default));
            var transfer = ConvergentTransfer<BigInteger, IntegerMaterial>.Create(material: default);
            var quotients = new BigInteger[letters];
            var images = new PresentedAlgebra<BigInteger, IntegerMaterial>.Element[letters];
            var word = free.Identity;

            for (var symbol = 0; (symbol < letters); ++symbol) {
                var quotientMix = unchecked((uint)((((uint)draw * 2654435761U) + ((uint)symbol * 0x85EBCA6BU)) ^ 0xC2B2AE35U));

                quotients[symbol] = (1 + (int)(quotientMix % 9U));
                images[symbol] = transfer.Digit(partialQuotient: quotients[symbol]);
                word = free.Multiply(left: word, right: free.Generator(symbol: symbol));
            }

            if (!PresentedFunctor<BigInteger, IntegerMaterial>.TryCreate(source: free, target: transfer.Algebra, images: images, functor: out var functor, obstruction: out var obstruction)) {
                return $"draw {draw}: the transfer morphism of {letters} letter(s) was refused at rule {obstruction.RuleIndex} and pair ({obstruction.LeftKey},{obstruction.RightKey}), where a free source has no relation to break";
            }

            var mapped = functor!.Map(value: word);
            var evaluated = transfer.Evaluate(partialQuotients: quotients);

            if (!transfer.Algebra.AreEqual(left: mapped, right: evaluated)) {
                return $"draw {draw}: the morphism maps the word of [{FormatQuotients(quotients: quotients)}] to an element the transfer's own fold does not reach";
            }

            for (var row = 0; (row < 2); ++row) {
                for (var column = 0; (column < 2); ++column) {
                    if (transfer.Entry(value: mapped, row: row, column: column) != transfer.Run(partialQuotients: quotients, row: row, column: column)) {
                        return $"draw {draw}: the morphism and the module run disagree at ({row},{column}) on [{FormatQuotients(quotients: quotients)}]";
                    }
                }
            }
        }

        return null;
    }

    private static string FormatQuotients(ReadOnlySpan<BigInteger> quotients) {
        var formatted = new string[quotients.Length];

        for (var index = 0; (index < quotients.Length); ++index) { formatted[index] = quotients[index].ToString(provider: CultureInfo.InvariantCulture); }

        return string.Join(separator: ",", value: formatted);
    }
}
