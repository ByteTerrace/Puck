using Puck.Commands;

namespace Puck.World;

/// <summary>
/// The console wire's TICK barrier: the state behind <c>world.wait</c>. It is armed by that verb with a release tick and
/// read by the issuing text session, so only that session's queued work AFTER a wait stays
/// queued until the world host has completed the requested number of ticks.
/// </summary>
/// <remarks>
/// The clock is the row's monotonic completed host-work counter, independent of replay rewinds and wall time.
/// Members run on the host pump. One instance serves a desktop's boot row; a multi-row host supplies one per row.
/// </remarks>
public sealed class WorldConsoleWaitGate {
    private bool m_armed;
    private ulong m_releaseTick;
    private uint m_epoch;

    /// <summary>Gets the last completed host-work tick published to this gate.</summary>
    public ulong Tick { get; private set; }

    /// <summary>Holds only <paramref name="session"/> until this clock has completed the requested ticks.</summary>
    /// <param name="session">The issuing text session. Other sessions keep draining.</param>
    /// <param name="ticks">The number of ticks to wait, counted from the last completed tick.</param>
    /// <returns>The earliest host-work tick that releases the session.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The tick count is zero.</exception>
    /// <exception cref="OverflowException">The release tick exceeds the clock range.</exception>
    public ulong Arm(TextCommandSession session, ulong ticks) {
        ArgumentNullException.ThrowIfNull(argument: session);
        ArgumentOutOfRangeException.ThrowIfZero(ticks);
        var release = checked(Tick + ticks);
        var epoch = m_epoch;
        session.HoldWhile(() => m_epoch == epoch && Tick < release);
        m_armed = true;
        m_releaseTick = Math.Max(m_releaseTick, release);

        return release;
    }
    /// <summary>Publishes a completed host-work tick. Each waiting session checks its own deadline.
    /// A clock regression invalidates every previous deadline, including expiry not yet observed by the pump.</summary>
    /// <param name="tick">The monotonic count of completed host-work ticks.</param>
    public void PublishTick(ulong tick) {
        if (tick < Tick) {
            // Expiry clears m_armed before the command pump necessarily observes the old predicate.
            // A reset must invalidate that predicate too, or the lower clock can revive an expired wait.
            m_armed = false;
            m_epoch++;
            m_releaseTick = tick;
        }
        Tick = tick;

        if (
            m_armed &&
            (tick >= m_releaseTick)
        ) {
            m_armed = false;
        }
    }
    /// <summary>Force-releases an armed hold whose release tick can no longer be reached — the row stopped
    /// advancing (paused, or an authored <c>rateHz</c> of 0) while a wait was armed, so <see cref="PublishTick"/>
    /// will never fire again to satisfy it on its own. A hold that can never complete must never be left hanging:
    /// this is what lets the held console stream behind it (including the very <c>world.rate resume</c> that would
    /// otherwise sit trapped behind its own release condition) keep draining.</summary>
    /// <returns><see langword="true"/> when a hold was actually released; <see langword="false"/> when nothing was
    /// armed (the ordinary case, checked every call so callers never need their own redundant guard).</returns>
    public bool ReleaseStalled() {
        if (!m_armed) {
            return false;
        }

        m_armed = false;
        m_releaseTick = Tick;
        m_epoch++;

        return true;
    }
}
