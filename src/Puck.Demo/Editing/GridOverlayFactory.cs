using System.Numerics;
using Puck.SdfVm;

namespace Puck.Demo.Editing;

/// <summary>
/// Composes a <see cref="GridOverlayState"/> from an editor's session-only <see cref="SnapConfig"/> — the
/// demo-side half of the grid-lock overlay split (grid-locking §4/§8): the engine-side type carries the value,
/// this factory carries the authoring-state-to-value policy so <c>Puck.SdfVm</c> never depends on the editors'
/// snap model.
/// </summary>
internal static class GridOverlayFactory {
    /// <summary>Composes the overlay from an editor's snap state. The world floor grid draws whenever the grid is
    /// visible (session default: visible while snap is on — the user may also want to SEE it to aim while snap is
    /// off); the object grid additionally when a reference is captured. Its finite patch covers the reference's
    /// footprint plus a couple of lattice cells.</summary>
    /// <param name="snap">The editor's snap configuration.</param>
    /// <param name="gridVisible">Whether the editor's grid overlay is visible.</param>
    /// <param name="floorY">The room's floor plane height.</param>
    /// <returns>The composed overlay, or <see cref="GridOverlayState.None"/> when no grid should draw.</returns>
    public static GridOverlayState From(SnapConfig snap, bool gridVisible, float floorY) {
        if (!gridVisible) {
            return GridOverlayState.None;
        }

        var flags = 1u;
        var worldPitch = new Vector2(x: PositiveOr(value: snap.Pitch.X, fallback: 0.25f), y: PositiveOr(value: snap.Pitch.Z, fallback: 0.25f));
        var objectOrigin = Vector3.Zero;
        var objectFrame = Quaternion.Identity;
        var objectPitch = Vector2.Zero;
        var objectPatchRadius = 0f;

        if (snap.Reference is { } reference) {
            flags |= 2u;
            objectOrigin = reference.Origin;
            objectFrame = reference.Frame;
            objectPitch = new Vector2(x: PositiveOr(value: reference.Pitch.X, fallback: 0.25f), y: PositiveOr(value: reference.Pitch.Z, fallback: 0.25f));
            objectPatchRadius = ((MathF.Max(x: reference.LocalHalfExtents.X, y: reference.LocalHalfExtents.Z) * 2.5f) + (MathF.Max(x: objectPitch.X, y: objectPitch.Y) * 2f));
        }

        return new GridOverlayState(
            Flags: flags,
            FloorY: floorY,
            ObjectFrame: objectFrame,
            ObjectOrigin: objectOrigin,
            ObjectPatchRadius: objectPatchRadius,
            ObjectPitch: objectPitch,
            WorldPitch: worldPitch
        );
    }

    private static float PositiveOr(float value, float fallback) =>
        ((value > 0f) ? value : fallback);
}
