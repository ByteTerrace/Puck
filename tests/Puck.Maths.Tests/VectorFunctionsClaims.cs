using System.Numerics;

namespace Puck.Maths.Tests;

/// <summary>Claims over <see cref="VectorFunctions"/>, the shared presentation-vector admission checks.</summary>
internal static class VectorFunctionsClaims {
    // A representative lane value from every finiteness class the IEEE-754 single format admits, plus a handful of
    // ordinary finite magnitudes; the full cross product over three lanes exercises every finite/non-finite mix a
    // Vector3 can carry without sweeping the carrier's whole 2³² lane space.
    private static readonly float[] LaneValues = [
        0f, -0f, 1f, -1f, 3.5f, -2.25f,
        float.Epsilon, -float.Epsilon,
        float.MinValue, float.MaxValue,
        float.PositiveInfinity, float.NegativeInfinity,
        float.NaN,
    ];

    public static string? IsFiniteVsOracle() {
        foreach (var x in LaneValues) {
            foreach (var y in LaneValues) {
                foreach (var z in LaneValues) {
                    var vector = new Vector3(x: x, y: y, z: z);
                    var expected = (float.IsFinite(f: x) && float.IsFinite(f: y) && float.IsFinite(f: z));
                    var actual = VectorFunctions.IsFinite(vector: vector);

                    if (actual != expected) {
                        return $"IsFinite({vector}) returned {actual}, but componentwise float.IsFinite says {expected}";
                    }
                }
            }
        }

        return null;
    }
}
