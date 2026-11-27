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
/// <param name="ScreenIndex">The screen's identity (0 through <see cref="SdfProgramBuilder.MaxScreenIdentities"/> − 1,
/// see <c>Puck.SdfVm.SdfWorldTables.SetScreenBound</c>) this
/// surface samples when a source is bound. An emitter face (<see cref="EmitterMaterial"/>) carries its ordinal among
/// the program's emitter faces (<see cref="SdfAppearance.EmitterFaces"/>) here instead: it names no screen material, and
/// a residency gives it an identity of its own past every screen it binds.</param>
/// <param name="EmitterMaterial">The material whose emission and paints an emitter face lights its surroundings with, or
/// -1 for a sampled screen. An emitter face is light alone: the shape whose face it is keeps its own material and
/// paints, and the face lights what stands before it as a screen's face does, with the mean of what the material shows
/// over the face (its painted albedo times its emissive), reduced on the GPU in the screens' emission pass.</param>
/// <param name="Instance">The program instance an emitter face was declared in (the builder's instance count when it was
/// declared), or -1 for a sampled screen. A face whose instance lies in a mesh template's range draws only where a copy
/// draws that template, so it lights only there, carried by each copy's pose (<c>Puck.SdfVm.SdfWorldResidency</c>'s
/// emitter faces); any other face lights where it was declared.</param>
public readonly record struct SdfScreenSurface(
    Vector3 Origin,
    Vector3 Right,
    Vector3 Up,
    float HalfWidth,
    float HalfHeight,
    int ScreenIndex,
    int EmitterMaterial = -1,
    int Instance = -1
) {
    /// <summary>Gets whether this is an emitter face: light alone, reduced from its material rather than sampled from a
    /// source.</summary>
    public bool IsEmitter => (EmitterMaterial >= 0);

    /// <summary>Returns the face carried by a copy's root matrix: its origin placed, its axes turned and its half-extents
    /// scaled by the matrix, everything else kept.</summary>
    /// <param name="objectToWorld">The copy's root matrix, a uniform scale, a rotation and a translation, in the
    /// row-vector convention of <see cref="Matrix4x4"/>.</param>
    /// <returns>The carried face.</returns>
    public SdfScreenSurface CarriedBy(Matrix4x4 objectToWorld) {
        var right = Vector3.TransformNormal(normal: Right, matrix: objectToWorld);
        var up = Vector3.TransformNormal(normal: Up, matrix: objectToWorld);
        var scale = right.Length();

        return (this with {
            HalfHeight = (HalfHeight * scale),
            HalfWidth = (HalfWidth * scale),
            Origin = Vector3.Transform(position: Origin, matrix: objectToWorld),
            Right = (right / scale),
            Up = Vector3.Normalize(value: up),
        });
    }
}
