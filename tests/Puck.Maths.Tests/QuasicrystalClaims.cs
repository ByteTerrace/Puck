using System.Numerics;
using Xunit;

namespace Puck.Maths.Tests;

/// <summary>
/// Claims over <c>MetallicQuasicrystal</c> and its random access, <c>ModularTransform</c> with
/// <c>ContinuedFraction</c>, <c>QuadraticInflation</c>, and the general <c>QuadraticQuasicrystal</c> chain including
/// its random access.
/// </summary>
/// <remarks>
/// Every oracle below is written out in this file rather than calling <c>Oracles.cs</c> or any <c>Puck.Maths</c>
/// kernel, per the shared-nothing discipline: <see cref="IntegerSquareRoot(BigInteger)"/> is an independent
/// BigInteger Newton-descent root that shares no code with <c>FixedQ4816.Sqrt</c> or
/// <c>BigIntegerFunctions.SquareRoot</c>; <see cref="ModularCuspOracle"/> and <see cref="ModularFormAction"/> are
/// self-contained BigInteger/Int128 reference arithmetic.
///
/// <para><b>Float-free.</b> House rules bar floating-point arithmetic from law logic. A fixed-point "one approximate
/// seam" member (an <c>InflationFactor</c> or <c>LongTileLength</c>) is therefore never compared against a
/// <see langword="double"/> reference at a loose tolerance. Instead the exact algebraic value
/// <c>(rationalNumerator + surdNumerator·√radicand) / denominator</c> is bracketed to Q16 raw ticks using
/// <see cref="BracketRawQ16"/> — an independent BigInteger square root, not a <see langword="double"/> anywhere —
/// and the subject's raw ticks are compared against that bracket within a small integer tolerance. Density
/// and frequency ratios (long:short tile counts approaching an inflation factor) are checked by exact BigInteger
/// cross-multiplication rather than a <see langword="double"/> division.</para>
///
/// <para><b>The fundamental-domain seam.</b> <c>GaussReduce(...).Transform.Apply(FixedComplex)</c> maps the
/// form's root into the fundamental domain, checked by <see cref="FormRootMappedIntoFundamentalDomain"/>:
/// the domain conditions <c>|Re z| &lt;= 1/2</c> and <c>|z| &gt;= 1</c> are ALGEBRAIC inequalities on the mapped
/// <see cref="FixedComplex"/>'s own raw ticks, decidable in exact <see cref="Int128"/> without ever predicting what
/// raw value <see cref="FixedComplex"/> division's rounding schedule returns — the check tests whatever raw value
/// the kernel actually produced, not a value bracketed in advance. The root fed in is itself only <see cref="BracketRawQ16"/>'s
/// Q16 approximation of the exact irrational root, and the kernel's own division rounds once per component, so the
/// two inequalities are widened by a small integer slack — measured across the full sweep to occur only at the
/// domain's own corners and edges, where the reduced form is degenerate (e.g. <c>(1,1,1)</c> or <c>(1,1,2)</c>), and
/// nowhere else.</para>
///
/// <para><b>No randomness.</b> House rules also bar fresh randomness from law logic (no <c>Random</c>, seeded or
/// not). Wherever an ensemble needs varying (modular words, cusp trial points, triples for associativity), the
/// values are derived from the loop index itself (bit decomposition, or a fixed odd-multiplier mix) — deterministic
/// on every run, not merely repeatable from a fixed seed.</para>
/// </remarks>
internal static class QuasicrystalClaims {
    // ---- shared helpers, sharing nothing with the subject kernels each claim checks ----

    /// <summary>The exact floor square root <c>⌊√value⌋</c>, by a bit-length seed and Newton descent, settled by the
    /// exact predicate <c>r² ≤ value &lt; (r+1)²</c>. Deliberately independent of <c>FixedQ4816.Sqrt</c> (a hardware-
    /// or double-seeded fixed-width kernel) and of <c>BigIntegerFunctions.SquareRoot</c> — calling either would check
    /// the tree against itself.</summary>
    private static BigInteger IntegerSquareRoot(BigInteger value) {
        if (value.Sign <= 0) { return BigInteger.Zero; }

        var root = (BigInteger.One << (int)((value.GetBitLength() + 1L) / 2L));

        while (true) {
            var next = ((root + (value / root)) >> 1);

            if (next >= root) { break; }

            root = next;
        }

        while ((root * root) > value) { root -= BigInteger.One; }
        while (((root + BigInteger.One) * (root + BigInteger.One)) <= value) { root += BigInteger.One; }

        return root;
    }

    /// <summary>Floor division for a positive <paramref name="denominator"/>.</summary>
    private static BigInteger FloorDiv(BigInteger numerator, BigInteger denominator) {
        var quotient = BigInteger.DivRem(numerator, denominator, out var remainder);

        return ((remainder.Sign < 0) ? (quotient - BigInteger.One) : quotient);
    }
    /// <summary>Ceiling division for a positive <paramref name="denominator"/>.</summary>
    private static BigInteger CeilDiv(BigInteger numerator, BigInteger denominator) {
        var quotient = BigInteger.DivRem(numerator, denominator, out var remainder);

        return ((remainder.Sign > 0) ? (quotient + BigInteger.One) : quotient);
    }

    /// <summary>Brackets the Q16 raw fixed-point value of <c>(rationalNumerator + surdNumerator·√radicand) /
    /// denominator</c> to within a fraction of one raw tick, using <see cref="IntegerSquareRoot(BigInteger)"/> at
    /// 48 extra bits of precision. <paramref name="radicand"/> and <paramref name="denominator"/> must be
    /// positive.</summary>
    private static (BigInteger Lower, BigInteger Upper) BracketRawQ16(
        BigInteger rationalNumerator,
        BigInteger surdNumerator,
        BigInteger radicand,
        BigInteger denominator) {
        const int ExtraBits = 48;
        var fine = (BigInteger.One << ExtraBits);
        var root = IntegerSquareRoot((radicand * fine) * fine); // floor(sqrt(radicand) * 2^ExtraBits)
        var scaleQ16 = (BigInteger.One << 16);
        var rationalTerm = ((rationalNumerator * scaleQ16) * fine);
        BigInteger lowNumerator;
        BigInteger highNumerator;

        if (surdNumerator >= BigInteger.Zero) {
            lowNumerator = (rationalTerm + ((surdNumerator * scaleQ16) * root));
            highNumerator = (rationalTerm + ((surdNumerator * scaleQ16) * (root + BigInteger.One)));
        } else {
            lowNumerator = (rationalTerm + ((surdNumerator * scaleQ16) * (root + BigInteger.One)));
            highNumerator = (rationalTerm + ((surdNumerator * scaleQ16) * root));
        }

        var divisor = (denominator * fine);

        return (FloorDiv(lowNumerator, divisor), CeilDiv(highNumerator, divisor));
    }
    /// <summary>Whether <paramref name="actual"/>'s raw ticks fall inside the exact bracket (widened by
    /// <paramref name="toleranceRawTicks"/> on each side) of <c>(rationalNumerator + surdNumerator·√radicand) /
    /// denominator</c>.</summary>
    private static bool WithinBracket(
        FixedQ4816 actual,
        BigInteger rationalNumerator,
        BigInteger surdNumerator,
        BigInteger radicand,
        BigInteger denominator,
        long toleranceRawTicks) {
        var (lower, upper) = BracketRawQ16(rationalNumerator: rationalNumerator, surdNumerator: surdNumerator, radicand: radicand, denominator: denominator);
        var actualRaw = (BigInteger)actual.Value;

        return ((actualRaw >= (lower - toleranceRawTicks)) && (actualRaw <= (upper + toleranceRawTicks)));
    }

