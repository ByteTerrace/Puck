using System.Globalization;
using System.Numerics;

using Xunit;

namespace Puck.Maths.Tests;

/// <summary>
/// Three claims: the fixed-width Jacobi symbol against its <see cref="BigInteger"/> sibling, the 128-bit-wide
/// binary-integer primitives (<c>ReverseBits</c>, the Morton pair/unpair, and <c>Exponentiate</c>), and the
/// CRC-32/ISO-HDLC published check value. Every oracle here is written out fresh — a definitional bit-by-bit or
/// <see cref="BigInteger"/> computation, or an external published constant, that shares no line with the subject it
/// checks — rather than calling <c>Oracles.cs</c>, so the evidence is self-contained. The declarations in
/// <see cref="LawRegistry"/> invoke these methods as Deep-tier laws.
/// </summary>
internal static class ScalarFieldClaims {
    // ---- shared deterministic operand generators (no System.Random, no wall clock) ----

    private static ulong NextUInt64(ref Pcg32XshRr generator) =>
        ((((ulong)generator.NextUInt32()) << 32) | generator.NextUInt32());

    private static UInt128 NextUInt128(ref Pcg32XshRr generator) =>
        ((((UInt128)NextUInt64(generator: ref generator)) << 64) | NextUInt64(generator: ref generator));

    // ---- jacobi symbol: UnsignedNumberFunctions.JacobiSymbol<T> (uint/ulong/UInt128) and its NumberTheoryFunctions
    // .JacobiSymbol(BigInteger,BigInteger) sibling, both against a freshly transcribed BigInteger descent ----

    /// <summary>
    /// A shared-nothing Jacobi-symbol reference. Structured as a recursive descent that counts every trailing factor of
    /// two BEFORE applying one combined sign flip, where both subjects strip factors of two one at a time inside a loop
    /// and flip per factor — the same reciprocity LAW re-derived independently, in different code, rather than the same
    /// code read twice. Exact throughout: nothing here rounds, so there is no rounding substrate to share either way.
    /// </summary>
    private static int JacobiSymbolReferenceDescent(BigInteger numerator, BigInteger denominator) {
        var reduced = (((numerator % denominator) + denominator) % denominator);

        return JacobiRecurse(value: reduced, modulus: denominator);
    }

    private static int JacobiRecurse(BigInteger value, BigInteger modulus) {
        if (BigInteger.Zero == value) { return ((BigInteger.One == modulus) ? 1 : 0); }

        var oddPart = value;
        var twoExponent = 0;

        while (oddPart.IsEven) {
            oddPart /= 2;
            ++twoExponent;
        }

        var modulusMod8 = (int)(modulus % 8);
        var twoFactorSign = (((1 == (twoExponent & 1)) && ((3 == modulusMod8) || (5 == modulusMod8))) ? -1 : 1);
        var reciprocitySign = ((((int)(oddPart % 4) == 3) && ((int)(modulus % 4) == 3)) ? -1 : 1);
        var nextValue = (modulus % oddPart);

        return (twoFactorSign * reciprocitySign * JacobiRecurse(value: nextValue, modulus: oddPart));
    }

