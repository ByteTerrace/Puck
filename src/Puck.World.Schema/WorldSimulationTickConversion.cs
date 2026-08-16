using Puck.Maths;

namespace Puck.World;

/// <summary>
/// Converts an authored duration in seconds to a whole count of SIMULATION ticks — the unit <c>WorldServer</c>'s own
/// tick counter (<c>NextInputTick</c>) advances in, one per <c>WorldServer.Step</c> call. Distinct from
/// <see cref="FixedTickConversion"/>'s engine tick (50400/s, fixed), which <c>Puck.World.Server.WorldBody</c>'s
/// per-body effect timers advance in.
/// <para>The simulation rate is an authored per-world field (<see cref="WorldSimulationDefaults"/>), so every method
/// here takes <c>ratePerSecond</c> as a parameter rather than caching one.</para>
/// <para>A world-rule countdown cell (a <c>setState</c>/<c>addState</c> effect's <c>valueSeconds</c>) compiles
/// through <see cref="FixedTickConversion.TryDurationEngineTicksExact"/> instead. This type serves
/// simulation-tick-scoped bookkeeping such as <c>population.reconnectGraceTicks</c> and the <c>$parked:</c> reserved
/// channel.</para>
/// </summary>
public static class WorldSimulationTickConversion {
    /// <summary>Converts an authored duration to a <see cref="CompiledTickDuration"/>, distinguishing an
    /// authored-disabled zero from the rate-0 case where no tick mapping exists at all
    /// (<see cref="DurationTicks(float, uint)"/> alone cannot signal that; it can only return a tick count, and 0
    /// is a legitimate count). At <paramref name="ratePerSecond"/> 0, a positive <paramref name="seconds"/> has no
    /// tick mapping (<see cref="CompiledTickDuration.Never"/>); a non-positive <paramref name="seconds"/> compiles
    /// to zero ticks at any rate, including 0. At a positive rate this defers entirely to
    /// <see cref="DurationTicks(float, uint)"/>.</summary>
    /// <param name="seconds">The authored duration.</param>
    /// <param name="ratePerSecond">The simulation rate (Hz) to convert against — a world's own
    /// <see cref="WorldDefinition.SimulationRateHz"/>.</param>
    public static CompiledTickDuration CompiledDuration(float seconds, uint ratePerSecond) {
        if (ratePerSecond == 0U) {
            return ((seconds > 0f)
                ? CompiledTickDuration.Never
                : CompiledTickDuration.FromTicks(ticks: 0)
            );
        }

        return CompiledTickDuration.FromTicks(ticks: checked((int)DurationTicks(
            ratePerSecond: ratePerSecond,
            seconds: seconds
        )));
    }
    /// <summary>Converts a duration to the smallest whole simulation-tick count no less than its exact value — a
    /// positive duration always advances by at least one tick; a non-positive duration converts to zero.
    /// <see cref="Puck.Maths.FixedQ4816"/> input and <see cref="Int128"/> intermediate arithmetic keep the
    /// conversion exact, so a duration that divides <paramref name="ratePerSecond"/> evenly always round-trips to
    /// the same tick count. Rounds up rather than refusing an inexact duration; a world-rule countdown authored via
    /// <c>valueSeconds</c> must use <see cref="FixedTickConversion.TryDurationEngineTicksExact"/> instead, which
    /// refuses.</summary>
    /// <param name="seconds">The duration to convert.</param>
    /// <param name="ratePerSecond">The simulation rate (Hz) to convert against — a world's own
    /// <see cref="WorldDefinition.SimulationRateHz"/>.</param>
    /// <returns><paramref name="seconds"/> in simulation ticks, rounded up; <c>0</c> when <paramref name="seconds"/>
    /// is zero or negative.</returns>
    public static ulong DurationTicks(FixedQ4816 seconds, uint ratePerSecond) {
        if (seconds <= FixedQ4816.Zero) {
            return 0UL;
        }

        var scaled = (((Int128)seconds.Value) * ratePerSecond);

        return checked((ulong)scaled.CeilingDivide(divisor: ((Int128)(1L << FixedQ4816.FractionBitCount))));
    }
    /// <summary>Convenience overload for the common <c>float</c>-authored document case — see
    /// <see cref="DurationTicks(FixedQ4816, uint)"/>.</summary>
    public static ulong DurationTicks(float seconds, uint ratePerSecond) {
        return DurationTicks(
            seconds: FixedQ4816.FromDouble(value: seconds),
            ratePerSecond: ratePerSecond
        );
    }
    /// <summary>The inverse of <see cref="DurationTicks(float, uint)"/> — the number of seconds a whole
    /// simulation-tick count spells, for round-tripping an authored value on save/read-back. Exact whenever
    /// <paramref name="ticks"/> is a multiple of <paramref name="ratePerSecond"/>; otherwise the nearest
    /// <c>float</c> to the true rational, which may reconvert through <see cref="DurationTicks(float, uint)"/> to a
    /// tick count one away from <paramref name="ticks"/>.
    /// <para>Refuses at rate 0 rather than dividing by zero — there is no tick-to-seconds mapping for a world whose
    /// simulation rate is 0, since every tick has already compiled through <see cref="CompiledTickDuration"/> by
    /// the time it reaches this codebase's request-time consumers.</para></summary>
    /// <param name="ticks">The tick count to convert.</param>
    /// <param name="ratePerSecond">The simulation rate (Hz) to convert against — a world's own
    /// <see cref="WorldDefinition.SimulationRateHz"/>.</param>
    /// <exception cref="InvalidOperationException"><paramref name="ratePerSecond"/> is 0.</exception>
    public static float SecondsFromTicks(int ticks, uint ratePerSecond) {
        if (ratePerSecond == 0U) {
            throw new InvalidOperationException(message: $"cannot convert {ticks} simulation ticks to seconds at simulation rate 0 — a resident, non-stepping world's durable stop has no tick-to-seconds mapping.");
        }

        return (ticks / ((float)ratePerSecond));
    }
}
