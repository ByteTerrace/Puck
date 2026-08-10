namespace Puck.Maths.Tests;

/// <summary>Claim bodies for the <c>mixed-scale</c> family — <see cref="FusedArithmetic"/>'s one-rounding products over
/// operands carried at DIFFERENT fixed-point scales, the kernel an inverse mass at one scale and an impulse at another
/// need. Every agreement is against <see cref="Oracles.MixedScaleProduct"/> or
/// <see cref="Oracles.MixedScaleTripleProduct"/>, which form the whole product in arbitrary width and decide the
/// single rounding against an explicit power-of-two denominator, where the subject accumulates a
/// sign-plus-<see cref="UInt128"/> magnitude and settles the tie against a half-unit it builds itself.</summary>
internal static class MixedScaleClaims {
    // Every fraction bit count is folded onto [0, 64] — the band a 64-bit carrier's scale can honestly occupy. The
    // pathological counts (negative, int.MinValue, int.MaxValue) are exercised by their own hand-derived claim rather
    // than swept here, because an oracle built from a power-of-two denominator cannot form 2^int.MaxValue.
    private static int FoldScale(long raw) => ((int)(((ulong)raw) % 65UL));

    /// <summary>The wrapping mixed-scale product against the independent oracle, at swept operand scales.</summary>
    /// <param name="left">Lane 0 = the first factor, lane 1 = the second.</param>
    /// <param name="right">Lanes 0..2 drive the two operand fraction bit counts and the output's.</param>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? ProductVsOracle(long[] left, long[] right) {
        var a = left[0];
        var b = left[1];
        var fractionBitsA = FoldScale(raw: right[0]);
        var fractionBitsB = FoldScale(raw: right[1]);
        var fractionBitsOut = FoldScale(raw: right[2]);

        var subject = FusedArithmetic.MixedScaleProduct(a: a, fractionBitsA: fractionBitsA, b: b, fractionBitsB: fractionBitsB, fractionBitsOut: fractionBitsOut);
        var oracle = Oracles.MixedScaleProduct(a: a, fractionBitsA: fractionBitsA, b: b, fractionBitsB: fractionBitsB, fractionBitsOut: fractionBitsOut);

