using Puck.Abstractions.Gpu;
using Puck.Abstractions.Presentation;

namespace Puck.Abstractions.Capture;

/// <summary>
/// A backend-neutral source of captured frames — the primary screen, a window, or any other producer that
/// can be pulled on a cadence. The result is a <see cref="Surface"/>, so a source may hand back CPU pixels or
/// a GPU/shared-handle variant; consumers that need host pixels convert through <see cref="IGpuSurfaceReadback"/>.
/// Implementations are not required to be thread-safe; drive each from a single capture loop.
/// </summary>
public interface IFrameCaptureSource {
    /// <summary>Captures the source's current content.</summary>
    /// <param name="surface">When this method returns <see langword="true"/>, the captured frame; otherwise
    /// an empty <see cref="Surface"/>.</param>
    /// <returns><see langword="true"/> if a frame was captured; otherwise <see langword="false"/> (for example a
    /// window source with no current match, or a transient capture failure).</returns>
    bool TryCapture(out Surface surface);
}
