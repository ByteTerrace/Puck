using Puck.Launcher;

namespace Puck.World;

/// <summary>
/// The console wire's TICK barrier: the state behind <c>world.wait</c>. It is armed by that verb with a release tick and
/// read every frame through <see cref="ITextCommandHoldGate"/>, so the queued lines AFTER a wait stay
/// queued until the world's fixed-step simulation has actually advanced the requested number of ticks.
/// </summary>
/// <remarks>
/// The clock is <see cref="WorldSimulation.Tick"/> — completed fixed ticks, published here at the end of each step — not
/// wall time: the same script advances the same number of ticks on every run and every machine, which is the whole point
/// of a scripted "drive, then read back". Both members run on the window-pump thread (the drain and the fixed step are
/// the same loop iteration), so no synchronization is needed.
/// </remarks>
internal sealed class WorldConsoleWaitGate : ITextCommandHoldGate {
    private bool m_armed;
    private ulong m_releaseTick;

    /// <summary>Gets the last completed simulation tick published to this gate.</summary>
    public ulong Tick { get; private set; }

    /// <summary>Holds the wire until the simulation has completed <paramref name="ticks"/> further ticks.</summary>
    /// <param name="ticks">The number of ticks to wait, counted from the last completed tick.</param>
    /// <returns>The tick the wire releases on.</returns>
    public ulong Arm(ulong ticks) {
        m_armed = true;
        m_releaseTick = (Tick + ticks);

        return m_releaseTick;
    }
    /// <summary>Whether the queued console stream is currently held — the <c>HoldGate</c> delegate.</summary>
    /// <returns><see langword="true"/> while the armed release tick is still in the future.</returns>
    public bool IsHolding() {
        return m_armed;
    }
    /// <summary>Publishes a completed simulation tick, releasing the wire once the armed release tick is reached.</summary>
    /// <param name="tick">The number of fixed ticks the simulation has completed.</param>
    public void PublishTick(ulong tick) {
        Tick = tick;

        if (
            m_armed &&
            (tick >= m_releaseTick)
        ) {
            m_armed = false;
        }
    }
    /// <summary>Force-releases an armed hold whose release tick can no longer be reached — the boot world stopped
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

        return true;
    }
}
