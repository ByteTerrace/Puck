namespace Puck.Maths;

/// <summary>Provides the canonical minimum-weight binary fields at the widths the library accelerates.</summary>
/// <remarks>
/// Each modulus is the standard minimum-weight irreducible polynomial at its degree, so reduction folds the fewest
/// terms and completes in two passes. Swan's theorem rules out a trinomial whenever the degree is a multiple of eight,
/// which every degree here is, so a weight-five pentanomial is the floor at each — a fact about the degrees rather
/// than a preference. The degree-8 field is the one the hardware Galois-field multiply is defined over, which is why
/// the published byte-field test vectors pin it directly. None of these constants is trusted on its word: the gate
/// re-proves each irreducible at run time.
/// </remarks>
public static class BinaryFields {
    /// <summary>Gets <c>GF(2^8)</c> under <c>t^8 + t^4 + t^3 + t + 1</c>.</summary>
    public static BinaryField<byte> Degree8 => BinaryField<byte>.Create(degree: 8, reductionTail: 0x1B);
    /// <summary>Gets <c>GF(2^16)</c> under <c>t^16 + t^5 + t^3 + t + 1</c>.</summary>
    public static BinaryField<ushort> Degree16 => BinaryField<ushort>.Create(degree: 16, reductionTail: 0x2B);
    /// <summary>Gets <c>GF(2^32)</c> under <c>t^32 + t^7 + t^3 + t^2 + 1</c>.</summary>
    public static BinaryField<uint> Degree32 => BinaryField<uint>.Create(degree: 32, reductionTail: 0x8DU);
    /// <summary>Gets <c>GF(2^64)</c> under <c>t^64 + t^4 + t^3 + t + 1</c>.</summary>
    /// <remarks>The tail coinciding with the degree-8 field's is a genuine coincidence of the minimum-weight pattern at the two degrees, not a transcription error.</remarks>
    public static BinaryField<ulong> Degree64 => BinaryField<ulong>.Create(degree: 64, reductionTail: 0x1BUL);
    /// <summary>Gets <c>GF(2^128)</c> under <c>t^128 + t^7 + t^2 + t + 1</c>.</summary>
    /// <remarks>The modulus is the minimum-weight irreducible at degree 128 in the natural, unreflected domain; the near-degree-127 reciprocal polynomial exists only to make a bit-reversed wire format work, which is a problem this library does not have.</remarks>
    public static BinaryField<UInt128> Degree128 => BinaryField<UInt128>.Create(degree: 128, reductionTail: ((UInt128)0x87));
}
