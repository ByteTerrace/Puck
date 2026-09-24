using System.Numerics;

namespace Puck.Maths;

/// <summary>A signed fixed-point carrier over a 64-bit two's-complement raw, described by where its binary point sits.</summary>
/// <typeparam name="TSelf">The carrier.</typeparam>
/// <remarks>
/// Every operation whose control flow is independent of the binary point is written once over this interface in
/// <see cref="SignedFixedPointArithmetic"/> and <see cref="FixedPointText"/>, and each carrier's public member forwards
/// to it. The carriers are structs, so each forwarder compiles to its own specialization with the fraction width
/// folded as a constant.
/// </remarks>
internal interface ISignedFixedPointFormat<TSelf> : INumberBase<TSelf> where TSelf : struct, ISignedFixedPointFormat<TSelf> {
    /// <summary>Gets the number of fractional bits in the raw.</summary>
    static abstract int FractionBitCount { get; }
    /// <summary>Gets the raw storage: the represented real number scaled by two to the <see cref="FractionBitCount"/>.</summary>
    long Value { get; }

    /// <summary>Wraps a raw storage bit pattern.</summary>
    /// <param name="value">The pre-scaled raw value.</param>
    /// <returns>The carrier whose <see cref="Value"/> equals <paramref name="value"/>.</returns>
    static abstract TSelf FromRawBits(long value);
}
