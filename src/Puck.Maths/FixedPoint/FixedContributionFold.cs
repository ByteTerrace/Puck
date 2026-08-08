namespace Puck.Maths;

/// <summary>Folds two raw fixed-point contribution sums around a fixed-point baseline.</summary>
/// <remarks>
/// The arithmetic contract is
/// <c>quantize(clamp(clamp(baseline + poolDelta, baseline - radius, baseline + radius) + outsidePoolDelta, minimum, maximum))</c>,
/// with the inner clamp omitted when no radius is supplied and the terminal quantization omitted when no threshold is
/// supplied. A null radius means no pool; zero is a valid zero-width pool. A null threshold leaves the ranged value
/// continuous; otherwise values at or above the threshold map to the maximum and values below it map to the minimum.
/// <para>
/// Contributions are accumulated in raw <see cref="long"/> sums and clamped ONCE after accumulation, never through a
/// saturating per-add. Saturating addition is commutative but not associative: near a boundary,
/// <c>sat(sat(a + b) + c)</c> can differ from <c>sat(a + sat(b + c))</c>, making an unordered contribution set depend
/// on iteration order. The two completed sums enter this member as raw Q48.16 units. Every addition and every pool
/// bound is widened to <see cref="Int128"/>, so all combinations of the public carrier and delta domains are total.
/// </para>
/// <para>
/// The exact raw-sum boundary is useful when callers form either supplied delta. If every term has magnitude at most
/// <c>One = 2^16</c>, <c>2^47 - 1</c> terms are safe independent of sign: the all-positive sum is
/// <c>2^63 - 2^16</c>, leaving exactly <c>2^16 - 1</c> positive raw units of margin, while <c>2^47</c> positive terms
/// total <c>2^63</c> and do not fit. The negative carrier has one extra raw magnitude, so <c>2^47</c> negative terms
/// land exactly on <see cref="long.MinValue"/> and the next term underflows. The baseline consumes none of that
/// accumulator margin because this member adds it only after widening. Across the member's complete public domain,
/// even the widest unpooled three-term intermediate has magnitude at most <c>3 * 2^63</c>, far inside
/// <see cref="Int128"/>.
/// </para>
/// <para>
/// This member performs no scale multiply and no rounding. Callers supply raw sums whose individual contribution
/// components have already been rounded exactly once. Any multiply producing such a component belongs before the
/// accumulation and uses <see cref="FixedQ4816"/>'s fixed-point multiply, whose pin is round-to-nearest with ties to
/// even; inserting a scale mapping or another rounding inside this fold would change that one-rounding contract.
/// </para>
/// <para>
/// For the binary specialization <c>minimum = 0</c>, <c>maximum = One</c>, a pooled radius <c>c</c> preserves either
/// base bit <c>h in {0, One}</c> at threshold <c>T</c> exactly when
/// <c>c &lt;= min(T - 1, One - T)</c> in raw units. From zero, the greatest pooled value must remain strictly below
/// <c>T</c>, giving <c>c &lt;= T - 1</c>; from one, the least pooled value must remain at or above <c>T</c>, giving
/// <c>c &lt;= One - T</c>. The asymmetry is the terminal <c>&gt;=</c>. At half threshold this becomes the familiar
/// below-half rule, but that rule is not general: at <c>h = One</c>, <c>T = 0.75</c>, <c>c = 0.5 &lt; T</c>, and
/// <c>poolDelta = -0.5</c>, the ranged value is <c>0.5</c> and quantizes to zero, flipping the base bit.
/// </para>
/// </remarks>
public static class FixedContributionFold {
    /// <summary>Evaluates the fixed-point contribution fold.</summary>
    /// <param name="baseline">The value around which a present pool is centered.</param>
    /// <param name="poolDeltaRaw">The completed raw Q48.16 contribution sum subject to the optional pool.</param>
    /// <param name="outsidePoolDeltaRaw">The completed raw Q48.16 contribution sum added after the pool.</param>
    /// <param name="poolRadius">The optional non-negative pool radius. Null means no pool; zero is a valid radius.</param>
    /// <param name="minimum">The inclusive final range minimum.</param>
    /// <param name="maximum">The inclusive final range maximum.</param>
    /// <param name="threshold">The optional terminal threshold, which must lie in the final range.</param>
    /// <param name="poolClamped"><see langword="true"/> exactly when a present pool changed the pooled intermediate;
    /// final range clamping does not affect it.</param>
    /// <returns>The continuous ranged value, or <paramref name="minimum"/>/<paramref name="maximum"/> after terminal
    /// quantization when <paramref name="threshold"/> is present.</returns>
    /// <exception cref="ArgumentException"><paramref name="minimum"/> is greater than <paramref name="maximum"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="poolRadius"/> is negative, or
    /// <paramref name="threshold"/> lies outside the inclusive final range.</exception>
    public static FixedQ4816 Evaluate(
        FixedQ4816 baseline,
        long poolDeltaRaw,
        long outsidePoolDeltaRaw,
        FixedQ4816? poolRadius,
        FixedQ4816 minimum,
        FixedQ4816 maximum,
        FixedQ4816? threshold,
        out bool poolClamped
    ) {
        if (minimum > maximum) {
            throw new ArgumentException(message: "The minimum cannot be greater than the maximum.", paramName: nameof(minimum));
        }

        if ((poolRadius is { } radius) && (radius < FixedQ4816.Zero)) {
            throw new ArgumentOutOfRangeException(paramName: nameof(poolRadius), message: "The pool radius cannot be negative.");
        }

        if ((threshold is { } crossing) && ((crossing < minimum) || (crossing > maximum))) {
            throw new ArgumentOutOfRangeException(paramName: nameof(threshold), message: "The threshold must lie within the inclusive range.");
        }

        var rawPooled = ((Int128)baseline.Value + poolDeltaRaw);
        var pooled = ((poolRadius is { } presentRadius)
            ? Clamp(value: rawPooled, minimum: ((Int128)baseline.Value - presentRadius.Value), maximum: ((Int128)baseline.Value + presentRadius.Value))
            : rawPooled);

        poolClamped = (pooled != rawPooled);

        var ranged = Clamp(value: (pooled + outsidePoolDeltaRaw), minimum: minimum.Value, maximum: maximum.Value);
        var rangedRaw = ((long)ranged);

        return ((threshold is { } presentThreshold)
            ? ((rangedRaw >= presentThreshold.Value) ? maximum : minimum)
            : FixedQ4816.FromRawBits(value: rangedRaw));
    }

    private static Int128 Clamp(Int128 value, Int128 minimum, Int128 maximum) =>
        ((value < minimum) ? minimum : ((value > maximum) ? maximum : value));
}