    /// <summary>Is <paramref name="needle"/> a contiguous factor of <paramref name="haystack"/>? A phase-independent
    /// witness that two constructions of one tiling share a language.</summary>
    private static bool IsFactorOfWord(ReadOnlySpan<bool> haystack, ReadOnlySpan<bool> needle) {
        for (var start = 0; (start <= (haystack.Length - needle.Length)); ++start) {
            if (haystack.Slice(start: start, length: needle.Length).SequenceEqual(other: needle)) { return true; }
        }

        return false;
    }
    /// <summary>The number of distinct length-<paramref name="k"/> factors of <paramref name="word"/>: exactly
    /// <c>k+1</c> for a Sturmian word, bounded for a periodic one.</summary>
    private static int WordComplexity(ReadOnlySpan<bool> word, int k) {
        var seen = new HashSet<ulong>();
        var mask = ((k == 64) ? ~0UL : ((1UL << k) - 1UL));
        var window = 0UL;

        for (var i = 0; (i < word.Length); ++i) {
            window = (((window << 1) | (word[i] ? 1UL : 0UL)) & mask);

            if (i >= (k - 1)) { seen.Add(item: window); }
        }

        return seen.Count;
    }

    /// <summary>The Mobius action on a cusp p/q, formed as an exact BigInteger rational reduction, calling no
    /// <c>ModularTransform</c> member.</summary>
    private static (long Numerator, long Denominator) ModularCuspOracle(ModularTransform g, long p, long q) {
        var numerator = (((BigInteger)g.A * p) + ((BigInteger)g.B * q));
        var denominator = (((BigInteger)g.C * p) + ((BigInteger)g.D * q));

        if (denominator.IsZero) { return (1L, 0L); }
        if (numerator.IsZero) { return (0L, 1L); }

        var divisor = BigInteger.GreatestCommonDivisor(left: numerator, right: denominator);

        numerator /= divisor;
        denominator /= divisor;

        if (denominator.Sign < 0) {
            numerator = -numerator;
            denominator = -denominator;
        }

        return ((long)numerator, (long)denominator);
    }
    /// <summary>The contravariant substitution action on a binary quadratic form <c>f(alpha*x+beta*y,
    /// gamma*x+delta*y)</c>, formed in Int128 and calling no <c>ModularTransform</c> member beyond reading its four
    /// data fields.</summary>
    private static (long A, long B, long C) ModularFormAction(long a, long b, long c, ModularTransform g) {
        var alpha = ((Int128)g.A);
        var beta = ((Int128)g.B);
        var gamma = ((Int128)g.C);
        var delta = ((Int128)g.D);
        var actedA = ((((Int128)a * alpha) * alpha) + (((Int128)b * alpha) * gamma) + (((Int128)c * gamma) * gamma));
        var actedB = ((((2 * (Int128)a) * alpha) * beta) + ((Int128)b * ((alpha * delta) + (beta * gamma))) + (((2 * (Int128)c) * gamma) * delta));
        var actedC = ((((Int128)a * beta) * beta) + (((Int128)b * beta) * delta) + (((Int128)c * delta) * delta));

        return (checked((long)actedA), checked((long)actedB), checked((long)actedC));
    }

    // ---- banner: "MetallicQuasicrystal random access (the general cut-and-project; subsumes the retired
    // golden/silver files)" ----

    /// <summary>For n=1..6: the ring-coordinate chain from the origin stays in the set, inverts under Previous,
    /// steps by exactly delta or delta-squared, advances monotonically, avoids the forbidden factors SS and
    /// L^(n+2), reaches density delta_n (checked by exact BigInteger cross-multiplication, not a
    /// <see langword="double"/> ratio), and Contains equals the walked vertex set over a coordinate box the walk
    /// fully covers. The walked word is a factor of the independently streamed <see cref="QuadraticQuasicrystal"/>
    /// substitution word (period [n]). Width-boundary regressions close the section: membership does not wrap at
    /// <see cref="long.MinValue"/>, traversal overflows rather than wrapping, <see cref="MetallicQuasicrystal.Position"/>
    /// refuses an out-of-range coordinate, a huge metallic index still rounds its factor to the plain integer, and a
    /// huge-index prefix is all long.</summary>
    public static string? MetallicRandomAccessMatchesStreamedWord() {
        for (var n = 1; (n <= 6); ++n) {
            if (!MetallicQuasicrystal.Contains(n: n, a: 0L, b: 0L)) { return $"the metallic origin is not a member at n={n}"; }

            var point = (A: 0L, B: 0L);
            var walkWord = new bool[4000];
            var visited = new HashSet<(long A, long B)>();
            var longCount = 0L;
            var shortCount = 0L;
            var run = 0;
            var previousWasLong = false;

            for (var step = 0; (step < walkWord.Length); ++step) {
                visited.Add(item: point);

                var isLong = MetallicQuasicrystal.StartsLongTile(n: n, a: point.A, b: point.B);
                var next = MetallicQuasicrystal.Next(n: n, a: point.A, b: point.B);

                walkWord[step] = isLong;

                if (!MetallicQuasicrystal.Contains(n: n, a: next.A, b: next.B)) { return $"the metallic walk left the set at n={n} step={step}"; }
                if (MetallicQuasicrystal.Previous(n: n, a: next.A, b: next.B) != point) { return $"Previous does not invert Next at n={n} step={step}"; }

                var deltaA = (next.A - point.A);
                var deltaB = (next.B - point.B);

                if (isLong ? ((deltaA != 1L) || (deltaB != n)) : ((deltaA != 0L) || (deltaB != 1L))) {
                    return $"the metallic step is not delta or delta-squared at n={n} step={step}";
                }
                if (MetallicQuasicrystal.Position(n: n, a: next.A, b: next.B) <= MetallicQuasicrystal.Position(n: n, a: point.A, b: point.B)) {
                    return $"metallic positions are not increasing at n={n} step={step}";
                }

                if (isLong) {
                    ++longCount;
                    run = (((step > 0) && previousWasLong) ? (run + 1) : 1);

                    if (run >= (n + 2)) { return $"a forbidden long run appears at n={n} step={step}"; }
                } else {
                    ++shortCount;

                    if ((step > 0) && !previousWasLong) { return $"the forbidden factor SS appears at n={n} step={step}"; }

                    run = 0;
                }

                previousWasLong = isLong;
                point = next;
            }

            if ((point.A <= 70L) || (point.B <= 70L)) { return $"the metallic walk did not cover the coordinate box at n={n}"; }

            // Density, exactly: |longCount*65536 - shortCount*factor.Value| / (shortCount*65536) <= 0.02, cross-multiplied.
            var factorRaw = (BigInteger)MetallicQuasicrystal.InflationFactor(n: n).Value;
            var lhs = ((BigInteger)longCount * 65536);
            var rhs = ((BigInteger)shortCount * factorRaw);
            var diff = BigInteger.Abs(lhs - rhs);

            if ((diff * 100) > (rhs * 2)) { return $"the metallic long:short ratio does not approach delta_{n}: long={longCount} short={shortCount}"; }

            for (var a = 0L; (a <= 70L); ++a) {
                for (var b = 0L; (b <= 70L); ++b) {
                    if (MetallicQuasicrystal.Contains(n: n, a: a, b: b) != visited.Contains(item: (a, b))) {
                        return $"Contains disagrees with the walked vertex set at n={n} ({a},{b})";
                    }
                }
            }

            var streamed = new bool[16000];

            QuadraticQuasicrystal.Word(p: n, q: 1L, d: (((long)n * n) + 4L), r: 2L, tiles: streamed);

            if (!IsFactorOfWord(haystack: streamed, needle: walkWord.AsSpan(0, 1200))) {
                return $"the metallic walk word is not a factor of the streamed substitution word at n={n}";
            }
        }

        if (MetallicQuasicrystal.Contains(n: 1, a: long.MinValue, b: 0L)) { return "long.MinValue wrapped into metallic membership"; }

        _ = Assert.Throws<OverflowException>(testCode: () => MetallicQuasicrystal.Next(n: 1, a: long.MaxValue, b: long.MaxValue));

        var positionRefusal = Assert.Throws<ArgumentOutOfRangeException>(testCode: () => MetallicQuasicrystal.Position(n: 1, a: long.MaxValue, b: 0L));

        Assert.Equal(expected: "value", actual: positionRefusal.ParamName);
        Assert.Equal(expected: FixedQ4816.FromInteger(value: int.MaxValue), actual: MetallicQuasicrystal.InflationFactor(n: int.MaxValue));

        var hugeIndexPrefix = new bool[16];

        MetallicQuasicrystal.Word(n: int.MaxValue, tiles: hugeIndexPrefix);

        if (Array.IndexOf(array: hugeIndexPrefix, value: false) >= 0) { return "the large-index metallic prefix is not all long"; }

        return null;
    }

