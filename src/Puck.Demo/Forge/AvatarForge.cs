using System.Numerics;
using Puck.Abstractions.Gpu;
using Puck.Cameras;
using Puck.Demo.Creator;
using Puck.Demo.Forge.Bake;
using Puck.SdfVm;

namespace Puck.Demo.Forge;

/// <summary>
/// The "2D land" of the pipeline, re-forged ON the bake pipeline: it snapshots a 3D <see cref="AvatarDefinition"/>
/// from the four overworld facings over a short walk cycle and crushes the snapshots through
/// <see cref="BakePipeline"/> (grade → joint palette fit → quantize → tile assembly) into ONE Humble GamingBrick
/// sprite sheet the overworld ROM plays back. The walk poses come from the source creation document's FRAMES when it
/// carries a timeline (each frame a per-shape transform snapshot, applied by shape id), and fall back to the honest
/// procedural whole-body nudge (a bob + an alternating sway) otherwise. The bake targets a DMG-like 4-colour OBJ
/// world — the CGB path constrained to ONE object palette (the hardware binds a single OBJ palette to a sprite;
/// slot 0 is transparent, fitted JOINTLY across every pose so no facing loses its colours) — and each pipeline
/// metasprite frame is re-composed into four 8×8 tiles in row-major order (top-left, top-right, bottom-left,
/// bottom-right) so the ROM can place it as a 2×2 metasprite with a single tile base.
/// </summary>
internal static class AvatarForge {
    /// <summary>The overworld facings, in the wire order the ROM's facing byte indexes (down, up, left, right).</summary>
    public const int FacingCount = 4;
    /// <summary>Poses per facing: a neutral idle frame then two alternating walk frames (a classic overworld cycle).</summary>
    public const int FramesPerFacing = 3;
    /// <summary>Total poses = facings × frames.</summary>
    public const int PoseCount = (FacingCount * FramesPerFacing);
    /// <summary>Each 16×16 pose is a 2×2 grid of 8×8 tiles.</summary>
    public const int TilesPerPose = 4;
    /// <summary>The sprite's pixel size (a 16×16 metasprite, the overworld standard).</summary>
    public const int SpriteSize = 16;

    private const int ReduceFactor = (SupersampleSize / SpriteSize);
    private const int SupersampleSize = 128;
    // The procedural walk, as fractions of the avatar bound: a step lifts the body (bob) and shifts it (sway); the two
    // walk frames sway opposite ways so alternating them reads as a stride. The idle frame is dead-centre. This is the
    // FALLBACK cycle — a creation document with authored timeline frames supplies its own poses instead.
    private static readonly Vector3[] WalkPose = [
        new Vector3(x: 0f, y: 0f, z: 0f),      // frame 0: idle / neutral
        new Vector3(x: 0.18f, y: 0.12f, z: 0f),  // frame 1: step A (sway +, bob up)
        new Vector3(x: -0.18f, y: 0.12f, z: 0f), // frame 2: step B (sway −, bob up)
    ];

    /// <summary>A forged avatar sprite sheet: one shared 8-byte OBJ palette and <see cref="PoseCount"/> poses of
    /// <see cref="TilesPerPose"/> ordered tiles each, concatenated (pose p occupies tiles p*4 .. p*4+3). The preview is a
    /// human-readable RGBA grid (facings down the rows, walk frames across) for the forge's <c>.sheet.png</c>.</summary>
    /// <param name="ObjectPalette">The shared 4-colour OBJ palette (8 bytes, RGB555; slot 0 = transparent).</param>
    /// <param name="SpriteTiles">All poses' tiles concatenated, 16 bytes each (PoseCount*TilesPerPose*16 bytes).</param>
    /// <param name="PreviewRgba">A FacingCount×FramesPerFacing grid of the 16×16 poses, RGBA8.</param>
    /// <param name="PreviewWidth">The preview's pixel width (FramesPerFacing × 16).</param>
    /// <param name="PreviewHeight">The preview's pixel height (FacingCount × 16).</param>
    public sealed record AvatarSheet(byte[] ObjectPalette, byte[] SpriteTiles, byte[] PreviewRgba, int PreviewWidth, int PreviewHeight) {
        /// <summary>The number of VRAM tiles the sheet occupies (PoseCount × TilesPerPose).</summary>
        public int TileCount => (SpriteTiles.Length / 16);
    }

    /// <summary>Bakes the sprite sheet with the procedural walk cycle and the classic style — the live in-engine
    /// forge path (the creator's commit loop has no timeline document).</summary>
    /// <param name="device">The GPU device (the live overworld's, or a one-shot host's).</param>
    /// <param name="gpu">The compute services.</param>
    /// <param name="avatar">The player's creation to forge.</param>
    /// <returns>The shared-palette sprite sheet.</returns>
    public static AvatarSheet Forge(IGpuDeviceContext device, IGpuComputeServices gpu, AvatarDefinition avatar) =>
        Forge(avatar: avatar, bundle: out _, device: device, framePoses: null, gpu: gpu, style: null);

