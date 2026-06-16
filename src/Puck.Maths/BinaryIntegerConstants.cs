using System.Numerics;
using System.Runtime.CompilerServices;

namespace Puck.Maths;

/// <summary>
/// Provides commonly needed constants for an arbitrary binary integer type, materialized through bit operations rather
/// than literal conversions so that they are valid for every width of <typeparamref name="T"/> (including types wider
/// than <see cref="ulong"/>, such as <see cref="System.Int128"/> and <see cref="System.UInt128"/>).
/// </summary>
/// <typeparam name="T">The binary integer type the constants are expressed in.</typeparam>
internal static class BinaryIntegerConstants<T> where T : IBinaryInteger<T> {
    /// <summary>Gets the base-2 logarithm of the bit width of <typeparamref name="T"/> (for example, <c>5</c> for a 32-bit type).</summary>
    public static T Log2Size {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => T.CreateTruncating(value: BitOperations.Log2(value: ((uint)(Unsafe.SizeOf<T>() << 3))));
    }
    /// <summary>Gets the value nine expressed as a <typeparamref name="T"/>.</summary>
    public static T Nine {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => (T.One << 3) | T.One;
    }
    /// <summary>Gets the total number of bits occupied by a <typeparamref name="T"/> (for example, <c>32</c> for a 32-bit type).</summary>
    public static T Size {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => T.CreateTruncating(value: (Unsafe.SizeOf<T>() << 3));
    }
    /// <summary>Gets the value ten expressed as a <typeparamref name="T"/>.</summary>
    public static T Ten {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => (T.One << 3) | (T.One << 1);
    }
}
