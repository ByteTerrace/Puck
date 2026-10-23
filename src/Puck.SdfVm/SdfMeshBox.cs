using System.Numerics;
using System.Runtime.CompilerServices;
using Puck.Abstractions.Cameras;

namespace Puck.SdfVm;

/// <summary>
/// The world-space box a mesh draw's triangles lie in: the box its mesh's positions span carried through the draw's
/// matrix and then its carry, a parallelepiped with a center and
/// three half axes, so the box is exact under any affine matrix. A view's mesh and glass passes draw only the draws whose
/// box may lie in the view's frustum, and a shadow cascade only those whose box may lie in its light's box; a draw a pass
/// skips has no triangle the pass's rasterizer would keep, so the cull changes no pixel. Presentation only.
/// </summary>
/// <param name="Center">The box's center, in world space.</param>
/// <param name="HalfX">The box's first half axis.</param>
/// <param name="HalfY">The box's second half axis.</param>
/// <param name="HalfZ">The box's third half axis.</param>
public readonly record struct SdfMeshBox(Vector3 Center, Vector3 HalfX, Vector3 HalfY, Vector3 HalfZ) {
    /// <summary>The least fraction a view's frustum sides are widened by before a draw is judged outside them.</summary>
    public const float ViewMargin = 0.01f;
    /// <summary>The pixels a view's frustum sides are widened by at least, past the temporal jitter's shift of the
    /// projection (under a pixel) and a triangle's coverage of the pixel centers along the image's edge.</summary>
    public const float ViewMarginPixels = 4f;

    /// <summary>Returns the fraction a view's frustum sides are widened by: <see cref="ViewMargin"/>, or
    /// <see cref="ViewMarginPixels"/> of the view's shorter side where that is more, as it is for a small nested
    /// window.</summary>
    /// <param name="width">The pixels across the view's render extent.</param>
    /// <param name="height">The pixels down the view's render extent.</param>
    /// <returns>The fraction of the frustum's half extents.</returns>
    public static float MarginOf(uint width, uint height) =>
        MathF.Max(x: ViewMargin, y: ((2f * ViewMarginPixels) / Math.Max(val1: 1u, val2: Math.Min(val1: width, val2: height))));

    /// <summary>Returns the box a draw's triangles lie in, when its triangles have one the host knows.</summary>
    /// <param name="draw">The draw.</param>
    /// <param name="box">The box, when this returns <see langword="true"/>.</param>
    /// <returns><see langword="false"/> for a draw whose mesh has members (<see cref="SdfMesh.Members"/>): those vertices
    /// ride dynamic-transform slots the GPU reads, so no host box bounds them and every pass draws them.</returns>
    public static bool TryOf(in SdfMeshDraw draw, out SdfMeshBox box) {
        var mesh = draw.Mesh;

        if (!mesh.Members.IsEmpty) {
            box = default;

            return false;
        }

        var bounds = MeshBoundsCache.GetValue(key: mesh, createValueCallback: static mesh => BoundsOf(positions: mesh.Positions.Span));
        var matrix = draw.ObjectToWorld;
        var center = ((bounds.Low + bounds.High) * 0.5f);
        var half = ((bounds.High - bounds.Low) * 0.5f);
        var rotation = draw.CarryRotation;
        var scale = draw.CarryScale;

        box = new SdfMeshBox(
            Center: ((Vector3.Transform(value: Vector3.Transform(position: center, matrix: matrix), rotation: rotation) * scale) + draw.CarryTranslation),
            HalfX: (Vector3.Transform(value: (half.X * new Vector3(x: matrix.M11, y: matrix.M12, z: matrix.M13)), rotation: rotation) * scale),
            HalfY: (Vector3.Transform(value: (half.Y * new Vector3(x: matrix.M21, y: matrix.M22, z: matrix.M23)), rotation: rotation) * scale),
            HalfZ: (Vector3.Transform(value: (half.Z * new Vector3(x: matrix.M31, y: matrix.M32, z: matrix.M33)), rotation: rotation) * scale)
        );

        return true;
    }

    /// <summary>Returns whether a view's mesh pass may draw any of the box: the camera's frustum widened by
    /// <paramref name="margin"/>, from its near plane to the frame's far distance, past which the surface pass keeps no
    /// hit.</summary>
    /// <param name="camera">The view's camera.</param>
    /// <param name="far">The frame's far distance, in world units (<see cref="SdfFrame.FarDistance"/>).</param>
    /// <param name="margin">The fraction the frustum's sides are widened by (<see cref="MarginOf"/>).</param>
    /// <returns><see langword="false"/> when no point of the box lies in the view.</returns>
    public bool MaySee(in CameraSnapshot camera, float far, float margin) =>
        camera.MaySee(
            center: Center,
            far: far,
            halfX: HalfX,
            halfY: HalfY,
            halfZ: HalfZ,
            margin: margin
        );
    /// <summary>Returns whether a shadow cascade's depth may hold any of the box: its light camera's square extent across
    /// the light and its depth interval along it, outside which the cascade's orthographic projection clips every
    /// triangle.</summary>
    /// <param name="cascade">The cascade's light camera.</param>
    /// <returns><see langword="false"/> when no point of the box lies in the cascade's box.</returns>
    public bool MayCast(in SdfMeshShadowCamera cascade) {
        var toward = (Center - cascade.Position);

        return (
            Within(axis: cascade.Right, low: -cascade.HalfHeight, high: cascade.HalfHeight, toward: toward) &&
            Within(axis: cascade.Up, low: -cascade.HalfHeight, high: cascade.HalfHeight, toward: toward) &&
            Within(axis: cascade.Forward, low: cascade.Near, high: cascade.Far, toward: toward)
        );
    }
    /// <summary>Returns whether the box, as a caster in a cascade, may shadow a receiver the view shows: the box swept
    /// along the light through the cascade's depth meets the view's frustum, as far as the cascade's sphere reaches and
    /// grown by <paramref name="reach"/>, the farthest a receiver's shadow filter reads beside it. A caster whose
    /// shadow falls only where the view shows nothing changes no shaded pixel.</summary>
    /// <param name="cascade">The cascade's light camera.</param>
    /// <param name="camera">The view's camera.</param>
    /// <param name="far">The frame's far distance, in world units.</param>
    /// <param name="margin">The fraction the view's frustum sides are widened by (<see cref="MarginOf"/>).</param>
    /// <param name="reach">The receivers' reach, in world units.</param>
    /// <returns><see langword="false"/> when no receiver the view shows can read the box in the cascade.</returns>
    public bool MayShadow(in SdfMeshShadowCamera cascade, in CameraSnapshot camera, float far, float margin, float reach) {
        // The cascade encloses the bounding sphere of its slice of the view, whose center lies its half extent short of
        // the far plane (SdfMeshShadowCascades), so no receiver reading it lies past the sphere's far side.
        var sphere = (cascade.Position + (cascade.Forward * (cascade.Far - cascade.HalfHeight)));
        var deepest = (Vector3.Dot(vector1: (sphere - camera.Position), vector2: camera.Forward) + cascade.HalfHeight);

        return camera.MaySee(
            center: Center,
            far: (MathF.Min(x: far, y: deepest) + reach),
            halfX: HalfX,
            halfY: HalfY,
            halfZ: HalfZ,
            margin: margin,
            reach: reach,
            sweep: (cascade.Forward * (cascade.Far - cascade.Near))
        );
    }

    // Each mesh's object-space box, computed once from its positions and kept while the mesh lives: a mesh never
    // changes its positions, and a frame stages thousands of draws.
    private static readonly ConditionalWeakTable<SdfMesh, MeshBounds> MeshBoundsCache = new();

    private static MeshBounds BoundsOf(ReadOnlySpan<Vector3> positions) {
        var low = new Vector3(value: float.PositiveInfinity);
        var high = new Vector3(value: float.NegativeInfinity);

        foreach (var position in positions) {
            low = Vector3.Min(value1: low, value2: position);
            high = Vector3.Max(value1: high, value2: position);
        }

        return new MeshBounds(High: high, Low: low);
    }

    private sealed record MeshBounds(Vector3 Low, Vector3 High);

    // Whether the box's extent along a unit axis meets [low, high].
    private bool Within(Vector3 axis, float low, float high, Vector3 toward) {
        var middle = Vector3.Dot(vector1: toward, vector2: axis);
        var reach = ((MathF.Abs(x: Vector3.Dot(vector1: HalfX, vector2: axis)) + MathF.Abs(x: Vector3.Dot(vector1: HalfY, vector2: axis))) + MathF.Abs(x: Vector3.Dot(vector1: HalfZ, vector2: axis)));

        return (((middle + reach) >= low) && ((middle - reach) <= high));
    }
}
