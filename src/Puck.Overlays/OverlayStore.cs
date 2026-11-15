using Puck.Hosting;

namespace Puck.Overlays;

/// <summary>The read seam every overlay writer consumes: the latest frame a host published for it.</summary>
/// <typeparam name="TFrame">The immutable frame snapshot the overlay renders.</typeparam>
public interface IOverlaySource<TFrame> {
    /// <summary>Copies the latest published frame, when one exists.</summary>
    /// <param name="frame">The latest frame, when published.</param>
    /// <returns><see langword="true"/> when a frame has been published.</returns>
    bool TrySnapshot(out TFrame frame);
}
/// <summary>
/// The one state store every overlay shares: a thin named wrapper over the shared <see cref="PublishBuffer{T}"/>.
/// The host's produce path publishes once per produced frame and the same-thread overlay writer reads, so backing
/// arrays may be reused across publishes with zero steady-state allocation; a feed on another thread must publish
/// freshly allocated snapshots. An empty frame draws nothing, so hiding is publishing an empty frame.
/// </summary>
/// <typeparam name="TFrame">The immutable frame snapshot the overlay renders.</typeparam>
public abstract class OverlayStore<TFrame> : IOverlaySource<TFrame> {
    private readonly PublishBuffer<TFrame> m_buffer = new();

    /// <summary>Publishes a frame (the writer side).</summary>
    /// <param name="frame">The frame to publish.</param>
    public void Publish(in TFrame frame) => m_buffer.Publish(frame: frame);
    /// <inheritdoc/>
    public bool TrySnapshot(out TFrame frame) => m_buffer.TrySnapshot(frame: out frame);
}