    /// <summary>Bakes the sprite sheet through the FULL bake pipeline: 12 views (4 facings × 3 poses) rasterize, the
    /// CPU stages grade + jointly palette-fit + quantize + assemble, and the pipeline's metasprite frames re-compose
    /// into the ROM's fixed 2×2-tile sheet shape. The produced <see cref="BakedAssetBundle"/> comes back too, so the
    /// tool modes can emit its <c>PBAK</c> blob beside the cartridge.</summary>
    /// <param name="device">The one-shot GPU device (from <see cref="ForgeHost"/>) or the live one.</param>
    /// <param name="gpu">The compute services.</param>
    /// <param name="avatar">The player's creation to forge.</param>
    /// <param name="framePoses">The authored walk poses (<see cref="FramesPerFacing"/> recentered shape lists, idle
    /// first), or null for the procedural sway/bob cycle.</param>
    /// <param name="style">The bake style (null = classic). The forge pins the avatar's own knobs on top: one object
    /// palette, and the supersample that fills the 16×16 cell from a 128×128 render.</param>
    /// <param name="bundle">The pipeline's asset bundle (tiles, palette, metasprite frames) for blob emission.</param>
    /// <returns>The shared-palette sprite sheet.</returns>
    public static AvatarSheet Forge(IGpuDeviceContext device, IGpuComputeServices gpu, AvatarDefinition avatar, IReadOnlyList<IReadOnlyList<AvatarShape>>? framePoses, BakeStyle? style, out BakedAssetBundle bundle) {
        ArgumentNullException.ThrowIfNull(avatar);

        var resolved = ((style ?? BakeStyles.Classic) with {
            MaxObjectPalettes = 1,
            SupersampleFactor = ReduceFactor,
        });
        var bound = PoseBound(avatar: avatar, framePoses: framePoses);
        var plan = BuildPlan(avatar: avatar, bound: bound, framePoses: framePoses, style: resolved);
        var views = new List<RasterizedView>(capacity: plan.Views.Count);

        for (var index = 0; (index < plan.Views.Count); index++) {
            views.Add(item: BakePipeline.Rasterize(device: device, gpu: gpu, plan: plan, viewIndex: index));
        }

        // The raw debug preview must snapshot BEFORE RunCpu grades the rasters in place.
        var rawPreview = ((Environment.GetEnvironmentVariable(variable: "PUCK_FORGE_RAW") is not null) ? ComposeRawPreview(views: views) : null);
        var result = BakePipeline.RunCpu(plan: plan, views: views);

        bundle = result.Assets;

        return BuildSheet(rawPreview: rawPreview, result: result);
    }

    /// <summary>Lifts a creation document into the forge's inputs: the recentered <see cref="AvatarDefinition"/>
    /// plus — when the document carries timeline FRAMES — the authored walk poses (idle = the rest pose, the walk
    /// frames = the timeline applied by shape id, all recentered by the SAME rest-pose offset so the cycle never
    /// jitters). A document without frames returns null poses and the forge falls back to the procedural nudge.</summary>
    /// <param name="document">The normalized creation document.</param>
    /// <param name="framePoses">The authored poses (idle first), or null.</param>
    /// <returns>The recentered avatar.</returns>
    public static AvatarDefinition FromCreation(CreationDocument document, out IReadOnlyList<IReadOnlyList<AvatarShape>>? framePoses) {
        ArgumentNullException.ThrowIfNull(document);

        var restShapes = (document.Shapes ?? []);
        var rest = new List<AvatarShape>(capacity: restShapes.Count);

        foreach (var shape in restShapes) {
            rest.Add(item: new AvatarShape(Position: shape.Position, Rotation: shape.Rotation, Scale: shape.Scale, Type: shape.Type));
        }

        var avatar = AvatarDefinition.FromPlacedShapes(shapes: rest);

        if ((document.Frames is not { Count: > 0 } frames) || (restShapes.Count == 0)) {
            framePoses = null;

            return avatar;
        }

        // The idle pose is the recentered rest; walk slots draw from the timeline (cycling when it is shorter than
        // the two walk frames the ROM's cycle plays).
        var offset = RecenterOffset(shapes: rest);
        var poses = new List<IReadOnlyList<AvatarShape>>(capacity: FramesPerFacing) { avatar.Shapes };

        for (var slot = 1; (slot < FramesPerFacing); slot++) {
            poses.Add(item: PoseShapes(frame: frames[((slot - 1) % frames.Count)], offset: offset, restShapes: restShapes));
        }

        framePoses = poses;

        return avatar;
    }

