using Puck.Commands;
using Puck.SdfVm;
using Puck.World.Client;

namespace Puck.World;

internal sealed partial class WorldCursorFeed {
    private SdfWorldPicker? m_hoverPicker;

    /// <summary>The optional demand shared with the editor inspector.</summary>
    public WorldEditorSeats? EditorSeats { get; init; }
    /// <summary>The last completed pixel sampled for the pointer's seat, with its captured identities.</summary>
    public SdfPickResult? Pick => m_hoverPicker?.Result;
    /// <summary>The view the demanded pointer pixel follows, including a passthrough pane's own residency.</summary>
    public SdfWorldView? PickView => m_hoverPicker?.View;
    /// <summary>The exact render instance the pointer pixel samples, or null while no consumer demands it.</summary>
    public string? PickInstance { get; private set; }

    private WorldPickTarget? m_gpuTarget;
    private string? m_gpuLabel;

    // Presentation destinations outline their pane. Build mode and a locally opened passthrough pane also demand
    // the pixel's geometry identity; nothing is sampled while neither consumer is active.
    private void DemandGpuHover(bool shown, int slot, SourceMapping? pane, float localX, float localY, bool inside) {
        SdfWorldPicker? picker = null;
        string? instance = null;

        if (shown && (m_bindings.IsBuilding(slot: slot) || (EditorSeats?.InspectorEnabled(slot: slot) ?? false) || (pane?.Destination == SourceDestination.Passthrough))) {
            if (pane is not null) {
                var hit = m_panes.HoveredPick.Hit;

                localX = ((float)(((double)hit.Coordinate.X) / pane.SourceWidth));
                localY = ((float)(((double)hit.Coordinate.Y) / pane.SourceHeight));
                instance = pane.Source.Name;
                picker = m_panes.FindPicker(instance: instance);
            } else if (inside) {
                instance = (m_viewports.Seat(slot: slot).RenderInstance ?? WorldViewGraphs.WorldInstance);
                picker = m_panes.FindPicker(instance: instance);
            }
        }
        PickInstance = ((picker is not null) ? instance : null);
        if (!ReferenceEquals(objA: m_hoverPicker, objB: picker)) {
            m_hoverPicker?.Clear();
            m_hoverPicker = picker;
        }
        if ((picker is not null) && (localX >= 0) && (localX < 1) && (localY >= 0) && (localY < 1)) {
            _ = picker.Demand(x: localX, y: localY, surface: (EditorSeats?.InspectorEnabled(slot: slot) ?? false));
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
