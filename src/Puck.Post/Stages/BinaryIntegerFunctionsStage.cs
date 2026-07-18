using Puck.Maths;

namespace Puck.Post;

/// <summary>
/// Tier-A stage A3. Checks the correctness edges of <see cref="BinaryIntegerFunctions"/> and <see cref="SecureRandom"/>
/// that a determinism gate cannot see: that <see cref="BinaryIntegerFunctions.GreatestCommonDivisor{T}"/> returns a
/// magnitude even through its zero fast paths, that the base-10 digit helpers treat <see cref="int.MinValue"/> as a
/// magnitude rather than throwing on an unrepresentable absolute value, that
/// <see cref="BinaryIntegerFunctions.Exponentiate{T}"/> rejects a negative exponent, and that a bounded
/// <see cref="SecureRandom"/> draw rejects an inverted interval and otherwise stays in range.
/// </summary>
internal sealed class BinaryIntegerFunctionsStage : IPostStage {
    /// <inheritdoc/>
    public string Name => "binary-integer-functions";

    /// <inheritdoc/>
    public PostTier Tier => PostTier.A;

    /// <inheritdoc/>
    public PostStageOutcome Run(PostContext context) {
        // GCD returns a magnitude on every path, including the zero fast paths and a mixed-sign general path.
        if ((GreatestCommonDivisorOf(other: 0, value: -3) != 3) || (GreatestCommonDivisorOf(other: -7, value: 0) != 7) ||
            (GreatestCommonDivisorOf(other: 8, value: -12) != 4) || (GreatestCommonDivisorOf(other: -24, value: 54) != 6) ||
            (GreatestCommonDivisorOf(other: 1, value: int.MinValue) != 1) || (GreatestCommonDivisorOf(other: 12, value: int.MinValue) != 4)) {
            return PostStageOutcome.Fail(detail: "greatest common divisor did not return a magnitude");
        }

        if ((LeastCommonMultipleOf(other: 6, value: 4) != 12) || (LeastCommonMultipleOf(other: 5, value: 0) != 0) ||
            (LeastCommonMultipleOf(other: 6, value: -4) != 12) || (LeastCommonMultipleOf(other: -6, value: 4) != 12) ||
            (LeastCommonMultipleOf(other: 2, value: int.MaxValue) != -2) || (LeastCommonMultipleOf(other: 2, value: -int.MaxValue) != -2)) {
            return PostStageOutcome.Fail(detail: "least common multiple wrong");
        }

        if (!Throws<OverflowException>(action: static () => _ = GreatestCommonDivisorOf(other: int.MinValue, value: int.MinValue))) {
            return PostStageOutcome.Fail(detail: "greatest common divisor did not report an unrepresentable magnitude");
        }

        // The base-10 digit helpers treat the value as a magnitude at int.MinValue (|int.MinValue| = 2147483648)
        // instead of throwing on the unrepresentable T.Abs(int.MinValue).
        ReadOnlySpan<int> minValueDigits = [8, 4, 6, 3, 8, 4, 7, 4, 1, 2];
        var enumerated = int.MinValue.EnumerateDigits().ToArray();

        if (enumerated.Length != minValueDigits.Length) {
            return PostStageOutcome.Fail(detail: $"EnumerateDigits(int.MinValue) yielded {enumerated.Length} digits, expected {minValueDigits.Length}");
        }

        for (var i = 0; (i < enumerated.Length); i++) {
            if (enumerated[i] != minValueDigits[i]) {
                return PostStageOutcome.Fail(detail: $"EnumerateDigits(int.MinValue) digit {i} = {enumerated[i]}, expected {minValueDigits[i]}");
            }
        }

        if ((int.MinValue.LogarithmBase10() != 10) || (int.MinValue.LeastSignificantDigit() != 8) ||
            (int.MinValue.MostSignificantDigit() != 2) || (int.MinValue.DigitalRoot() != 2)) {
            return PostStageOutcome.Fail(detail: "a base-10 digit helper mishandled int.MinValue");
        }

        // Digit ops carry sign and magnitude for ordinary values (the reversed magnitude of int.MinValue overflows int,
        // so it is exercised through representable operands here).
        if ((1230.ReverseDigits() != 321) || ((-1230).ReverseDigits() != -321) ||
            (0.DigitalRoot() != 0) || ((-18).DigitalRoot() != 9)) {
            return PostStageOutcome.Fail(detail: "digit reverse/root sign or magnitude wrong");
        }

        // Exponentiate: the ordinary powers hold, and a negative exponent is rejected rather than silently meaningless.
        if ((2.Exponentiate(exponent: 10) != 1024) || (3.Exponentiate(exponent: 0) != 1) || (5.Exponentiate(exponent: 3) != 125)) {
            return PostStageOutcome.Fail(detail: "Exponentiate wrong for a non-negative exponent");
        }

        if (!Throws<ArgumentOutOfRangeException>(action: static () => _ = 2.Exponentiate(exponent: -1))) {
            return PostStageOutcome.Fail(detail: "Exponentiate did not reject a negative exponent");
        }

        // SecureRandom rejects an inverted interval instead of wrapping it into a huge unrelated span, and an ordinary
        // bounded draw stays inside its inclusive interval.
        if (!Throws<ArgumentOutOfRangeException>(action: static () => _ = SecureRandom.NextUInt<uint>(maximum: 1U, minimum: 2U))) {
            return PostStageOutcome.Fail(detail: "SecureRandom did not reject an inverted interval");
        }

        for (var n = 0; (n < 4_096); n++) {
            var draw = SecureRandom.NextUInt<uint>(maximum: 10U, minimum: 5U);

            if ((draw < 5U) || (draw > 10U)) {
                return PostStageOutcome.Fail(detail: $"SecureRandom bounded draw {draw} left [5, 10]");
            }
        }

        return PostStageOutcome.Pass(detail: "gcd/lcm magnitude, MinValue-safe digit helpers, Exponentiate rejection, and bounded SecureRandom checks agree with their references");
    }

    private static int GreatestCommonDivisorOf(int value, int other) =>
        value.GreatestCommonDivisor(other: other);
    private static int LeastCommonMultipleOf(int value, int other) =>
        value.LeastCommonMultiple(other: other);
    private static bool Throws<TException>(Action action) where TException : Exception {
        try {
            action();
        } catch (TException) {
            return true;
        }

        return false;
    }
}
