using Puck.SdfVm;

namespace Puck.World.Client;

public sealed partial class WorldFramePresenter {
    /// <summary>Gets each seat's editor state: its grid and snapping over the document's <c>editor</c> section, which the
    /// views of building seats draw.</summary>
    public WorldEditorSeats Editor { get; }

    // The grid a seat's view draws this frame, for the world the seat edits: none unless the seat builds;
    // otherwise its grid over that world's editor section, on the working plane its mode names (a fixed height, or the
    // surface under its pointer when following, else the base of the placement it last put down there), with the
    // lattice of the reference it captured there.
    private GridOverlayState SeatGrid(int slot) {
        if (!m_seatBindings.IsBuilding(slot: slot)) {
            return GridOverlayState.Hidden;
        }

        // The world the seat edits, whether it is drawn in that world's own scene or across an adjacency in this frame:
        // its editor section sets the pitch and snapping the seat's edits land on.
        var endpoint = m_continuum.Route(slot: slot).Endpoint;
        var world = endpoint.Identity;
        var definition = (string.Equals(a: world, b: WorldDefinitionLoader.BootInstanceName, comparisonType: StringComparison.Ordinal)
            ? m_client.Definition
            : endpoint.Definition);
        var grid = Editor.GridOf(document: definition.Editor, slot: slot);

        // The frame the seat's view draws the edited world in: the same frame, or across an adjacency. A seat whose view
        // nothing relates to the world it edits draws no grid rather than one in the wrong frame.
        if (!grid.Visible || !m_continuum.TryEditingPath(path: out var path, slot: slot)) {
            return GridOverlayState.Hidden;
        }

        var snap = Editor.SnapOf(document: definition.Editor, slot: slot);
        var planeY = grid.PlaneY;

        // Heights here are the edited world's own: the pointer probe answers in that world's frame.
        if (grid.Mode == WorldEditorGridMode.Follow) {
            if (Editor.PointerProbe?.Invoke(arg: slot) is { } hit) {
                Editor.Follow(height: hit.Point.Y, slot: slot, world: world);
            }

            planeY = (Editor.FollowedHeightOf(slot: slot, world: world) ?? (BaseOf(definition: definition, id: Editor.CurrentOf(slot: slot, world: world)) ?? planeY));
        }

        return WorldEditorGeometry.InViewFrame(
            grid: WorldEditorGeometry.Overlay(
                grid: grid,
                planeY: planeY,
                reference: (((Editor.ReferenceOf(slot: slot, world: world) is { } id) && (WorldDefinitionRows.FindPlacement(id: id, placements: definition.Placements) is { } placement))
                    ? WorldEditorGeometry.ReferenceOf(definition: definition, pitch: grid.ResolvedPitch, placement: placement)
                    : null),
                snap: snap
            ),
            path: path
        );
    }
    // The height of a placement's resolved position, or null when there is none of that id.
    private static float? BaseOf(WorldDefinition definition, string? id) => (((id is not null) && (WorldDefinitionRows.FindPlacement(id: id, placements: definition.Placements) is { } placement))
        ? WorldDefinitionRows.ResolvedPosition(definition: definition, placement: placement).Y
        : null);
}
