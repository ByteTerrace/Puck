using System.Numerics;
using System.Text.Json.Serialization;
using Puck.Maths;

namespace Puck.State;

/// <summary>
/// A <see cref="StateRow"/>'s continuous accumulation trait: the row's stored cell is a base value, and the
/// read value advances with elapsed engine ticks at an exact per-second rational rate from the engine tick it was
/// last explicitly set (<see cref="StateCellClock.EpochEngineTick"/>). Used for regen, fractional accumulation, a
/// day/night clock — anything that should move on its own between observations.
/// </summary>
/// <remarks>
/// <para>Nothing per-tick materializes or journals: the computed value (<see cref="ComputeCurrentValue"/>) is a pure
/// function of the base, the carrying cell's own <see cref="StateCellClock.EpochEngineTick"/>, the rate, and the
/// engine tick asked about. An explicit write — <c>UpsertStateRow</c> re-authoring the row, or an
/// <c>UpsertStateCell</c> — rebases: the written value becomes the new base and the cell's clock epoch becomes the
/// engine tick the write applied at.</para>
/// <para><see cref="ComputeCurrentValue"/> is applied only by <see cref="StateReader"/>'s central known-cell
/// computation, so both its name and compiled-handle entrances, every aggregate, read-back, rule gate, HUD binding,
/// and arithmetic write resolve an advancing row through the same code. An
/// <c>add</c> against an advancing row adds to what a reader sees, never to the stored base.</para>
/// <para>A rule's own <c>compareState</c> reads an advancing row's live computed value like any other row. A rule's
/// <c>setState</c>/<c>addState</c> effect against an advancing row's slot cell is an explicit write, so it rebases —
/// a rule that writes the same row every tick overrides this trait's accumulation with its own.</para>
/// <para><see cref="PerSecondNumerator"/>/<see cref="PerSecondDenominator"/> is an exact fraction of the row's own
/// displayed unit per second — the unit its <c>value</c>, <see cref="StateRow.Min"/>, and <see cref="StateRow.Max"/>
/// are authored in, not raw storage — evaluated against the engine's fixed 50,400-tick-per-second clock
/// (<see cref="FixedTickConversion.TicksPerSecond"/>, the same base <c>valueSeconds</c> duration authoring already
/// uses), never against the world's own <c>simulation.rateHz</c>. At a constant simulation rate this reproduces the
/// same value at every simulation-tick boundary a per-tick rational rate would have, but unlike a per-tick rate it
/// means the same thing regardless of the world's simulation rate, and a live rate change moves no epoch and skews
/// no accumulation, because engine ticks accrue at a fixed real-time pace no simulation rate can move. A world
/// authored at <c>simulation.rateHz: 0</c> never steps, so its engine tick never accrues either — this trait is
/// legal there and simply reads whatever base the last explicit write left, unmoving. For
/// <see cref="CellKind.Fixed"/>, the numerator is additionally scaled by <c>2^FixedQ4816.FractionBitCount</c> before
/// allocating via <see cref="Puck.Maths.DiscreteMeasure"/>'s exact rational allocation, so a rate accumulates without
/// rounding drift.</para>
/// <para>The rate may be negative (decay/drain); a negative rate is the exact mirror of its positive twin, not a
/// floor of the signed affine function — <see cref="Puck.Maths.DiscreteMeasure"/> accepts only a non-negative rate,
/// so this type floors the magnitude and negates it. Decay and regen at equal magnitude stay symmetric.</para>
/// <para>A declared <see cref="StateRow.Min"/>/<see cref="StateRow.Max"/> clamps the computed value on every read;
/// it never rewrites the stored base or epoch. A value that must wrap is a <see cref="StateCycle"/>, not an
/// advance.</para>
/// </remarks>
/// <param name="PerSecondNumerator">The per-second rate's signed numerator, in the row's own displayed unit (see
/// this type's remarks). Negative accumulates downward (decay); zero is declared but inert.</param>
/// <param name="PerSecondDenominator">The per-second rate's denominator. Refused at zero or below.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record StateAdvance(long PerSecondNumerator, long PerSecondDenominator) {
    /// <summary>Computes <paramref name="row"/>'s current value: <paramref name="baseValue"/> plus the exact
    /// accumulation between <paramref name="epochEngineTick"/> and <paramref name="currentEngineTick"/>, clamped
    /// into the row's declared envelope. A <paramref name="currentEngineTick"/> preceding
    /// <paramref name="epochEngineTick"/> reads as zero elapsed rather than a negative accumulation.</summary>
    /// <param name="row">The carrying row (for its <see cref="CellKind"/> and envelope).</param>
    /// <param name="baseValue">The row's stored raw cell value.</param>
    /// <param name="currentEngineTick">The engine tick (<see cref="FixedTickConversion.TicksPerSecond"/> per second)
    /// to compute the value as of — never a simulation tick, and never converted at the world's current simulation
    /// rate.</param>
    /// <param name="epochEngineTick">The carrying cell's own <see cref="StateCellClock.EpochEngineTick"/> — the
    /// engine tick the base value was last explicitly set, or its behavior last settled.</param>
    /// <returns>The computed, envelope-clamped raw value.</returns>
    public long ComputeCurrentValue(StateRow row, long baseValue, ulong currentEngineTick, long epochEngineTick = 0L) {
        ArgumentNullException.ThrowIfNull(argument: row);

        var delta = 0L;

        if (
            (PerSecondNumerator != 0) &&
            (currentEngineTick > ((ulong)Math.Max(
            val1: epochEngineTick,
            val2: 0L
        )))
        ) {
            var scale = ((row.Kind == CellKind.Fixed)
                ? (1L << FixedQ4816.FractionBitCount)
                : 1L
            );
            var elapsed = (currentEngineTick - ((ulong)Math.Max(
                val1: epochEngineTick,
                val2: 0L
            )));

            if (TryAccumulate(
                elapsed: elapsed,
                magnitude: out var magnitude,
                scale: scale
            )) {
                delta = ((PerSecondNumerator < 0)
                    ? -magnitude
                    : magnitude
                );
            } else {
                // A magnitude past long.MaxValue can still land inside long once the base is added (a drain from a
                // positive base), so the sum is formed exactly and saturated as a whole rather than the magnitude alone.
                var wide = AccumulatedMagnitude(
                    elapsed: elapsed,
                    scale: scale
                );
                var exact = (baseValue + ((PerSecondNumerator < 0)
                    ? -wide
                    : wide));

                return row.ClampToEnvelope(value: ((exact > long.MaxValue)
                    ? long.MaxValue
                    : ((exact < long.MinValue)
                        ? long.MinValue
                        : ((long)exact))));
            }
        }

        // A saturating add into long and a clamp after is the same answer as clamping the exact sum: every envelope
        // bound is itself a long, so saturation can only move a value that is already outside every bound, and it
        // moves it to the same side. Stating the envelope once (StateRow.ClampToEnvelope) is what keeps this read
        // clamp and the rule-effect write's "could this move the cell" test from drifting apart.
        var raw = ((delta >= 0L)
            ? ((baseValue > (long.MaxValue - delta))
                ? long.MaxValue
                : (baseValue + delta))
            : ((baseValue < (long.MinValue - delta))
                ? long.MinValue
                : (baseValue + delta)
        ));

        return row.ClampToEnvelope(value: raw);
    }

    // |rate| · scale allocated over the elapsed engine ticks, as ⌊elapsed · |rate| · scale / (denominator ·
    // TicksPerSecond)⌋ — the exact rational allocation of DiscreteMeasure, with the per-second rate's own denominator
    // folded together with the engine's fixed ticks-per-second so the allocation is exact over engine ticks directly.
    // The compiled signed-64-bit form answers every read the tick can produce in long arithmetic; the exact form
    // remains behind it for a rate or an elapsed span the bounded representation cannot hold, so the two never
    // disagree on a value, only on cost.
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
        (TryAccumulate(
            elapsed: elapsed,
            magnitude: out var magnitude,
            scale: scale
        )
            ? magnitude
            : ExactMeasure(scale: scale).AmountBetween(
                end: elapsed,
                start: BigInteger.Zero
            )
        );
    private CompiledDiscreteMeasure64 CompiledFor(long scale) {
        var cache = m_compiled;

        // The rate fields are the cache key, so a `with` copy that changes the rate recompiles on its first read
        // rather than answering from the copied cache; the holder is one immutable reference, so a concurrent reader
        // sees either the old cache or the new one. Each scale compiles on its first read only.
        if (
            (cache is null) ||
            (cache.PerSecondNumerator != PerSecondNumerator) ||
            (cache.PerSecondDenominator != PerSecondDenominator)
        ) {
            cache = new CompiledMeasureCache(
                PerSecondNumerator: PerSecondNumerator,
                PerSecondDenominator: PerSecondDenominator
            );
            m_compiled = cache;
        }

        return cache.For(
            advance: this,
            scale: scale
        );
    }
    // The per-second rate's denominator, folded together with the engine's own fixed ticks-per-second, so the
    // resulting measure allocates directly over engine ticks: rate/second == (rate · scale)/(denominator ·
    // TicksPerSecond) per engine tick, exactly (never rounded), regardless of the world's own simulation rate.
    private DiscreteMeasure ExactMeasure(long scale) =>
        DiscreteMeasure.Rational(
            denominator: (((BigInteger)PerSecondDenominator) * FixedTickConversion.TicksPerSecond),
            numerator: (BigInteger.Abs(value: ((BigInteger)PerSecondNumerator)) * scale)
        );

    /// <summary>Tests equality over the authored members alone; the compiled-measure cache is runtime acceleration
    /// and never part of the record's identity.</summary>
    public bool Equals(StateAdvance? other) =>
        ((other is not null) && (PerSecondNumerator == other.PerSecondNumerator) && (PerSecondDenominator == other.PerSecondDenominator));
    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(
        value1: PerSecondNumerator,
        value2: PerSecondDenominator
    );

    // Runtime acceleration beside the immutable record, excluded from its equality above. Invalid compiled values are
    // cached too, so an exact-only rate does not retry compilation on every read.
    private CompiledMeasureCache? m_compiled;

    private sealed class CompiledMeasureCache(long PerSecondNumerator, long PerSecondDenominator) {
        private CompiledDiscreteMeasure64? m_fixed;
        private CompiledDiscreteMeasure64? m_integer;

        public long PerSecondNumerator { get; } = PerSecondNumerator;
        public long PerSecondDenominator { get; } = PerSecondDenominator;

        private static CompiledDiscreteMeasure64 Compile(StateAdvance advance, long scale) {
            _ = advance.ExactMeasure(scale: scale).TryCompileInt64(compiled: out var compiled);

            return compiled;
        }

        public CompiledDiscreteMeasure64 For(StateAdvance advance, long scale) {
            if (scale == FixedQ4816.One.Value) {
                return (m_fixed ??= Compile(
                    advance: advance,
                    scale: scale
                ));
            }

            return (m_integer ??= Compile(
                advance: advance,
                scale: scale
            ));
        }
    }
}
