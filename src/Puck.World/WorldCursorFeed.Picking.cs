using Puck.Commands;
using Puck.SdfVm;
using Puck.World.Client;

namespace Puck.World;

internal sealed partial class WorldCursorFeed {
    private SdfWorldPicker? m_hoverPicker;
    private WorldPickTarget? m_gpuTarget;
    private string? m_gpuLabel;

    // Presentation destinations outline their pane. Build mode and a locally opened passthrough pane also demand
    // the pixel's geometry identity; nothing is sampled while neither consumer is active.
    private void DemandGpuHover(bool shown, int slot, SourceMapping? pane, float localX, float localY, bool inside) {
        SdfWorldPicker? picker = null;

        if (shown && (m_bindings.IsBuilding(slot: slot) || (pane?.Destination == SourceDestination.Passthrough))) {
            if (pane is not null) {
                var hit = m_panes.HoveredPick.Hit;

                localX = ((float)(((double)hit.Coordinate.X) / pane.SourceWidth));
                localY = ((float)(((double)hit.Coordinate.Y) / pane.SourceHeight));
                picker = m_panes.FindPicker(instance: pane.Source.Name);
            } else if (inside) {
                picker = m_panes.FindPicker(instance: WorldViewGraphs.WorldInstance);
            }
        }
        if (!ReferenceEquals(objA: m_hoverPicker, objB: picker)) {
            m_hoverPicker?.Clear();
            m_hoverPicker = picker;
        }
        if ((picker is not null) && (localX >= 0) && (localX < 1) && (localY >= 0) && (localY < 1)) {
            _ = picker.Demand(x: localX, y: localY);
        }
    }
    private string? GpuHoverLabel() {
        var target = (m_hoverPicker?.Result?.Target as WorldPickTarget);

        if (!ReferenceEquals(objA: m_gpuTarget, objB: target)) {
            m_gpuTarget = target;
            m_gpuLabel = target switch {
                { Placement: { } placement } => $"placement '{placement}'",
                { BodyIndex: { } body } => $"body {body}",
                _ => null,
            };
        }
        return m_gpuLabel;
    }
}