    /// <summary>The flat pose index for a (facing, frame) pair — the SAME layout the overworld ROM computes as
    /// facing × <see cref="FramesPerFacing"/> + frame.</summary>
    public static int PoseIndex(int facing, int frame) => ((facing * FramesPerFacing) + frame);

    // The camera-framing bound: the avatar's own bound, widened so no AUTHORED pose crops (the procedural nudge
    // scales with the bound, so the fallback needs no widening).
    private static float PoseBound(AvatarDefinition avatar, IReadOnlyList<IReadOnlyList<AvatarShape>>? framePoses) {
        var bound = MathF.Max(x: avatar.BoundRadius, y: 0.1f);

        foreach (var pose in (framePoses ?? [])) {
            foreach (var shape in pose) {
                bound = MathF.Max(x: bound, y: (shape.Position.Length() + AvatarDefinition.Reach(scale: shape.Scale, type: shape.Type)));
            }
        }

        return bound;
    }

    // The plan: one program per walk pose, one camera per facing (the original forge's tight 3/4 orbit — the avatar
    // must FILL a 16×16 sprite to survive the crush), views in pose-index order so the pipeline's frames land exactly
    // where the ROM expects facing × 3 + frame.
    private static BakePlan BuildPlan(AvatarDefinition avatar, float bound, IReadOnlyList<IReadOnlyList<AvatarShape>>? framePoses, BakeStyle style) {
        var programs = new SdfProgram[FramesPerFacing];

        for (var frame = 0; (frame < FramesPerFacing); frame++) {
            programs[frame] = ((framePoses is { } poses)
                ? (avatar with { Shapes = poses[frame] }).BuildProgram()
                : avatar.BuildProgram(poseOffset: (WalkPose[frame] * bound)));
        }

        var target = new Vector3(x: 0f, y: (bound * 0.55f), z: 0f);
        var horizontalDistance = (bound * 1.5f);
        var verticalDistance = (bound * 0.75f);
        var views = new List<BakeView>(capacity: PoseCount);

        for (var facing = 0; (facing < FacingCount); facing++) {
            var azimuth = (facing * (MathF.PI / 2f));
            var camera = CameraSnapshot.LookAt(
                fieldOfViewRadians: (45f * (MathF.PI / 180f)),
                position: (target + new Vector3(x: (MathF.Sin(x: azimuth) * horizontalDistance), y: verticalDistance, z: (MathF.Cos(x: azimuth) * horizontalDistance))),
                target: target,
                viewportHeight: SupersampleSize,
                viewportWidth: SupersampleSize
            );

            for (var frame = 0; (frame < FramesPerFacing); frame++) {
                views.Add(item: new BakeView(Camera: camera, Name: $"f{facing}p{frame}", Program: programs[frame]));
            }
        }

        return new BakePlan(
            Budget: new BakeBudget(),
            Intent: BakeIntent.Sprite,
            NativeHeight: SpriteSize,
            NativeWidth: SpriteSize,
            Style: style,
            Target: BakeTarget.Cgb,
            Views: views
        );
    }

    // Adapts the pipeline's output back to the sheet contract: every metasprite frame re-composes into a full 16×16
    // index grid (flips resolved in software — the ROM plays plain tiles), then slices into its four ordered tiles.
    private static AvatarSheet BuildSheet(byte[]? rawPreview, BakeResult result) {
        var sprites = result.Assets.Sprites[0];
        var spriteTiles = new byte[((PoseCount * TilesPerPose) * 16)];

        for (var pose = 0; (pose < PoseCount); pose++) {
            var indices = ComposePoseIndices(frame: sprites.Frames[pose], tiles: sprites.Tiles);
            var tiles = SceneForge.SliceTilesOrdered(height: SpriteSize, indices: indices, tileCount: out var count, width: SpriteSize);

            if (count != TilesPerPose) {
                throw new InvalidOperationException(message: $"A {SpriteSize}×{SpriteSize} pose must slice into {TilesPerPose} tiles (got {count}).");
            }

            tiles.CopyTo(array: spriteTiles, index: ((pose * TilesPerPose) * 16));
        }

        return new AvatarSheet(
            ObjectPalette: ObjectPaletteBytes(palettes: sprites.Palettes),
            PreviewHeight: result.PreviewHeight,
            PreviewRgba: (rawPreview ?? result.PreviewRgba),
            PreviewWidth: result.PreviewWidth,
            SpriteTiles: spriteTiles
        );
    }

    // The joint fit ran with a ONE-palette budget, so the wire form is exactly the sheet's 8 bytes; a degenerate
    // all-transparent avatar (no fitted palette at all) falls back to black.
    private static byte[] ObjectPaletteBytes(BakedPaletteSet palettes) =>
        ((palettes.Rgb555Data.Length == 8) ? palettes.Rgb555Data : new byte[8]);