    // ---- banner: "ModularTransform + ContinuedFraction (the modular group beneath the three motions)" ----

    /// <summary>S is elliptic of order four, S*T elliptic of order six, T parabolic, and a hand-built hyperbolic
    /// element classifies correctly. A deterministic ensemble of 512 words in S and integer translations (derived
    /// from each index's own bits, not from <c>System.Random</c>) all carry determinant one, invert as their own
    /// adjugate, and classify by their trace; composition is associative; the cusp action agrees with an
    /// independent BigInteger rational oracle, is a group action, and S is a cusp involution.</summary>
    public static string? ModularTransformClassesAndCuspAction() {
        if (ModularTransform.S.Classify() != ModularClass.Elliptic) { return "S is not elliptic"; }
        if ((ModularTransform.S * ModularTransform.T).Classify() != ModularClass.Elliptic) { return "S*T is not elliptic"; }
        if (ModularTransform.T.Classify() != ModularClass.Parabolic) { return "T is not parabolic"; }
        if (ModularTransform.Create(a: 2L, b: 1L, c: 1L, d: 1L).Classify() != ModularClass.Hyperbolic) { return "[2,1,1,1] is not hyperbolic"; }

        var spin = ModularTransform.Identity;

        for (var power = 1; (power <= 4); ++power) {
            spin *= ModularTransform.S;

            if ((spin == ModularTransform.Identity) != (power == 4)) { return $"S is not order four at power {power}"; }
        }

        var hex = ModularTransform.Identity;

        for (var power = 1; (power <= 6); ++power) {
            hex *= (ModularTransform.S * ModularTransform.T);

            if ((hex == ModularTransform.Identity) != (power == 6)) { return $"S*T is not order six at power {power}"; }
        }

        // A deterministic ensemble of 512 words, built from each index's own bit pattern rather than any random
        // source: bit `step` of `index` chooses S or an integer translation whose shift cycles through -3..3.
        var words = new ModularTransform[512];

        for (var index = 0; (index < words.Length); ++index) {
            var word = ModularTransform.Identity;

            for (var step = 0; (step < 7); ++step) {
                var useS = (((index >> step) & 1) == 0);

                word = useS
                    ? (ModularTransform.S * word)
                    : (ModularTransform.Create(a: 1L, b: (((index + step) % 7) - 3), c: 0L, d: 1L) * word);
            }

            words[index] = word;

            if (Int128.One != (((Int128)word.A * word.D) - ((Int128)word.B * word.C))) { return $"word {index} does not have determinant one"; }
            if ((word * word.Inverse) != ModularTransform.Identity) { return $"word {index}'s inverse is not the adjugate inverse"; }

            var absoluteTrace = Int128.Abs(value: ((Int128)word.A + word.D));
            var expectedClass = ((absoluteTrace < 2)
                ? ModularClass.Elliptic
                : ((absoluteTrace == 2) ? ModularClass.Parabolic : ModularClass.Hyperbolic));

            if (word.Classify() != expectedClass) { return $"word {index}'s Classify disagrees with its trace"; }
        }

        for (var index = 0; (index < 200); ++index) {
            var x = words[index % words.Length];
            var y = words[(index * 7 + 3) % words.Length];
            var z = words[(index * 13 + 5) % words.Length];

            if (((x * y) * z) != (x * (y * z))) { return $"composition is not associative at triple {index}"; }
        }

        for (var index = 0; (index < words.Length); ++index) {
            var word = words[index];

            for (var trial = 0; (trial < 6); ++trial) {
                var p = (long)(((ulong)((index * 6364136223846793005L) + (trial * 1442695040888963407L))) % 41UL) - 20L;
                var q = (long)(((ulong)((index * 2862933555777941757L) + (trial * 3037000493L))) % 41UL);

                if ((p == 0L) && (q == 0L)) { q = 1L; }

                if (word.Apply(numerator: p, denominator: q) != ModularCuspOracle(g: word, p: p, q: q)) {
                    return $"the cusp action disagrees with the rational oracle at word {index} trial {trial}";
                }

                var other = words[(index + trial + 1) % words.Length];
                var composed = (word * other).Apply(numerator: p, denominator: q);
                var (innerP, innerQ) = other.Apply(numerator: p, denominator: q);

                if (composed != word.Apply(numerator: innerP, denominator: innerQ)) {
                    return $"the cusp action is not a group action at word {index} trial {trial}";
                }

                var (sP, sQ) = ModularTransform.S.Apply(numerator: p, denominator: q);

                if (ModularTransform.S.Apply(numerator: sP, denominator: sQ) != ModularCuspOracle(g: ModularTransform.Identity, p: p, q: q)) {
                    return $"S is not a cusp involution at trial {trial}";
                }
            }
        }

        return null;
    }

    /// <summary>Gauss reduction over every positive-definite form with <c>1&lt;=a&lt;=24</c>, <c>-24&lt;=b&lt;=24</c>,
    /// <c>1&lt;=c&lt;=24</c> (a fully deterministic sweep — no random draw): the reduced form satisfies
    /// <c>-A&lt;B&lt;=A&lt;=C</c>, the transform has determinant one, the discriminant is preserved, the transform's
    /// inverse carries the original form to the reduced one under the independent contravariant form action
    /// (<see cref="ModularFormAction"/>), reduction is idempotent, and the transform maps the ORIGINAL form's root
    /// into the fundamental domain (<see cref="FormRootMappedIntoFundamentalDomain"/>).</summary>
    public static string? GaussReductionEntersFundamentalDomain() {
        var reductions = 0;

        for (var a = 1L; (a <= 24L); ++a) {
            for (var b = -24L; (b <= 24L); ++b) {
                for (var c = 1L; (c <= 24L); ++c) {
                    // The definiteness filter and the preserved discriminant below are both formed in BigInteger. At
                    // these bounds a narrower carrier would hold them, but the quantity being judged is exactly the one
                    // the subject judges, so the oracle must not borrow the subject's width to judge it.
                    if ((((BigInteger)b * b) - ((4 * (BigInteger)a) * c)) >= BigInteger.Zero) { continue; }

                    var reduction = ModularTransform.GaussReduce(a: a, b: b, c: c);

                    if (!((-reduction.A < reduction.B) && (reduction.B <= reduction.A) && (reduction.A <= reduction.C))) {
                        return $"the reduced form violates -A < B <= A <= C at ({a},{b},{c})";
                    }
                    if (Int128.One != (((Int128)reduction.Transform.A * reduction.Transform.D) - ((Int128)reduction.Transform.B * reduction.Transform.C))) {
                        return $"the reduction transform is not determinant one at ({a},{b},{c})";
                    }

                    var sourceDiscriminant = (((BigInteger)b * b) - ((4 * (BigInteger)a) * c));
                    var reducedDiscriminant = (((BigInteger)reduction.B * reduction.B) - ((4 * (BigInteger)reduction.A) * reduction.C));

                    if (sourceDiscriminant != reducedDiscriminant) { return $"the reduction did not preserve the discriminant at ({a},{b},{c})"; }
                    if (ModularFormAction(a: a, b: b, c: c, g: reduction.Transform.Inverse) != (reduction.A, reduction.B, reduction.C)) {
                        return $"the reduction transform does not carry the original form to the reduced one at ({a},{b},{c})";
                    }
                    if (ModularTransform.GaussReduce(a: reduction.A, b: reduction.B, c: reduction.C).Transform != ModularTransform.Identity) {
                        return $"the reduction is not idempotent at ({a},{b},{c})";
                    }
                    if (FormRootMappedIntoFundamentalDomain(a: a, b: b, c: c, transform: reduction.Transform) is { } domainFailure) {
                        return domainFailure;
                    }

                    ++reductions;
                }
            }
        }

        return ((reductions > 0) ? null : "no positive-definite form was reduced -- the sweep is vacuous");
    }

