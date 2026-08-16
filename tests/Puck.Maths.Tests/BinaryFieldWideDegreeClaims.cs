using System.Numerics;

namespace Puck.Maths.Tests;

/// <summary>
/// An INDEPENDENT verdict on the binary field's irreducibility criterion at degrees 32, 64 and 128 — the wide end of
/// the GF(2)[t] ring and its GF(2^k) quotients.
/// </summary>
/// <remarks>
/// <para>
/// The narrow end is covered elsewhere. The A001037 census to degree 16 is
/// <c>deep.polynomial-irreducible-census-and-trial-division</c> and <c>deep.binary-field-irreducible-census</c>; the
/// five catalog presets are re-proved irreducible by <c>binary-field.irreducibility-vs-trial-division</c>; the four
/// published carryless-multiply reference vectors are <c>Subjects.BinaryCarrylessVectors</c>, read by
/// <c>polynomial.multiply-vs-carryless-oracle</c>; the region legs are
/// <c>binary-field.regions-vs-oracle</c> and <c>binary-field.region-lengths-vs-scalar-rung</c>.
/// </para>
/// <para>
/// What none of those reach is a verdict at the wide degrees that is not simply asserted. Trial division — the
/// definition, and the oracle every narrow irreducibility law answers to — has a divisor space that doubles with the
/// degree and is out of reach above the teens, so at 32, 64 and 128 a positive statement would otherwise rest on the
/// published catalog constants and the only negative statement available would be <c>t^d + 1</c>, reducible by the
/// Frobenius identity. Calling <see cref="BinaryField{T}.IsIrreducible"/> on the three wide catalog moduli and taking
/// <see langword="true"/> for an answer is no verdict at all.
/// </para>
/// <para>
/// This file gives two complete verdicts instead, neither of which runs a decision procedure:
/// </para>
/// <list type="bullet">
/// <item><description>
/// POSITIVE — an exact multiplicative-ORDER certificate in <see cref="BigInteger"/>. An element <c>a</c> with
/// <c>a^(2^d − 1) = 1</c> and <c>a^((2^d − 1)/p) ≠ 1</c> at every prime <c>p</c> dividing <c>2^d − 1</c> has order
/// exactly <c>2^d − 1</c>, so its powers are that many distinct units of a ring holding <c>2^d</c> elements, one of
/// which is zero — every non-zero element is therefore a unit, the quotient is a field, and the modulus is
/// irreducible. Complete, not probabilistic, and it shares nothing with Ben-Or/Rabin: no Frobenius exponentiation, no
/// greatest common divisor, no carrier. The prime lists are the published Fermat-number factorizations of
/// <c>2^32 − 1</c>, <c>2^64 − 1</c> and <c>2^128 − 1</c>, and neither the factorization nor the primality of a factor
/// is trusted — <see cref="GroupOrderFactorizationFailure"/> re-multiplies each list and re-proves every factor prime
/// by trial division before a certificate reads it.
/// </description></item>
/// <item><description>
/// NEGATIVE — moduli built as the carryless PRODUCT of two non-constant factors in <see cref="BigInteger"/>. A product
/// of two non-constant polynomials is reducible by definition, so the reference verdict is derived without consulting
/// the subject and without any decision at all. These are the first negative rows the suite has at the wide degrees
/// beyond the single perfect power <c>t^d + 1</c>.
/// </description></item>
/// </list>
/// <para>
/// Both arms run on <see cref="UInt128"/> as well as on each degree's natural carrier. Building all three degrees over
/// <c>BinaryField&lt;UInt128&gt;</c> puts degrees 32 and 64 on the degree-below-the-carrier-width path — a different
/// mask, a different split and a different fold from the degree == width case the catalog fields alone reach.
/// </para>
/// </remarks>
internal static class BinaryFieldWideDegreeClaims {
    /// <summary>The three wide degrees and the catalog tail at each, built over the 128-bit carrier at every
    /// degree.</summary>
    private static readonly (int Degree, UInt128 Tail)[] WideCatalog = [(32, ((UInt128)0x8D)), (64, ((UInt128)0x1B)), (128, ((UInt128)0x87))];
    /// <summary>The published prime factorizations of <c>2^d − 1</c> at the three wide degrees, from the Fermat-number
    /// factorizations <c>2^32 + 1 = 641 · 6700417</c> and <c>2^64 + 1 = 274177 · 67280421310721</c>. Neither the
    /// products nor the primality of any factor is taken on its word — see
    /// <see cref="GroupOrderFactorizationFailure"/>.</summary>
    private static readonly (int Degree, ulong[] Primes)[] WideGroupOrderFactors = [
        (32, [3UL, 5UL, 17UL, 257UL, 65_537UL]),
        (64, [3UL, 5UL, 17UL, 257UL, 641UL, 65_537UL, 6_700_417UL]),
        (128, [3UL, 5UL, 17UL, 257UL, 641UL, 65_537UL, 274_177UL, 6_700_417UL, 67_280_421_310_721UL]),
    ];
    /// <summary>The factor degrees the reducible-by-construction moduli are split at, as a fraction of the whole. Each
    /// row is a distinct shape: a linear factor against everything else, a near-balanced split, the perfect square, and
    /// two off-centre splits — the arrangements a distinct-degree criterion that stopped one exponent early, or that
    /// mishandled a repeated factor, would answer differently on.</summary>
    private static readonly (int Numerator, int Denominator)[] ReducibleSplits = [(0, 0), (1, 4), (1, 2), (3, 8), (7, 16)];