    // One pose's 16×16 index grid from its anchor-relative OAM entries: each entry's tile decodes from the shared
    // bank, its attribute flips (the CGB dedupe's mirror reuse) resolve in software, and absent cells stay index 0
    // (transparent). The anchor is the native cell's centre, so offsets land on the grid's own quadrants.
    private static byte[] ComposePoseIndices(MetaspriteFrame frame, BakedTileSet tiles) {
        const int anchor = (SpriteSize / 2);
        var indices = new byte[(SpriteSize * SpriteSize)];

        foreach (var entry in frame.Entries) {
            var tile = HgbImage.DecodeTile2bpp(tileBytes: tiles.TileData.AsSpan(start: (entry.TileId * 16), length: 16));
            var flipX = ((entry.Attributes & 0x20) != 0);
            var flipY = ((entry.Attributes & 0x40) != 0);

            for (var row = 0; (row < 8); row++) {
                for (var column = 0; (column < 8); column++) {
                    var x = ((anchor + entry.OffsetX) + column);
                    var y = ((anchor + entry.OffsetY) + row);

                    if ((x < 0) || (x >= SpriteSize) || (y < 0) || (y >= SpriteSize)) {
                        continue;
                    }

                    indices[((y * SpriteSize) + x)] = tile[(((flipY ? (7 - row) : row) * 8) + (flipX ? (7 - column) : column))];
                }
            }
        }

        return indices;
    }

    // DEBUG (PUCK_FORGE_RAW): the pre-crush renders (box-reduced to native, captured before the CPU stages grade the
    // rasters in place), so the SDF output is visible independent of quantization. Same grid as the quantized preview.
    private static byte[] ComposeRawPreview(IReadOnlyList<RasterizedView> views) {
        var previewWidth = (FramesPerFacing * SpriteSize);
        var preview = new byte[((previewWidth * (FacingCount * SpriteSize)) * 4)];

        for (var pose = 0; (pose < views.Count); pose++) {
            var native = HgbImage.BoxReduce(factor: ReduceFactor, height: views[pose].Height, outHeight: out _, outWidth: out _, rgba: views[pose].Rgba, width: views[pose].Width);

            PaintRawCell(facing: (pose / FramesPerFacing), frame: (pose % FramesPerFacing), preview: preview, previewWidth: previewWidth, rgba: native);
        }

        return preview;
    }
    private static void PaintRawCell(byte[] preview, int previewWidth, int facing, int frame, byte[] rgba) {
        var originX = (frame * SpriteSize);
        var originY = (facing * SpriteSize);

        for (var y = 0; (y < SpriteSize); y++) {
            for (var x = 0; (x < SpriteSize); x++) {
                var src = (((y * SpriteSize) + x) * 4);
                var dst = ((((originY + y) * previewWidth) + (originX + x)) * 4);

                preview[dst] = rgba[src];
                preview[(dst + 1)] = rgba[(src + 1)];
                preview[(dst + 2)] = rgba[(src + 2)];
                preview[(dst + 3)] = 0xFF;
            }
        }
    }

    // The recenter offset FromPlacedShapes derives (horizontal centroid on X/Z, the lowest reached point on Y) —
    // replicated so the timeline frames recenter by the REST pose's offset, exactly like the bake planner's doctrine.
    private static Vector3 RecenterOffset(IReadOnlyList<AvatarShape> shapes) {
        var centroid = Vector3.Zero;
        var lowestY = float.MaxValue;

        foreach (var shape in shapes) {
            centroid += shape.Position;
            lowestY = MathF.Min(x: lowestY, y: (shape.Position.Y - AvatarDefinition.Reach(scale: shape.Scale, type: shape.Type)));
        }

        centroid /= shapes.Count;

        return new Vector3(x: centroid.X, y: lowestY, z: centroid.Z);
    }

    // One timeline frame applied over the rest shapes: a transform moves the shape it names, everything else keeps
    // its rest transform, and the whole pose recenters by the shared rest offset.
    private static List<AvatarShape> PoseShapes(FrameDocument frame, Vector3 offset, IReadOnlyList<ShapeDocument> restShapes) {
        var posed = new List<AvatarShape>(capacity: restShapes.Count);

        foreach (var shape in restShapes) {
            var transform = frame.Transforms.FirstOrDefault(predicate: entry => (entry.Id == shape.Id));

            var (position, rotation, scale) = ((transform is { } pose)
                ? (pose.Position, pose.Rotation, pose.Scale)
                : (shape.Position, shape.Rotation, shape.Scale));

            posed.Add(item: new AvatarShape(Position: (position - offset), Rotation: rotation, Scale: scale, Type: shape.Type));
        }

        return posed;
    }
}