    /// <summary>The interior-point seam: the ORIGINAL form's root, mapped by <paramref name="transform"/> through
    /// the real <see cref="ModularTransform.Apply(FixedComplex)"/> kernel, must land in the fundamental domain
    /// <c>|Re z| &lt;= 1/2</c>, <c>|z| &gt;= 1</c>. Both are ALGEBRAIC inequalities on the mapped point's own raw
    /// ticks, tested directly in <see cref="Int128"/> — never a prediction of what raw value the kernel's own
    /// division rounding should produce, so it needs no knowledge of that rounding schedule at all. The root fed in
    /// is <see cref="BracketRawQ16"/>'s Q16 approximation of the exact irrational root <c>(-b + i*sqrt(4ac-b^2)) /
    /// 2a</c>, so the two inequalities carry a small integer slack for that approximation plus the kernel's own
    /// one-rounding-per-component division. That slack matters only at the domain's own corners and edges — where
    /// the reduced form is degenerate, e.g. <c>(1,1,1)</c> (the elliptic fixed point) or <c>(1,1,2)</c> (the left
    /// edge) — measured at most twenty-three raw ticks (real part) and fifteen (magnitude) over the full sweep;
    /// sixty-four covers both with room to spare and is still under a tenth of a percent of the unit scale.</summary>
    private static string? FormRootMappedIntoFundamentalDomain(long a, long b, long c, ModularTransform transform) {
        const long toleranceRawTicks = 64L;
        const long halfUnitRaw = (1L << (FixedQ4816.FractionBitCount - 1));
        const long unitRaw = (1L << FixedQ4816.FractionBitCount);

        var (realLow, realHigh) = BracketRawQ16(rationalNumerator: -b, surdNumerator: 0, radicand: BigInteger.One, denominator: (2 * a));
        var (imaginaryLow, imaginaryHigh) = BracketRawQ16(rationalNumerator: 0, surdNumerator: 1, radicand: ((4 * (BigInteger)a * c) - ((BigInteger)b * b)), denominator: (2 * a));
        var sourceRoot = new FixedComplex(
            Real: FixedQ4816.FromRawBits(value: (long)((realLow + realHigh) / 2)),
            Imaginary: FixedQ4816.FromRawBits(value: (long)((imaginaryLow + imaginaryHigh) / 2))
        );
        var mapped = transform.Apply(point: sourceRoot);
        var realMagnitude = Math.Abs(value: mapped.Real.Value);

        if (realMagnitude > (halfUnitRaw + toleranceRawTicks)) {
            return $"the reduction seam did not map the root's real part into |Re z| <= 1/2 at ({a},{b},{c}): real raw {mapped.Real.Value}";
        }

        var squaredMagnitude = (((Int128)mapped.Real.Value * mapped.Real.Value) + ((Int128)mapped.Imaginary.Value * mapped.Imaginary.Value));
        var floorBound = ((Int128)(unitRaw - toleranceRawTicks) * (unitRaw - toleranceRawTicks));

        if (squaredMagnitude < floorBound) {
            return $"the reduction seam did not map the root into |z| >= 1 at ({a},{b},{c}): squared magnitude raw {squaredMagnitude}";
        }

        return null;
    }

    /// <summary>Gauss reduction's admit-or-refuse decision is exact over the whole signed <see langword="long"/> domain,
    /// not merely over small coefficients. The expectation is the definiteness predicate stated directly — the leading
    /// coefficient is positive AND <c>b² - 4ac</c> is negative — evaluated in exact <see cref="BigInteger"/>, which shares
    /// no expression and no carrier with the subject's own test. That matters because the discriminant of a legal triple
    /// needs up to 129 signed bits: <c>(long.MaxValue, 0, long.MinValue)</c> is indefinite and <c>(long.MaxValue, 0,
    /// long.MaxValue)</c> is definite, and any oracle that recomputes <c>b² - 4ac</c> in a 128-bit carrier misjudges both.
    /// The ensemble is a full cross product of fourteen carrier corners plus hand-placed triples that sit exactly ON the
    /// <c>b² = 4ac</c> boundary and one tick inside it, at three different magnitudes. Every admitted form is then held to
    /// the documented reduced region <c>0 &lt; A</c>, <c>-A &lt; B &lt;= A &lt;= C</c>, and to discriminant preservation
    /// recomputed in <see cref="BigInteger"/>.</summary>
    public static string? GaussReductionDefinitenessAcrossTheCarrier() {
        long[] corners = [
            long.MinValue,
            (long.MinValue + 1L),
            -((1L << 62) + 1L),
            -(1L << 62),
            -3L,
            -1L,
            0L,
            1L,
            3L,
            (1L << 32),
            (1L << 62),
            ((1L << 62) + 1L),
            (long.MaxValue - 1L),
            long.MaxValue,
        ];
        // Each pair is a triple whose b² equals 4ac exactly (indefinite, degenerate) and the same triple one tick inside
        // the boundary (definite), at magnitudes that straddle what a 128-bit discriminant can hold.
        (long A, long B, long C)[] boundary = [
            (1L, 2L, 1L),
            (1L, 1L, 1L),
            ((1L << 62), (1L << 32), 1L),
            ((1L << 62), ((1L << 32) - 1L), 1L),
            ((1L << 62), long.MinValue, (1L << 62)),
            ((1L << 62), long.MaxValue, (1L << 62)),
        ];
        var admitted = 0;
        var refused = 0;

        foreach (var a in corners) {
            foreach (var b in corners) {
                foreach (var c in corners) {
                    if (GaussDefinitenessDecision(a: a, b: b, c: c, admitted: ref admitted, refused: ref refused) is { } cornerFailure) {
                        return cornerFailure;
                    }
                }
            }
        }

        foreach (var (a, b, c) in boundary) {
            if (GaussDefinitenessDecision(a: a, b: b, c: c, admitted: ref admitted, refused: ref refused) is { } boundaryFailure) {
                return boundaryFailure;
            }
        }

        if (admitted <= 0) { return "no form was admitted -- the ensemble decides nothing"; }
        if (refused <= 0) { return "no form was refused -- the ensemble decides nothing"; }

        return null;
    }

    /// <summary>One triple of the definiteness ensemble: the expectation is formed here in exact
    /// <see cref="BigInteger"/> and the subject is asked to admit or refuse. An <see cref="OverflowException"/> counts as
    /// admission — it is the documented outcome of a form the definiteness test ACCEPTED whose reduction then exceeded
    /// the carrier, so it says the decision was "definite" just as loudly as a returned reduction does.</summary>
    private static string? GaussDefinitenessDecision(long a, long b, long c, ref int admitted, ref int refused) {
        var discriminant = (((BigInteger)b * b) - ((4 * (BigInteger)a) * c));
        var definite = ((a > 0L) && (discriminant < BigInteger.Zero));
        GaussReduction reduction;

        try {
            reduction = ModularTransform.GaussReduce(a: a, b: b, c: c);
        }
        catch (ArgumentOutOfRangeException) {
            ++refused;

            return (definite ? $"a positive-definite form was refused at ({a},{b},{c}): exact discriminant {discriminant}" : null);
        }
        catch (OverflowException) {
            ++admitted;

            return (definite ? null : $"an indefinite form was admitted at ({a},{b},{c}): exact discriminant {discriminant}");
        }

        if (definite == false) {
            return $"an indefinite form was admitted at ({a},{b},{c}): exact discriminant {discriminant}, reduced to ({reduction.A},{reduction.B},{reduction.C})";
        }
        if (!((0L < reduction.A) && (-reduction.A < reduction.B) && (reduction.B <= reduction.A) && (reduction.A <= reduction.C))) {
            return $"the reduced form violates 0 < A and -A < B <= A <= C at ({a},{b},{c}): ({reduction.A},{reduction.B},{reduction.C})";
        }

        var reducedDiscriminant = (((BigInteger)reduction.B * reduction.B) - ((4 * (BigInteger)reduction.A) * reduction.C));

        if (reducedDiscriminant != discriminant) {
            return $"the reduction did not preserve the discriminant at ({a},{b},{c}): {discriminant} became {reducedDiscriminant}";
        }

        ++admitted;

        return null;
    }

