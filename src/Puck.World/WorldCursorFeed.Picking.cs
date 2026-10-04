using Puck.Commands;
using Puck.SdfVm;
using Puck.World.Client;

namespace Puck.World;

internal sealed partial class WorldCursorFeed : IDisposable {
    private SdfWorldPicker? m_hoverPicker;
    private SdfWorldPicker? m_pointerPicker;
    private int m_pointerSlot;
    private float m_pointerX;
    private float m_pointerY;
    private string? m_pointerInstance;
    private readonly WorldExplainPick m_explain = new();
    private WorldIndirectReferenceResult? m_explanationReference;

    /// <summary>The optional demand shared with the editor inspector.</summary>
    public WorldEditorSeats? EditorSeats { get; init; }
    /// <summary>The last completed pixel sampled for the pointer's seat, with its captured identities.</summary>
    public SdfPickResult? Pick => m_hoverPicker?.Result ?? (HoldsCapturedView ? m_explain.Captured : null);
    /// <summary>The view the demanded pointer pixel follows, including a passthrough pane's own residency.</summary>
    public SdfWorldView? PickView => m_hoverPicker?.View ?? (HoldsCapturedView ? m_explain.CapturedView : null);
    /// <summary>The exact render instance the pointer pixel samples, or null while no consumer demands it.</summary>
    public string? PickInstance { get; private set; }
    /// <summary>The latest explicit explanation, retaining its fenced census even after the pointer moves.</summary>
    public WorldExplainPick Explanation => m_explain;
    /// <summary>The retained CPU reference only when the inspector displays the same immutable receiver answer.</summary>
    public WorldIndirectReferenceResult? ReferenceOf(SdfPickResult? pick) =>
        (pick?.Indirect is { } indirect && ReferenceEquals(indirect, m_explain.Captured?.Indirect)) ? m_explanationReference : null;
    /// <summary>Stores the once-evaluated reference beside the explanation's completed immutable answer.</summary>
    public void RetainReference(WorldIndirectReferenceResult? reference) => m_explanationReference = reference;
    private bool HoldsCapturedView => (m_explain.CapturedSlot == m_pointerSlot) && (m_pointerPicker?.View is { } view) && (view == m_explain.CapturedView);

    /// <summary>Requests one pixel through the same route used by hover and the inspector.</summary>
    /// <param name="slot">The acting seat, which must own the current pointer route.</param>
    /// <param name="describe">The callback invoked once with the fenced answer.</param>
    /// <returns>The pending settlement or a named refusal until the current route has actually rendered.</returns>
    public CommandResult Explain(int slot, Func<SdfPickResult, CommandResult> describe) {
        // A registered picker can already follow a replacement whose graph is still building or refused. Its view
        // alone is no evidence that this route can answer a pixel; use the renderer's existing binding completion.
        if (slot != m_pointerSlot || m_pointerInstance is not { } instance ||
            m_panes.Pickers?.HasRenderedResolvedView(instance: instance) != true) {
            return CommandResult.Error("[world.explain: no rendered pixel at the acting seat or pane]");
        }
        return m_explain.Request(slot, m_pointerPicker, m_pointerX, m_pointerY, describe);
    }

    private readonly WorldPickLabel m_gpuLabel = new();

    // Resolve the pointer once even when no continuous consumer is enabled. Explanation takes one surfaced demand
    // from this same route; ordinary hover never overwrites it while its fence is outstanding.
    private void DemandGpuHover(bool shown, int slot, SourceMapping? pane, float localX, float localY, bool inside) {
        SdfWorldPicker? picker = null;
        string? instance = null;

        if (shown) {
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
        if (!float.IsFinite(localX) || !float.IsFinite(localY) || (localX < 0) || (localX >= 1) || (localY < 0) || (localY >= 1)) { picker = null; }
        m_pointerPicker = picker;
        m_pointerSlot = slot;
        m_pointerX = localX;
        m_pointerY = localY;
        m_pointerInstance = instance;
        var explaining = m_hoverPicker is { } held && m_explain.Holds(held);
        m_explain.Poll(slot, picker);
        var continuous = m_bindings.IsBuilding(slot: slot) || (EditorSeats?.InspectorEnabled(slot: slot) ?? false) || (pane?.Destination == SourceDestination.Passthrough);
        if ((picker is not null) && !continuous && !m_explain.Holds(picker)) { picker = null; }
        PickInstance = ((picker is not null || HoldsCapturedView) ? m_pointerInstance : null);
        if (!ReferenceEquals(objA: m_hoverPicker, objB: picker)) {
            // The explanation settled its own cancellation. A superseding consumer owns any newer request.
            if (!explaining) { m_hoverPicker?.Clear(); }
            m_hoverPicker = picker;
        }
        if (picker is not null) {
            m_explain.Demand(picker, x: localX, y: localY, surface: (EditorSeats?.InspectorEnabled(slot: slot) ?? false));
        }
    }
    private string? GpuHoverLabel() => m_gpuLabel.Of(pick: Pick);
    /// <summary>Settles the outstanding explanation before the presentation owner retires.</summary>
    public void Dispose() => m_explain.Dispose();
}