    public static string? JacobiSymbolFixedWidthVsExactDescentSurface() {
        // Multiplicativity and periodicity, over the THREE unsigned carriers: self-contained statements that lean on no
        // oracle at all, so they catch a defect even if the reference descent above shared the subject's own mistake.
        var multiplicativityRng = Pcg32XshRr.Create(state: 0x9F1D2B6A5C3E7081UL, stream: 11UL);

        for (var trial = 0; (trial < 20_000); ++trial) {
            var modulus32 = (multiplicativityRng.NextUInt32() | 1U);
            var a32 = (multiplicativityRng.NextUInt32() % modulus32);
            var b32 = (multiplicativityRng.NextUInt32() % modulus32);
            var ja32 = a32.JacobiSymbol(modulus: modulus32);
            var jb32 = b32.JacobiSymbol(modulus: modulus32);
            var product32 = ((uint)(((ulong)a32 * b32) % modulus32));
            var jab32 = product32.JacobiSymbol(modulus: modulus32);

            if (jab32 != (ja32 * jb32)) {
                return $"uint multiplicativity failed at a={a32} b={b32} n={modulus32}: J(ab)={jab32} J(a)*J(b)={(ja32 * jb32)}";
            }
            if (modulus32 <= (uint.MaxValue - a32)) {
                var jShifted32 = (a32 + modulus32).JacobiSymbol(modulus: modulus32);

                if (jShifted32 != ja32) {
                    return $"uint periodicity failed at a={a32} n={modulus32}: J(a+n)={jShifted32} J(a)={ja32}";
                }
            }

            var modulus64 = (NextUInt64(generator: ref multiplicativityRng) | 1UL);
            var a64 = (NextUInt64(generator: ref multiplicativityRng) % modulus64);
            var b64 = (NextUInt64(generator: ref multiplicativityRng) % modulus64);
            var ja64 = a64.JacobiSymbol(modulus: modulus64);
            var jb64 = b64.JacobiSymbol(modulus: modulus64);
            var product64 = ((ulong)(((UInt128)a64 * b64) % modulus64));
            var jab64 = product64.JacobiSymbol(modulus: modulus64);

            if (jab64 != (ja64 * jb64)) {
                return $"ulong multiplicativity failed at a={a64} b={b64} n={modulus64}: J(ab)={jab64} J(a)*J(b)={(ja64 * jb64)}";
            }
            if (modulus64 <= (ulong.MaxValue - a64)) {
                var jShifted64 = (a64 + modulus64).JacobiSymbol(modulus: modulus64);

                if (jShifted64 != ja64) {
                    return $"ulong periodicity failed at a={a64} n={modulus64}: J(a+n)={jShifted64} J(a)={ja64}";
                }
            }

            var modulus128 = (NextUInt128(generator: ref multiplicativityRng) | UInt128.One);
            var a128 = (NextUInt128(generator: ref multiplicativityRng) % modulus128);
            var b128 = (NextUInt128(generator: ref multiplicativityRng) % modulus128);
            var ja128 = a128.JacobiSymbol(modulus: modulus128);
            var jb128 = b128.JacobiSymbol(modulus: modulus128);
            var product128 = ((UInt128)(((BigInteger)a128 * b128) % modulus128));
            var jab128 = product128.JacobiSymbol(modulus: modulus128);

            if (jab128 != (ja128 * jb128)) {
                return $"UInt128 multiplicativity failed at a={a128} b={b128} n={modulus128}: J(ab)={jab128} J(a)*J(b)={(ja128 * jb128)}";
            }
            if (modulus128 <= (UInt128.MaxValue - a128)) {
                var jShifted128 = (a128 + modulus128).JacobiSymbol(modulus: modulus128);

                if (jShifted128 != ja128) {
                    return $"UInt128 periodicity failed at a={a128} n={modulus128}: J(a+n)={jShifted128} J(a)={ja128}";
                }
            }
        }

        // BigInteger's own multiplicativity and periodicity, exercised with NEGATIVE numerators specifically — the
        // regime the three unsigned carriers cannot reach at all, and the one where NumberTheoryFunctions.JacobiSymbol's
        // own FloorModulo(BigInteger,BigInteger) call has to floor rather than merely truncate.
        var bigRng = Pcg32XshRr.Create(state: 0x2E8B1F4C9A5D3067UL, stream: 13UL);

        for (var trial = 0; (trial < 20_000); ++trial) {
            var modulus = (BigInteger)(NextUInt64(generator: ref bigRng) | 1UL);
            var a = ((BigInteger)NextUInt64(generator: ref bigRng) % modulus);
            var b = ((BigInteger)NextUInt64(generator: ref bigRng) % modulus);

            // Every third draw is negated, so the sweep exercises numerator < 0 on both the multiplicativity operands
            // and the periodicity probe rather than only on the reduction inside JacobiSymbolReferenceDescent above.
            if (0 == (trial % 3)) { a = -a; }
            if (1 == (trial % 3)) { b = -b; }

            var ja = NumberTheoryFunctions.JacobiSymbol(numerator: a, denominator: modulus);
            var jb = NumberTheoryFunctions.JacobiSymbol(numerator: b, denominator: modulus);
            var product = (((a * b) % modulus + modulus) % modulus);
            var jab = NumberTheoryFunctions.JacobiSymbol(numerator: product, denominator: modulus);

            if (jab != (ja * jb)) {
                return $"BigInteger multiplicativity failed at a={a} b={b} n={modulus}: J(ab)={jab} J(a)*J(b)={(ja * jb)}";
            }

            var jShiftedDown = NumberTheoryFunctions.JacobiSymbol(numerator: (a - modulus), denominator: modulus);
            var jShiftedUp = NumberTheoryFunctions.JacobiSymbol(numerator: (a + modulus), denominator: modulus);

            if ((jShiftedDown != ja) || (jShiftedUp != ja)) {
                return $"BigInteger periodicity failed at a={a} n={modulus}: J(a-n)={jShiftedDown} J(a+n)={jShiftedUp} J(a)={ja}";
            }
        }

        // The classical leg AND the cross-carrier agreement in the same sweep: value and modulus both fit every
        // carrier, so all four — JacobiSymbol<uint>, JacobiSymbol<ulong>, JacobiSymbol<UInt128> (the SAME generic
        // method, three instantiations) and NumberTheoryFunctions.JacobiSymbol(BigInteger,BigInteger) (a separately
        // written function) — are required to equal the independent BigInteger descent, and therefore each other.
        var crossRng = Pcg32XshRr.Create(state: 0x7C6A3E1B8F2D5049UL, stream: 17UL);

        for (var trial = 0; (trial < 30_000); ++trial) {
            var modulusRaw = (crossRng.NextUInt32() | 1U);
            var valueRaw = crossRng.NextUInt32();
            var expected = JacobiSymbolReferenceDescent(numerator: valueRaw, denominator: modulusRaw);
            var uintResult = valueRaw.JacobiSymbol(modulus: modulusRaw);
            var ulongResult = ((ulong)valueRaw).JacobiSymbol(modulus: (ulong)modulusRaw);
            var uint128Result = ((UInt128)valueRaw).JacobiSymbol(modulus: (UInt128)modulusRaw);
            var bigResult = NumberTheoryFunctions.JacobiSymbol(numerator: valueRaw, denominator: modulusRaw);

            if ((uintResult != expected) || (ulongResult != expected) || (uint128Result != expected) || (bigResult != expected)) {
                return $"cross-carrier disagreement at value={valueRaw} modulus={modulusRaw}: expected={expected} uint={uintResult} ulong={ulongResult} UInt128={uint128Result} BigInteger={bigResult}";
            }
        }

        // Full-width sweeps at ulong and UInt128, past the 32-bit cross section above, each against the same
        // independent descent — where the fixed-width kernel's own trailing-zero-count and parity-bit accumulation are
        // exercised at their real width rather than folded into a 32-bit value.
        var wideRng = Pcg32XshRr.Create(state: 0x35E0A9C4712F68D3UL, stream: 19UL);

        for (var trial = 0; (trial < 20_000); ++trial) {
            var modulus64 = (NextUInt64(generator: ref wideRng) | 1UL);
            var value64 = NextUInt64(generator: ref wideRng);
            var expected64 = JacobiSymbolReferenceDescent(numerator: value64, denominator: modulus64);
            var actual64 = value64.JacobiSymbol(modulus: modulus64);

            if (actual64 != expected64) {
                return $"ulong full-width disagreement at value={value64} modulus={modulus64}: expected={expected64} actual={actual64}";
            }

            var modulus128 = (NextUInt128(generator: ref wideRng) | UInt128.One);
            var value128 = NextUInt128(generator: ref wideRng);
            var expected128 = JacobiSymbolReferenceDescent(numerator: (BigInteger)value128, denominator: (BigInteger)modulus128);
            var actual128 = value128.JacobiSymbol(modulus: modulus128);

            if (actual128 != expected128) {
                return $"UInt128 full-width disagreement at value={value128} modulus={modulus128}: expected={expected128} actual={actual128}";
            }
        }

        // Carrier-edge boundary vectors: zero, one, the shared-factor case at value == modulus, and both carrier
        // extremes, at every carrier — every MaxValue is odd, so each stands as its own legal modulus.
        (BigInteger Value, BigInteger Modulus)[] boundaryVectors = [
            (0, 1), (0, 3), (1, 1), (1, 3), (2, 3), (3, 3),
            (uint.MaxValue, uint.MaxValue), (uint.MaxValue, (uint.MaxValue - 2U)), ((uint.MaxValue - 1U), uint.MaxValue),
            (ulong.MaxValue, ulong.MaxValue), (ulong.MaxValue, (ulong.MaxValue - 2UL)), ((ulong.MaxValue - 1UL), ulong.MaxValue),
            ((BigInteger)UInt128.MaxValue, (BigInteger)UInt128.MaxValue),
            ((BigInteger)UInt128.MaxValue, (BigInteger)(UInt128.MaxValue - 2U)),
            ((BigInteger)(UInt128.MaxValue - 1U), (BigInteger)UInt128.MaxValue),
        ];
        foreach (var (value, modulus) in boundaryVectors) {
            var expected = JacobiSymbolReferenceDescent(numerator: value, denominator: modulus);

            if (modulus <= uint.MaxValue) {
                var actual = ((uint)value).JacobiSymbol(modulus: (uint)modulus);

                if (actual != expected) { return $"uint boundary mismatch at value={value} modulus={modulus}: expected={expected} actual={actual}"; }
            }
            if (modulus <= ulong.MaxValue) {
                var actual = ((ulong)value).JacobiSymbol(modulus: (ulong)modulus);

                if (actual != expected) { return $"ulong boundary mismatch at value={value} modulus={modulus}: expected={expected} actual={actual}"; }
            }

            var actual128 = ((UInt128)value).JacobiSymbol(modulus: (UInt128)modulus);

            if (actual128 != expected) { return $"UInt128 boundary mismatch at value={value} modulus={modulus}: expected={expected} actual={actual128}"; }

            var actualBig = NumberTheoryFunctions.JacobiSymbol(numerator: value, denominator: modulus);

            if (actualBig != expected) { return $"BigInteger boundary mismatch at value={value} modulus={modulus}: expected={expected} actual={actualBig}"; }
        }

        // Refusals: an even or zero modulus is refused on every unsigned carrier, and BigInteger's sibling refuses a
        // non-positive OR even denominator — the wider refusal surface a signed carrier admits.
        foreach (var modulus in new uint[] { 0U, 2U, 4U, (uint.MaxValue - 1U) }) {
            var refusal = Assert.Throws<ArgumentOutOfRangeException>(testCode: () => _ = 1U.JacobiSymbol(modulus: modulus));

            Assert.Equal(expected: "modulus", actual: refusal.ParamName);
        }
        foreach (var modulus in new ulong[] { 0UL, 2UL, 4UL, (ulong.MaxValue - 1UL) }) {
            var refusal = Assert.Throws<ArgumentOutOfRangeException>(testCode: () => _ = 1UL.JacobiSymbol(modulus: modulus));

            Assert.Equal(expected: "modulus", actual: refusal.ParamName);
        }
        foreach (var modulus in new UInt128[] { UInt128.Zero, ((UInt128)2), ((UInt128)4), (UInt128.MaxValue - 1U) }) {
            var refusal = Assert.Throws<ArgumentOutOfRangeException>(testCode: () => _ = UInt128.One.JacobiSymbol(modulus: modulus));

            Assert.Equal(expected: "modulus", actual: refusal.ParamName);
        }
        foreach (var denominator in new BigInteger[] { 0, -1, -7, 4, -4 }) {
            var refusal = Assert.Throws<ArgumentOutOfRangeException>(
                testCode: () => _ = NumberTheoryFunctions.JacobiSymbol(numerator: BigInteger.One, denominator: denominator)
            );

            Assert.Equal(expected: "denominator", actual: refusal.ParamName);
        }

        return null;
    }