    /// <summary>The golden, silver, and four surd continued-fraction expansions (period structure and block) match a
    /// hand-declared table, and their convergents approach the ideal value — checked by an exact BigInteger
    /// cross-multiplied bound rather than a <see langword="double"/> comparison. Algebraically identical
    /// (p,q,d,r) scalings — including one whose <c>q²·d</c> exceeds <see cref="long"/> — produce identical
    /// expansions, and an oversized partial quotient overflows rather than narrowing.</summary>
    public static string? ContinuedFractionPeriodsAndFullWidthRegressions() {
        (long P, long Q, long D, long R, int Start, long[] Period)[] cases = [
            (1L, 1L, 5L, 2L, 0, [1L]),                       // golden ratio (1 + sqrt 5) / 2
            (1L, 1L, 2L, 1L, 0, [2L]),                       // silver ratio 1 + sqrt 2
            (0L, 1L, 2L, 1L, 1, [2L]),                       // sqrt 2 = [1; (2)]
            (0L, 1L, 3L, 1L, 1, [1L, 2L]),                   // sqrt 3 = [1; (1, 2)]
            (0L, 1L, 7L, 1L, 1, [1L, 1L, 1L, 4L]),           // sqrt 7 = [2; (1, 1, 1, 4)]
            (0L, 1L, 13L, 1L, 1, [1L, 1L, 1L, 1L, 6L]),      // sqrt 13 = [3; (1, 1, 1, 1, 6)]
        ];
        Span<long> terms = stackalloc long[64];

        foreach (var testCase in cases) {
            var written = ContinuedFraction.Expand(
                p: testCase.P,
                q: testCase.Q,
                d: testCase.D,
                r: testCase.R,
                terms: terms,
                periodStart: out var start,
                periodLength: out var length
            );

            if ((written <= 0) || (start != testCase.Start) || (length != testCase.Period.Length)) {
                return $"the period structure is wrong for d={testCase.D}";
            }

            for (var offset = 0; (offset < length); ++offset) {
                if (terms[start + offset] != testCase.Period[offset]) { return $"the period block is wrong for d={testCase.D}"; }
            }

            // Independent convergence check in exact BigInteger rather than double: unfold head + several periods,
            // run the convergent recurrence, and require the squared cross-multiplied distance from the ideal value
            // (P + Q*sqrt(D))/R to be within one part in a million of the squared surd term.
            BigInteger previousNumerator = 0;
            BigInteger numerator = 1;
            BigInteger previousDenominator = 1;
            BigInteger denominator = 0;

            for (var repeat = 0; (repeat < 24); ++repeat) {
                var term = ((repeat < start) ? terms[repeat] : terms[start + ((repeat - start) % length)]);

                (previousNumerator, numerator) = (numerator, ((term * numerator) + previousNumerator));
                (previousDenominator, denominator) = (denominator, ((term * denominator) + previousDenominator));
            }

            var lhs = (((BigInteger)testCase.R * numerator) - ((BigInteger)testCase.P * denominator));
            var lhsSquare = (lhs * lhs);
            var rhsSquare = ((((BigInteger)testCase.Q * testCase.Q) * testCase.D) * (denominator * denominator));
            var diff = BigInteger.Abs(lhsSquare - rhsSquare);

            if ((diff * 1_000_000) > rhsSquare) { return $"the convergents do not approach the value for d={testCase.D}"; }
        }

        var smallEquivalent = new long[32];
        var wideEquivalent = new long[32];
        var smallWritten = ContinuedFraction.Expand(p: 0L, q: 1L, d: 3L, r: 1L, terms: smallEquivalent, periodStart: out var smallStart, periodLength: out var smallLength);
        var wideWritten = ContinuedFraction.Expand(p: 0L, q: long.MaxValue, d: 3L, r: long.MaxValue, terms: wideEquivalent, periodStart: out var wideStart, periodLength: out var wideLength);

        if ((smallWritten != wideWritten) || (smallStart != wideStart) || (smallLength != wideLength) ||
            !smallEquivalent.AsSpan(0, smallWritten).SequenceEqual(wideEquivalent.AsSpan(0, wideWritten))) {
            return "the full-width common-scale equivalence changed the expansion";
        }

        const long Scale = (long.MaxValue / 6L);

        smallWritten = ContinuedFraction.Expand(p: 0L, q: 5L, d: 3L, r: 6L, terms: smallEquivalent, periodStart: out smallStart, periodLength: out smallLength);
        wideWritten = ContinuedFraction.Expand(p: 0L, q: (5L * Scale), d: 3L, r: (6L * Scale), terms: wideEquivalent, periodStart: out wideStart, periodLength: out wideLength);

        if ((smallWritten != wideWritten) || (smallStart != wideStart) || (smallLength != wideLength) ||
            !smallEquivalent.AsSpan(0, smallWritten).SequenceEqual(wideEquivalent.AsSpan(0, wideWritten))) {
            return "the full-width normalization equivalence changed the expansion";
        }

        _ = Assert.Throws<OverflowException>(testCode: () => ContinuedFraction.Expand(
            p: long.MaxValue,
            q: long.MaxValue,
            d: long.MaxValue,
            r: 1L,
            terms: new long[8],
            periodStart: out _,
            periodLength: out _
        ));

        return null;
    }

    // ---- banner: "QuadraticInflation + MetallicQuasicrystal (the inflation lens beneath the quasicrystal chains)" ----

