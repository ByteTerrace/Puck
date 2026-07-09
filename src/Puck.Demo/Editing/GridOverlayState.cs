using System.Numerics;

namespace Puck.Demo.Editing;

/// <summary>
/// The grid-lock overlay's shared VALUE (grid-locking §4/§8, the <c>SdfDebugMode</c> facade precedent) — the thin
/// data the visualization channel uploads, computed once from an editor's session-only snap state. Both editors
/// (<c>WorldScene</c>/<c>CreatorScene</c>) compute it via <see cref="From"/>; the frame source receives the fields as
/// primitives (never naming this type, to stay under its coupling ceiling). Reads authoring state only — never the
/// deterministic simulation.
/// </summary>
/// <param name="Flags">Overlay flags: bit0 = draw the world floor grid, bit1 = draw the object grid.</param>
/// <param name="WorldPitch">The world floor grid's per-axis lattice pitch (X/Z).</param>
/// <param name="FloorY">The floor plane height the world grid draws on.</param>
/// <param name="ObjectOrigin">The object grid's reference frame origin (world space).</param>
/// <param name="ObjectFrame">The object grid's reference frame orientation.</param>
/// <param name="ObjectPitch">The object grid's per-axis in-plane pitch (reference-local X/Z).</param>
/// <param name="ObjectPatchRadius">The object grid's finite-patch radius (reference-local units).</param>
internal readonly record struct GridOverlayState(
    uint Flags,
    Vector2 WorldPitch,
    float FloorY,
    Vector3 ObjectOrigin,
    Quaternion ObjectFrame,
    Vector2 ObjectPitch,
    float ObjectPatchRadius
) {
    /// <summary>No overlay — the all-zero upload a frame outside an editor (or with the grid hidden) sends,
    /// byte-identical to before the channel existed.</summary>
    public static GridOverlayState None =>
        new(Flags: 0u, WorldPitch: Vector2.Zero, FloorY: 0f, ObjectOrigin: Vector3.Zero, ObjectFrame: Quaternion.Identity, ObjectPitch: Vector2.Zero, ObjectPatchRadius: 0f);

    /// <summary>Composes the overlay from an editor's snap state. The world floor grid draws whenever the grid is
    /// visible (session default: visible while snap is on — the user may also want to SEE it to aim while snap is
    /// off); the object grid additionally when a reference is captured. Its finite patch covers the reference's
    /// footprint plus a couple of lattice cells.</summary>
    /// <param name="snap">The editor's snap configuration.</param>
    /// <param name="gridVisible">Whether the editor's grid overlay is visible.</param>
    /// <param name="floorY">The room's floor plane height.</param>
    /// <returns>The composed overlay, or <see cref="None"/> when no grid should draw.</returns>
    public static GridOverlayState From(SnapConfig snap, bool gridVisible, float floorY) {
        if (!gridVisible) {
            return None;
        }

        var flags = 1u;
        var worldPitch = new Vector2(PositiveOr(value: snap.Pitch.X, fallback: 0.25f), PositiveOr(value: snap.Pitch.Z, fallback: 0.25f));
        var objectOrigin = Vector3.Zero;
        var objectFrame = Quaternion.Identity;
        var objectPitch = Vector2.Zero;
        var objectPatchRadius = 0f;

        if (snap.Reference is { } reference) {
            flags |= 2u;
            objectOrigin = reference.Origin;
            objectFrame = reference.Frame;
            objectPitch = new Vector2(PositiveOr(value: reference.Pitch.X, fallback: 0.25f), PositiveOr(value: reference.Pitch.Z, fallback: 0.25f));
            objectPatchRadius = ((MathF.Max(reference.LocalHalfExtents.X, reference.LocalHalfExtents.Z) * 2.5f) + (MathF.Max(objectPitch.X, objectPitch.Y) * 2f));
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