    // ---- binary integers at their 128-bit width: BitwisePair<ulong,UInt128>, BitwiseUnpair<UInt128,ulong>,
    // UInt128/Int128.ReverseBits, and UInt128.Exponentiate wrapping modulo 2^128 ----

    /// <summary>A bit-by-bit reversal over the full 128-bit width, transcribed from the DEFINITION rather than from
    /// <see cref="BinaryIntegerFunctions.ReverseBits{T}(T)"/>'s width-agnostic SWAR butterfly, which this shares no line
    /// with.</summary>
    private static UInt128 ReverseBitsReference(UInt128 value) {
        var reversed = UInt128.Zero;

        for (var bit = 0; (bit < 128); ++bit) {
            reversed = ((reversed << 1) | (value & UInt128.One));
            value >>= 1;
        }

        return reversed;
    }

    /// <summary>A bit-by-bit Morton interleave, transcribed from the DEFINITION — value's bits at the even positions,
    /// other's at the odd ones — rather than from <see cref="BinaryIntegerFunctions.BitwisePair{TInput,TResult}"/>'s
    /// PDEP/SWAR implementation, which this shares no line with.</summary>
    private static UInt128 BitwisePairReference(ulong value, ulong other) {
        var paired = UInt128.Zero;

        for (var bit = 0; (bit < 64); ++bit) {
            paired |= (((UInt128)((value >> bit) & 1UL)) << (bit << 1));
            paired |= (((UInt128)((other >> bit) & 1UL)) << ((bit << 1) + 1));
        }

        return paired;
    }

