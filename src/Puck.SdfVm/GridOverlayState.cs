using System.Numerics;

namespace Puck.SdfVm;

/// <summary>
/// The grid-lock overlay's shared VALUE (grid-locking §4/§8, the <c>SdfDebugMode</c> facade precedent) — the thin
/// data the visualization channel uploads, computed once from an editor's session-only snap state. Both editors
/// (<c>WorldScene</c>/<c>CreatorScene</c>) compute it via <c>Puck.Demo.Editing.GridOverlayFactory.From</c>; the frame
/// source receives the fields as primitives (never naming this type, to stay under its coupling ceiling). Reads
/// authoring state only — never the deterministic simulation.
/// </summary>
/// <param name="Flags">Overlay flags: bit0 = draw the world floor grid, bit1 = draw the object grid.</param>
/// <param name="WorldPitch">The world floor grid's per-axis lattice pitch (X/Z).</param>
/// <param name="FloorY">The floor plane height the world grid draws on.</param>
/// <param name="ObjectOrigin">The object grid's reference frame origin (world space).</param>
/// <param name="ObjectFrame">The object grid's reference frame orientation.</param>
/// <param name="ObjectPitch">The object grid's per-axis in-plane pitch (reference-local X/Z).</param>
/// <param name="ObjectPatchRadius">The object grid's finite-patch radius (reference-local units).</param>
public readonly record struct GridOverlayState(
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
}
