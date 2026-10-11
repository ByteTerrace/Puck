namespace Puck.Maths;

/// <summary>The thirty-wheel sieve's constant tables, presieve patterns and packet helpers, shared by the interval sieve and
/// the prime counter's semiprime stream.</summary>
/// <remarks>Tables expose read-only views, and the pattern accessor checks its group index before reading the table.
/// The laws <c>prime-exploration.wheel-tables-match-their-derivation</c> and
/// <c>prime-exploration.presieve-jacobsthal-run-bound</c> prove the tables against their derivations.</remarks>
public static partial class PrimeSieveKernels {
    /// <summary>Gets the byte mask that clears one product's bit for every prime channel and multiplier phase.</summary>
    /// <remarks>
    /// <para>Row <c>primeChannel</c>, an index into <see cref="PrimeWheel30.Residues"/>, holds, at each ascending
    /// multiplier phase <c>source</c>, the complement of the
    /// numeric bit <see cref="PrimeWheel30.TargetBit(int, int)"/> of the product's residue, at index
    /// <c>primeChannel · ChannelCount + source</c>. The values are written out as constant data, so a read costs one
    /// load from a constant address and no caller can overwrite them; the law
    /// <c>prime-exploration.wheel-tables-match-their-derivation</c> proves they equal the derivation.</para>
    /// </remarks>
    public static ReadOnlySpan<byte> ClearMasks => [
        0xFE, 0xFD, 0xFB, 0xF7, 0xEF, 0xDF, 0xBF, 0x7F,
        0xFD, 0xDF, 0xEF, 0xFE, 0x7F, 0xF7, 0xFB, 0xBF,
        0xDF, 0xF7, 0x7F, 0xFD, 0xBF, 0xFE, 0xEF, 0xFB,
        0xF7, 0xFE, 0xBF, 0xDF, 0xFB, 0xFD, 0x7F, 0xEF,
        0xFB, 0xEF, 0xFE, 0xBF, 0xFD, 0x7F, 0xF7, 0xDF,
        0xEF, 0x7F, 0xFD, 0xFB, 0xDF, 0xBF, 0xFE, 0xF7,
        0x7F, 0xBF, 0xDF, 0xEF, 0xF7, 0xFB, 0xFD, 0xFE,
        0xBF, 0xFB, 0xF7, 0x7F, 0xFE, 0xEF, 0xDF, 0xFD,
    ];

    /// <summary>Holds, for each residue modulo thirty, the number of wheel units at or below it.</summary>
    public static class WheelUnitsThrough {
        /// <summary>Gets, for every remainder from zero through twenty-nine, the number of units of thirty at or below it.</summary>
        /// <remarks>The values are written out as constant data, so a read costs one load from a constant address and no
        /// caller can overwrite them; the law <c>prime-exploration.wheel-tables-match-their-derivation</c> proves each
        /// equals the population count of <see cref="PrimeWheel30.PrefixMask(int)"/>.</remarks>
        public static ReadOnlySpan<byte> Counts => [
            0, 1, 1, 1, 1, 1, 1, 2, 2, 2, 2, 3, 3, 4, 4,
            4, 4, 5, 5, 6, 6, 6, 6, 7, 7, 7, 7, 7, 7, 8,
        ];
    }
}
