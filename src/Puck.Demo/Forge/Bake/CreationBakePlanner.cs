using System.Numerics;
using Puck.Cameras;
using Puck.Demo.Creator;
using Puck.SdfVm;

namespace Puck.Demo.Forge.Bake;

/// <summary>
/// Translates a <c>puck.creation.v1</c> document into a <see cref="BakePlan"/>: the shapes recenter (feet on y = 0,
/// horizontal centroid at the origin — the same convention <see cref="AvatarDefinition.FromPlacedShapes"/> settled,
/// so bake cameras frame consistently no matter where in the room the player built), each pose folds into its own
/// STATIC program (Translate → Rotate → Scale then the canonical primitive, blend/smooth riding the instruction),
/// and the intent picks the views — sprite: 4 orbit facings × (1 + the document's timeline frames); background: one
/// head-on 160×144 view framing the whole bound.
/// </summary>
internal static class CreationBakePlanner {
    /// <summary>A sprite pose bakes into a 32×32 native cell (up to a 4×4-tile metasprite).</summary>
    public const int SpriteNativeEdge = 32;
    /// <summary>A background bakes at the panel's native 160×144.</summary>
    public const int BackgroundNativeWidth = 160;
    /// <summary>The background native height.</summary>
    public const int BackgroundNativeHeight = 144;

    private const int SpriteFacingCount = 4;

    /// <summary>Builds the plan for a normalized document.</summary>
    /// <param name="document">The creation (a normalized <see cref="CreationDocument"/> — nulls already defaulted).</param>
    /// <param name="target">The hardware target.</param>
    /// <param name="style">The resolved style (see <see cref="BakeStyles.Resolve"/>).</param>
    /// <returns>The plan, ready for <see cref="BakePipeline"/>.</returns>
    public static BakePlan Plan(CreationDocument document, BakeTarget target, BakeStyle style) {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(style);

        var intent = (((document.Intent ?? CreatorIntent.Object) == CreatorIntent.Sprite) ? BakeIntent.Sprite : BakeIntent.Background);
        var materials = ResolveMaterials(document: document);
        var restShapes = (document.Shapes ?? []);
        var poses = BuildPoses(document: document, restShapes: restShapes);
        var offset = RecenterOffset(shapes: restShapes);
        var bound = Bound(offset: offset, poses: poses);
        var views = ((intent == BakeIntent.Sprite)
            ? SpriteViews(bound: bound, materials: materials, offset: offset, poses: poses, style: style)
            : BackgroundViews(bound: bound, materials: materials, offset: offset, restShapes: restShapes, style: style));

        return new BakePlan(
            Budget: new BakeBudget(),
            Intent: intent,
            NativeHeight: ((intent == BakeIntent.Sprite) ? SpriteNativeEdge : BackgroundNativeHeight),
            NativeWidth: ((intent == BakeIntent.Sprite) ? SpriteNativeEdge : BackgroundNativeWidth),
            Style: style,
            Target: target,
            Views: views
        );
    }

    // The document's material palette (normalized exactly like CreatorScene.LoadDocument), or the default sweep.
    private static SdfMaterial[] ResolveMaterials(CreationDocument document) {
        var materials = CreatorScene.DefaultPalette();

        if (document.Palette is { } palette) {
            for (var index = 0; ((index < palette.Count) && (index < materials.Length)); index++) {
                var entry = palette[index];
                var defaults = new SdfMaterial(Albedo: entry.Albedo);

                materials[index] = (defaults with {
                    Emissive = (entry.Emissive ?? defaults.Emissive),
                    Shininess = (entry.Shininess ?? defaults.Shininess),
                    Specular = (entry.Specular ?? defaults.Specular),
                });
            }
        }

        return materials;
    }

    // The pose list: the rest pose (the document shapes as authored) followed by every timeline frame applied by
    // shape id — a frame moves what it names and leaves the rest.
    private static List<IReadOnlyList<ShapeDocument>> BuildPoses(CreationDocument document, IReadOnlyList<ShapeDocument> restShapes) {
        var poses = new List<IReadOnlyList<ShapeDocument>>(capacity: (1 + (document.Frames?.Count ?? 0))) { restShapes };

        foreach (var frame in (document.Frames ?? [])) {
            var posed = new List<ShapeDocument>(capacity: restShapes.Count);

            foreach (var shape in restShapes) {
                var transform = frame.Transforms.FirstOrDefault(predicate: entry => (entry.Id == shape.Id));

                posed.Add(item: ((transform is { } pose)
                    ? (shape with { Position = pose.Position, Rotation = pose.Rotation, Scale = pose.Scale })
                    : shape));
            }

            poses.Add(item: posed);
        }

        return poses;
    }

