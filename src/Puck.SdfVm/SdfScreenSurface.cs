using System.Numerics;

namespace Puck.SdfVm;

/// <summary>One STATIC screen surface: the world-space frame of a <see cref="SdfShapeType.ScreenSlab"/> instance's
/// front face, so the world renderer can map a world-space hit point to a <c>[0,1]²</c> UV and sample a bound screen
/// source instead of shading the flat <see cref="SdfProgramBuilder.ScreenMaterialId"/> material. Declared once at
/// program-build time (the "program uploaded once" seam) — a screen on moving geometry is out of scope; stands don't
/// move.</summary>
/// <param name="Origin">The front face's world-space center.</param>
/// <param name="Right">The unit world-space axis the UV's U increases along; need not be orthogonal to
/// <paramref name="Up"/> in the packed data (the shader projects the hit point onto each axis independently), but an
/// orthonormal right/up pair (matching the slab's actual local X/Y axes) is what makes the UV agree with the slab's
/// geometry.</param>
/// <param name="Up">The unit world-space axis the UV's V increases against (V = 0 at the top).</param>
/// <param name="HalfWidth">The half-extent along <paramref name="Right"/> (matches the slab's local X half-extent).</param>
/// <param name="HalfHeight">The half-extent along <paramref name="Up"/> (matches the slab's local Y half-extent).</param>
/// <param name="ScreenIndex">The screen source slot (0 through <see cref="SdfProgramBuilder.MaxScreenSurfaces"/> − 1,
/// see <see cref="SdfWorldEngine.SetScreenSource"/>) this
/// surface samples when a source is bound.</param>
public readonly record struct SdfScreenSurface(
    Vector3 Origin,
    Vector3 Right,
    Vector3 Up,
    float HalfWidth,
    float HalfHeight,
    int ScreenIndex
);