    public static string? BinaryIntegerWideCarrierSurface() {
        var rng = Pcg32XshRr.Create(state: 0x6155A55E5B5EC91EUL, stream: 23UL);

        for (var trial = 0; (trial < 30_000); ++trial) {
            var value = NextUInt128(generator: ref rng);
            var expectedReversed = ReverseBitsReference(value: value);

            if (value.ReverseBits() != expectedReversed) {
                return $"UInt128.ReverseBits disagreed with the bit-by-bit reference at value={value}";
            }
            if (unchecked((Int128)value).ReverseBits() != unchecked((Int128)expectedReversed)) {
                return $"Int128.ReverseBits disagreed with the bit-by-bit reference at value={value}";
            }

            var low = NextUInt64(generator: ref rng);
            var high = NextUInt64(generator: ref rng);
            var expectedPair = BitwisePairReference(value: low, other: high);
            var actualPair = low.BitwisePair<ulong, UInt128>(other: high);

            if (actualPair != expectedPair) {
                return $"BitwisePair<ulong,UInt128> disagreed with the Morton reference at value={low} other={high}";
            }

            var (unpairedLow, unpairedHigh) = expectedPair.BitwiseUnpair<UInt128, ulong>();

            if ((unpairedLow != low) || (unpairedHigh != high)) {
                return $"BitwiseUnpair<UInt128,ulong> did not invert the pair at value={low} other={high}";
            }

            var exponent = (rng.NextUInt32() % 130U);
            var expectedPower = ((UInt128)BigInteger.ModPow(value: (BigInteger)value, exponent: exponent, modulus: (BigInteger.One << 128)));
            var actualPower = value.Exponentiate(exponent: (UInt128)exponent);

            if (actualPower != expectedPower) {
                return $"UInt128.Exponentiate disagreed with BigInteger.ModPow mod 2^128 at value={value} exponent={exponent}";
            }
        }

        // Boundary vectors: zero, one, the width-1 bit, alternating patterns, and both carrier extremes.
        UInt128[] boundaryValues = [
            UInt128.Zero, UInt128.One, ((UInt128)2), (UInt128.One << 127),
            ((UInt128)0xAAAAAAAAAAAAAAAAUL | (((UInt128)0xAAAAAAAAAAAAAAAAUL) << 64)),
            ((UInt128)0x5555555555555555UL | (((UInt128)0x5555555555555555UL) << 64)),
            UInt128.MaxValue, (UInt128.MaxValue - 1U),
        ];
        foreach (var value in boundaryValues) {
            var expectedReversed = ReverseBitsReference(value: value);

            if (value.ReverseBits() != expectedReversed) {
                return $"UInt128.ReverseBits boundary mismatch at value={value}";
            }
            if (unchecked((Int128)value).ReverseBits() != unchecked((Int128)expectedReversed)) {
                return $"Int128.ReverseBits boundary mismatch at value={value}";
            }
        }

        // Exponentiate: the zero-exponent identity (including the historically fraught 0^0 = 1 convention), the
        // one-exponent identity, and a wide exponent whose square-and-multiply schedule is many steps deep.
        if (UInt128.Zero.Exponentiate(exponent: UInt128.Zero) != UInt128.One) {
            return "UInt128.Exponentiate(0, 0) was not one";
        }
        if (UInt128.MaxValue.Exponentiate(exponent: UInt128.Zero) != UInt128.One) {
            return "UInt128.Exponentiate(MaxValue, 0) was not one";
        }
        if (UInt128.MaxValue.Exponentiate(exponent: UInt128.One) != UInt128.MaxValue) {
            return "UInt128.Exponentiate(MaxValue, 1) was not MaxValue";
        }

        var deepBase = ((UInt128)0x0123456789ABCDEFUL | (((UInt128)0xFEDCBA9876543210UL) << 64));
        var deepExponent = ((UInt128)1_000_003UL);
        var expectedDeepPower = ((UInt128)BigInteger.ModPow(value: (BigInteger)deepBase, exponent: deepExponent, modulus: (BigInteger.One << 128)));

        if (deepBase.Exponentiate(exponent: deepExponent) != expectedDeepPower) {
            return "UInt128.Exponentiate disagreed with BigInteger.ModPow mod 2^128 at the deep exponent ladder";
        }

        return null;
    }

