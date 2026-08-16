using Puck.Abstractions.Presentation;
using Puck.Hosting;

namespace Puck.Overlays;

/// <summary>One seat's drawn pointer cursor, scoped to its viewport rect. Positions are pixels in full-frame space;
/// the seat's clip rect confines them, so a cursor near a split boundary cuts, never bleeds into a neighbour's
/// view.</summary>
/// <param name="Viewport">The seat's viewport rect in normalized frame space.</param>
/// <param name="X">The cursor hotspot x, px.</param>
/// <param name="Y">The cursor hotspot y, px.</param>
/// <param name="Hover">Whether the cursor currently rests on something hit-testable (an overlay panel or a picked
/// world row) — the accent highlight tier.</param>
/// <param name="HoverLabel">The hovered thing's short label (the tooltip text), or empty for a bare hover-less
/// cursor.</param>
/// <param name="SizePx">The ring radius, px (the world-authored cursor size; the host clamps it to the writer's
/// legal band before publishing).</param>
/// <param name="Role">The bare cursor's color role (the world-authored hue); <paramref name="Hover"/> lights the
/// accent tier regardless.</param>
public readonly record struct OverlayCursorSeat(
    NormalizedRect Viewport,
    float X,
    float Y,
    bool Hover,
    string HoverLabel,
    float SizePx,
    OverlayColorRole Role
);
/// <summary>The per-frame cursor snapshot — one entry per seat whose pointer is currently visible (the host owns the
/// visibility policy; an empty frame draws nothing, so hiding is simply not publishing the seat).</summary>
/// <param name="Seats">The cursor-bearing seats, in slot order.</param>
public readonly record struct OverlayCursorFrame(
    ReadOnlyMemory<OverlayCursorSeat> Seats
);
/// <summary>The read seam <see cref="CursorWriter"/> consumes; the host's cursor feed is the writer.</summary>
public interface ICursorSource {
    /// <summary>Copies the latest published frame, when one exists.</summary>
    /// <param name="frame">The latest frame, when published.</param>
    /// <returns><see langword="true"/> when a frame has been published.</returns>
    bool TrySnapshot(out OverlayCursorFrame frame);
}
/// <summary>
/// The cursor state store. A thin named wrapper over the shared <see cref="PublishBuffer{T}"/>. Same threading
/// contract as <see cref="EditorGizmoStore"/>: the host's feed publishes once per produced frame and the same-thread
/// overlay writer reads, so backing arrays may be reused across publishes with zero steady-state allocation.
/// </summary>
public sealed class CursorStore : ICursorSource {
    private readonly PublishBuffer<OverlayCursorFrame> m_buffer = new();

    /// <summary>Publishes a frame (the writer side).</summary>
    /// <param name="frame">The frame to publish.</param>
    public void Publish(in OverlayCursorFrame frame) => m_buffer.Publish(frame: frame);
    /// <inheritdoc/>
    public bool TrySnapshot(out OverlayCursorFrame frame) => m_buffer.TrySnapshot(frame: out frame);
}
