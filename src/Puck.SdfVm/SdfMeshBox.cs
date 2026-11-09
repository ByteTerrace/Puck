using System.Numerics;
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

    /// <summary>Gets the half extents, along the axes of the frame the box lies in, of the axis-aligned box that holds
    /// it: the absolute values of its three half axes summed.</summary>
    public Vector3 AxisHalf => ((Vector3.Abs(value: HalfX) + Vector3.Abs(value: HalfY)) + Vector3.Abs(value: HalfZ));

    /// <summary>Returns the box a box of half extents <paramref name="half"/> about <paramref name="center"/> becomes
    /// when turned by <paramref name="turn"/> and moved by <paramref name="offset"/>.</summary>
    /// <param name="center">The box's center before the turn.</param>
    /// <param name="half">The box's half extents along its own axes, none negative.</param>
    /// <param name="turn">The turn, a unit quaternion.</param>
    /// <param name="offset">The move after the turn.</param>
    /// <returns>The turned and moved box.</returns>
    public static SdfMeshBox Turned(Vector3 center, Vector3 half, Quaternion turn, Vector3 offset) => new(
        Center: (offset + Vector3.Transform(value: center, rotation: turn)),
        HalfX: Vector3.Transform(value: new Vector3(x: half.X, y: 0f, z: 0f), rotation: turn),
        HalfY: Vector3.Transform(value: new Vector3(x: 0f, y: half.Y, z: 0f), rotation: turn),
        HalfZ: Vector3.Transform(value: new Vector3(x: 0f, y: 0f, z: half.Z), rotation: turn)
    );

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
    /// ride dynamic-transform slots the GPU reads, so no host box bounds them and every pass draws them. A displaced
    /// draw's box spans its displacement's band of heights (<see cref="SdfMeshDraw.Displacement"/>) as well as its
    /// mesh's.</returns>
    public static bool TryOf(in SdfMeshDraw draw, out SdfMeshBox box) {
        var mesh = draw.Mesh;

        if (!mesh.Members.IsEmpty) {
            box = default;

            return false;
        }

        var (low, high) = (mesh.PositionLow, mesh.PositionHigh);

        if (draw.Displacement is { } displacement) {
            low.Y = MathF.Min(x: low.Y, y: displacement.Low);
            high.Y = MathF.Max(x: high.Y, y: displacement.High);
        }
        var matrix = draw.ObjectToWorld;
        var center = ((low + high) * 0.5f);
        var half = ((high - low) * 0.5f);
        var rotation = draw.CarryRotation;
        var scale = draw.CarryScale;
        var rowX = new Vector3(x: matrix.M11, y: matrix.M12, z: matrix.M13);
        var rowY = new Vector3(x: matrix.M21, y: matrix.M22, z: matrix.M23);
        var rowZ = new Vector3(x: matrix.M31, y: matrix.M32, z: matrix.M33);

        box = new SdfMeshBox(
            Center: ((Vector3.Transform(value: Vector3.Transform(position: center, matrix: matrix), rotation: rotation) * scale) + draw.CarryTranslation),
            HalfX: (Vector3.Transform(value: (half.X * rowX), rotation: rotation) * scale),
            HalfY: (Vector3.Transform(value: (half.Y * rowY), rotation: rotation) * scale),
            HalfZ: (Vector3.Transform(value: (half.Z * rowZ), rotation: rotation) * scale)
        ) {
            Stretch = (StretchOf(rowX: rowX, rowY: rowY, rowZ: rowZ) * MathF.Abs(x: scale)),
        };

        return true;
    }

    /// <summary>Gets the most the draw's matrix and carry lengthen an object-space distance, a bound on the matrix's
    /// largest singular value, exact for a matrix whose rows are perpendicular: a mesh's error in object units is at most
    /// this many world units.</summary>
    public float Stretch { get; init; } = 1f;

    // The square root of the Gershgorin bound on the largest eigenvalue of the rows' Gram matrix: the largest row's sum of
    // its absolute dot products with every row.
    private static float StretchOf(Vector3 rowX, Vector3 rowY, Vector3 rowZ) {
        var xy = MathF.Abs(x: Vector3.Dot(vector1: rowX, vector2: rowY));
        var xz = MathF.Abs(x: Vector3.Dot(vector1: rowX, vector2: rowZ));
        var yz = MathF.Abs(x: Vector3.Dot(vector1: rowY, vector2: rowZ));
        var largest = MathF.Max(
            x: ((rowX.LengthSquared() + xy) + xz),
            y: MathF.Max(x: ((rowY.LengthSquared() + xy) + yz), y: ((rowZ.LengthSquared() + xz) + yz))
        );

        return MathF.Sqrt(x: largest);
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

    /// <summary>Writes boxes that together hold every receiver a caster can shadow in a cascade: the caster's sphere swept
    /// along the light from the receivers' reach before it to the far side of the cascade's sphere (no receiver reading the
    /// cascade lies past it), grown across the light by the receivers' reach and by the chord a light turned by up to
    /// <paramref name="turn"/> sweeps over that length, and cut along the light into boxes about as long as they are
    /// wide. A receiver outside every box reads none of the caster in the cascade, in any of its layers (depth, exit or
    /// optical), while the light stays within <paramref name="turn"/> of the cascade's.</summary>
    /// <param name="cascade">The cascade's light camera.</param>
    /// <param name="center">The caster sphere's center, in world space.</param>
    /// <param name="radius">The caster sphere's radius, in world units.</param>
    /// <param name="reach">The receivers' reach in the cascade, in world units (<see cref="SdfMeshCull.ReachOf"/>).</param>
    /// <param name="turn">The angle, in radians, the light may turn from the cascade's while the boxes still hold.</param>
    /// <param name="boxes">The boxes to write; its length is the most the sweep is cut into, at least one.</param>
    /// <returns>The boxes written, from the light's side.</returns>
    /// <exception cref="ArgumentException"><paramref name="boxes"/> is empty.</exception>
    public static int ShadowReach(in SdfMeshShadowCamera cascade, Vector3 center, float radius, float reach, float turn, Span<SdfMeshBox> boxes) {
        if (boxes.IsEmpty) {
            throw new ArgumentException(message: "At least one box is required.", paramName: nameof(boxes));
        }

        var forward = cascade.Forward;
        var sphere = (cascade.Position + (forward * (cascade.Far - cascade.HalfHeight)));
        var toSphere = (sphere - center);
        var start = -(radius + reach);
        var end = (MathF.Max(x: start, y: ((Vector3.Dot(vector1: toSphere, vector2: forward) + cascade.HalfHeight) + (toSphere.Length() * turn))) + reach);
        var length = (end - start);
        var across = ((radius + reach) + (length * turn));
        var count = (int)Math.Clamp(value: MathF.Ceiling(x: (length / MathF.Max(x: (2f * across), y: 1.0e-6f))), min: 1f, max: boxes.Length);
        var step = (length / count);

        for (var index = 0; (index < count); index++) {
            boxes[index] = new SdfMeshBox(
                Center: (center + (forward * (start + ((index + 0.5f) * step)))),
                HalfX: (cascade.Right * across),
                HalfY: (cascade.Up * across),
                HalfZ: (forward * (0.5f * step))
            );
        }

        return count;
    }

    // Whether the box's extent along a unit axis meets [low, high].
    private bool Within(Vector3 axis, float low, float high, Vector3 toward) {
        var middle = Vector3.Dot(vector1: toward, vector2: axis);
        var reach = ((MathF.Abs(x: Vector3.Dot(vector1: HalfX, vector2: axis)) + MathF.Abs(x: Vector3.Dot(vector1: HalfY, vector2: axis))) + MathF.Abs(x: Vector3.Dot(vector1: HalfZ, vector2: axis)));

        return (((middle + reach) >= low) && ((middle - reach) <= high));
    }
}