    /// <summary>Six surds' <see cref="QuadraticInflation"/> invariants (period length, determinant, discriminant)
    /// match a hand-declared table; the determinant is exactly <c>(-1)^period</c>; the geodesic and its axis
    /// classify hyperbolic and the axis is unimodular; the exact surd <c>(trace+sqrt(disc))/2</c> is a root of the
    /// matrix's characteristic polynomial (checked in exact <see cref="QuadraticSurd"/> field arithmetic, not
    /// double); <see cref="QuadraticInflation.InflationFactor"/> brackets that same exact root to a handful of raw
    /// Q16 ticks; and the golden/silver discriminants (5 and 8) come out of the lens rather than being fed in. The
    /// general polynomial continued-fraction tail analyzer specializes exactly to the golden affine model and
    /// verifies a wide r&gt;&gt;p^2 contraction certificate, and two malformed analyses are refused.</summary>
    public static string? QuadraticInflationInvariantsAndPolynomialTails() {
        (long P, long Q, long D, long R, int Period, long Det, long Disc)[] cases = [
            (1L, 1L, 5L, 2L, 1, -1L, 5L),    // golden phi
            (1L, 1L, 2L, 1L, 1, -1L, 8L),    // silver 1 + sqrt 2
            (0L, 1L, 2L, 1L, 1, -1L, 8L),    // sqrt 2, same geodesic as silver
            (0L, 1L, 3L, 1L, 2, 1L, 12L),    // sqrt 3 (even period, det +1)
            (0L, 1L, 7L, 1L, 4, 1L, 252L),   // sqrt 7 (even period, det +1)
            (0L, 1L, 13L, 1L, 5, -1L, 1300L), // sqrt 13 (odd period, det -1)
        ];

        foreach (var testCase in cases) {
            var inflation = QuadraticInflation.FromQuadraticIrrational(p: testCase.P, q: testCase.Q, d: testCase.D, r: testCase.R);

            if ((inflation.PeriodLength != testCase.Period) || (inflation.Determinant != testCase.Det) || (inflation.Discriminant != testCase.Disc)) {
                return $"the inflation invariants are wrong at d={testCase.D}";
            }
            if (inflation.Determinant != (((inflation.PeriodLength & 1) == 0) ? 1L : -1L)) {
                return $"the determinant sign disagrees with the period parity at d={testCase.D}";
            }
            if ((inflation.GeodesicClass != ModularClass.Hyperbolic) || (inflation.Axis.Classify() != ModularClass.Hyperbolic)) {
                return $"the geodesic is not hyperbolic at d={testCase.D}";
            }
            if ((inflation.Axis * inflation.Axis.Inverse) != ModularTransform.Identity) {
                return $"the axis is not unimodular at d={testCase.D}";
            }

            // The exact characteristic-root identity lambda^2 - trace*lambda + det == 0, in QuadraticSurd field
            // arithmetic -- no floating point anywhere, and no restatement of QuadraticInflation's own algorithm.
            var lambda = QuadraticSurd.Create(rationalNumerator: inflation.Trace, surdNumerator: 1, radicand: inflation.Discriminant, denominator: 2);
            var characteristic = ((lambda * lambda) - (QuadraticSurd.Rational(inflation.Trace) * lambda)) + QuadraticSurd.Rational(inflation.Determinant);

            if (characteristic != QuadraticSurd.Zero) { return $"the exact surd is not a root of the characteristic polynomial at d={testCase.D}"; }

            // The one approximate seam: InflationFactor() must bracket that same exact root to a handful of raw ticks.
            if (!WithinBracket(actual: inflation.InflationFactor(), rationalNumerator: inflation.Trace, surdNumerator: 1, radicand: inflation.Discriminant, denominator: 2, toleranceRawTicks: 8L)) {
                return $"InflationFactor() does not bracket the exact characteristic root at d={testCase.D}";
            }
        }

        if ((QuadraticInflation.FromQuadraticIrrational(p: 1L, q: 1L, d: 5L, r: 2L) != QuadraticInflation.FromQuadraticIrrational(p: 1L, q: 1L, d: 5L, r: 2L)) ||
            (QuadraticInflation.FromQuadraticIrrational(p: 1L, q: 1L, d: 5L, r: 2L).Discriminant != 5L) ||
            (QuadraticInflation.FromQuadraticIrrational(p: 1L, q: 1L, d: 2L, r: 1L).Discriminant != 8L)) {
            return "the golden/silver discriminants are wrong";
        }

        var goldenTail = MetallicPolynomialContinuedFraction.Analyze(metallicIndex: BigInteger.One);

        if ((goldenTail.Slope != QuadraticSurd.Create(1, 1, 5, 2)) ||
            (goldenTail.Offset != QuadraticSurd.Create(-5, -3, 5, 10)) ||
            !goldenTail.VerifyIntervalCertificate()) {
            return "the general polynomial tail's golden specialization is wrong";
        }

        var goldenAsymptotics = goldenTail.AsymptoticCoefficients(termCount: 16);

        if ((goldenAsymptotics.Count != 16) || (goldenAsymptotics[0] != goldenTail.Offset)) {
            return "the golden asymptotic series is wrong";
        }

        var wideTail = PolynomialContinuedFractionTail.Analyze(
            linear: 1,
            constant: 0,
            numeratorQuadratic: 100,
            numeratorLinear: -3,
            numeratorConstant: 7
        );

        if (!wideTail.VerifyIntervalCertificate() || (wideTail.CertifiedInterval(wideTail.IntervalCertificate.Cutoff).Lower.Sign <= 0)) {
            return "the wide r>>p^2 contraction certificate is wrong";
        }

        _ = Assert.Throws<ArgumentOutOfRangeException>(testCode: () => PolynomialContinuedFractionTail.Analyze(1, -2, 1, 0, 0));
        _ = Assert.Throws<ArgumentOutOfRangeException>(testCode: () => PolynomialContinuedFractionTail.Analyze(1, 0, 1, 0, -2));

        return null;
    }

    /// <summary>MetallicQuasicrystal.Word (n=1 and n=2) reproduces the golden and silver words the ring-coordinate
    /// walk from the origin independently produces; the silver generator does NOT contain the golden word; for
    /// n=1..6 the streamed word's long:short frequency approaches delta_n (exact BigInteger cross-multiplication);
    /// and the streamed word is a fixed point of its own substitution (sigma(word) == word).</summary>
    public static string? MetallicReproducesGoldenSilverAndIsAFixedPoint() {
        var metallicGolden = new bool[8192];
        var metallicSilver = new bool[8192];

        MetallicQuasicrystal.Word(n: 1, tiles: metallicGolden);
        MetallicQuasicrystal.Word(n: 2, tiles: metallicSilver);

        var goldenFromOrigin = new bool[1500];
        var silverFromOrigin = new bool[1500];
        var goldenWalk = (A: 0L, B: 0L);
        var silverWalk = (A: 0L, B: 0L);

        for (var i = 0; (i < goldenFromOrigin.Length); ++i) {
            goldenFromOrigin[i] = MetallicQuasicrystal.StartsLongTile(n: 1, a: goldenWalk.A, b: goldenWalk.B);
            goldenWalk = MetallicQuasicrystal.Next(n: 1, a: goldenWalk.A, b: goldenWalk.B);
        }
        for (var i = 0; (i < silverFromOrigin.Length); ++i) {
            silverFromOrigin[i] = MetallicQuasicrystal.StartsLongTile(n: 2, a: silverWalk.A, b: silverWalk.B);
            silverWalk = MetallicQuasicrystal.Next(n: 2, a: silverWalk.A, b: silverWalk.B);
        }

        if (!IsFactorOfWord(haystack: metallicGolden, needle: goldenFromOrigin) ||
            !IsFactorOfWord(haystack: metallicSilver, needle: silverFromOrigin)) {
            return "MetallicQuasicrystal.Word does not reproduce the golden/silver ring-coordinate word";
        }
        if (IsFactorOfWord(haystack: metallicSilver, needle: goldenFromOrigin.AsSpan(0, 256))) {
            return "the silver generator matched the golden word";
        }

        for (var n = 1; (n <= 6); ++n) {
            var word = new bool[20000];

            MetallicQuasicrystal.Word(n: n, tiles: word);

            var longCount = 0L;

            foreach (var isLong in word) { if (isLong) { ++longCount; } }

            var shortCount = (word.Length - longCount);
            var factorRaw = (BigInteger)MetallicQuasicrystal.InflationFactor(n: n).Value;
            var lhs = ((BigInteger)longCount * 65536);
            var rhs = ((BigInteger)shortCount * factorRaw);
            var diff = BigInteger.Abs(lhs - rhs);

            if ((diff * 100) > (rhs * 2)) { return $"the streamed metallic frequency is off at n={n}"; }

            // sigma(word) == word: expand each tile (long -> long^n short, short -> long) and match the word in place.
            var cursor = 0;

            foreach (var isLong in word) {
                if (cursor >= (word.Length - (n + 1))) { break; }

                if (isLong) {
                    for (var repeat = 0; (repeat < n); ++repeat) {
                        if (!word[cursor++]) { return $"the streamed word is not a fixed point of its own substitution at n={n}"; }
                    }
                    if (word[cursor++]) { return $"the streamed word is not a fixed point of its own substitution at n={n}"; }
                } else if (!word[cursor++]) {
                    return $"the streamed word is not a fixed point of its own substitution at n={n}";
                }
            }
        }

        return null;
    }

    // ---- banner: "QuadraticQuasicrystal (the general chain: arbitrary CF period, not just metallic [n])" ----

