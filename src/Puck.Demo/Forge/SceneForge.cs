using System.Numerics;
using Puck.Abstractions.Gpu;
using Puck.Cameras;
using Puck.Compositing;
using Puck.SdfVm;

namespace Puck.Demo.Forge;

/// <summary>
/// The reusable render-and-crush core of the forge: author an SDF scene, render it on the GPU, box-reduce a supersample
/// to the target grid, derive a 4-colour palette, quantize, and slice into brick tiles. Used by the static-scene
/// forge (<see cref="RomForge"/>). Deliberately backend-neutral and side-effect free — it returns baked assets; the
/// caller decides what cartridge to assemble around them.
/// </summary>
internal static class SceneForge {
    public const int ScreenHeight = 144;
    public const int ScreenWidth = 160;

    private const int RoomReduceFactor = 2;
    private const int RoomSupersampleHeight = 288;
    private const int RoomSupersampleWidth = 320;

    /// <summary>A forged background: deduplicated tiles, a 32×32 tilemap, the derived palette, the unique-tile count,
    /// and the raw quantized indices (for previews).</summary>
    public sealed record RoomAssets(byte[] TileData, byte[] TileMap, HgbImage.Rgb[] Palette, int TileCount, byte[] Indices);

    /// <summary>A forged sprite: its tiles in row-major order (NOT deduplicated — OAM needs a known order), the derived
    /// palette (slot 0 = the sampled transparent background), the tile count, the raw indices, and its pixel size.</summary>
    public sealed record SpriteAssets(byte[] TileData, HgbImage.Rgb[] Palette, int TileCount, byte[] Indices, int Width, int Height);

    public static byte[] Render(IGpuDeviceContext device, IGpuComputeServices gpu, SdfProgram program, int width, int height, CameraSnapshot camera) {
        using var engine = new SdfWorldEngine(
            device: device,
            gpu: gpu,
            height: (uint)height,
            kernels: SdfWorldKernels.Load(bytecodeExtension: ".spv"),
            options: new SdfWorldEngineOptions(Program: program),
            width: (uint)width
        );

        var frame = new SdfFrame(
            Program: program,
            ProgramChanged: false,
            Views: [new SdfViewSnapshot(Camera: camera, Region: new NormalizedRect(X: 0f, Y: 0f, Width: 1f, Height: 1f))],
            Time: 0f,
            WarpAmount: 0f
        );

        return engine.RenderFrame(frame: frame);
    }

    /// <summary>Adds the shared room — a floor plane inside four low border walls — to <paramref name="builder"/>, so
    /// callers that want more in the same world (e.g. the membrane pane's dynamic avatar) append to it. Single-sourcing
    /// the room keeps the GB background and the live SDF pane the SAME room.</summary>
    public static SdfProgramBuilder AddRoom(SdfProgramBuilder builder) {
        ArgumentNullException.ThrowIfNull(builder);

        var floor = builder.AddMaterial(material: new SdfMaterial(Albedo: new Vector3(x: 0.34f, y: 0.56f, z: 0.36f)));
        var wall = builder.AddMaterial(material: new SdfMaterial(Albedo: new Vector3(x: 0.52f, y: 0.46f, z: 0.60f)));

        _ = builder.Plane(normal: Vector3.UnitY, offset: 0f, material: floor);
        _ = builder.ResetPoint().Translate(offset: new Vector3(x: 0f, y: 0.6f, z: -6f)).Box(halfExtents: new Vector3(x: 6f, y: 0.6f, z: 0.3f), round: 0.06f, material: wall);
        _ = builder.ResetPoint().Translate(offset: new Vector3(x: 0f, y: 0.6f, z: 6f)).Box(halfExtents: new Vector3(x: 6f, y: 0.6f, z: 0.3f), round: 0.06f, material: wall);
        _ = builder.ResetPoint().Translate(offset: new Vector3(x: -6f, y: 0.6f, z: 0f)).Box(halfExtents: new Vector3(x: 0.3f, y: 0.6f, z: 6f), round: 0.06f, material: wall);
        _ = builder.ResetPoint().Translate(offset: new Vector3(x: 6f, y: 0.6f, z: 0f)).Box(halfExtents: new Vector3(x: 0.3f, y: 0.6f, z: 6f), round: 0.06f, material: wall);

        return builder;
    }

    /// <summary>Authors the shared room the tilemap is forged from.</summary>
    public static SdfProgram BuildRoomScene() => AddRoom(builder: new SdfProgramBuilder()).Build();

    /// <summary>The room camera — a near-top-down 3/4 view. Exposed so the live SDF pane frames the room the same way
    /// the GB background was forged from.</summary>
    public static CameraSnapshot RoomCamera(int width, int height) =>
        CameraSnapshot.LookAt(
            position: new Vector3(x: 0f, y: 16f, z: 3.5f),
            target: new Vector3(x: 0f, y: 0f, z: 0f),
            fieldOfViewRadians: (52f * (MathF.PI / 180f)),
            viewportWidth: (uint)width,
            viewportHeight: (uint)height
        );

