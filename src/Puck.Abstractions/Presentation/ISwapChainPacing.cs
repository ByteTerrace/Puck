namespace Puck.Abstractions.Presentation;

/// <summary>
/// An optional presenter capability: a swap chain that presents on the display's vertical blank and can pace the host
/// loop by blocking until its present queue has room, which the display makes on a vertical blank. A host paced this way
/// starts each iteration when a frame reaches the display, so its interval is present to present, aligned to vertical
/// blank, rather than a timer's period plus the timer's wake-up error. Both backends keep one presented frame queued
/// behind the display, so a frame's CPU work overlaps the previous frame's GPU work: Direct3D 12 waits on the swap
/// chain's frame-latency waitable object at a maximum frame latency of two, and Vulkan waits through
/// <c>VK_KHR_present_wait</c> on a FIFO swap chain until the present before the last is visible.
/// </summary>
public interface ISwapChainPacing {
    /// <summary>The most display refreshes a present may wait for: the largest Direct3D 12 sync interval.</summary>
    const uint MaxRefreshesPerPresent = 4U;

    /// <summary>Selects the display refreshes each following present waits for, then blocks until the swap chain's
    /// present queue has room, or a bound a stalled or occluded display cannot exceed has passed.</summary>
    /// <param name="refreshesPerPresent">The refreshes each present waits for: 1 presents on every vertical blank, 2 on
    /// every other one, at most <see cref="MaxRefreshesPerPresent"/>. Zero asks for no pacing: presents wait for one
    /// refresh, and the call returns <see langword="false"/> without waiting.</param>
    /// <returns><see langword="true"/> when the swap chain paced this iteration: it waited for the display, or found the
    /// queue already had room. <see langword="false"/> when it did not, so the host paces the iteration by its own timer:
    /// no pacing was asked for, no swap chain exists, the swap chain does not present on vertical blank (a tearing or
    /// mailbox present mode), it cannot present at that many refreshes, nothing was presented since its last wait, or
    /// the bound passed.</returns>
    bool TryWaitForFrame(uint refreshesPerPresent);
}