    // The recenter offset, from the REST pose only (frames animate around the rest frame — recentering per pose
    // would make a walk cycle jitter): horizontal centroid on X/Z, the lowest reached point on Y.
    private static Vector3 RecenterOffset(IReadOnlyList<ShapeDocument> shapes) {
        if (shapes.Count == 0) {
            return Vector3.Zero;
        }

        var centroid = Vector3.Zero;
        var lowestY = float.MaxValue;

        foreach (var shape in shapes) {
            centroid += shape.Position;
            lowestY = MathF.Min(x: lowestY, y: (shape.Position.Y - WorldHalfExtents(shape: shape).Y));
        }

        centroid /= shapes.Count;

        return new Vector3(x: centroid.X, y: lowestY, z: centroid.Z);
    }

    // The camera-framing bound: the farthest recentered reach over EVERY pose, so no walk frame crops.
    private static float Bound(Vector3 offset, List<IReadOnlyList<ShapeDocument>> poses) {
        var bound = 0.5f;

        foreach (var pose in poses) {
            foreach (var shape in pose) {
                bound = MathF.Max(x: bound, y: ((shape.Position - offset).Length() + WorldHalfExtents(shape: shape).Length()));
            }
        }

        return bound;
    }

    // A shape's world-space axis-aligned half-extents: the canonical per-axis extents scaled, then the extent box
    // rotated (|R|·e — the standard tight AABB of a rotated box).
    private static Vector3 WorldHalfExtents(ShapeDocument shape) {
        var extents = (AvatarDefinition.AxisExtents(type: shape.Type) * shape.Scale);
        var rotation = Matrix4x4.CreateFromQuaternion(quaternion: shape.Rotation);

        return new Vector3(
            x: (((MathF.Abs(x: rotation.M11) * extents.X) + (MathF.Abs(x: rotation.M21) * extents.Y)) + (MathF.Abs(x: rotation.M31) * extents.Z)),
            y: (((MathF.Abs(x: rotation.M12) * extents.X) + (MathF.Abs(x: rotation.M22) * extents.Y)) + (MathF.Abs(x: rotation.M32) * extents.Z)),
            z: (((MathF.Abs(x: rotation.M13) * extents.X) + (MathF.Abs(x: rotation.M23) * extents.Y)) + (MathF.Abs(x: rotation.M33) * extents.Z))
        );
    }

    // One pose's static program: shapes in DOCUMENT ORDER, each folded as Translate → Rotate → Scale then its
    // canonical primitive with the authored blend/smooth — grouped shapes therefore blend in document order and
    // ungrouped ones are plain unions, exactly as authored. An empty creation bakes a single unit sphere so the
    // pipeline never renders nothing.
    private static SdfProgram BuildProgram(IReadOnlyList<ShapeDocument> shapes, SdfMaterial[] materials, Vector3 offset) {
        var builder = new SdfProgramBuilder();

        if (shapes.Count == 0) {
            _ = AvatarDefinition.AppendPrimitive(
                chain: builder.ResetPoint(),
                material: builder.AddMaterial(material: materials[0]),
                type: AvatarPrimitive.Sphere
            );

            return builder.Build();
        }

        var materialIds = new int[materials.Length];

        for (var index = 0; (index < materials.Length); index++) {
            materialIds[index] = builder.AddMaterial(material: materials[index]);
        }

        foreach (var shape in shapes) {
            var chain = builder
                .ResetPoint()
                .Translate(offset: (shape.Position - offset))
                .Rotate(rotation: shape.Rotation)
                .Scale(scale: shape.Scale);

            _ = AvatarDefinition.AppendPrimitive(
                blend: (shape.Blend ?? SdfBlendOp.Union),
                chain: chain,
                material: materialIds[Math.Clamp(value: (shape.Material ?? 0), max: (materials.Length - 1), min: 0)],
                smooth: (shape.Smooth ?? 0f),
                type: shape.Type
            );
        }

        return builder.Build();
    }