    /// <summary>Square-equivalent <see cref="QuadraticSurd"/> representations compare, hash, set-deduplicate and add
    /// as one value. For seven CF periods, <see cref="QuadraticQuasicrystalIndex"/>'s random access (TileAt,
    /// CountLongTiles) matches the streamed word over 2048 indices, a remote prefix identity holds at
    /// <c>2^512+12345</c>, the word is Sturmian (exactly k+1 distinct length-k factors for k=1..24), and the exact
    /// tile-length inflation identity <c>lambda*l = A*l + C</c> holds in exact QuadraticSurd field arithmetic. The
    /// general generator reproduces the hand-coded golden word, and the WordComplexity oracle has teeth (a
    /// synthetic period-3 word does not report k+1).</summary>
    public static string? GeneralQuasicrystalIsSturmianAndTileLengthConsistent() {
        var nonCanonicalSilver = QuadraticSurd.Create(2, 1, 8, 2);
        var canonicalSilver = QuadraticSurd.Create(1, 1, 2, 1);
        var equivalentSurds = new HashSet<QuadraticSurd> { nonCanonicalSilver, canonicalSilver };

        if ((nonCanonicalSilver != canonicalSilver) ||
            (nonCanonicalSilver.CompareTo(canonicalSilver) != 0) ||
            (nonCanonicalSilver.GetHashCode() != canonicalSilver.GetHashCode()) ||
            (equivalentSurds.Count != 1) ||
            ((nonCanonicalSilver + canonicalSilver) != (QuadraticSurd.Rational(2) * canonicalSilver))) {
            return "square-equivalent QuadraticSurd representations disagree";
        }

        (long P, long Q, long D, long R)[] cases = [
            (1L, 1L, 5L, 2L), (1L, 1L, 2L, 1L), (0L, 1L, 2L, 1L), (0L, 1L, 3L, 1L), (0L, 1L, 7L, 1L), (0L, 1L, 13L, 1L), (0L, 1L, 23L, 1L),
        ];
        var word = new bool[40_000];

        foreach (var testCase in cases) {
            QuadraticQuasicrystal.Word(p: testCase.P, q: testCase.Q, d: testCase.D, r: testCase.R, tiles: word);

            var index = QuadraticQuasicrystal.Compile(p: testCase.P, q: testCase.Q, d: testCase.D, r: testCase.R);
            var streamedLongCount = BigInteger.Zero;

            for (var position = 0; (position < 2048); ++position) {
                if ((index.TileAt(position) != word[position]) || (index.CountLongTiles(position) != streamedLongCount)) {
                    return $"random access disagrees with the streamed word at d={testCase.D} index={position}";
                }
                if (word[position]) { ++streamedLongCount; }
            }

            var remoteIndex = ((BigInteger.One << 512) + 12345);
            var remoteLongs = index.CountLongTiles(remoteIndex);
            var remoteAdvance = (index.CountLongTiles(remoteIndex + 1) - remoteLongs);

            if (remoteAdvance != (index.TileAt(remoteIndex) ? BigInteger.One : BigInteger.Zero)) {
                return $"the remote prefix identity fails at d={testCase.D}";
            }

            for (var k = 1; (k <= 24); ++k) {
                if (WordComplexity(word: word, k: k) != (k + 1)) { return $"the word is not Sturmian at d={testCase.D} k={k}"; }
            }

            // The tile lengths are the left Perron eigenvector: lambda*l = A*l + C, in exact QuadraticSurd arithmetic.
            var trace = (index.A + index.D);
            var determinant = ((index.A * index.D) - (index.B * index.C));
            var lambda = QuadraticSurd.Create(rationalNumerator: trace, surdNumerator: 1, radicand: ((trace * trace) - (4 * determinant)), denominator: 2);

            if ((lambda * index.ExactLongTileLength) != ((QuadraticSurd.Rational(index.A) * index.ExactLongTileLength) + QuadraticSurd.Rational(index.C))) {
                return $"the tile-length inflation identity fails at d={testCase.D}";
            }

            // The one approximate seam: QuadraticQuasicrystal.LongTileLength must bracket the exact tile length.
            var actualLength = QuadraticQuasicrystal.LongTileLength(p: testCase.P, q: testCase.Q, d: testCase.D, r: testCase.R);

            var exactTileLength = index.ExactLongTileLength;

            if (!WithinBracket(actual: actualLength, rationalNumerator: exactTileLength.RationalNumerator, surdNumerator: exactTileLength.SurdNumerator, radicand: exactTileLength.Radicand, denominator: exactTileLength.Denominator, toleranceRawTicks: 32L)) {
                return $"QuadraticQuasicrystal.LongTileLength does not bracket the exact tile length at d={testCase.D}";
            }
        }

        var goldenReference = new bool[200];
        var goldenWalk = (A: 0L, B: 0L);

        for (var i = 0; (i < goldenReference.Length); ++i) {
            goldenReference[i] = MetallicQuasicrystal.StartsLongTile(n: 1, a: goldenWalk.A, b: goldenWalk.B);
            goldenWalk = MetallicQuasicrystal.Next(n: 1, a: goldenWalk.A, b: goldenWalk.B);
        }

        var generalGoldenWord = new bool[8192];

        QuadraticQuasicrystal.Word(p: 1L, q: 1L, d: 5L, r: 2L, tiles: generalGoldenWord);

        if (!IsFactorOfWord(haystack: generalGoldenWord, needle: goldenReference)) { return "the general generator does not reproduce the golden word"; }

        var periodicProbe = new bool[300];

        for (var i = 0; (i < periodicProbe.Length); ++i) { periodicProbe[i] = ((i % 3) == 0); }

        if (WordComplexity(word: periodicProbe, k: 10) == 11) { return "the WordComplexity oracle has no teeth"; }

        return null;
    }

    /// <summary>Contract and scale regressions outside the ordinary small-period table: a perfect-square radicand is
    /// refused; a 217-term period streams and indexes correctly and stays Sturmian; <see cref="QuadraticInflation"/>
    /// overflows on that period (the term product exceeds <see cref="long"/>); a 3-billion partial quotient keeps
    /// its prefix and brackets its tile length to a handful of raw ticks; and <see cref="QuadraticQuasicrystal.Positions"/>
    /// overflows on 50,000 long tiles.</summary>
    public static string? QuadraticQuasicrystalWidthAndPeriodRegressions() {
        var perfectSquareRefusal = Assert.Throws<ArgumentOutOfRangeException>(testCode: () => QuadraticQuasicrystal.Word(p: 0L, q: 1L, d: 4L, r: 1L, tiles: []));

        Assert.Equal(expected: "d", actual: perfectSquareRefusal.ParamName);

        var longPeriodWord = new bool[512];

        QuadraticQuasicrystal.Word(p: 0L, q: 1L, d: 9949L, r: 1L, tiles: longPeriodWord); // period length 217

        var longPeriodIndex = QuadraticQuasicrystal.Compile(p: 0L, q: 1L, d: 9949L, r: 1L);

        if (longPeriodIndex.PeriodLength != 217) { return "the long-period index lost the period"; }

        for (var index = 0; (index < longPeriodWord.Length); ++index) {
            if (longPeriodIndex.TileAt(index) != longPeriodWord[index]) { return $"the long-period random access is wrong at index={index}"; }
        }
        for (var k = 1; (k <= 12); ++k) {
            if (WordComplexity(word: longPeriodWord, k: k) != (k + 1)) { return $"the long-period word is not Sturmian at k={k}"; }
        }

        _ = Assert.Throws<OverflowException>(testCode: () => QuadraticInflation.FromQuadraticIrrational(p: 0L, q: 1L, d: 9949L, r: 1L));

        const long LargeQuotient = 3_000_000_000L;
        const long LargeDiscriminant = 9_000_000_000_000_000_004L;
        var largePrefix = new bool[16];

        QuadraticQuasicrystal.Word(p: LargeQuotient, q: 1L, d: LargeDiscriminant, r: 2L, tiles: largePrefix);

        if (Array.IndexOf(array: largePrefix, value: false) >= 0) { return "the large-quotient prefix is wrong"; }

        var largeIndex = QuadraticQuasicrystal.Compile(p: LargeQuotient, q: 1L, d: LargeDiscriminant, r: 2L);
        var largeActualLength = QuadraticQuasicrystal.LongTileLength(p: LargeQuotient, q: 1L, d: LargeDiscriminant, r: 2L);

        var largeExactTileLength = largeIndex.ExactLongTileLength;

        if (!WithinBracket(actual: largeActualLength, rationalNumerator: largeExactTileLength.RationalNumerator, surdNumerator: largeExactTileLength.SurdNumerator, radicand: largeExactTileLength.Radicand, denominator: largeExactTileLength.Denominator, toleranceRawTicks: (1L << 20))) {
            return "the large-quotient tile length lost precision";
        }

        var overflowTiles = Enumerable.Repeat(element: true, count: 50_000).ToArray();

        _ = Assert.Throws<OverflowException>(testCode: () => QuadraticQuasicrystal.Positions(
            p: LargeQuotient,
            q: 1L,
            d: LargeDiscriminant,
            r: 2L,
            tiles: overflowTiles,
            positions: new FixedQ4816[overflowTiles.Length]
        ));

        return null;
    }

