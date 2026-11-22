using System.Numerics;
using Puck.Abstractions.Cameras;
using Puck.SignedDistance;

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

    /// <summary>Returns the box a part declares, in its group's frame, through the pose the group's geometry holds for the
    /// part's slot (<see cref="SdfMeshGeometryBuilder.Poses"/>), read as the bake reads a pose
    /// (<see cref="SdfMeshGeometryBuilder.PoseOf"/>).</summary>
    /// <param name="part">The part, whose corners lie in its own slot's frame.</param>
    /// <param name="pose">The geometry's pose for <paramref name="part"/>'s slot.</param>
    /// <returns>The part's box in the group's frame.</returns>
    public static SdfMeshBox OfPart(SdfInstancePart part, DynamicTransform pose) {
        var (position, orientation) = SdfMeshGeometryBuilder.PoseOf(transform: pose);

        return Turned(
            center: (0.5f * (part.Low + part.High)),
            half: (0.5f * (part.High - part.Low)),
            offset: position,
            turn: orientation
        );
    }
    /// <summary>Returns the box a box in a slot's own frame becomes where the slot's transform puts it: scaled by the slot's
    /// units (<see cref="SdfMeshGeometryBuilder.UnitsOf"/>), then posed as the bake reads a pose
    /// (<see cref="SdfMeshGeometryBuilder.PoseOf"/>).</summary>
    /// <param name="center">The box's center, in the slot's frame.</param>
    /// <param name="half">The box's half extents along the slot's axes, none negative.</param>
    /// <param name="transform">The slot's transform.</param>
    /// <returns>The box in the frame the transform places its slot in.</returns>
    public static SdfMeshBox Carried(Vector3 center, Vector3 half, DynamicTransform transform) {
        var units = SdfMeshGeometryBuilder.UnitsOf(transform: transform);
        var (position, orientation) = SdfMeshGeometryBuilder.PoseOf(transform: transform);

        return Turned(
            center: (center * units),
            half: (half * units),
            offset: position,
            turn: orientation
        );
    }

    /// <summary>Returns the fraction a view's frustum sides are widened by: <see cref="ViewMargin"/>, or
    /// <see cref="ViewMarginPixels"/> of the view's shorter side where that is more, as it is for a small nested
    /// window.</summary>
    /// <param name="width">The pixels across the view's render extent.</param>
    /// <param name="height">The pixels down the view's render extent.</param>
    /// <returns>The fraction of the frustum's half extents.</returns>
    public static float MarginOf(uint width, uint height) =>
        MathF.Max(x: ViewMargin, y: ((2f * ViewMarginPixels) / Math.Max(val1: 1u, val2: Math.Min(val1: width, val2: height))));

    /// <summary>Returns the box a draw's triangles lie in, when its triangles have one the host knows, for a draw a frame
    /// does not place (<see cref="SdfMeshDraw.FixedPlacement"/>): the static scene's and an offline tool's.</summary>
    /// <param name="draw">The draw, not placed by its slot (<see cref="SdfMeshDraw.PlacedBySlot"/>).</param>
    /// <param name="box">The box, when this returns <see langword="true"/>.</param>
    /// <returns><see langword="false"/> for a draw whose mesh has members (<see cref="SdfMesh.Members"/>): those vertices
    /// ride dynamic-transform slots, so the record's box is the one a residency's tables derive from the slots
    /// (<see cref="TryOfSkin"/>) whenever they move, not the draw's. A displaced
    /// draw's box spans its displacement's band of heights (<see cref="SdfMeshDraw.Displacement"/>) as well as its
    /// mesh's.</returns>
    /// <exception cref="InvalidOperationException"><paramref name="draw"/> is placed by its slot.</exception>
    public static bool TryOfFixed(in SdfMeshDraw draw, out SdfMeshBox box) {
        if (!TryOfObject(draw: in draw, center: out var center, half: out var half)) {
            box = default;

            return false;
        }
        box = Placed(center: center, draw: in draw, half: half, matrix: draw.FixedPlacement);

        return true;
    }
    /// <summary>Returns the box a draw's triangles lie in within a frame, when its triangles have one the host knows: the
    /// box <see cref="TryOfFixed"/> returns, placed where the frame's transforms place the draw
    /// (<see cref="SdfMeshDraw.PlacementIn"/>), so a draw riding its slot is boxed where its slot's row stands.</summary>
    /// <param name="draw">The draw.</param>
    /// <param name="transforms">The frame's dynamic transforms.</param>
    /// <param name="box">The box, when this returns <see langword="true"/>.</param>
    /// <returns><see langword="false"/> for a draw whose mesh has members.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="transforms"/> is <see langword="null"/>.</exception>
    public static bool TryOf(in SdfMeshDraw draw, IReadOnlyList<DynamicTransform> transforms, out SdfMeshBox box) {
        ArgumentNullException.ThrowIfNull(argument: transforms);
        if (!TryOfObject(draw: in draw, center: out var center, half: out var half)) {
            box = default;

            return false;
        }
        box = Placed(center: center, draw: in draw, half: half, matrix: draw.PlacementIn(transforms: transforms));

        return true;
    }
    /// <summary>Returns the box a draw's triangles lie in within its own object space, before its matrix and carry: the
    /// box <see cref="TryOf"/> places.</summary>
    /// <param name="draw">The draw.</param>
    /// <param name="center">The box's center, in object space, when this returns <see langword="true"/>.</param>
    /// <param name="half">The box's half extents along the object axes, when this returns <see langword="true"/>.</param>
    /// <returns><see langword="false"/> for a draw whose mesh has members, as <see cref="TryOf"/> returns.</returns>
    public static bool TryOfObject(in SdfMeshDraw draw, out Vector3 center, out Vector3 half) {
        var mesh = draw.Mesh;

        if (!mesh.Members.IsEmpty) {
            (center, half) = (default, default);

            return false;
        }

        var (low, high) = (mesh.PositionLow, mesh.PositionHigh);

        if (draw.Displacement is { } displacement) {
            low.Y = MathF.Min(x: low.Y, y: displacement.Low);
            high.Y = MathF.Max(x: high.Y, y: displacement.High);
        }
        (center, half) = (((low + high) * 0.5f), ((high - low) * 0.5f));

        return true;
    }

    /// <summary>The factor a skin's bound grows by over the sphere that holds every member's copy of its rest box: a
    /// vertex two members share lies on the screw motion between their two copies of it, turned at most half a turn
    /// (the blend takes the shorter way round, <c>sdfMeshSkinPose</c>), so within the sphere whose diameter joins the
    /// two copies, and that sphere lies within the square root of two of the radius that holds both ends. The rest is
    /// room for rounding.</summary>
    public const float SkinBlendReach = 1.4143f;

    /// <summary>Returns a box that holds a skinned draw's triangles under a table of transforms: the mesh's rest box
    /// carried by each member's motion (<see cref="SdfMeshSkin.MotionOf"/>) and by the root's, which is none, held in one
    /// sphere grown by <see cref="SkinBlendReach"/> for the vertices two members share, then placed as the root's row in
    /// the table, the draw's scale and its carry place the mesh (<see cref="Ridden"/>). It bounds the draw as the kernels
    /// pose it from the same table (<c>frame/sdf-mesh-skin.hlsli</c>); a table a view's pose overlay replaced rows of
    /// (<see cref="SdfPoseOverlay"/>) bounds it as that view poses it.</summary>
    /// <param name="draw">The draw, whose mesh has members.</param>
    /// <param name="transforms">The dynamic transforms the draw's root slot and its members' slots are read from.</param>
    /// <param name="box">The box, when this returns <see langword="true"/>.</param>
    /// <returns><see langword="false"/> for a draw with no mesh or no root slot, a slot outside
    /// <paramref name="transforms"/>, or a matrix with no finite positive scale: nothing then bounds the draw.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="transforms"/> is <see langword="null"/>.</exception>
    public static bool TryOfSkin(in SdfMeshDraw draw, IReadOnlyList<DynamicTransform> transforms, out SdfMeshBox box) {
        ArgumentNullException.ThrowIfNull(argument: transforms);
        box = default;

        if ((draw.Mesh is not { } mesh) || (draw.FrameSlot is not { } root) || (root < 0) || (root >= transforms.Count)) {
            return false;
        }

        // The scale a member's offset from the root is divided by (sdfMeshDrawScale).
        var scale = draw.SlotScale;

        if (!float.IsFinite(f: scale) || (scale <= 0f)) {
            return false;
        }

        var (rootPosition, rootOrientation) = SdfMeshGeometryBuilder.PoseOf(transform: transforms[root]);
        var unturn = Quaternion.Conjugate(value: rootOrientation);
        var rest = ((mesh.PositionLow + mesh.PositionHigh) * 0.5f);
        var (low, high) = (rest, rest);

        foreach (ref readonly var member in mesh.Members.Span) {
            var slot = (((long)root) + member.SlotOffset);

            if ((slot < 0) || (slot >= transforms.Count)) {
                return false;
            }

            var (position, orientation) = SdfMeshGeometryBuilder.PoseOf(transform: transforms[((int)slot)]);
            var (turn, shift) = SdfMeshSkin.MotionOf(
                orientation: (unturn * orientation),
                position: (Vector3.Transform(value: (position - rootPosition), rotation: unturn) / scale),
                rest: in member
            );
            var center = (Vector3.Transform(value: rest, rotation: turn) + shift);

            low = Vector3.Min(value1: low, value2: center);
            high = Vector3.Max(value1: high, value2: center);
        }

        // Every copy of the rest box lies within its own half diagonal of its center.
        var radius = (SkinBlendReach * (((high - low) * 0.5f).Length() + ((mesh.PositionHigh - mesh.PositionLow) * 0.5f).Length()));

        if (!float.IsFinite(f: radius)) {
            return false;
        }
        box = Ridden(
            carryRotation: draw.CarryRotation,
            carryScale: draw.CarryScale,
            carryTranslation: draw.CarryTranslation,
            center: ((low + high) * 0.5f),
            half: new Vector3(value: radius),
            scale: scale,
            slot: transforms[root]
        );

        return true;
    }

    /// <summary>Returns the box an object-space box of a draw riding its frame slot becomes where the kernels pose the
    /// draw (<c>sdfSelectionBox</c>, <c>sdfMeshPoseOf</c>): scaled by the draw's scale, turned by the slot's orientation
    /// and moved to its position as they stand in the slot's row, then through the draw's carry. The row's orientation is
    /// read as the row holds it, as the kernels read it, so an unset one is the identity.</summary>
    /// <param name="center">The box's center, in the draw's object space.</param>
    /// <param name="half">The box's half extents along the object axes, none negative.</param>
    /// <param name="slot">The slot's transform, as its row of the frame's dynamic transforms holds it.</param>
    /// <param name="scale">The draw's scale (<see cref="SdfMeshDraw.SlotScale"/>).</param>
    /// <param name="carryRotation">The draw's carry rotation (<see cref="SdfMeshDraw.CarryRotation"/>).</param>
    /// <param name="carryTranslation">The draw's carry translation (<see cref="SdfMeshDraw.CarryTranslation"/>).</param>
    /// <param name="carryScale">The draw's carry scale (<see cref="SdfMeshDraw.CarryScale"/>).</param>
    /// <returns>The box in world space.</returns>
    public static SdfMeshBox Ridden(Vector3 center, Vector3 half, in DynamicTransform slot, float scale, Quaternion carryRotation, Vector3 carryTranslation, float carryScale) {
        var orientation = slot.Orientation;

        return Placed(
            carryRotation: carryRotation,
            carryScale: carryScale,
            carryTranslation: carryTranslation,
            center: center,
            half: half,
            origin: slot.Position,
            rowX: (Vector3.Transform(value: Vector3.UnitX, rotation: orientation) * scale),
            rowY: (Vector3.Transform(value: Vector3.UnitY, rotation: orientation) * scale),
            rowZ: (Vector3.Transform(value: Vector3.UnitZ, rotation: orientation) * scale)
        );
    }

    /// <summary>Returns the world-axis box that holds two boxes, the larger of their stretches its own.</summary>
    /// <param name="first">The first box.</param>
    /// <param name="second">The second box.</param>
    /// <returns>The box.</returns>
    public static SdfMeshBox Enclosing(in SdfMeshBox first, in SdfMeshBox second) {
        var low = Vector3.Min(value1: (first.Center - first.AxisHalf), value2: (second.Center - second.AxisHalf));
        var high = Vector3.Max(value1: (first.Center + first.AxisHalf), value2: (second.Center + second.AxisHalf));
        var half = ((high - low) * 0.5f);

        return new SdfMeshBox(Center: ((low + high) * 0.5f), HalfX: new Vector3(x: half.X, y: 0f, z: 0f), HalfY: new Vector3(x: 0f, y: half.Y, z: 0f), HalfZ: new Vector3(x: 0f, y: 0f, z: half.Z)) {
            Stretch = MathF.Max(x: first.Stretch, y: second.Stretch),
        };
    }

    // The box an object-space box of a draw's mesh becomes under the draw's matrix and then its carry.
    private static SdfMeshBox Placed(in SdfMeshDraw draw, Vector3 center, Vector3 half, in Matrix4x4 matrix) {
        return Placed(
            carryRotation: draw.CarryRotation,
            carryScale: draw.CarryScale,
            carryTranslation: draw.CarryTranslation,
            center: center,
            half: half,
            origin: matrix.Translation,
            rowX: new Vector3(x: matrix.M11, y: matrix.M12, z: matrix.M13),
            rowY: new Vector3(x: matrix.M21, y: matrix.M22, z: matrix.M23),
            rowZ: new Vector3(x: matrix.M31, y: matrix.M32, z: matrix.M33)
        );
    }
    // The box an object-space box becomes under an affine map's rows (the row-vector convention) and translation, then a
    // carry.
    private static SdfMeshBox Placed(Vector3 center, Vector3 half, Vector3 rowX, Vector3 rowY, Vector3 rowZ, Vector3 origin, Quaternion carryRotation, Vector3 carryTranslation, float carryScale) {
        var placed = ((((center.X * rowX) + (center.Y * rowY)) + (center.Z * rowZ)) + origin);

        return new SdfMeshBox(
            Center: ((Vector3.Transform(value: placed, rotation: carryRotation) * carryScale) + carryTranslation),
            HalfX: (Vector3.Transform(value: (half.X * rowX), rotation: carryRotation) * carryScale),
            HalfY: (Vector3.Transform(value: (half.Y * rowY), rotation: carryRotation) * carryScale),
            HalfZ: (Vector3.Transform(value: (half.Z * rowZ), rotation: carryRotation) * carryScale)
        ) {
            Stretch = (StretchOf(rowX: rowX, rowY: rowY, rowZ: rowZ) * MathF.Abs(x: carryScale)),
        };
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

    // Whether the box's extent along a unit axis meets [low, high].
    private bool Within(Vector3 axis, float low, float high, Vector3 toward) {
        var middle = Vector3.Dot(vector1: toward, vector2: axis);
        var reach = ((MathF.Abs(x: Vector3.Dot(vector1: HalfX, vector2: axis)) + MathF.Abs(x: Vector3.Dot(vector1: HalfY, vector2: axis))) + MathF.Abs(x: Vector3.Dot(vector1: HalfZ, vector2: axis)));

        return (((middle + reach) >= low) && ((middle - reach) <= high));
    }
}
