namespace Puck.Maths;

/// <summary>Identifies one rung of the bulk region-scaling ladder beneath <see cref="BinaryField{T}"/>.</summary>
/// <remarks>
/// The members ascend narrowest-first, and within a width the nibble-split byte shuffle precedes the hardware
/// Galois-field affine transform. Dispatch runs in the reverse order — widest-first, affine before split — and lives
/// in <c>BinaryFieldKernels.TryMultiplyAccumulateRegionByte</c>; this type only names the rungs. Each rung is a
/// separately named kernel that queries no instruction-set support of its own, so a verifier holding the matching
/// support flag can execute any two rungs over the same region inside one process and compare them.
/// </remarks>
internal enum BinaryFieldRegionTier {
    /// <summary>The element-at-a-time loop over the scalar field multiply, available on every machine.</summary>
    Scalar = 0,
    /// <summary>The 128-bit nibble-split byte shuffle.</summary>
    Split128 = 1,
    /// <summary>The 128-bit Galois-field affine transform.</summary>
    Affine128 = 2,
    /// <summary>The 256-bit nibble-split byte shuffle.</summary>
    Split256 = 3,
    /// <summary>The 256-bit Galois-field affine transform.</summary>
    Affine256 = 4,
    /// <summary>The 512-bit nibble-split byte shuffle.</summary>
    Split512 = 5,
    /// <summary>The 512-bit Galois-field affine transform.</summary>
    Affine512 = 6,
}
