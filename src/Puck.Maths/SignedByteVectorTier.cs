namespace Puck.Maths;

/// <summary>Identifies one rung of the signed 8-bit vector dot product acceleration ladder.</summary>
internal enum SignedByteVectorTier {
    /// <summary>The element-at-a-time loop over scalar products, available on every machine.</summary>
    Scalar = 0,
    /// <summary>The 128-bit SIMD lane execution.</summary>
    Vector128 = 1,
    /// <summary>The 256-bit SIMD lane execution.</summary>
    Vector256 = 2,
    /// <summary>The 512-bit SIMD lane execution.</summary>
    Vector512 = 3,
}