    /// <summary>Forges the room background: render → reduce to the screen → quantize → slice + dedup into BG tiles.</summary>
    public static RoomAssets ForgeRoom(IGpuDeviceContext device, IGpuComputeServices gpu) {
        var roomCamera = RoomCamera(width: RoomSupersampleWidth, height: RoomSupersampleHeight);
        var high = Render(device: device, gpu: gpu, program: BuildRoomScene(), width: RoomSupersampleWidth, height: RoomSupersampleHeight, camera: roomCamera);
        var room = HgbImage.BoxReduce(rgba: high, width: RoomSupersampleWidth, height: RoomSupersampleHeight, factor: RoomReduceFactor, outWidth: out _, outHeight: out _);

        var palette = HgbImage.MedianCutPalette(pixels: CollectPixels(rgba: room, count: (ScreenWidth * ScreenHeight)), count: 4);
        var indices = HgbImage.Quantize(rgba: room, width: ScreenWidth, height: ScreenHeight, palette: palette);

        HgbImage.SliceTilesDeduplicated(indices: indices, width: ScreenWidth, height: ScreenHeight, tileData: out var tiles, tileIds: out var ids, tileCount: out var count);

        return new RoomAssets(TileData: tiles, TileMap: BuildTileMap(tileIds: ids, tilesWide: (ScreenWidth / 8), tilesHigh: (ScreenHeight / 8)), Palette: palette, TileCount: count, Indices: indices);
    }

    /// <summary>Forges a sprite from an SDF scene: render at a supersample → box-reduce → sample the corner as the
    /// transparent slot 0 → quantize → slice into ordered tiles.</summary>
    public static SpriteAssets ForgeSprite(IGpuDeviceContext device, IGpuComputeServices gpu, SdfProgram program, CameraSnapshot camera, int supersampleWidth, int supersampleHeight, int reduceFactor) {
        var high = Render(device: device, gpu: gpu, program: program, width: supersampleWidth, height: supersampleHeight, camera: camera);
        var image = HgbImage.BoxReduce(rgba: high, width: supersampleWidth, height: supersampleHeight, factor: reduceFactor, outWidth: out var width, outHeight: out var height);

        var background = HgbImage.PixelAt(rgba: image, width: width, x: 0, y: 0);
        var foreground = CollectForegroundPixels(rgba: image, count: (width * height), background: background, thresholdSquared: 1600);
        var palette = HgbImage.MedianCutPalette(pixels: foreground, count: 4, seed: [background]);
        var indices = HgbImage.Quantize(rgba: image, width: width, height: height, palette: palette);
        var tiles = SliceTilesOrdered(indices: indices, width: width, height: height, tileCount: out var count);

        return new SpriteAssets(TileData: tiles, Palette: palette, TileCount: count, Indices: indices, Width: width, Height: height);
    }

    /// <summary>The 32×32 tilemap: the forged image in the top-left, the rest (offscreen) left as tile 0.</summary>
    public static byte[] BuildTileMap(byte[] tileIds, int tilesWide, int tilesHigh) {
        var map = new byte[0x400];

        for (var row = 0; (row < tilesHigh); row++) {
            for (var column = 0; (column < tilesWide); column++) {
                map[((row * 32) + column)] = tileIds[((row * tilesWide) + column)];
            }
        }

        return map;
    }

    /// <summary>Slices a WxH index image into 8×8 tiles in row-major order with NO dedup — a metasprite needs its tiles
    /// in a known order for OAM to reference them.</summary>
    public static byte[] SliceTilesOrdered(byte[] indices, int width, int height, out int tileCount) {
        var tilesWide = (width / 8);
        var tilesHigh = (height / 8);

        tileCount = (tilesWide * tilesHigh);

        var tileData = new byte[(tileCount * 16)];
        var tileIndices = new byte[64];
        var tile = 0;

        for (var tileY = 0; (tileY < tilesHigh); tileY++) {
            for (var tileX = 0; (tileX < tilesWide); tileX++) {
                for (var row = 0; (row < 8); row++) {
                    for (var column = 0; (column < 8); column++) {
                        tileIndices[((row * 8) + column)] = indices[((((tileY * 8) + row) * width) + ((tileX * 8) + column))];
                    }
                }

                HgbImage.EncodeTile2bpp(tileIndices: tileIndices).CopyTo(array: tileData, index: (tile * 16));
                tile++;
            }
        }

        return tileData;
    }
    public static List<HgbImage.Rgb> CollectPixels(byte[] rgba, int count) {
        var pixels = new List<HgbImage.Rgb>(capacity: count);

        for (var pixel = 0; (pixel < count); pixel++) {
            var offset = (pixel * 4);

            pixels.Add(item: new HgbImage.Rgb(R: rgba[offset], G: rgba[(offset + 1)], B: rgba[(offset + 2)]));
        }

        return pixels;
    }
    public static List<HgbImage.Rgb> CollectForegroundPixels(byte[] rgba, int count, HgbImage.Rgb background, int thresholdSquared) {
        var pixels = new List<HgbImage.Rgb>();

        for (var pixel = 0; (pixel < count); pixel++) {
            var offset = (pixel * 4);
            var colour = new HgbImage.Rgb(R: rgba[offset], G: rgba[(offset + 1)], B: rgba[(offset + 2)]);

            if (colour.DistanceSquaredTo(other: background) > thresholdSquared) {
                pixels.Add(item: colour);
            }
        }

        // A near-empty foreground (tiny sprite) would starve the median cut — fall back to everything.
        return ((pixels.Count >= 3) ? pixels : CollectPixels(rgba: rgba, count: count));
    }
}
