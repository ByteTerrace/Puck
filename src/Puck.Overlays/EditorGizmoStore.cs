using Puck.Compositing;
using Puck.Hosting;

namespace Puck.Overlays;

/// <summary>One projected gizmo chip — a geometry-less document row (a speaker) made visible in editor mode: the
/// host projects its resolved WORLD pose into the seat's viewport and the writer draws an icon chip there (plus a
/// translucent radius ring for region rows). Positions are PIXELS in full-frame space; the seat's clip rect confines
/// them.</summary>
/// <param name="CenterX">The chip center x, px.</param>
/// <param name="CenterY">The chip center y, px.</param>
/// <param name="RingRadiusPx">The projected support-radius ring, px (0 = no ring — point rows).</param>
/// <param name="Bed">Whether the row is a region (bed) — selects the concentric-presence icon.</param>
/// <param name="Selected">Whether an editing seat selects the row (the ACCENT chip tier).</param>
/// <param name="Pulse">Whether the row's change shimmer is live (the HELD chip tier — the visual echo of a
/// just-applied speaker mutation).</param>
public readonly record struct OverlayGizmoChip(
    float CenterX,
    float CenterY,
    float RingRadiusPx,
    bool Bed,
    bool Selected,
    bool Pulse
);

/// <summary>One EDITING seat's gizmo set, scoped to its viewport rect.</summary>
/// <param name="Viewport">The seat's viewport rect in normalized frame space.</param>
/// <param name="Chips">The projected chips visible in this seat's view.</param>
public readonly record struct OverlayGizmoSeat(
    NormalizedRect Viewport,
    ReadOnlyMemory<OverlayGizmoChip> Chips
);

/// <summary>The per-frame editor-gizmo snapshot — one entry per EDITING seat (an empty frame draws nothing; the
/// host publishes every produced frame so leaving editor mode clears the chips).</summary>
/// <param name="Seats">The editing seats, in slot order.</param>
public readonly record struct OverlayGizmoFrame(
    ReadOnlyMemory<OverlayGizmoSeat> Seats
);

/// <summary>The read seam <see cref="EditorGizmoWriter"/> consumes; the host's frame source is the writer.</summary>
public interface IEditorGizmoSource {
    /// <summary>Copies the latest published frame, when one exists.</summary>
    /// <param name="frame">The latest frame, when published.</param>
    /// <returns><see langword="true"/> when a frame has been published.</returns>
    bool TrySnapshot(out OverlayGizmoFrame frame);
}

/// <summary>
/// The editor-gizmo state store. A thin named wrapper over the shared <see cref="PublishBuffer{T}"/>. Same threading
/// contract as <see cref="EditorHudStore"/>: the host's produce path publishes and the same-thread overlay writer
/// reads, so backing arrays may be reused across publishes with zero steady-state allocation.
/// </summary>
public sealed class EditorGizmoStore : IEditorGizmoSource {
    private readonly PublishBuffer<OverlayGizmoFrame> m_buffer = new();

    /// <summary>Publishes a frame (the writer side).</summary>
    /// <param name="frame">The frame to publish.</param>
    public void Publish(in OverlayGizmoFrame frame) => m_buffer.Publish(frame: frame);

    /// <inheritdoc/>
    public bool TrySnapshot(out OverlayGizmoFrame frame) => m_buffer.TrySnapshot(frame: out frame);
}
