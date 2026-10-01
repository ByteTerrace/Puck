using System.Text.Json.Serialization;
using Puck.Hosting;
using Puck.Maths;

namespace Puck.World;

/// <summary>
/// What a projection carries in place of a state clock's row: the clock's phase at one authoritative tick and the rate
/// it moves at from there. The authority and the recipient call the one prediction, <see cref="Predict"/>, so the
/// authority knows, at every authoritative tick, the phase the recipient presents, and sends a new anchor exactly when
/// that prediction differs from its own phase (<see cref="WorldClockAnchorLedger"/>).
/// </summary>
/// <remarks>
/// <para>A phase is a whole turn as an unsigned 64-bit count: <c>2^64</c> is one turn, and arithmetic wraps, so a clock
/// that passes a whole unit needs no reduction. A Fixed row's phase is its fractional bits, exactly
/// (<see cref="PhaseOf"/>), so the phase a recipient predicts is the phase the authority's own presentation reads, bit
/// for bit, at every authoritative tick.</para>
/// <para>The rate is the phase one authoritative tick adds, <see cref="Step"/> engine ticks long. It is nonzero only over
/// a span the authority has proved affine (<see cref="WorldClockAnchors.Read"/>); rate changes, quantized advances,
/// staircases, eased rows and seeks all carry rate zero and re-anchor wherever the phase moves.</para>
/// </remarks>
/// <param name="Tick">The engine tick the anchor stands at.</param>
/// <param name="Phase">The phase at <paramref name="Tick"/>, a whole turn being <c>2^64</c>.</param>
/// <param name="Rate">The phase one authoritative tick adds, wrapping; zero for a clock held still.</param>
/// <param name="Step">The engine ticks one authoritative tick spans; zero exactly when <paramref name="Rate"/> is.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WorldClockAnchor(
    ulong Tick,
    ulong Phase,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] long Rate = 0L,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] ulong Step = 0UL
) {
    // 2^-64: one phase unit as a share of a turn.
    private const double TurnPerUnit = (1d / 18446744073709551616d);

    /// <summary>Gets whether the anchor is well formed: a rate exactly when it names the ticks it is per.</summary>
    [JsonIgnore]
    public bool IsWellFormed => ((Rate == 0L) == (Step == 0UL));

    /// <summary>Returns a value's phase: a Fixed value's fractional bits as a share of <c>2^64</c>, exactly; zero for
    /// every other kind, whose values are whole.</summary>
    /// <param name="kind">The row's kind.</param>
    /// <param name="raw">The raw value.</param>
    /// <returns>The phase.</returns>
    public static ulong PhaseOf(CellKind kind, long raw) => ((kind == CellKind.Fixed)
        ? unchecked((((ulong)raw) << (64 - FixedQ4816.FractionBitCount)))
        : 0UL
    );
    /// <summary>Returns a phase as a share of a turn, in <c>[0, 1)</c>.</summary>
    /// <param name="phase">The phase.</param>
    /// <returns>The share.</returns>
    public static double ToTurn(ulong phase) {
        var turn = (phase * TurnPerUnit);

        // The nearest double to a phase within half a unit of a whole turn rounds up to it.
        return ((turn >= 1d)
            ? 0d
            : turn
        );
    }
    /// <summary>Predicts the phase at an engine tick: the anchor's phase plus its rate over the authoritative ticks
    /// elapsed, floored, wrapping. Exact at every authoritative tick, where the elapsed engine ticks are a multiple of
    /// <see cref="Step"/>; a tick before the anchor extrapolates back along the same line.</summary>
    /// <param name="engineTick">The engine tick.</param>
    /// <returns>The predicted phase.</returns>
    public ulong Predict(ulong engineTick) {
        if ((Rate == 0L) || (Step == 0UL)) {
            return Phase;
        }

        var elapsed = (((Int128)engineTick) - Tick);
        var advance = (((Int128)Rate) * elapsed).FloorDivide(divisor: ((Int128)Step));

        return unchecked((Phase + ((ulong)advance)));
    }
    /// <summary>Returns the phase a frame presents at a presented tick, as a share of a turn: the prediction at its
    /// whole tick, moved on by the rate over its fraction.</summary>
    /// <param name="tick">The presented tick.</param>
    /// <returns>The phase, in <c>[0, 1)</c>.</returns>
    public double PhaseAt(PresentedTick tick) {
        var whole = ToTurn(phase: Predict(engineTick: tick.Whole));

        if ((Rate == 0L) || (Step == 0UL) || (tick.Fraction == 0d)) {
            return whole;
        }

        var turn = (whole + (((Rate * TurnPerUnit) * tick.Fraction) / Step));

        return (turn - Math.Floor(d: turn));
    }
}
