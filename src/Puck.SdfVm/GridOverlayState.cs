using System.Numerics;

namespace Puck.SdfVm;

/// <summary>The editor grid one view draws, which its pass block carries: a seat building with the grid on renders
/// its own, and a seat playing renders <see cref="Hidden"/>. Presentation state, never simulation.</summary>
/// <param name="Flags">Which grids draw.</param>
/// <param name="WorldPitch">The world lattice's pitch on its own X, Y and Z (<see cref="WorldFrame"/>), in world units; a
/// component at or below zero draws no lines across that axis.</param>
/// <param name="PlaneY">The working plane's height along the lattice's own up, in world units, which
/// <see cref="GridOverlayFlags.World"/> draws on.</param>
/// <param name="LineWidth">The drawn line's width in pixels at the surface; the on-plane band scales with it, so the
/// lines neither vanish up close nor shimmer far away.</param>
/// <param name="ObjectOrigin">The object grid's reference origin, in world space.</param>
/// <param name="ObjectFrame">The object grid's reference orientation.</param>
/// <param name="ObjectPitch">The object lattice's pitch on the reference's own X, Y and Z, in reference-local units; a
/// component at or below zero draws no lines across that axis.</param>
/// <param name="ObjectPatchRadius">The object grid's patch radius around its origin, in reference-local units.</param>
/// <param name="WorldOrigin">The origin of the world the lattice belongs to, in the view's world space: zero for a seat
/// editing the world its view draws, and that world's origin carried through the adjacency it is drawn across
/// otherwise.</param>
/// <param name="WorldFrame">The orientation of the world the lattice belongs to in the view's world space: identity for a
/// seat editing the world its view draws. The world lattice, its pitch, <see cref="PlaneY"/> and its up are all in that
/// frame, so the lines drawn are the lines the seat's edits snap to.</param>
public readonly record struct GridOverlayState(
    GridOverlayFlags Flags,
    Vector3 WorldPitch,
    float PlaneY,
    float LineWidth,
    Vector3 ObjectOrigin,
    Quaternion ObjectFrame,
    Vector3 ObjectPitch,
    float ObjectPatchRadius,
    Vector3 WorldOrigin,
    Quaternion WorldFrame
) {
    /// <summary>The line width a grid draws at by default, in pixels.</summary>
    public const float DefaultLineWidth = 1.5f;

    /// <summary>Gets the state of a view that draws no grid.</summary>
    public static GridOverlayState Hidden { get; } = new(
        Flags: GridOverlayFlags.None,
        LineWidth: DefaultLineWidth,
        ObjectFrame: Quaternion.Identity,
        ObjectOrigin: Vector3.Zero,
        ObjectPatchRadius: 0f,
        ObjectPitch: Vector3.Zero,
        PlaneY: 0f,
        WorldFrame: Quaternion.Identity,
        WorldOrigin: Vector3.Zero,
        WorldPitch: Vector3.Zero
    );
    /// <summary>Gets whether the view draws any grid.</summary>
    public bool IsVisible => (Flags != GridOverlayFlags.None);
}