    // ---- banner: "QuadraticQuasicrystal.Chain random access (the general cut-and-project: ANY CF period, not just
    // metallic [n])" ----

    /// <summary>For n=1..6, the general single-term <see cref="QuadraticQuasicrystal.Chain"/> (tile-count
    /// coordinates) and <see cref="MetallicQuasicrystal"/> (ring coordinates) realize ONE tiling: each walk word is a
    /// factor of the other, phase aside. For four CF periods NOT already covered by
    /// <c>quasicrystal.chain-walk-vs-streamed-word</c> (golden, sqrt3, sqrt13) — sqrt7 (4-term), sqrt19 (6-term),
    /// sqrt23, and a non-unit-numerator (3,1,11,1) — the chain's ring-coordinate walk from the origin stays in the
    /// acceptance window, inverts under Previous, steps by exactly the long or short tile vector, advances
    /// monotonically, matches the streamed word's long density (exact BigInteger cross-multiplication), Contains
    /// equals the walked vertex set over a coordinate box the walk fully covers, the walk word is a factor of the
    /// independently streamed word, and the chain's cached lens (<see cref="QuadraticQuasicrystal.Chain.Inflation"/>,
    /// <see cref="QuadraticQuasicrystal.Chain.InflationFactor"/>) equals <see cref="QuadraticInflation"/> built the
    /// same way.</summary>
    public static string? ChainSingleTermMatchesMetallicAndNewPeriodsWalk() {
        for (var n = 1; (n <= 6); ++n) {
            var chain = QuadraticQuasicrystal.Chain.FromQuadraticIrrational(p: n, q: 1L, d: (((long)n * n) + 4L), r: 2L);
            var chainPoint = (A: 0L, B: 0L);
            var metallicPoint = (A: 0L, B: 0L);
            var chainWord = new bool[2000];
            var metallicWord = new bool[2000];

            for (var step = 0; (step < chainWord.Length); ++step) {
                chainWord[step] = chain.StartsLongTile(a: chainPoint.A, b: chainPoint.B);
                chainPoint = chain.Next(a: chainPoint.A, b: chainPoint.B);
                metallicWord[step] = MetallicQuasicrystal.StartsLongTile(n: n, a: metallicPoint.A, b: metallicPoint.B);
                metallicPoint = MetallicQuasicrystal.Next(n: n, a: metallicPoint.A, b: metallicPoint.B);
            }

            if (!IsFactorOfWord(haystack: metallicWord, needle: chainWord.AsSpan(0, 900)) ||
                !IsFactorOfWord(haystack: chainWord, needle: metallicWord.AsSpan(0, 900))) {
                return $"the single-term chain and the metallic ring walk do not realize the same tiling at n={n}";
            }
        }

        (long P, long Q, long D, long R)[] newPeriods = [
            (0L, 1L, 7L, 1L),   // sqrt 7 = [2; (1, 1, 1, 4)], a 4-term period
            (0L, 1L, 19L, 1L),  // sqrt 19, a 6-term period
            (0L, 1L, 23L, 1L),  // sqrt 23
            (3L, 1L, 11L, 1L),  // a non-unit-numerator (p,q,d,r)
        ];
        var streamed = new bool[60_000];

        foreach (var testCase in newPeriods) {
            var chain = QuadraticQuasicrystal.Chain.FromQuadraticIrrational(p: testCase.P, q: testCase.Q, d: testCase.D, r: testCase.R);

            if (!chain.Contains(a: 0L, b: 0L)) { return $"the chain origin is not a member at d={testCase.D}"; }

            var point = (A: 0L, B: 0L);
            var longCount = 0L;
            var walkWord = new bool[6000];
            var visited = new HashSet<(long A, long B)>();

            for (var step = 0; (step < walkWord.Length); ++step) {
                visited.Add(item: point);

                var isLong = chain.StartsLongTile(a: point.A, b: point.B);
                var next = chain.Next(a: point.A, b: point.B);

                walkWord[step] = isLong;

                if (!chain.Contains(a: next.A, b: next.B)) { return $"the chain walk left the acceptance window at d={testCase.D} step={step}"; }
                if (chain.Previous(a: next.A, b: next.B) != point) { return $"Previous does not invert Next at d={testCase.D} step={step}"; }
                if (isLong ? (((next.A - point.A) != 1L) || ((next.B - point.B) != 0L)) : (((next.A - point.A) != 0L) || ((next.B - point.B) != 1L))) {
                    return $"the chain step is not the long or short vector at d={testCase.D} step={step}";
                }
                if (chain.Position(a: next.A, b: next.B) <= chain.Position(a: point.A, b: point.B)) {
                    return $"the chain positions are not increasing at d={testCase.D} step={step}";
                }

                if (isLong) { ++longCount; }

                point = next;
            }

            QuadraticQuasicrystal.Word(p: testCase.P, q: testCase.Q, d: testCase.D, r: testCase.R, tiles: streamed);

            var streamedLong = 0L;

            foreach (var tile in streamed) { if (tile) { ++streamedLong; } }

            // Density agreement, exactly: cross-multiply longCount/walkWord.Length against streamedLong/streamed.Length.
            var lhs = ((BigInteger)longCount * streamed.Length);
            var rhs = ((BigInteger)streamedLong * walkWord.Length);
            var diff = BigInteger.Abs(lhs - rhs);

            if ((diff * 100) > (rhs * 2)) { return $"the chain density disagrees with the streamed word at d={testCase.D}"; }

            const long Box = 60L;

            if ((point.A <= Box) || (point.B <= Box)) { return $"the chain walk did not cover the coordinate box at d={testCase.D}"; }

            for (var a = 0L; (a <= Box); ++a) {
                for (var b = 0L; (b <= Box); ++b) {
                    if (chain.Contains(a: a, b: b) != visited.Contains(item: (a, b))) {
                        return $"Contains disagrees with the walked vertex set at d={testCase.D} ({a},{b})";
                    }
                }
            }

            if (!IsFactorOfWord(haystack: streamed, needle: walkWord.AsSpan(0, 1500))) {
                return $"the chain random access is not a factor of the streamed word at d={testCase.D}";
            }

            var inflation = QuadraticInflation.FromQuadraticIrrational(p: testCase.P, q: testCase.Q, d: testCase.D, r: testCase.R);

            if ((chain.Inflation != inflation) || (chain.InflationFactor != inflation.InflationFactor())) {
                return $"the chain's cached lens is not the inflation matrix at d={testCase.D}";
            }
        }

        return null;
    }
}
