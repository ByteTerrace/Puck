using System.Numerics;

namespace Puck.Maths;

/// <summary>Admission checks on <see cref="System.Numerics"/> vectors, shared wherever a document or engine boundary
/// admits an authored float before it is quantized to fixed point or reaches the GPU.</summary>
public static class VectorFunctions {
    /// <summary>Returns whether every component of <paramref name="vector"/> is finite (neither NaN nor an
    /// infinity).</summary>
    /// <param name="vector">The vector to test.</param>
    /// <returns><see langword="true"/> when every component is finite.</returns>
    public static bool IsFinite(Vector3 vector) => Vector3.AllWhereAllBitsSet(vector: Vector3.IsFinite(vector: vector));
}
