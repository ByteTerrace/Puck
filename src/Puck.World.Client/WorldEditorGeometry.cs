using System.Numerics;
using Puck.SdfVm;
using Puck.World.Authoring;

namespace Puck.World.Client;

/// <summary>The editor's one statement of what a seat's grid draws and what a captured reference is: the grid overlay
/// policy the presenter writes into each seat's view, and the snap reference both that overlay and the snapping verbs
/// align to. Pure functions of the seat's grid and snapping and the document.</summary>
public static class WorldEditorGeometry {
    /// <summary>Returns the half extents a placement stands for, in its own axes: half its scale on each axis, a unit
    /// creation's.</summary>
    /// <param name="placement">The placement.</param>
    /// <returns>The half extents, in world units.</returns>
    public static Vector3 HalfExtentsOf(WorldPlacement placement) {
        ArgumentNullException.ThrowIfNull(argument: placement);

        return new Vector3(value: (0.5f * placement.Scale));
    }
    /// <summary>Returns the snap reference a placement makes: its resolved world position and yaw, its half extents,
    /// the grid's pitch as its own lattice, and a face capture radius of half the finer horizontal pitch.</summary>
    /// <param name="definition">The document the placement is resolved in.</param>
    /// <param name="placement">The placement.</param>
    /// <param name="pitch">The seat's grid pitch, in world units.</param>
    /// <returns>The reference.</returns>
    public static SnapReference ReferenceOf(WorldDefinition definition, WorldPlacement placement, Vector3 pitch) {
        var frame = WorldDefinitionRows.ResolvedFrame(
            definition: definition,
            placement: placement
        );

        return new SnapReference(
            FaceRadius: (0.5f * MathF.Min(x: pitch.X, y: pitch.Z)),
            Frame: Quaternion.CreateFromAxisAngle(
                angle: (frame.YawDegrees * (MathF.PI / 180f)),
                axis: Vector3.UnitY
            ),
            LocalHalfExtents: HalfExtentsOf(placement: placement),
            Origin: frame.Position,
            Pitch: pitch
        );
    }
    /// <summary>Returns the grid a building seat's view draws: nothing while the grid is hidden; otherwise the world
    /// lattice on every surface in <see cref="WorldEditorGridMode.Surface"/>, or on the working plane at
    /// <paramref name="planeY"/> in the other modes; and, with a captured reference, the reference's own lattice within
    /// the snapping's patch radius, measured in multiples of its largest half extent plus two pitches.</summary>
    /// <param name="grid">The seat's grid.</param>
    /// <param name="snap">The seat's snapping.</param>
    /// <param name="planeY">The working plane's height, in world units.</param>
    /// <param name="reference">The captured reference, or <see langword="null"/> for none.</param>
    /// <returns>The view's grid.</returns>
    public static GridOverlayState Overlay(WorldEditorGrid grid, WorldEditorSnap snap, float planeY, SnapReference? reference) {
        ArgumentNullException.ThrowIfNull(argument: grid);
        ArgumentNullException.ThrowIfNull(argument: snap);

        if (!grid.Visible) {
            return GridOverlayState.Hidden;
        }

        var pitch = grid.ResolvedPitch;
        var state = (GridOverlayState.Hidden with {
            Flags = ((grid.Mode == WorldEditorGridMode.Surface)
                ? GridOverlayFlags.World | GridOverlayFlags.Surface
                : GridOverlayFlags.World),
            LineWidth = grid.LineWidth,
            PlaneY = planeY,
            WorldPitch = pitch,
        });

        if (reference is not { } captured) {
            return state;
        }

        var half = captured.LocalHalfExtents;

        return (state with {
            Flags = state.Flags | GridOverlayFlags.Object,
            ObjectFrame = captured.Frame,
            ObjectOrigin = captured.Origin,
            ObjectPatchRadius = ((MathF.Max(x: half.X, y: MathF.Max(x: half.Y, y: half.Z)) * snap.ObjectPatchRadius) + (2f * MathF.Max(x: pitch.X, y: pitch.Z))),
            ObjectPitch = captured.Pitch,
        });
    }
}
