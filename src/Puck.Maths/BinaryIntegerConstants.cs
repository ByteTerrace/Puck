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
    /// <summary>
    /// Gets a value indicating whether <typeparamref name="T"/> has no fixed bit width — that is, whether
    /// <see cref="Size"/> and <see cref="Log2Size"/> are meaningless for it.
    /// </summary>
    /// <remarks>
    /// <see cref="BigInteger"/> is the only <see cref="IBinaryInteger{TSelf}"/> in the base class library for which this
    /// is <see langword="true"/>; every fixed-width type (through <see cref="Int128"/> and <see cref="UInt128"/>) reports
    /// <see langword="false"/>. Because every such type is a value type, a closed generic method is JIT-compiled once
    /// per instantiation rather than shared, so <c>typeof(T) == typeof(BigInteger)</c> is a compile-time constant at
    /// each call site: the branch it guards is eliminated entirely for every fixed-width instantiation, costing nothing
    /// on that hot path, while still evaluating correctly should a future reference-type <see cref="IBinaryInteger{TSelf}"/>
    /// ever share canonical generic code.
    /// </remarks>
    public static bool IsUnbounded {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => (typeof(T) == typeof(BigInteger));
    }
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
    /// <summary>Throws when <typeparamref name="T"/> has no fixed bit width, for an operation whose result requires one.</summary>
    /// <param name="operationName">The name of the operation being guarded, reported in the exception message.</param>
    /// <exception cref="NotSupportedException"><typeparamref name="T"/> is <see cref="BigInteger"/>.</exception>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void ThrowIfUnbounded(string operationName) {
        if (IsUnbounded) {
            throw new NotSupportedException(message: $"{operationName} requires a fixed bit width and does not support {nameof(BigInteger)}.");
        }
    }
}
