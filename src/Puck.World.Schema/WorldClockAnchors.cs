using System.Numerics;
using Puck.Maths;

namespace Puck.World;

/// <summary>
/// The authority's side of a state clock's anchors: its phase at an authoritative tick, read as its own presentation
/// reads the clock's row, and the polynomial the row's value follows from there, when the row's trait proves one.
/// </summary>
public static class WorldClockAnchors {
    /// <summary>Reads a state clock's anchor at an authoritative tick: its row's slot value as a presentation reads it
    /// (an advancing row at the engine tick, an eased row's follower, clamped to the row's envelope), its phase
    /// (<see cref="WorldClockAnchor.PhaseOf"/>), and a base and curve when the span from here is proved polynomial.</summary>
    /// <remarks>A span is polynomial when the row is Fixed, its slot's one trait is an advance (affine, or nested on
    /// parents that are themselves advances), and the curve the arena composes for it (<see cref="StateArena.TryReadClockCurve"/>)
    /// reads both this tick's value and the next authoritative tick's exactly, so neither the envelope nor a piece of a
    /// parent's shape has turned it yet. Every other row, eased, cycling, held at its envelope, or moved only by writes,
    /// carries no curve.</remarks>
    /// <param name="definition">The authority's installed document.</param>
    /// <param name="arena">The live store, or null at a seed boundary.</param>
    /// <param name="clock">A state clock the document declares.</param>
    /// <param name="tick">The authoritative simulation tick.</param>
    /// <param name="engineTick">The engine tick that simulation tick stands at.</param>
    /// <returns>The anchor, or <see langword="null"/> when the clock's row holds no number: a clock that reads no
    /// phase.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="definition"/> or <paramref name="clock"/> is
    /// <see langword="null"/>.</exception>
    public static WorldClockAnchor? Read(WorldDefinition definition, WorldClock clock, ulong tick, ulong engineTick, StateArena? arena = null) {
        ArgumentNullException.ThrowIfNull(argument: definition);
        ArgumentNullException.ThrowIfNull(argument: clock);

        if (
            (clock.State is not { } rowName) ||
            !WorldStateReader.TryReadEased(
            arena: arena,
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

        var (baseValue, curve) = ProvedCurve(
            arena: arena,
            definition: definition,
            engineTick: engineTick,
            raw: raw,
            row: row,
            tick: tick
        );

        return new WorldClockAnchor(
            Base: baseValue,
            Curve: curve,
            Phase: WorldClockAnchor.PhaseOf(
                kind: row.Kind,
                raw: raw
            ),
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

    // The base and curve a Fixed advancing row follows from here, when the arena proves it: the slot's exact value as a
    // polynomial in the elapsed engine ticks (StateArena.TryReadClockCurve), turned into the accumulation over the base
    // in raw units, which reads this tick's value and the next authoritative tick's, so the envelope holds neither.
    // Every other row (eased, cycling, clamped, held, or a rate that moves no phase) carries none.
    private static (long Base, IReadOnlyList<string>? Curve) ProvedCurve(WorldDefinition definition, WorldStateRow row, long raw, ulong tick, ulong engineTick, StateArena? arena) {
        var rateHz = definition.SimulationRateHz;
        var store = (arena ?? WorldStateReader.Arena(definition: definition));

        if (
            (row.Kind != CellKind.Fixed) ||
            (rateHz <= 0) ||
            !FixedTickConversion.TryPerRate(period: out var step, ratePerSecond: ((uint)rateHz)) ||
            !store.Catalog.TryResolve(handle: out var handle, lane: StateLane.Document, name: row.Name.Value) ||
            !store.Keys.TryResolve(key: out var slot, name: StateRow.SlotKey)
        ) {
            return (0L, null);
        }

        var behavior = store.LiveBehavior(key: slot, rowOrdinal: handle.Ordinal);

        if (
            (behavior.Dynamics is not null) ||
            (behavior.Cycle is not null) ||
            (behavior.Advance is not { PerSecondNumerator: not 0L }) ||
            !store.TryReadClockCurve(baseValue: out var baseValue, curve: out var value, engineTick: engineTick, rowOrdinal: handle.Ordinal)
        ) {
            return (0L, null);
        }

        var accumulation = ((new Rational(Numerator: StateAdvance.CarrierScale(kind: row.Kind), Denominator: BigInteger.One) * value) -
            RationalPolynomial.Constant(value: new Rational(Numerator: baseValue, Denominator: BigInteger.One)));

        if (
            (accumulation.Degree <= 0) ||
            ((baseValue + accumulation.Evaluate(x: Rational.Zero).Truncate()) != raw) ||
            !WorldStateReader.TryReadEased(
                arena: arena,
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
            ((baseValue + accumulation.Evaluate(x: new Rational(Numerator: step, Denominator: BigInteger.One)).Truncate()) != nextRaw) ||
            (accumulation.Coefficients.Length > WorldClockAnchor.MaxCurveCoefficients)
        ) {
            return (0L, null);
        }

        return (baseValue, WorldClockAnchor.Spell(curve: accumulation));
    }
}