    // ---- CRC-32/ISO-HDLC: the published catalogue check value, built from BinaryPolynomial's own %, << and + ----

    /// <summary>
    /// The catalogue check value of CRC-32/ISO-HDLC (the "check" field of the reveng.sourceforge.io CRC catalogue
    /// entry, and the value libraries from zlib to .NET's own <c>System.IO.Hashing.Crc32</c> agree on for the ASCII
    /// string "123456789"), computed as what a CRC actually IS — the Euclidean remainder of the message polynomial
    /// times t^32 by the generator polynomial — using only <see cref="BinaryPolynomial"/>'s own <c>%</c>, <c>&lt;&lt;</c>
    /// and <c>+</c> operators, with the reflected wire convention supplied by reversing each input byte and the final
    /// register. Nothing here is a second implementation of CRC-32: it is the SUBJECT's own ring arithmetic, checked
    /// against an external anchor no line of this suite computes.
    /// </summary>
    public static string? BinaryPolynomialCrc32PublishedVectorSurface() {
        var generator = new BinaryPolynomial(bits: 0x1_04C11DB7UL);
        var monomial = new BinaryPolynomial(bits: (1UL << 32));
        var register = new BinaryPolynomial(bits: 0xFFFFFFFFUL);

        foreach (var character in "123456789") {
            var reflectedByte = ((byte)character).ReverseBits();

            for (var bit = 7; (0 <= bit); --bit) {
                var term = ((1 == ((reflectedByte >>> bit) & 1)) ? monomial : BinaryPolynomial.Zero);

                register = (((register << 1) + term) % generator);
            }
        }

        var checkValue = (((uint)register.Bits).ReverseBits() ^ 0xFFFFFFFFU);

        if (0xCBF43926U != checkValue) {
            return $"CRC-32/ISO-HDLC check value mismatch: expected 0xCBF43926, got 0x{checkValue:X8}";
        }

        return null;
    }

