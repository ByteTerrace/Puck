using Puck.Hosting;

namespace Puck.Overlays;

/// <summary>The host seam <see cref="OverlayFrameSlots"/> acquires HUD <c>Frame</c>-element content through — one
/// live source per opaque key the host assigned (a declared frame source's registry key).</summary>
public interface IOverlayFrameSources {
    /// <summary>Attempts to acquire this produced frame's content for <paramref name="key"/>.</summary>
    /// <param name="key">The opaque source id the host handed the HUD element.</param>
    /// <param name="lease">The acquired lease on a same-device sampleable image, when live.</param>
    /// <returns><see langword="true"/> when content is live this frame; <see langword="false"/> when there is
    /// nothing to show (the element then draws nothing, never a placeholder).</returns>
    bool TryAcquire(int key, out GpuImageLease lease);
}
