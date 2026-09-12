using System.Numerics;

namespace Puck.Maths.Tests;

internal static partial class Oracles {
    // Form the whole arbitrary-width result before deciding whether the carrier can hold it.
    public static (int Kind, long Cycles) CostSum(long left, long right) => CostResult((BigInteger)left + right);
    public static (int Kind, long Cycles) CostProduct(long left, long right) => CostResult((BigInteger)left * right);

    private static (int Kind, long Cycles) CostResult(BigInteger exact) =>
        exact > long.MaxValue ? (2, 0L) : (0, (long)exact);

    public static long CostBudget(long frequency, long numerator, long denominator, int rate) =>
        rate == 0 ? 0L : (long)((BigInteger)frequency * numerator / ((BigInteger)denominator * rate));

    // Compare undivided exact work against the reservation: no shared divided-allowance decision.
    public static bool CostAdmitted(long cycles, long frequency, long numerator, long denominator, int rate) =>
        rate > 0 && (BigInteger)cycles * denominator * rate <= (BigInteger)frequency * numerator;

    public static BigInteger CostReferenceTicks(long cycles, long frequency) {
        var quotient = BigInteger.DivRem((BigInteger)cycles * 50_400, frequency, out var remainder);
        return remainder.IsZero ? quotient : quotient + BigInteger.One;
    }

    public static (Int128 Numerator, long Denominator) CostStepFraction(long cycles, long frequency, int rate) =>
        rate == 0 ? (Int128.Zero, 1L) : ((Int128)((BigInteger)cycles * rate), frequency);
}
