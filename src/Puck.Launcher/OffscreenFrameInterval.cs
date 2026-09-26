namespace Puck.Launcher;

/// <summary>
/// The wall interval the offscreen host hands each composed frame (<c>FrameContext.FrameDeltaTicks</c>): every
/// host-loop interval since the frame before it, since the host composes no frame for an iteration that steps nothing
/// (<see cref="OffscreenTickHostedService.ComposesFrame"/>), clamped as the pump clamps a runaway interval. A frame's
/// presentation animation therefore advances by the time that actually passed, however many iterations composed
/// nothing in between.
/// </summary>
public sealed class OffscreenFrameInterval {
    private ulong m_pendingTicks;

    /// <summary>Adds one host-loop iteration's interval and, when the iteration composes a frame, returns the interval
    /// that frame spans and starts the next one empty.</summary>
    /// <param name="deltaTicks">The iteration's wall interval, in engine ticks.</param>
    /// <param name="composes">Whether the iteration composes a frame.</param>
    /// <param name="maxFrameTicks">The most a frame's interval may span, in engine ticks: the pump's own clamp.</param>
    /// <returns>The frame's interval in engine ticks, at most <paramref name="maxFrameTicks"/>; zero for an iteration
    /// that composes nothing.</returns>
    public ulong Take(ulong deltaTicks, bool composes, ulong maxFrameTicks) {
        m_pendingTicks = ((m_pendingTicks > (ulong.MaxValue - deltaTicks))
            ? ulong.MaxValue
            : (m_pendingTicks + deltaTicks));

        if (!composes) {
            return 0UL;
        }

        var interval = Math.Min(
            val1: m_pendingTicks,
            val2: maxFrameTicks
        );

        m_pendingTicks = 0UL;

        return interval;
    }
}
