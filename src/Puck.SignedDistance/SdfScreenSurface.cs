using System.Numerics;

namespace Puck.SignedDistance;

/// <summary>One screen surface's baseline world-space frame: the frame of a <see cref="SdfShapeType.ScreenSlab"/>
/// instance's
/// front face, so the world renderer can map a world-space hit point to a <c>[0,1]²</c> UV and sample a bound screen
/// source instead of shading the flat <see cref="SdfProgramBuilder.ScreenMaterialId"/> material. The program declares
/// this baseline at build time; a renderer may replace it per frame through its screen-surface transform seam (for
/// example, <c>Puck.SdfVm.ISdfFrameSource.ScreenSurfaceTransforms</c>) without rebuilding the program.</summary>
/// <param name="Origin">The front face's finite world-space center.</param>
/// <param name="Right">The unit world-space axis the UV's U increases along. Must be orthogonal to
/// <paramref name="Up"/>: the shader projects the hit point onto each axis independently, while the slab's geometry
/// rides the rotation derived from the pair, so only an orthonormal pair makes the UV agree with the geometry it
/// labels. Refused by <see cref="SdfProgramBuilder.ScreenSlab(Vector3, float, Vector3, Vector3, Vector3, int, SdfBlendOp, float)"/>
/// and by the <see cref="SdfProgram"/> constructor.</param>
/// <param name="Up">The unit world-space axis the UV's V increases against (V = 0 at the top).</param>
/// <param name="HalfWidth">The finite, positive half-extent along <paramref name="Right"/> (matches the slab's local X
/// half-extent).</param>
/// <param name="HalfHeight">The finite, positive half-extent along <paramref name="Up"/> (matches the slab's local Y
/// half-extent).</param>
/// <param name="ScreenIndex">The screen source slot (0 through <see cref="SdfProgramBuilder.MaxScreenSurfaces"/> − 1,
/// see <c>Puck.SdfVm.SdfWorldEngine.SetScreenSource</c>) this
/// surface samples when a source is bound.</param>
public readonly record struct SdfScreenSurface(
    Vector3 Origin,
    Vector3 Right,
    Vector3 Up,
    float HalfWidth,
    float HalfHeight,
    int ScreenIndex
);
