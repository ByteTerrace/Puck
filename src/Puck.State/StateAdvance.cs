using System.Numerics;
using System.Text.Json.Serialization;
using Puck.Maths;

namespace Puck.State;

/// <summary>
/// A <see cref="StateRow"/>'s continuous accumulation trait: the row's stored cell is a base value, and the
/// read value advances with elapsed ticks at an exact per-tick rational rate from the tick it was last explicitly
/// set (<see cref="EpochTick"/>). Used for regen, fractional accumulation, a day/night clock — anything that should
/// move on its own between observations.
/// </summary>
/// <remarks>
/// <para>Nothing per-tick materializes or journals: the computed value (<see cref="ComputeCurrentValue"/>) is a pure
/// function of the base, <see cref="EpochTick"/>, the rate, and the tick asked about. An explicit write —
/// <c>UpsertStateRow</c> re-authoring the row, or a slot-cell <c>UpsertStateCell</c> — rebases: the written value
/// becomes the new base and <see cref="EpochTick"/> becomes the tick the write applied at.</para>
/// <para><see cref="ComputeCurrentValue"/> is applied only by <see cref="StateReader"/>'s central known-cell
/// computation, so both its name and compiled-handle entrances, every aggregate, read-back, rule gate, HUD binding,
/// and arithmetic write resolve an advancing row through the same code. An
/// <c>add</c> against an advancing row adds to what a reader sees, never to the stored base.</para>
/// <para>A rule's own <c>compareState</c> reads an advancing row's live computed value like any other row. A rule's
/// <c>setState</c>/<c>addState</c> effect against an advancing row's slot cell is an explicit write, so it rebases —
/// a rule that writes the same row every tick overrides this trait's accumulation with its own.</para>
/// <para><see cref="RateNumerator"/>/<see cref="RateDenominator"/> is an exact fraction of the row's own displayed
/// unit per tick — the unit its <c>value</c>, <see cref="StateRow.Min"/>, and <see cref="StateRow.Max"/>
/// are authored in, not raw storage. For <see cref="CellKind.Int"/> the two coincide. For
/// <see cref="CellKind.Fixed"/> they do not: <see cref="ComputeCurrentValue"/> scales the numerator by
/// <c>2^FixedQ4816.FractionBitCount</c> before allocating via <see cref="Puck.Maths.DiscreteMeasure"/>'s exact
/// rational allocation, so a rate accumulates without rounding drift.</para>
/// <para>The rate may be negative (decay/drain); a negative rate is the exact mirror of its positive twin, not a
/// floor of the signed affine function — <see cref="Puck.Maths.DiscreteMeasure"/> accepts only a non-negative rate,
/// so this type floors the magnitude and negates it. Decay and regen at equal magnitude stay symmetric.</para>
/// <para>A declared <see cref="StateRow.Min"/>/<see cref="StateRow.Max"/> or
/// <see cref="StateRow.NonNegative"/> floor clamps the computed value on every read; it never rewrites the
/// stored base or epoch. A value that must wrap is a <see cref="StateCycle"/>, not an advance.</para>
/// </remarks>
/// <param name="RateNumerator">The per-tick rate's signed numerator, in the row's own displayed unit (see this
/// type's remarks). Negative accumulates downward (decay); zero is declared but inert.</param>
/// <param name="RateDenominator">The per-tick rate's denominator. Refused at zero or below.</param>
/// <param name="EpochTick">The server tick the rate starts accumulating from — the tick the row's base value was
/// last explicitly set, or the loaded document's own authored value for a row never set since. A negative value is
/// refused; in practice this can only be violated by an authored boot document, since every live write rebases to
/// the applying tick before validation sees it.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record StateAdvance(long RateNumerator, long RateDenominator, long EpochTick = 0) {
    /// <summary>Computes <paramref name="row"/>'s current value: <paramref name="baseValue"/> plus the exact
    /// accumulation between <see cref="EpochTick"/> and <paramref name="currentTick"/>, clamped into the row's
    /// declared envelope. A <paramref name="currentTick"/> preceding <see cref="EpochTick"/> reads as zero elapsed
    /// rather than a negative accumulation.</summary>
    /// <param name="row">The carrying row (for its <see cref="CellKind"/> and envelope).</param>
    /// <param name="baseValue">The row's stored raw cell value.</param>
    /// <param name="currentTick">The tick to compute the value as of.</param>
    /// <returns>The computed, envelope-clamped raw value.</returns>
    public long ComputeCurrentValue(StateRow row, long baseValue, ulong currentTick) {
        ArgumentNullException.ThrowIfNull(argument: row);

        var delta = 0L;

        if (
            (RateNumerator != 0) &&
            (currentTick > ((ulong)Math.Max(val1: EpochTick, val2: 0L)))
        ) {
            var scale = ((row.Kind == CellKind.Fixed)
                ? (1L << FixedQ4816.FractionBitCount)
                : 1L
            );
            var elapsed = (currentTick - ((ulong)Math.Max(val1: EpochTick, val2: 0L)));

            if (TryAccumulate(elapsed: elapsed, scale: scale, magnitude: out var magnitude)) {
                delta = ((RateNumerator < 0) ? -magnitude : magnitude);
            } else {
                // A magnitude past long.MaxValue can still land inside long once the base is added (a drain from a
                // positive base), so the sum is formed exactly and saturated as a whole rather than the magnitude alone.
                var wide = AccumulatedMagnitude(elapsed: elapsed, scale: scale);
                var exact = (baseValue + ((RateNumerator < 0) ? -wide : wide));

                return row.ClampToEnvelope(value: ((exact > long.MaxValue) ? long.MaxValue : ((exact < long.MinValue) ? long.MinValue : ((long)exact))));
            }
        }

        // A saturating add into long and a clamp after is the same answer as clamping the exact sum: every envelope
        // bound is itself a long, so saturation can only move a value that is already outside every bound, and it
        // moves it to the same side. Stating the envelope once (StateRow.ClampToEnvelope) is what keeps this read
        // clamp and the rule-effect write's "could this move the cell" test from drifting apart.
        var raw = ((delta >= 0L)
            ? ((baseValue > (long.MaxValue - delta)) ? long.MaxValue : (baseValue + delta))
            : ((baseValue < (long.MinValue - delta)) ? long.MinValue : (baseValue + delta)));

        return row.ClampToEnvelope(value: raw);
    }

    // |rate| · scale allocated over the elapsed ticks, as ⌊elapsed · |rate| · scale / denominator⌋ — the exact
    // rational allocation of DiscreteMeasure. The compiled signed-64-bit form answers every read the tick can produce
    // in long arithmetic; the exact form remains behind it for a rate or an elapsed span the bounded representation
    // cannot hold, so the two never disagree on a value, only on cost.
    private bool TryAccumulate(ulong elapsed, long scale, out long magnitude) {
        var compiled = CompiledFor(scale: scale);

        if (
            compiled.IsValid &&
            (elapsed <= long.MaxValue) &&
            compiled.TryAmountBetween(
                amount: out magnitude,
                end: ((long)elapsed),
                start: 0L
            )
        ) {
            return true;
        }

        magnitude = 0L;

        return false;
    }
    private BigInteger AccumulatedMagnitude(ulong elapsed, long scale) =>
        (TryAccumulate(elapsed: elapsed, scale: scale, magnitude: out var magnitude)
            ? magnitude
            : ExactMeasure(scale: scale).AmountBetween(
                end: elapsed,
                start: BigInteger.Zero
            ));
    private CompiledDiscreteMeasure64 CompiledFor(long scale) {
        var cache = m_compiled;

        // The rate fields are the cache key, so a `with` copy that changes the rate recompiles on its first read
        // rather than answering from the copied cache; the holder is one immutable reference, so a concurrent reader
        // sees either the old cache or the new one. Each scale compiles on its first read only.
        if ((cache is null) || (cache.RateNumerator != RateNumerator) || (cache.RateDenominator != RateDenominator)) {
            cache = new CompiledMeasureCache(RateNumerator: RateNumerator, RateDenominator: RateDenominator);
            m_compiled = cache;
        }

        return cache.For(advance: this, scale: scale);
    }
    private DiscreteMeasure ExactMeasure(long scale) =>
        DiscreteMeasure.Rational(
            denominator: RateDenominator,
            numerator: (BigInteger.Abs(value: ((BigInteger)RateNumerator)) * scale)
        );

    /// <summary>Tests equality over the authored members alone; the compiled-measure cache is runtime acceleration
    /// and never part of the record's identity.</summary>
    public bool Equals(StateAdvance? other) =>
        ((other is not null) && (RateNumerator == other.RateNumerator) && (RateDenominator == other.RateDenominator) && (EpochTick == other.EpochTick));
    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(value1: RateNumerator, value2: RateDenominator, value3: EpochTick);

    // Runtime acceleration beside the immutable record, excluded from its equality above. Invalid compiled values are
    // cached too, so an exact-only rate does not retry compilation on every read.
    private CompiledMeasureCache? m_compiled;

    private sealed class CompiledMeasureCache(long RateNumerator, long RateDenominator) {
        private CompiledDiscreteMeasure64? m_fixed;
        private CompiledDiscreteMeasure64? m_integer;

        public long RateNumerator { get; } = RateNumerator;
        public long RateDenominator { get; } = RateDenominator;

        public CompiledDiscreteMeasure64 For(StateAdvance advance, long scale) {
            if (scale == FixedQ4816.One.Value) {
                return (m_fixed ??= Compile(advance: advance, scale: scale));
            }

            return (m_integer ??= Compile(advance: advance, scale: scale));
        }
        private static CompiledDiscreteMeasure64 Compile(StateAdvance advance, long scale) {
            _ = advance.ExactMeasure(scale: scale).TryCompileInt64(compiled: out var compiled);

            return compiled;
        }
    }
}
