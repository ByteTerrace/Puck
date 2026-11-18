using System.Runtime.InteropServices;

namespace Puck.Maths;

/// <summary>Operations on the stored representation of unmanaged values.</summary>
public static class Bitwise {
    /// <summary>Compares every stored bit, including floating-point signs and payloads.
    /// Padding participates too; callers comparing numeric content use types with no padding.</summary>
    /// <typeparam name="T">The unmanaged representation.</typeparam>
    /// <param name="left">The first value.</param>
    /// <param name="right">The second value.</param>
    /// <returns>Whether both values have the same stored bytes.</returns>
    public static bool Equal<T>(in T left, in T right) where T : unmanaged =>
        MemoryMarshal.AsBytes(span: new ReadOnlySpan<T>(reference: in left)).SequenceEqual(other: MemoryMarshal.AsBytes(span: new ReadOnlySpan<T>(reference: in right)));
}
