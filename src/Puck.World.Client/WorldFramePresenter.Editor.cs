using Puck.SdfVm;

namespace Puck.World.Client;

public sealed partial class WorldFramePresenter {
    /// <summary>Gets each seat's editor state: its grid and snapping over the document's <c>editor</c> section, which the
    /// views of building seats draw.</summary>
    public WorldEditorSeats Editor { get; }

    // The grid a seat's view draws this frame, in the world the seat is presented in: none unless the seat builds;
    // otherwise its grid over that world's editor section, on the working plane its mode names (a fixed height, or the
    // surface under its pointer when following, else the base of the placement it last put down there), with the
    // lattice of the reference it captured there.
    private GridOverlayState SeatGrid(int slot) {
        if (!m_seatBindings.IsBuilding(slot: slot)) {
            return GridOverlayState.Hidden;
        }

        var world = m_continuum.Route(slot: slot).Endpoint.Identity;
        var definition = (m_continuum.PresentedElsewhere(slot: slot)?.Definition ?? m_client.Definition);
        var grid = Editor.GridOf(document: definition.Editor, slot: slot);

        if (!grid.Visible) {
            return GridOverlayState.Hidden;
        }

        var snap = Editor.SnapOf(document: definition.Editor, slot: slot);
        var planeY = grid.PlaneY;

        if (grid.Mode == WorldEditorGridMode.Follow) {
            if (Editor.PointerProbe?.Invoke(arg: slot) is { } hit) {
                Editor.Follow(height: hit.Point.Y, slot: slot, world: world);
            }

            planeY = (Editor.FollowedHeightOf(slot: slot, world: world) ?? (BaseOf(definition: definition, id: Editor.CurrentOf(slot: slot, world: world)) ?? planeY));
        }

        return WorldEditorGeometry.Overlay(
            grid: grid,
            planeY: planeY,
            reference: (((Editor.ReferenceOf(slot: slot, world: world) is { } id) && (WorldDefinitionRows.FindPlacement(id: id, placements: definition.Placements) is { } placement))
                ? WorldEditorGeometry.ReferenceOf(definition: definition, pitch: grid.ResolvedPitch, placement: placement)
                : null),
            snap: snap
        );
    }
    // The height of a placement's resolved position, or null when there is none of that id.
    private static float? BaseOf(WorldDefinition definition, string? id) => (((id is not null) && (WorldDefinitionRows.FindPlacement(id: id, placements: definition.Placements) is { } placement))
        ? WorldDefinitionRows.ResolvedPosition(definition: definition, placement: placement).Y
        : null);
}
