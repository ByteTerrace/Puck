using Puck.Shaders;

namespace Puck.SdfVm;

/// <summary>The key light's distant shadow map (<c>render.distantShadows</c>, a device table's row): one more entry of
/// the cascade set past the far cascade, which draws the frame's static draws within <see cref="Reach"/> metres of the
/// eye and holds them until the light turns past <see cref="TurnRadians"/>, the eye crosses a cell of the entry's grid
/// (<see cref="SdfMeshShadowCascades.CellOf"/>) or a static draw changes. Its texels are its own, so its reach costs the
/// near and far cascades no precision. The default is off. Presentation only.</summary>
/// <param name="Reach">The distance, in metres from the eye in every direction, the map covers; positive while on.</param>
/// <param name="Texels">The texels along each side of the map's depth at full shadow quality, a power of two from
/// <see cref="SdfWorldPackage.MeshDistantShadowMinResolution"/> through
/// <see cref="SdfWorldPackage.MeshDistantShadowMaxResolution"/> while on.</param>
/// <param name="TurnRadians">The light turn, in radians, a held map tolerates before it is drawn again: the most the
/// shadows it holds lag a moving light by.</param>
/// <param name="Frames">The frames one redraw is spread over (<see cref="Slices"/>): each draws a share of the casters
/// into the back layer, and the map receivers read changes only when the last share lands.</param>
public readonly record struct SdfDistantShadows(float Reach, int Texels, float TurnRadians, int Frames = 1) {
    /// <summary>Gets the shares one redraw is drawn in, one a frame: <see cref="Frames"/>, at least one.</summary>
    public int Slices => Math.Max(val1: Frames, val2: 1);
    /// <summary>Gets whether the map is drawn at all.</summary>
    public bool IsOn => ((Reach > 0f) && (Texels > 0));

    /// <summary>Returns the texels along each side of the map's depth at a view's shadow resolution: the authored size
    /// halved once for each step the resolution falls below full, down to
    /// <see cref="SdfWorldPackage.MeshDistantShadowMinResolution"/>; zero while the map is off.</summary>
    /// <param name="resolution">The view's shadow resolution.</param>
    /// <returns>The texels a side.</returns>
    public int TexelsAt(SdfShadowResolution resolution) => (IsOn
        ? Math.Max(val1: (Texels >> ((int)resolution)), val2: Math.Min(val1: Texels, val2: SdfWorldPackage.MeshDistantShadowMinResolution))
        : 0);
}
