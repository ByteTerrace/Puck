using System.Numerics;

namespace Puck.World;

internal sealed partial class WorldCursorFeed {
    private Vector2? m_pointerOverride;

    // A console automation point affects this presentation feed alone. Clearing returns to the real pointer store.
    public bool TryOverridePointer(Vector2? point) {
        var width = ((m_viewports.ClientWidth > 0) ? m_viewports.ClientWidth : (uint)m_panes.DisplayWidth);
        var height = ((m_viewports.ClientHeight > 0) ? m_viewports.ClientHeight : (uint)m_panes.DisplayHeight);

        if ((point is { } value) && (!float.IsFinite(f: value.X) || !float.IsFinite(f: value.Y) ||
            (value.X < 0) || (value.Y < 0) || (value.X >= width) || (value.Y >= height))) {
            return false;
        }
        m_pointerOverride = point;
        return true;
    }

    private bool HasPosition(int slot) => (m_pointerOverride.HasValue || m_pointer.HasPosition(slot: slot));
}