    /// <summary>Proves the two conversion seams the library's cross-machine promise rests on are answered in code
    /// rather than by an architecture's choice: not-a-number converts to a stated value, and the exact-surd rendering
    /// does not read the ambient culture.</summary>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    /// <remarks>
    /// <para>
    /// Both were contract gaps rather than arithmetic ones. NaN survived <c>Round</c> and <c>Clamp</c> and reached an
    /// unchecked floating-to-unsigned conversion, for which the CLI specifies no portable result — x64 happens to give
    /// zero, and the tests pinned that. And <c>QuadraticSurd.ToString</c> formatted its components with the ambient
    /// provider, so a host culture could inject U+200E, U+061C or U+2212 into text that reaches logs and golden files.
    /// </para>
    /// <para>
    /// The culture half is exercised by switching the current culture around the call, which is the only way to observe
    /// the defect from inside one process; the culture is restored in a finally so the rest of the run is unaffected.
    /// </para>
    /// </remarks>
    public static string? ConversionSeamsDoNotDependOnTheHost() {
        if (UnitFraction16.FromDouble(value: double.NaN) != default) { return "UnitFraction16.FromDouble(NaN) did not fold to zero"; }
        if (UnitFraction32.FromDouble(value: double.NaN) != default) { return "UnitFraction32.FromDouble(NaN) did not fold to zero"; }

        // The neighbours either side of the fold still behave, so the NaN arm is a fold rather than a swallow.
        if (UnitFraction16.FromDouble(value: -1d) != default) { return "UnitFraction16.FromDouble(-1) did not clamp to zero"; }
        if (UnitFraction32.FromDouble(value: -1d) != default) { return "UnitFraction32.FromDouble(-1) did not clamp to zero"; }
        if (UnitFraction16.FromDouble(value: 2d) != UnitFraction16.MaxValue) { return "UnitFraction16.FromDouble(2) did not clamp to the maximum"; }

        var surd = QuadraticSurd.Create(rationalNumerator: -1234567890, surdNumerator: 1, radicand: 2, denominator: 3);
        var invariant = surd.ToString();
        var original = CultureInfo.CurrentCulture;

        // The test host runs in globalization-invariant mode, so a culture cannot be fetched by name. A CLONE of the
        // invariant culture with its own negative sign needs no ICU data and reproduces the defect exactly: a component
        // formatted through the AMBIENT provider picks the marker up, one formatted invariantly cannot.
        var hostile = (CultureInfo)CultureInfo.InvariantCulture.Clone();

        hostile.NumberFormat.NegativeSign = "−";

        try {
            CultureInfo.CurrentCulture = hostile;

            var rendered = surd.ToString();

            if (rendered != invariant) {
                return $"QuadraticSurd.ToString read the current culture: a host whose negative sign is U+2212 rendered '{rendered}' where the invariant rendering is '{invariant}'";
            }

            // The probe has teeth only if the ambient provider really would have changed the text, so prove that here
            // rather than assuming it: the same component formatted ambiently must differ from the invariant rendering.
            if (surd.RationalNumerator.ToString() == surd.RationalNumerator.ToString(provider: CultureInfo.InvariantCulture)) {
                return "the hostile culture did not change ambient BigInteger formatting, so this probe proves nothing";
            }
        } finally {
            CultureInfo.CurrentCulture = original;
        }

        return null;
    }
}
