using System.Numerics;

namespace Puck.Maths;

internal static class PolynomialTailIndex {
    public static void RequirePositive(BigInteger tailIndex) {
        if (tailIndex <= BigInteger.Zero) {
            throw new ArgumentOutOfRangeException(
                paramName: nameof(tailIndex),
                message: "the tail index must be positive"
            );
        }
    }
}
