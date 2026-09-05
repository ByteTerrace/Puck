using System.Runtime.CompilerServices;

namespace Puck.Maths;

public readonly partial record struct FixedQ4816 {
    internal const int SinCosFractionBitCount = 60;
    internal const long SinCosQuarterTurnQ64 = (1L << 62);
    internal const long SinCosTwoPiQ60 = 7244019458077122842L; // round(2π · 2^60)
    internal const ulong SinCosInvTwoPiQ96High = 683565275UL;
    internal const ulong SinCosInvTwoPiQ96Low = 10633286012715521524UL;
    internal static UInt128 SinCosInvTwoPiQ96 {
        [MethodImpl(methodImplOptions: MethodImplOptions.AggressiveInlining)]
        get => ((((UInt128)SinCosInvTwoPiQ96High) << 64) | SinCosInvTwoPiQ96Low);
    }

    // Taylor coefficients for |residual| ≤ π/256: sine through degree five, cosine through degree four.
    internal const long SinPolyC1Q60 = -192153584101141163L;
    internal const long SinPolyC2Q60 = 9607679205057058L;
    internal const long CosPolyC1Q60 = -576460752303423488L;
    internal const long CosPolyC2Q60 = 48038396025285291L;

    // round(sin(i·π/128)·2^60), i = 0..64. Cosine reads the same table at 64 − i.
    // scalar.trigonometry-constants checks every entry against a Machin-derived interval series.
    internal static ReadOnlySpan<long> SinCosTableQ60 => [
        0L, 28294110113536504L, 56571176913125535L, 84814167351074653L,
        113006068906017470L, 141129899830620387L, 169168719380752196L, 197105638019954891L,
        224923827593068887L, 252606531462884448L, 280137074603713366L, 307498873645800920L,
        334675446864527722L, 361650424108384337L, 388407556659738432L, 414930727022454700L,
        441203958630471860L, 467211425471488651L, 492937461619961867L, 518366570673674118L,
        543483435088187080L, 568272925403557503L, 592720109357758155L, 616810260881314114L,
        640528868967736374L, 663861646414409556L, 686794538428668529L, 709313731093879966L,
        731405659690429196L, 753057016866600074L, 774254760654426065L, 794986122325684078L,
        815238614083298888L, 835000036583525154L, 854258486284375919L, 873002362615871209L,
        891220374967787610L, 908901549490699688L, 926035235706216538L, 942611112922431727L,
        958619196450722178L, 974049843620151246L, 988893759585853128L, 1003142002927899856L,
        1016785991037278313L, 1029817505285732987L, 1042228695976360312L, 1054012087071972566L,
        1065160580698383124L, 1075667461419900464L, 1085526400284455520L, 1094731458635925751L,
        1103277091691359535L, 1111158151880946079L, 1118369891948718997L, 1124907967812125795L,
        1130768441178740757L, 1135947781918545051L, 1140442870190345041L, 1144250998321047972L,
        1147369872436662991L, 1149797613844045067L, 1151532760162549490L, 1152574266204915294L,
        1152921504606846976L,
    ];

    /// <summary>Computes the cosine of <paramref name="angle"/>, given in fixed-point radians.</summary>
    /// <param name="angle">The angle in radians.</param>
    /// <returns>The cosine, in <c>[−1, 1]</c>, exactly the cosine component of <see cref="SinCos"/>.</returns>
    /// <remarks>Reconstructs only the requested component. Prefer <see cref="SinCos"/> when both are needed.</remarks>
    public static FixedQ4816 Cos(FixedQ4816 angle) =>
        new(Value: SinCosComponent(fractionalTurns: ReduceSinCosAngle(angle: angle.Value, fractionBitCount: FractionBitCount), cosine: true));

    /// <summary>Computes the sine of <paramref name="angle"/>, given in fixed-point radians.</summary>
    /// <param name="angle">The angle in radians.</param>
    /// <returns>The sine, in <c>[−1, 1]</c>, exactly the sine component of <see cref="SinCos"/>.</returns>
    /// <remarks>Reconstructs only the requested component. Prefer <see cref="SinCos"/> when both are needed.</remarks>
    public static FixedQ4816 Sin(FixedQ4816 angle) =>
        new(Value: SinCosComponent(fractionalTurns: ReduceSinCosAngle(angle: angle.Value, fractionBitCount: FractionBitCount), cosine: false));

    /// <summary>Computes the sine and cosine of <paramref name="angle"/> in one pass.</summary>
    /// <param name="angle">The angle in radians; any representable value is accepted.</param>
    /// <returns>The pair <c>(Sin, Cos)</c>, each in <c>[−1, 1]</c>.</returns>
    /// <remarks>Integer-only Q96 reciprocal reduction followed by a Q60 quarter-wave table and a small Taylor
    /// correction. Absolute error is bounded by 0.50000001 raw Q16 ULP across the full carrier; this is an error
    /// envelope, not a guarantee of correctly rounded output. Sine is exactly odd and cosine exactly even.</remarks>
    public static (FixedQ4816 Sin, FixedQ4816 Cos) SinCos(FixedQ4816 angle) =>
        SinCosFromTurns(fractionalTurns: ReduceSinCosAngle(angle: angle.Value, fractionBitCount: FractionBitCount));

    /// <summary>Computes sine and cosine directly from a binary fraction of one turn.</summary>
    /// <param name="fractionalTurns">The phase modulo one turn, scaled by 2^64. Zero is zero turns and
    /// <c>1UL &lt;&lt; 62</c> is one quarter turn; unsigned wrapping implements whole-turn periodicity.</param>
    /// <returns>The pair <c>(Sin, Cos)</c>, each in <c>[−1, 1]</c>, with exact cardinal directions.</returns>
    /// <remarks>Avoids conversion through rounded radians. Uses the same integer Q60 kernel as <see cref="SinCos"/>
    /// and the same 0.50000001 raw Q16 ULP error envelope.</remarks>
    public static (FixedQ4816 Sin, FixedQ4816 Cos) SinCosTurns(ulong fractionalTurns) =>
        SinCosFromTurns(fractionalTurns: unchecked((long)fractionalTurns));

    /// <summary>Evaluates the exact half of a Q16 angle, including its low raw bit.</summary>
    internal static (FixedQ4816 Sin, FixedQ4816 Cos) SinCosHalfAngle(FixedQ4816 angle) =>
        SinCosFromTurns(fractionalTurns: ReduceSinCosAngle(angle: angle.Value, fractionBitCount: (FractionBitCount + 1)));

    /// <summary>Evaluates an angle carried at Q32 without first rounding it to Q16.</summary>
    internal static (FixedQ4816 Sin, FixedQ4816 Cos) SinCosQ32(long angleQ32) =>
        SinCosFromTurns(fractionalTurns: ReduceSinCosAngle(angle: angleQ32, fractionBitCount: (2 * FractionBitCount)));

    /// <summary>Evaluates a nonnegative Q16 angle over the full unsigned raw range.</summary>
    internal static (FixedQ4816 Sin, FixedQ4816 Cos) SinCosRaw(ulong rawAngle) =>
        SinCosFromTurns(fractionalTurns: unchecked((long)((ulong)(unchecked(((UInt128)rawAngle) * SinCosInvTwoPiQ96) >> 48))));

    // C = round(2^96/2π). Only product bits [32+f, 95+f] are needed; f is 16, 17 or 32, so wrapping the
    // UInt128 product discards no contributing bit. Its modulo is exact; the irrational reciprocal is approximate.
    // At the unsigned Q16 maximum the reciprocal error contributes < 2π/2^33 raw ULP. Magnitude-first reduction
    // preserves the exact negative-angle symmetry, including well-defined evaluation of the signed minimum.
    [MethodImpl(methodImplOptions: MethodImplOptions.AggressiveInlining)]
    private static long ReduceSinCosAngle(long angle, int fractionBitCount) {
        var sign = (angle >> 63);
        var magnitude = unchecked((ulong)((angle ^ sign) - sign));
        var turns = unchecked((long)((ulong)(unchecked(((UInt128)magnitude) * SinCosInvTwoPiQ96) >> (32 + fractionBitCount))));

        return unchecked(((turns ^ sign) - sign));
    }

    private static (FixedQ4816 Sin, FixedQ4816 Cos) SinCosFromTurns(long fractionalTurns) {
        var (sin, cos) = SinCosCore(fractionalTurns: fractionalTurns);

        return (new(Value: NarrowSinCosQ60(value: sin)), new(Value: NarrowSinCosQ60(value: cos)));
    }

    // The same reduction and reconstruction arithmetic as the pair, with two reconstruction multiplies instead
    // of four. Do not implement cosine by offsetting the phase here: independently rounded residuals could differ
    // at the last Q60 bit, making projection equality depend on which side of a Q16 midpoint that bit falls.
    [MethodImpl(methodImplOptions: MethodImplOptions.AggressiveInlining)]
    private static long SinCosComponent(long fractionalTurns, bool cosine) {
        var quadrant = SinCosResidual(fractionalTurns: fractionalTurns, index: out var index, sin: out var sin, cos: out var cos);
        var value = (cosine
            ? (BigMulShift60(x: SinCosTableQ60[64 - index], y: cos) - BigMulShift60(x: SinCosTableQ60[index], y: sin))
            : (BigMulShift60(x: SinCosTableQ60[index], y: cos) + BigMulShift60(x: SinCosTableQ60[64 - index], y: sin)));
        var negative = (((quadrant + (cosine ? 1 : 0)) & 2) != 0);
        var raw = NarrowSinCosQ60(value: value);

        return (negative ? -raw : raw);
    }

    // Full signed Q60 results: the Gaussian sampler multiplies by its radius before narrowing, so the shared core
    // must retain its guard precision. The table's complementary indices exploit sine/cosine octant symmetry.
    internal static (long SinQ60, long CosQ60) SinCosCore(long fractionalTurns) {
        var quadrant = SinCosResidual(fractionalTurns: fractionalTurns, index: out var index, sin: out var sin, cos: out var cos);
        var tableSin = SinCosTableQ60[index];
        var tableCos = SinCosTableQ60[64 - index];
        var reconstructedSin = (BigMulShift60(x: tableSin, y: cos) + BigMulShift60(x: tableCos, y: sin));
        var reconstructedCos = (BigMulShift60(x: tableCos, y: cos) - BigMulShift60(x: tableSin, y: sin));

        return (
            (((quadrant & 2) != 0) ? -reconstructedSin : reconstructedSin),
            ((((quadrant + 1) & 2) != 0) ? -reconstructedCos : reconstructedCos)
        );
    }

    // One quarter wave, sampled at i/256 turns. Nearest-node reduction bounds |r| by π/256. The omitted terms
    // are at most |r|^7/7! and |r|^6/6!, together below 5e-15. Table/coefficient rounding and the fixed-width
    // products contribute less than 1e-16 more. Thus the core error is < 6e-15, or < 4e-10 raw Q16 ULP;
    // adding the full unsigned Q96 reduction error and the closing half-ULP stays below 0.50000001 ULP.
    // In particular rounding cannot escape ±One, so no output clamp is needed.
    [MethodImpl(methodImplOptions: MethodImplOptions.AggressiveInlining)]
    private static int SinCosResidual(long fractionalTurns, out int index, out long sin, out long cos) {
        var phase = unchecked((ulong)fractionalTurns);
        var quadrant = ((int)(phase >> 62));
        var offset = (phase & (((ulong)SinCosQuarterTurnQ64) - 1UL));
        var position = (((quadrant & 1) != 0) ? (((ulong)SinCosQuarterTurnQ64) - offset) : offset);
        index = ((int)((position + (1UL << 55)) >> 56));
        var residual = (((long)position) - (((long)index) << 56));
        var x = Math.BigMul(a: residual, b: SinCosTwoPiQ60, low: out _);
        var u = BigMulShift60(x: x, y: x);
        sin = (x + BigMulShift60(x: BigMulShift60(x: x, y: u), y: (SinPolyC1Q60 + BigMulShift60(x: u, y: SinPolyC2Q60))));
        cos = ((1L << SinCosFractionBitCount) + BigMulShift60(x: u, y: (CosPolyC1Q60 + BigMulShift60(x: u, y: CosPolyC2Q60))));

        return quadrant;
    }

    // Signed (x·y) >> 60. The bounded residuals and unit-scale coefficients fit the signed product.
    [MethodImpl(methodImplOptions: MethodImplOptions.AggressiveInlining)]
    private static long BigMulShift60(long x, long y) {
        // The explicit halves keep .NET 10's inliner from leaving Int128 shift/multiply helper calls in the singles.
        var high = Math.BigMul(a: x, b: y, low: out var low);

        return ((high << 4) | ((long)(((ulong)low) >> 60)));
    }

    // Add half minus one plus the retained parity: below half never carries, above half always carries, and a
    // tie carries exactly when the retained integer is odd. Magnitude + bias fits ulong even for |long.MinValue|.
    [MethodImpl(methodImplOptions: MethodImplOptions.AggressiveInlining)]
    private static long NarrowSinCosQ60(long value) {
        const int Shift = (SinCosFractionBitCount - FractionBitCount);
        var sign = (value >> 63);
        var magnitude = unchecked((ulong)((value ^ sign) - sign));
        var rounded = ((long)((magnitude + ((1UL << (Shift - 1)) - 1UL) + ((magnitude >> Shift) & 1UL)) >> Shift));

        return ((rounded ^ sign) - sign);
    }
}
