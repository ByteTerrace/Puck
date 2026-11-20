using System.Numerics;
using Puck.Shaders;

namespace Puck.SdfVm;

/// <summary>The key light's distant shadow map (<c>render.distantShadows</c>, a device table's row): one more entry of
/// the cascade set past the far cascade, which draws the frame's static draws within <see cref="Reach"/> metres of the
/// eye and holds them until the light turns past <see cref="TurnRadians"/>, the eye crosses a cell of
/// the map's grid (<see cref="CellOf"/>) or a static draw changes. Its texels are its own, so its reach costs the near
/// and far cascades no precision. The default is off. Presentation only.</summary>
/// <param name="Reach">The distance, in metres from the eye in every direction, the map covers; positive while on.</param>
/// <param name="Texels">The texels along each side of the map's depth at full shadow quality, a power of two from
/// <see cref="SdfWorldPackage.MeshDistantShadowMinResolution"/> through
/// <see cref="SdfWorldPackage.MeshDistantShadowMaxResolution"/> while on.</param>
/// <param name="TurnRadians">The light turn, in radians, a held map tolerates before it is drawn again: the most the
/// shadows it holds lag a moving light by.</param>
/// <param name="Frames">The frames one redraw is spread over (<see cref="Slices"/>): each draws a share of the casters
/// into the back map, and the map receivers read changes only when the last share lands.</param>
public readonly record struct SdfDistantShadows(float Reach, int Texels, float TurnRadians, int Frames = 1) {
    /// <summary>The cells of the map's grid across its reach: the eye may move one cell, an eighth of the reach, before
    /// the map is centred again.</summary>
    public const int CellsPerReach = 8;

    /// <summary>Gets the shares one redraw is drawn in, one a frame: <see cref="Frames"/>, at least one.</summary>
    public int Slices => Math.Max(val1: Frames, val2: 1);
    /// <summary>Gets whether a redraw is spread over more than one frame through the back map.</summary>
    public bool IsSliced => (IsOn && (Slices > 1));
    /// <summary>Gets whether the map is drawn at all.</summary>
    public bool IsOn => ((Reach > 0f) && (Texels > 0));
    /// <summary>Gets the side of one cell of the grid the map's centre snaps to, in metres.</summary>
    public float Cell => (Reach / CellsPerReach);
    /// <summary>Gets the half extent of the map's square, in metres: its reach and the most an eye inside a cell lies from
    /// the cell's centre, rounded up to a whole metre, so a receiver at the reach in any direction from anywhere in the
    /// cell is inside it.</summary>
    public float HalfExtent => MathF.Ceiling(x: (Reach + (Cell * 0.8660254f)));

    /// <summary>Returns the texels along each side of the map's depth at a view's shadow resolution: the authored size
    /// halved once for each step the resolution falls below full, down to
    /// <see cref="SdfWorldPackage.MeshDistantShadowMinResolution"/>; zero while the map is off.</summary>
    /// <param name="resolution">The view's shadow resolution.</param>
    /// <returns>The texels a side.</returns>
    public int TexelsAt(SdfShadowResolution resolution) => (IsOn
        ? Math.Max(val1: (Texels >> ((int)resolution)), val2: Math.Min(val1: Texels, val2: SdfWorldPackage.MeshDistantShadowMinResolution))
        : 0);
    /// <summary>Returns the cell of the map's grid an eye stands in, which the map is centred on.</summary>
    /// <param name="eye">The eye, in world space.</param>
    /// <returns>The cell's centre, in world space.</returns>
    public Vector3 CellOf(Vector3 eye) {
        var cell = Cell;

        return new Vector3(x: (MathF.Round(x: (eye.X / cell)) * cell), y: (MathF.Round(x: (eye.Y / cell)) * cell), z: (MathF.Round(x: (eye.Z / cell)) * cell));
    }
}
