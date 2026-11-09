using Puck.Abstractions.Cameras;

namespace Puck.SdfVm;

/// <summary>
/// Which level of detail one raster pass draws each mesh draw at (<see cref="SdfMesh.Levels"/>), chosen beside the box cull
/// (<see cref="SdfMeshCull"/>) from the draw's box and its mesh's recorded errors. A view's mesh pass takes the coarsest
/// level whose error projects to at most <see cref="CoarserPixels"/> display pixels over the nearest point of the draw's
/// box, and goes back to a finer one only when the level it drew shows more than <see cref="FinerPixels"/>; a shadow
/// cascade takes the coarsest level whose error is within one of its texels. The glass pass, and every pass under
/// <see cref="Finest"/>, draws each mesh's own triangles. A draw with no host box draws its own triangles too.
/// Presentation only.
/// </summary>
public readonly struct SdfMeshDetail {
    /// <summary>The display pixels a level's error may project to for a view to take it.</summary>
    public const float CoarserPixels = 1f;
    /// <summary>The display pixels a level a view drew may project to before the view takes a finer one: the band between
    /// this and <see cref="CoarserPixels"/> keeps a draw standing at the boundary from switching every frame.</summary>
    public const float FinerPixels = 1.1f;

    private readonly CameraSnapshot m_camera;
    private readonly float m_pixels;
    private readonly float m_texel;
    private readonly int m_forced;
    private readonly byte m_kind;

    private SdfMeshDetail(byte kind, CameraSnapshot camera, float pixels, float texel, int forced) {
        m_kind = kind;
        m_camera = camera;
        m_pixels = pixels;
        m_texel = texel;
        m_forced = forced;
    }

    /// <summary>Gets the choice that draws every mesh's own triangles.</summary>
    public static SdfMeshDetail Finest => default;

    // GPU selection's level controls, paired with sdf-mesh-selection.hlsli.
    internal void WriteGpu(Span<System.Numerics.Vector4> rows, bool held) {
        rows[0] = new(m_kind, m_forced, m_pixels / CoarserPixels, m_texel);
        rows[1] = new(m_camera.Position, held ? 1f : 0f);
        rows[2] = new(m_camera.Forward, FinerPixels / CoarserPixels);
    }

    /// <summary>Returns the choice of a view's mesh pass.</summary>
    /// <param name="camera">The view's camera.</param>
    /// <param name="displayHeight">The pixels down the view's display extent, after upscaling.</param>
    /// <returns>The choice.</returns>
    public static SdfMeshDetail Of(in CameraSnapshot camera, uint displayHeight) {
        // A displacement of the surface by e at forward depth z moves its image by at most e / z tangent units times the
        // norm of the projection's derivative, sqrt(1 + a^2 + b^2) for an image point at tangents (a, b), whose largest
        // value is at the frustum's corner.
        var across = (MathF.Abs(x: camera.FrustumOffset.X) + (camera.AspectRatio * camera.TanHalfFieldOfView));
        var down = (MathF.Abs(x: camera.FrustumOffset.Y) + camera.TanHalfFieldOfView);
        var stretch = MathF.Sqrt(x: ((1f + (across * across)) + (down * down)));

        return new SdfMeshDetail(camera: camera, forced: 0, kind: 1, pixels: (((0.5f * Math.Max(val1: displayHeight, val2: 1u)) / camera.TanHalfFieldOfView) * stretch), texel: 0f);
    }
    /// <summary>Returns the choice of one of a view's shadow cascades.</summary>
    /// <param name="cascade">The cascade's light camera.</param>
    /// <param name="texels">The texels across a side of the cascade's depth.</param>
    /// <returns>The choice.</returns>
    public static SdfMeshDetail Of(in SdfMeshShadowCamera cascade, int texels) =>
        new(camera: default, forced: 0, kind: 2, pixels: 0f, texel: cascade.TexelOf(texels: texels));
    /// <summary>Returns the choice that draws every mesh at one level, or its coarsest where it has fewer.</summary>
    /// <param name="level">The level; zero is each mesh's own triangles.</param>
    /// <returns>The choice.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="level"/> is negative.</exception>
    public static SdfMeshDetail Forced(int level) {
        ArgumentOutOfRangeException.ThrowIfNegative(value: level);

        return new SdfMeshDetail(camera: default, forced: level, kind: 3, pixels: 0f, texel: 0f);
    }

}
/// <summary>
/// Which levels of detail a frame's raster passes draw (<see cref="SdfFrame.MeshDetail"/>): the choice by error each pass
/// makes (<see cref="SdfMeshDetail"/>), or one level everywhere, so <c>world.detail</c> shows what a level changes.
/// </summary>
public enum SdfMeshDetailLevel : byte {
    /// <summary>Each pass chooses each draw's level by the error it can show.</summary>
    Auto = 0,
    /// <summary>Every pass draws each mesh's own triangles.</summary>
    Level0 = 1,
    /// <summary>Every pass draws each mesh's first coarser level where it has one.</summary>
    Level1 = 2,
    /// <summary>Every pass draws each mesh's second coarser level, or its coarsest where it has fewer.</summary>
    Level2 = 3,
}
