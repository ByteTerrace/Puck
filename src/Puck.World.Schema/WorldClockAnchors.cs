using Puck.Hosting;
using Puck.Maths;

namespace Puck.World;

/// <summary>
/// The authority's side of a state clock's anchors: its phase at an authoritative tick, read as its own presentation
/// reads the clock's row, and the rate of the affine span the phase stands on, when the row's trait proves one.
/// </summary>
public static class WorldClockAnchors {
    /// <summary>Reads a state clock's anchor at an authoritative tick: its row's slot value as a presentation reads it
    /// (an advancing row at the engine tick, an eased row's follower, clamped to the row's envelope), its phase
    /// (<see cref="WorldClockAnchor.PhaseOf"/>), and a rate when the span from here is proved affine.</summary>
    /// <remarks>A span is affine when the row is Fixed, its slot's one trait is an advance, the advance adds the same
    /// whole raw amount every authoritative tick (its rate times the tick is a whole number of raw units), and the next
    /// tick reads exactly that much more, so the envelope does not hold it. A rate that sums a whole number of units
    /// moves no phase and carries rate zero. Every other row, eased, cycling, quantized, held at its envelope, or
    /// moved only by writes, carries rate zero.</remarks>
    /// <param name="definition">The authority's installed document.</param>
    /// <param name="clock">A state clock the document declares.</param>
    /// <param name="tick">The authoritative simulation tick.</param>
    /// <param name="engineTick">The engine tick that simulation tick stands at.</param>
    /// <returns>The anchor, or <see langword="null"/> when the clock's row holds no number: a clock that reads no
    /// phase.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="definition"/> or <paramref name="clock"/> is
    /// <see langword="null"/>.</exception>
    public static WorldClockAnchor? Read(WorldDefinition definition, WorldClock clock, ulong tick, ulong engineTick) {
        ArgumentNullException.ThrowIfNull(argument: definition);
        ArgumentNullException.ThrowIfNull(argument: clock);

        if (
            (clock.State is not { } rowName) ||
            !WorldStateReader.TryReadEased(
            definition: definition,
            engineTick: engineTick,
            key: null,
            rawValue: out var read,
            row: out var row,
            rowName: rowName,
            text: out _,
            tick: tick
        ) ||
            (read is not { } raw) ||
            (row.Kind is not (CellKind.Fixed or CellKind.Int))
        ) {
            return null;
        }

        var (rate, step) = AffineRate(
            definition: definition,
            engineTick: engineTick,
            raw: raw,
            row: row,
            tick: tick
        );

        return new WorldClockAnchor(
            Phase: WorldClockAnchor.PhaseOf(
                kind: row.Kind,
                raw: raw
            ),
            Rate: rate,
            Step: step,
            Tick: engineTick
        );
    }
    /// <summary>Returns the phases a document's state clocks read as it was loaded, which its validation admitted: the
    /// seed a late view takes for a clock whose row holds no number when it joins. A clock whose row held none at load
    /// seeds from its row's closed envelope, zero clamped into it; a clock whose row has no closed envelope seeds
    /// nothing.</summary>
    /// <param name="definition">The document as loaded.</param>
    /// <returns>The seed phases, by clock name.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="definition"/> is <see langword="null"/>.</exception>
    public static IReadOnlyDictionary<string, ulong> Seeds(WorldDefinition definition) {
        ArgumentNullException.ThrowIfNull(argument: definition);

        var seeds = new Dictionary<string, ulong>(comparer: StringComparer.Ordinal);

        foreach (var clock in (definition.Timeline.Clocks ?? [])) {
            if (clock?.State is not { } rowName) {
                continue;
            }

            if (Read(
                clock: clock,
                definition: definition,
                engineTick: 0UL,
                tick: 0UL
            ) is { } loaded) {
                seeds[clock.Name] = loaded.Phase;

                continue;
            }

            if (
                (definition.State.FirstOrDefault(predicate: row => string.Equals(
                    a: row.Name.Value,
                    b: rowName,
                    comparisonType: StringComparison.Ordinal
                )) is { Min: not null, Max: not null } closed) &&
                (closed.Kind is CellKind.Fixed or CellKind.Int)
            ) {
                seeds[clock.Name] = WorldClockAnchor.PhaseOf(
                    kind: closed.Kind,
                    raw: closed.ClampToEnvelope(value: 0L)
                );
            }
        }

        return seeds;
    }

    // The phase one authoritative tick adds over a span proved affine, and the engine ticks it spans; zero for both
    // otherwise.
    private static (long Rate, ulong Step) AffineRate(WorldDefinition definition, WorldStateRow row, long raw, ulong tick, ulong engineTick) {
        var rateHz = definition.SimulationRateHz;

        if (
            (row.Kind != CellKind.Fixed) ||
            (rateHz <= 0) ||
            ((EngineTicks.PerSecond % ((ulong)rateHz)) != 0UL)
        ) {
            return (0L, 0UL);
        }

        var behavior = EffectiveBehavior.Resolve(
            cell: StateRows.FindCell(
                cells: row.Cells,
                key: StateRow.SlotKey
            ),
            row: row
        );

        if (
            (behavior.Dynamics is not null) ||
            (behavior.Cycle is not null) ||
            (behavior.Advance is not { PerSecondNumerator: not 0L } advance) ||
            (advance.PerSecondDenominator <= 0L)
        ) {
            return (0L, 0UL);
        }

        var step = (EngineTicks.PerSecond / ((ulong)rateHz));
        var numerator = ((Int128.Abs(value: advance.PerSecondNumerator) * (1L << FixedQ4816.FractionBitCount)) * ((Int128)step));
        var denominator = (((Int128)advance.PerSecondDenominator) * EngineTicks.PerSecond);

        var (magnitude, remainder) = Int128.DivRem(
            left: numerator,
            right: denominator
        );

        if (remainder != Int128.Zero) {
            return (0L, 0UL);
        }

        var perTick = ((advance.PerSecondNumerator < 0L)
            ? -magnitude
            : magnitude
        );

        if (
            !WorldStateReader.TryReadEased(
            definition: definition,
            engineTick: (engineTick + step),
            key: null,
            rawValue: out var next,
            row: out _,
            rowName: row.Name.Value,
            text: out _,
            tick: (tick + 1UL)
        ) ||
            (next is not { } nextRaw) ||
            ((((Int128)nextRaw) - raw) != perTick)
        ) {
            return (0L, 0UL);
        }

        var rate = unchecked((long)(((ulong)perTick) << (64 - FixedQ4816.FractionBitCount)));

        return ((rate == 0L)
            ? (0L, 0UL)
            : (rate, step)
        );
    }
}
