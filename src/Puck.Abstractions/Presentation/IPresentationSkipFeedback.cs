namespace Puck.Abstractions.Presentation;

/// <summary>
/// An optional presenter capability that reports how many frames the backend has SKIPPED — submitted no GPU work for
/// (e.g. a Vulkan swapchain image that was not ready this tick: <c>VulkanFramePresentationResult.Skipped</c>) — so a
/// host-side diagnostic (the <c>[frame-timing]</c> digest) can surface it. A skip is not an error: the backend just
/// retries next tick. A presenter that never skips (or has no such concept) simply does not implement this interface;
/// callers probe with an <see langword="as"/> cast and treat absence as zero, exactly like <see cref="IPresentTimingFeedback"/>.
/// </summary>
public interface IPresentationSkipFeedback {
    /// <summary>The running total of skipped presents since this presenter was created (never resets, so a caller
    /// diffs successive reads to get a per-window count).</summary>
    ulong SkippedPresentCount { get; }
}