    /// <summary>The candidate elements the order certificate searches for a generator, as packed coefficient vectors:
    /// <c>t</c>, <c>t + 1</c>, <c>t²</c> and upward. A primitive element has density <c>φ(2^d − 1)/(2^d − 1)</c>, which
    /// is above two fifths at all three wide degrees, so a fixed deterministic ladder this long finds one unless the
    /// modulus genuinely has none.</summary>
    private const int GeneratorCandidateCeiling = 96;

    /// <summary>Proves the irreducibility criterion at degrees 32, 64 and 128 against evidence that never runs a
    /// decision: the exact order certificate on the catalog modulus and on the first further modulus the subject
    /// accepts, reducible-by-construction moduli the subject must reject, and the same verdicts read back on the
    /// 128-bit carrier at every degree.</summary>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? WideDegreeIrreducibilityCertificatesSurface() =>
        WideDegreeIrreducibilityFailure(acceptedFloor: 1, certifyCeiling: 1, reducibleRows: 5, scannedTails: 512);
    /// <summary>The scale sibling of <see cref="WideDegreeIrreducibilityCertificatesSurface"/>: sixteen thousand moduli
    /// scanned at each wide degree with EVERY accepted one certified, and sixty reducible constructions per degree
    /// rejected. Written inline rather than through a <c>Domain</c>, because an Exhaustive case that consumed one would
    /// advance the frontier counter its Default sibling reads.</summary>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? WideDegreeIrreducibilitySweepSurface() =>
        WideDegreeIrreducibilityFailure(acceptedFloor: 8, certifyCeiling: int.MaxValue, reducibleRows: 60, scannedTails: 16_384);

    /// <summary>The shared body of both wide-degree laws.</summary>
    /// <param name="scannedTails">How many odd tails are offered to the subject at each degree.</param>
    /// <param name="certifyCeiling">How many accepted moduli per degree are certified before the scan stops.</param>
    /// <param name="acceptedFloor">The least number of accepted moduli each degree must produce, so the ACCEPTING arm
    /// is exercised rather than merely available: a subject that called everything reducible would satisfy the negative
    /// arm outright and prove nothing.</param>
    /// <param name="reducibleRows">How many reducible-by-construction moduli each degree must reject.</param>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    private static string? WideDegreeIrreducibilityFailure(int scannedTails, int certifyCeiling, int acceptedFloor, int reducibleRows) {
        if (GroupOrderFactorizationFailure() is { } factorizationFailure) { return factorizationFailure; }

        foreach (var (degree, catalogTail) in WideCatalog) {
            // The catalog row first: the one every wide-degree statement in the suite leans on.
            if (!WideDecision(degree: degree, tail: catalogTail)) {
                return $"the degree-{degree} catalog modulus t^{degree} + 0x{catalogTail:X} is called reducible";
            }
            if (CertificateFailure(degree: degree, tail: catalogTail) is { } catalogFailure) { return catalogFailure; }
            if (CarrierAgreementFailure(degree: degree, tail: catalogTail) is { } catalogCarrier) { return catalogCarrier; }

            var accepted = 0;
            var certified = 0;

            for (var tail = UInt128.One; (tail <= ((UInt128)((2 * scannedTails) - 1))); tail += ((UInt128)2)) {
                var decided = WideDecision(degree: degree, tail: tail);

                if (CarrierAgreementFailure(degree: degree, tail: tail) is { } scanCarrier) { return scanCarrier; }
                if (!decided) { continue; }

                ++accepted;

                if (certified >= certifyCeiling) { continue; }

                if (CertificateFailure(degree: degree, tail: tail) is { } scanFailure) { return scanFailure; }

                ++certified;

                if ((certified >= certifyCeiling) && (accepted >= acceptedFloor)) { break; }
            }

            if (accepted < acceptedFloor) {
                return $"the subject accepted {accepted} of the first {scannedTails} odd tails at degree {degree}, below the floor of {acceptedFloor} — the accepting arm of the criterion is no longer exercised at that degree";
            }

            if (ReducibleConstructionFailure(degree: degree, rows: reducibleRows) is { } reducibleFailure) { return reducibleFailure; }
        }

