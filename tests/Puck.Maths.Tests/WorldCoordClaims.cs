using Xunit;

namespace Puck.Maths.Tests;

/// <summary>
/// World-coordinate claims: the fixed-point world grid and its round-trip contracts.
/// </summary>
/// <remarks>
/// <para>
/// <c>world-coord3</c> (Tier-A A2) exercised <see cref="FixedPosition"/>'s canonical construction, cell carry,
/// <see cref="FixedPosition.Delta(FixedPosition)"/>/<see cref="FixedPosition.TryDelta(FixedPosition, out FixedVector3)"/>,
/// translating add, and far-cell invariance, one axis at a time. Every one of those statements is already pinned,
/// across all three axes AT ONCE (a strictly stronger sweep than the stage's per-axis loop), by the five laws in
/// <c>laws/position.json</c>: <c>position.canonical-vs-oracle</c> (construction, carry, refusal, idempotence,
/// deconstruction, the throwing constructor, the two shorthand constructors), <c>position.delta-vs-oracle</c>
/// (<c>Delta</c>/<c>TryDelta</c>, both code paths, the refusal predicate, antisymmetry), <c>position.translate-vs-oracle</c>
/// (<c>TryTranslate</c>/<c>operator+</c>, the narrow and <c>Int128</c> paths, the refusal predicate),
/// <c>position.group-structure-exact</c> (the torsor law and associativity), and <c>position.render-relative-ladder</c>
/// (<c>ToRenderRelative</c>, its refusal parity with <c>Delta</c>, and the far-cell invariance the stage's near/far
/// comparison also checked). <see cref="FixedVector3"/>'s construction and arithmetic are separately covered by
/// <c>laws/vector.json</c>. The one thing the stage checked that none of those laws states in so many words — that two
/// (cell, local) constructions of the identical physical point are structurally equal and hash identically — is a fact
/// about the compiler-generated equality of a <c>readonly record struct</c> once its canonical fields are proven equal,
/// not a fact about <see cref="FixedPosition"/>'s own logic, so it earns no separate claim here. <c>world-coord3</c>
/// therefore contributes NO new law: this file carries no method for it.
/// </para>
/// <para>
/// <c>binary-integer-functions</c> (Tier-A A3) exercised <see cref="BinaryIntegerFunctions"/>'s GCD/LCM magnitude
/// contract, the base-10 digit helpers at <see cref="int.MinValue"/>, <see cref="BinaryIntegerFunctions.Exponentiate{T}"/>'s
/// negative-exponent refusal, and a bounded <c>SecureRandom</c> draw. The <c>SecureRandom</c> half is already fully
/// pinned by <c>sampling.secure-random-intervals</c> (inverted-interval refusal and bounded-draw range, at five carrier
/// widths including 32-bit). <c>core.binary-integer-contracts</c> already credits every member the stage touches, but
/// its own claim (<see cref="CoreSurfaceClaims.BinaryIntegerSurface"/>) only exercises ordinary small
/// operands — never a zero fast path, a mixed sign, or <see cref="int.MinValue"/> itself, and never
/// <c>Exponentiate</c>'s negative-exponent guard, which is unreachable at every unsigned instantiation the suite
/// otherwise sweeps. <see cref="BinaryIntegerSignedExtremesAndRefusalsSurface"/> below is the genuine delta: the
/// signed-extreme and refusal edges the stage reached and no Default-tier law did.
/// </para>
/// <para>
/// The declaration in <see cref="LawRegistry"/> invokes this method as a Default-tier law, so it participates in both
/// the ordinary test gate and the mechanically generated public-member coverage ledger.
/// </para>
/// </remarks>
internal static class WorldCoordClaims {
    public static string? BinaryIntegerSignedExtremesAndRefusalsSurface() {
        // GCD returns a magnitude at the zero fast paths and on the ordinary mixed-sign general path, verified against
        // elementary Euclidean division worked out by hand -- a different algorithm and a different derivation route
        // from the subject's Stein binary descent.
        Assert.Equal(expected: 3, actual: GreatestCommonDivisorOf(other: 0, value: -3));
        Assert.Equal(expected: 7, actual: GreatestCommonDivisorOf(other: -7, value: 0));
        Assert.Equal(expected: 4, actual: GreatestCommonDivisorOf(other: 8, value: -12));
        Assert.Equal(expected: 6, actual: GreatestCommonDivisorOf(other: -24, value: 54));

        // The two near-signed-minimum branches the member's own documentation describes: exactly one operand at
        // int.MinValue returns the OTHER operand's own highest power-of-two factor, and BOTH at int.MinValue throws
        // OverflowException naming the unrepresentable magnitude.
        Assert.Equal(expected: 1, actual: GreatestCommonDivisorOf(other: 1, value: int.MinValue));
        Assert.Equal(expected: 4, actual: GreatestCommonDivisorOf(other: 12, value: int.MinValue));
        Assert.Throws<OverflowException>(testCode: static () => _ = GreatestCommonDivisorOf(other: int.MinValue, value: int.MinValue));

        // LCM over representable operands, against |a*b| / gcd(a,b) worked out by hand, with lcm(n, 0) conventionally
        // zero -- independent of the subject's divide-before-multiply order and its sign correction.
        Assert.Equal(expected: 12, actual: LeastCommonMultipleOf(other: 6, value: 4));
        Assert.Equal(expected: 0, actual: LeastCommonMultipleOf(other: 5, value: 0));
        Assert.Equal(expected: 12, actual: LeastCommonMultipleOf(other: 6, value: -4));
        Assert.Equal(expected: 12, actual: LeastCommonMultipleOf(other: -6, value: 4));

        // The documented overflow contract: the true product 2*(2^31 - 1) exceeds int, the member's own doc says the
        // result WRAPS rather than throws, and the sign correction reads the PRE-multiplication operand signs rather
        // than the wrapped product's sign. No finite oracle decides a value the carrier cannot hold, so this states
        // the carrier's own declared contract directly.
        Assert.Equal(expected: -2, actual: LeastCommonMultipleOf(other: 2, value: int.MaxValue));
        Assert.Equal(expected: -2, actual: LeastCommonMultipleOf(other: 2, value: -int.MaxValue));

        // The base-10 digit helpers at int.MinValue, whose magnitude 2^31 = 2,147,483,648 is not representable in int
        // and is the one operand each member's own doc calls out as the reason it abs-es a remainder or a quotient
        // rather than the whole value. Against the decimal expansion of 2^31 worked out on paper, independent of and
        // prior to any of the subject's DivRem or modulo-nine-residue implementations.
        Assert.Equal(expected: [8, 4, 6, 3, 8, 4, 7, 4, 1, 2], actual: int.MinValue.EnumerateDigits().ToArray());
        Assert.Equal(expected: 10, actual: int.MinValue.LogarithmBase10());
        Assert.Equal(expected: 8, actual: int.MinValue.LeastSignificantDigit());
        Assert.Equal(expected: 2, actual: int.MinValue.MostSignificantDigit());
        // 2+1+4+7+4+8+3+6+4+8 = 47, which reduces to 4+7 = 11, which reduces to 1+1 = 2.
        Assert.Equal(expected: 2, actual: int.MinValue.DigitalRoot());

        // Exponentiate<int> rejects a negative exponent with ArgumentOutOfRangeException, exactly as its own doc
        // requires. The guard folds away to nothing for the unsigned UInt128 instantiation
        // scalar.binary-integer-wide-carrier-vs-oracle already sweeps, so a SIGNED instantiation is the only place
        // this branch is reachable at all.
        Assert.Throws<ArgumentOutOfRangeException>(testCode: static () => _ = 2.Exponentiate(exponent: -1));

        return null;
    }

    private static int GreatestCommonDivisorOf(int value, int other) =>
        value.GreatestCommonDivisor(other: other);
    private static int LeastCommonMultipleOf(int value, int other) =>
        value.LeastCommonMultiple(other: other);
}