    // The sprite views: 4 orbit facings × every pose, facing-major (the preview grid reads facings down, poses
    // across). One program per pose, shared by its four facings.
    private static List<BakeView> SpriteViews(float bound, SdfMaterial[] materials, Vector3 offset, List<IReadOnlyList<ShapeDocument>> poses, BakeStyle style) {
        var views = new List<BakeView>(capacity: (SpriteFacingCount * poses.Count));
        var programs = new List<SdfProgram>(capacity: poses.Count);

        foreach (var pose in poses) {
            programs.Add(item: BuildProgram(materials: materials, offset: offset, shapes: pose));
        }

        var raster = (uint)(SpriteNativeEdge * style.SupersampleFactor);
        var target = new Vector3(x: 0f, y: (bound * 0.5f), z: 0f);
        var horizontalDistance = (bound * 1.15f);
        var verticalDistance = (bound * 0.5f);

        for (var facing = 0; (facing < SpriteFacingCount); facing++) {
            var azimuth = (facing * (MathF.PI / 2f));
            var camera = CameraSnapshot.LookAt(
                fieldOfViewRadians: (45f * (MathF.PI / 180f)),
                position: (target + new Vector3(x: (MathF.Sin(x: azimuth) * horizontalDistance), y: verticalDistance, z: (MathF.Cos(x: azimuth) * horizontalDistance))),
                target: target,
                viewportHeight: raster,
                viewportWidth: raster
            );

            for (var pose = 0; (pose < poses.Count); pose++) {
                views.Add(item: new BakeView(Camera: camera, Name: $"f{facing}p{pose}", Program: programs[pose]));
            }
        }

        return views;
    }

    // The background view: one head-on 160×144 shot that FITS the recentered axis-aligned box — the distance is
    // whichever of the width/height extents demands more, plus the box's half-depth so the nearest face clears the
    // frame (a radial bound would badly over-frame a wide, flat scene).
    private static List<BakeView> BackgroundViews(float bound, SdfMaterial[] materials, Vector3 offset, IReadOnlyList<ShapeDocument> restShapes, BakeStyle style) {
        _ = bound; // The sprite path frames radially; the background fits the box below.

        const float tanHalfFov = 0.41421356f; // tan(45°/2)
        const float fill = 0.92f;

        var (centre, halfExtents) = BoundingBox(offset: offset, shapes: restShapes);
        var aspect = (BackgroundNativeWidth / (float)BackgroundNativeHeight);
        var distance = (MathF.Max(x: halfExtents.Y, y: (halfExtents.X / aspect)) / (tanHalfFov * fill));
        var camera = CameraSnapshot.LookAt(
            fieldOfViewRadians: (45f * (MathF.PI / 180f)),
            position: (centre + new Vector3(x: 0f, y: (halfExtents.Y * 0.25f), z: (distance + halfExtents.Z))),
            target: centre,
            viewportHeight: (uint)(BackgroundNativeHeight * style.SupersampleFactor),
            viewportWidth: (uint)(BackgroundNativeWidth * style.SupersampleFactor)
        );

        return [new BakeView(Camera: camera, Name: "scene", Program: BuildProgram(materials: materials, offset: offset, shapes: restShapes))];
    }

    // The recentered axis-aligned bounding box over the rest shapes (position ± reach per axis).
    private static (Vector3 Centre, Vector3 HalfExtents) BoundingBox(Vector3 offset, IReadOnlyList<ShapeDocument> shapes) {
        if (shapes.Count == 0) {
            return (new Vector3(x: 0f, y: 0.5f, z: 0f), new Vector3(value: 0.5f));
        }

        var min = new Vector3(value: float.MaxValue);
        var max = new Vector3(value: float.MinValue);

        foreach (var shape in shapes) {
            var reach = WorldHalfExtents(shape: shape);
            var local = (shape.Position - offset);

            min = Vector3.Min(value1: min, value2: (local - reach));
            max = Vector3.Max(value1: max, value2: (local + reach));
        }

        return ((0.5f * (min + max)), (0.5f * (max - min)));
    }
}