        return null;
    }
    /// <summary>The subject's decision at a wide degree, taken on the 128-bit carrier.</summary>
    /// <param name="degree">The extension degree.</param>
    /// <param name="tail">The modulus below its leading term.</param>
    /// <returns>Whether the subject calls the modulus irreducible.</returns>
    private static bool WideDecision(int degree, UInt128 tail) =>
        BinaryField<UInt128>.Create(degree: degree, reductionTail: tail).IsIrreducible();
    /// <summary>The same decision read back on the degree's NATURAL carrier, where the degree equals the carrier's
    /// width. Degree 128 has only the one carrier and is skipped.</summary>
    /// <param name="degree">The extension degree.</param>
    /// <param name="tail">The modulus below its leading term.</param>
    /// <returns>The counterexample text, or <see langword="null"/> when the two carriers agree.</returns>
    private static string? CarrierAgreementFailure(int degree, UInt128 tail) {
        if (128 == degree) { return null; }

        var wide = WideDecision(degree: degree, tail: tail);
        var natural = ((32 == degree)
            ? BinaryField<uint>.Create(degree: 32, reductionTail: ((uint)tail)).IsIrreducible()
            : BinaryField<ulong>.Create(degree: 64, reductionTail: ((ulong)tail)).IsIrreducible());

        return ((wide == natural)
            ? null
            : $"t^{degree} + 0x{tail:X} is called {wide} on the 128-bit carrier and {natural} on its own {degree}-bit one");
    }
    /// <summary>Certifies that a modulus the subject called irreducible really is, by exhibiting an element of the
    /// quotient ring whose multiplicative order is exactly <c>2^degree − 1</c>.</summary>
    /// <param name="degree">The extension degree.</param>
    /// <param name="tail">The modulus below its leading term.</param>
    /// <returns>The counterexample text, or <see langword="null"/> when the certificate stands.</returns>
    private static string? CertificateFailure(int degree, UInt128 tail) {
        var reductionTail = ((BigInteger)tail);
        var groupOrder = ((BigInteger.One << degree) - BigInteger.One);
        var primes = PrimesOf(degree: degree);

        for (var candidate = 2; (candidate <= GeneratorCandidateCeiling); ++candidate) {
            var element = ((BigInteger)candidate);
            var maximal = true;

            // The proper-divisor tests come first: they are what discriminates, and a candidate that is not a generator
            // usually fails the smallest of them, which keeps the search from paying for a full-order power per miss.
            foreach (var prime in primes) {
                if (!Power(degree: degree, exponent: (groupOrder / prime), reductionTail: reductionTail, value: element).IsOne) { continue; }

                maximal = false;

                break;
            }

            if (!maximal) { continue; }

            // Order divides the group order AND no proper divisor of it: the order is the group order exactly.
            if (Power(degree: degree, exponent: groupOrder, reductionTail: reductionTail, value: element).IsOne) { return null; }
        }

        return $"t^{degree} + 0x{tail:X} is called irreducible, but no element of order 2^{degree} − 1 exists among the first {(GeneratorCandidateCeiling - 1)} candidates, so the quotient's non-zero elements do not all invert and it is not a field";
    }
    /// <summary>Requires the subject to reject moduli that are reducible BY CONSTRUCTION — the carryless product of two
    /// non-constant polynomials, formed in <see cref="BigInteger"/> without consulting the subject.</summary>
    /// <param name="degree">The extension degree the products are built to.</param>
    /// <param name="rows">How many products to build.</param>
    /// <returns>The counterexample text, or <see langword="null"/> when every product is rejected.</returns>
    private static string? ReducibleConstructionFailure(int degree, int rows) {
        for (var row = 0; (row < rows); ++row) {
            var (numerator, denominator) = ReducibleSplits[(row % ReducibleSplits.Length)];
            var lowDegree = ((0 == denominator) ? 1 : ((degree * numerator) / denominator));
            var highDegree = (degree - lowDegree);

            // A perfect square whenever the split is exactly balanced and the two factor patterns coincide, which is the
            // repeated-factor shape a squarefree-blind criterion answers differently on.
            var low = Factor(degree: lowDegree, salt: ((row / ReducibleSplits.Length) + 1));
            var high = Factor(degree: highDegree, salt: ((lowDegree == highDegree) ? ((row / ReducibleSplits.Length) + 1) : ((row / ReducibleSplits.Length) + 7)));
            var product = Oracles.CarrylessProduct(left: low, right: high);
            var tail = (product - (BigInteger.One << degree));

            // Both factors carry a non-zero constant term, so the product does too and Create admits it; and the degrees
            // add exactly in GF(2)[t], so the product lands on the intended degree. Both are asserted rather than
            // assumed, because a construction that silently degenerated would make the whole arm vacuous.
            if ((tail < BigInteger.Zero) || (tail >= (BigInteger.One << degree)) || tail.IsEven) {
                return $"the degree-{degree} reducible construction at row {row} produced the tail {tail}, which is not a legal modulus below t^{degree}";
            }

            var packed = ((UInt128)tail);

            if (WideDecision(degree: degree, tail: packed)) {
                return $"t^{degree} + 0x{packed:X} is called irreducible, and it is the product of a degree-{lowDegree} and a degree-{highDegree} polynomial by construction";
            }

            if (CarrierAgreementFailure(degree: degree, tail: packed) is { } carrier) { return carrier; }
        }

        return null;
    }
    /// <summary>A deterministic monic polynomial of the requested degree with a non-zero constant term.</summary>
    /// <param name="degree">The factor's degree, at least one.</param>
    /// <param name="salt">Varies the interior coefficients between rows.</param>
    /// <returns>The packed coefficient vector.</returns>
    private static BigInteger Factor(int degree, int salt) {
        var value = (BigInteger.One << degree) | BigInteger.One;

        for (var exponent = 1; (exponent < degree); ++exponent) {
            if (0 != (((exponent * salt) + (salt / 2)) % 3)) { continue; }

            value |= (BigInteger.One << exponent);
        }

        return value;
    }
    /// <summary>Re-proves every published factorization this file's certificates rest on: each list multiplies back to
    /// <c>2^d − 1</c> and every factor is prime by trial division. A composite listed as prime would let an element of
    /// SMALLER order pass the certificate, so the certificates are only as good as this check.</summary>
    /// <returns>The counterexample text, or <see langword="null"/> when every factorization holds.</returns>
    private static string? GroupOrderFactorizationFailure() {
        foreach (var (degree, primes) in WideGroupOrderFactors) {
            var product = BigInteger.One;

            foreach (var prime in primes) {
                if (!IsPrime(value: prime)) { return $"{prime}, listed as a prime divisor of 2^{degree} − 1, is composite"; }

                product *= prime;
            }

            var groupOrder = ((BigInteger.One << degree) - BigInteger.One);

            if (product != groupOrder) { return $"the listed prime divisors of 2^{degree} − 1 multiply to {product}, not to {groupOrder}"; }
        }

        return null;
    }
    /// <summary>The distinct prime divisors of <c>2^degree − 1</c>.</summary>
    /// <param name="degree">The extension degree.</param>
    /// <returns>The prime divisors.</returns>
    private static ulong[] PrimesOf(int degree) {
        foreach (var (candidate, primes) in WideGroupOrderFactors) {
            if (candidate == degree) { return primes; }
        }

        throw new InvalidOperationException(message: $"no published factorization of 2^{degree} − 1 is declared.");
    }
    /// <summary>Primality by trial division — the definition, over a carrier wide enough for the largest listed
    /// factor.</summary>
    /// <param name="value">The candidate.</param>
    /// <returns>Whether the candidate is prime.</returns>
    private static bool IsPrime(ulong value) {
        if (value < 2UL) { return false; }
        if (0UL == (value % 2UL)) { return (2UL == value); }

        for (var divisor = 3UL; ((divisor * divisor) <= value); divisor += 2UL) {
            if (0UL == (value % divisor)) { return false; }
        }

        return true;
    }
    /// <summary>A power in <c>GF(2)[t]/(t^degree + reductionTail)</c>, by square-and-multiply over
    /// <see cref="Oracles.BinaryFieldProduct"/> — the shared-nothing BigInteger product the catalog-field laws already
    /// answer to.</summary>
    /// <param name="value">The base, reduced.</param>
    /// <param name="exponent">The exponent.</param>
    /// <param name="degree">The extension degree.</param>
    /// <param name="reductionTail">The modulus below its leading term.</param>
    /// <returns>The reduced power.</returns>
    private static BigInteger Power(BigInteger value, BigInteger exponent, int degree, BigInteger reductionTail) {
        var result = BigInteger.One;
        var square = value;
        var bits = ((int)exponent.GetBitLength());

        for (var bit = 0; (bit < bits); ++bit) {
            if (!((exponent >> bit) & BigInteger.One).IsZero) {
                result = Oracles.BinaryFieldProduct(degree: degree, left: result, reductionTail: reductionTail, right: square);
            }

            if ((bit + 1) < bits) {
                square = Oracles.BinaryFieldProduct(degree: degree, left: square, reductionTail: reductionTail, right: square);
            }
        }

        return result;
    }
}