        return ((subject == oracle.Raw)
            ? null
            : $"mixed-scale product at ({a}@{fractionBitsA} x {b}@{fractionBitsB} -> {fractionBitsOut}): subject={subject} oracle={oracle.Raw}");
    }

    /// <summary>The checked mixed-scale product: it must refuse exactly when the correctly rounded product leaves the
    /// signed 64-bit raw, leave its output at zero when it does, and otherwise agree with the wrapping face and the
    /// oracle alike.</summary>
    /// <param name="left">Lane 0 = the first factor, lane 1 = the second.</param>
    /// <param name="right">Lanes 0..2 drive the two operand fraction bit counts and the output's.</param>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? CheckedProductMatchesRepresentability(long[] left, long[] right) {
        var a = left[0];
        var b = left[1];
        var fractionBitsA = FoldScale(raw: right[0]);
        var fractionBitsB = FoldScale(raw: right[1]);
        var fractionBitsOut = FoldScale(raw: right[2]);

        var subjectOk = FusedArithmetic.TryMixedScaleProduct(
            a: a,
            fractionBitsA: fractionBitsA,
            b: b,
            fractionBitsB: fractionBitsB,
            fractionBitsOut: fractionBitsOut,
            result: out var result
        );
        var oracle = Oracles.MixedScaleProduct(a: a, fractionBitsA: fractionBitsA, b: b, fractionBitsB: fractionBitsB, fractionBitsOut: fractionBitsOut);

        if (subjectOk != oracle.Fits) {
            return $"checked mixed-scale product outcome at ({a}@{fractionBitsA} x {b}@{fractionBitsB} -> {fractionBitsOut}): subject={subjectOk} oracle={oracle.Fits}";
        }

        // The refusal contract is "false AND the output zero" — checked against the subject directly, never merely
        // against the oracle, even when both decline.
        if (!subjectOk) {
            return ((result == 0L)
                ? null
                : $"checked mixed-scale product refused at ({a}@{fractionBitsA} x {b}@{fractionBitsB} -> {fractionBitsOut}) but left {result} behind");
        }

        return ((result == oracle.Raw)
            ? null
            : $"checked mixed-scale product at ({a}@{fractionBitsA} x {b}@{fractionBitsB} -> {fractionBitsOut}): subject={result} oracle={oracle.Raw}");
    }

    /// <summary>The three-factor mixed-scale product: it answers exactly when the exact triple product stays inside
    /// <see cref="UInt128"/> AND the rounded value fits the raw, and agrees with the oracle when it does.</summary>
    /// <param name="left">Lanes 0..2 = the three factors.</param>
    /// <param name="right">Lanes 0..3 drive the three operand fraction bit counts and the output's.</param>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? TripleProductVsOracle(long[] left, long[] right) {
        var a = left[0];
        var b = left[1];
        var c = left[2];
        var fractionBitsA = FoldScale(raw: right[0]);
        var fractionBitsB = FoldScale(raw: right[1]);
        var fractionBitsC = FoldScale(raw: right[2]);
        var fractionBitsOut = FoldScale(raw: right[3]);

        var subjectOk = FusedArithmetic.TryMixedScaleProduct(
            a: a,
            fractionBitsA: fractionBitsA,
            b: b,
            fractionBitsB: fractionBitsB,
            c: c,
            fractionBitsC: fractionBitsC,
            fractionBitsOut: fractionBitsOut,
            result: out var result
        );
        var oracle = Oracles.MixedScaleTripleProduct(
            a: a,
            fractionBitsA: fractionBitsA,
            b: b,
            fractionBitsB: fractionBitsB,
            c: c,
            fractionBitsC: fractionBitsC,
            fractionBitsOut: fractionBitsOut
        );
        var expected = (oracle.WidthFits && oracle.Fits);
        var operands = $"({a}@{fractionBitsA} x {b}@{fractionBitsB} x {c}@{fractionBitsC} -> {fractionBitsOut})";

        if (subjectOk != expected) {
            return $"triple mixed-scale product outcome at {operands}: subject={subjectOk} expected={expected} (widthFits={oracle.WidthFits}, fits={oracle.Fits})";
        }

        if (!subjectOk) {
            return ((result == 0L)
                ? null
                : $"triple mixed-scale product refused at {operands} but left {result} behind");
        }

        return ((result == ((long)oracle.Exact))
            ? null
            : $"triple mixed-scale product at {operands}: subject={result} oracle={oracle.Exact}");
    }

    /// <summary>Pins the exponent extremes the swept laws deliberately do not reach: a fraction bit count is an
    /// <see cref="int"/>, so a caller can name a combination whose exponent would wrap in <see cref="int"/> arithmetic
    /// or alias a shift count modulo 128. All five witnesses are hand-derived.
    /// <para><c>fractionBitsA = fractionBitsB = 64</c> with <c>fractionBitsOut = 0</c> shifts right by 128: the largest
    /// product two raws can form is <c>2^126</c>, a quarter of the discarded unit, so the correctly rounded result is
    /// zero and the checked face ANSWERS it rather than refusing. <c>fractionBitsOut = int.MaxValue</c> shifts left
    /// past the accumulator's width: the true value is a multiple of <c>2^64</c>, so the wrapping face is congruent at
    /// zero while the checked face refuses. <c>fractionBitsA = int.MinValue</c> makes the exponent
    /// <c>0 − int.MinValue = 2^31</c>, which only stays positive because the three counts are combined in
    /// <see cref="long"/>; computed in <see cref="int"/> it would wrap negative and turn a left shift into a
    /// rounding right shift.</para></summary>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? ExtremeScaleCountsAreCongruent() {
        const long huge = long.MinValue;

        var quarter = FusedArithmetic.MixedScaleProduct(a: huge, fractionBitsA: 64, b: huge, fractionBitsB: 64, fractionBitsOut: 0);

        if (quarter != 0L) {
            return $"a product of 2^126 shifted right by 128 rounded to {quarter}, expected 0 (a quarter of the discarded unit rounds down)";
        }

        if (!FusedArithmetic.TryMixedScaleProduct(a: huge, fractionBitsA: 64, b: huge, fractionBitsB: 64, fractionBitsOut: 0, result: out var checkedQuarter) || (checkedQuarter != 0L)) {
            return $"the checked face refused a representable zero at a 128-bit right shift, or answered {checkedQuarter}";
        }

        var wrapped = FusedArithmetic.MixedScaleProduct(a: huge, fractionBitsA: 0, b: huge, fractionBitsB: 0, fractionBitsOut: int.MaxValue);

        if (wrapped != 0L) {
            return $"a left shift past the carrier answered {wrapped}, expected the congruent 0";
        }

        if (FusedArithmetic.TryMixedScaleProduct(a: huge, fractionBitsA: 0, b: huge, fractionBitsB: 0, fractionBitsOut: int.MaxValue, result: out var refused) || (refused != 0L)) {
            return $"the checked face answered {refused} at a left shift past the carrier instead of refusing";
        }

        // Both operand counts at int.MinValue drive the exponent to +2^32, a left shift far past the accumulator,
        // ONLY because the three counts are combined in long: subtracting them in int wraps to exactly zero
        // (0 − int.MinValue is int.MinValue, and int.MinValue − int.MinValue is 0), which would answer the bare
        // product 9 instead. This is the discriminating witness — a single int.MinValue count does NOT discriminate,
        // because both readings then land on a shift the accumulator answers as zero anyway.
        var wrappingExponent = FusedArithmetic.MixedScaleProduct(a: 3L, fractionBitsA: int.MinValue, b: 3L, fractionBitsB: int.MinValue, fractionBitsOut: 0);

        if (wrappingExponent != 0L) {
            return $"an exponent of 2^32 answered {wrappingExponent}, expected the congruent 0 — the counts are not being combined in long";
        }

        return (!FusedArithmetic.TryMixedScaleProduct(a: 3L, fractionBitsA: int.MinValue, b: 3L, fractionBitsB: int.MinValue, fractionBitsOut: 0, result: out var wrappingChecked) && (wrappingChecked == 0L)
            ? null
            : $"the checked face answered {wrappingChecked} at an exponent of 2^32 instead of refusing");
    }
}
