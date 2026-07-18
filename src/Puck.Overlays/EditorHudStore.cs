using Puck.Compositing;
using Puck.Hosting;

namespace Puck.Overlays;

/// <summary>One editing seat's HUD snapshot — pre-formatted lines (the host formats only when the underlying editor
/// state changes) scoped to the seat's viewport rect.</summary>
/// <param name="Viewport">The seat's viewport rect in normalized frame space (its <c>LayoutRegion</c>).</param>
/// <param name="SelectionLine">The selection readout (section, id, position), or empty for none.</param>
/// <param name="ContextLine">The candidate-pool / camera context hint, or empty.</param>
/// <param name="DragLine">The live drag readout, or empty while idle.</param>
/// <param name="DragActive">Whether a drag is live (the accent ring state).</param>
public readonly record struct OverlayEditorSeat(
    NormalizedRect Viewport,
    string SelectionLine,
    string ContextLine,
    string DragLine,
    bool DragActive
);

/// <summary>The per-frame editor-HUD snapshot the unified overlay renders — one entry per EDITING seat (an empty
/// frame draws nothing).</summary>
/// <param name="Seats">The editing seats, in slot order.</param>
public readonly record struct OverlayEditorHudFrame(
    ReadOnlyMemory<OverlayEditorSeat> Seats
);

/// <summary>The read seam <see cref="EditorHudWriter"/> consumes; the host's editor feed is the writer.</summary>
public interface IEditorHudSource {
    /// <summary>Copies the latest published frame, when one exists.</summary>
    /// <param name="frame">The latest frame, when published.</param>
    /// <returns><see langword="true"/> when a frame has been published.</returns>
    bool TrySnapshot(out OverlayEditorHudFrame frame);
}

/// <summary>
/// The editor-HUD state store. A thin named wrapper over the shared <see cref="PublishBuffer{T}"/>. Same threading
/// contract as <see cref="BindingBarStore"/>: a same-thread <c>FeedTick</c> feed may reuse backing arrays across
/// publishes with zero steady-state allocation.
/// </summary>
public sealed class EditorHudStore : IEditorHudSource {
    private readonly PublishBuffer<OverlayEditorHudFrame> m_buffer = new();

    /// <summary>Publishes a frame (the writer side).</summary>
    /// <param name="frame">The frame to publish.</param>
    public void Publish(in OverlayEditorHudFrame frame) => m_buffer.Publish(frame: frame);

    /// <inheritdoc/>
    public bool TrySnapshot(out OverlayEditorHudFrame frame) => m_buffer.TrySnapshot(frame: out frame);
}
