using System.Diagnostics;

namespace Puck.World.Protocol;

/// <summary>
/// The time a delivered snapshot arrived, as its receiver stamps it for presentation interpolation
/// (<see cref="Puck.World.Client.WorldSessionMirror.ResolveInterpolationAlpha"/>). A delivery handed over as it is made arrived now. One
/// an authority's outbox replays later arrived when its tick finished executing, which the replay states for its
/// duration (<see cref="ReplayAt"/>), so a frame that applies a tick after it finished interpolates toward it as far
/// as a frame that applied it at once would. Presentation only: no simulation state reads it.
/// </summary>
public static class WorldDeliveryClock {
    [ThreadStatic] private static long Replayed;

    /// <summary>Gets the stopwatch timestamp a snapshot delivered now on the calling thread arrived at: the time the
    /// replay in progress states, or the current time when none is.</summary>
    public static long Arrival => ((Replayed != 0L) ? Replayed : Stopwatch.GetTimestamp());

    /// <summary>States the arrival time of every delivery the calling thread makes until the returned scope is
    /// disposed.</summary>
    /// <param name="timestamp">The stopwatch timestamp the deliveries arrived at, or zero for the current time at each
    /// delivery.</param>
    /// <returns>The scope whose disposal restores the arrival time stated before it.</returns>
    public static Scope ReplayAt(long timestamp) {
        var scope = new Scope(previous: Replayed);

        Replayed = timestamp;

        return scope;
    }

    /// <summary>A stated arrival time, ended when disposed. Only <see cref="ReplayAt"/> produces one.</summary>
    public readonly ref struct Scope {
        private readonly long m_previous;

        internal Scope(long previous) => m_previous = previous;

        /// <summary>Restores the arrival time stated before this scope.</summary>
        public void Dispose() => Replayed = m_previous;
    }
}
