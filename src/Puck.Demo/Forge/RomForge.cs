using System.Numerics;
using Puck.Abstractions.Gpu;
using Puck.Cameras;
using Puck.Capture;
using Puck.HumbleGamingBrick;
using Puck.HumbleGamingBrick.Interfaces;
using Puck.SdfVm;

namespace Puck.Demo.Forge;

/// <summary>
/// The <c>--forge</c> tool mode: the first artifact of the SDF-authored-cartridge pipeline. It authors a mini overworld
/// scene as SDF programs (<see cref="SceneForge"/>), crushes it to the Humble GamingBrick's indexed-tile world, assembles a
/// genuine CGB ROM around the baked assets (<see cref="HgbCartridge.Build"/>), writes the <c>.gbc</c> (plus a preview
/// PNG), and self-verifies by booting the result on a real Humble machine. Boot it separately with <c>--rom</c>.
/// </summary>
internal static class RomForge {
    private const int CreatureSupersample = 128;
    private const int CreatureReduceFactor = 8; // 128 -> 16, a 2x2-tile metasprite.
    private const int CreatureSize = 16;
    private const int MaxTiles = 256; // Single-byte tile ids under 0x8000 unsigned addressing.

    /// <summary>Runs the forge inside the shared GPU host; returns 0 on success.</summary>
    public static Task<int> RunAsync(string outputPath, string[] args) {
        ArgumentException.ThrowIfNullOrEmpty(outputPath);

        return ForgeHost.RunAsync(args: args, work: (device, gpu) => {
            Forge(device: device, gpu: gpu, outputPath: outputPath);

            return 0;
        });
    }

    /// <summary>Forges the Pocket Camera viewfinder cartridge and self-verifies it. Unlike the SDF forge this needs no
    /// GPU — the ROM's pixels come from the M64282FP sensor at run time — so it builds, writes, and boots synchronously:
    /// the boot runs against the emulator's default (deterministic gradient) sensor, so <c>&lt;out&gt;.emulated.png</c>
    /// shows the forged ROM driving the real camera protocol and dithering the gradient onto the brick screen.</summary>
    /// <param name="outputPath">Where to write the <c>.gbc</c>.</param>
    /// <returns>0 on success.</returns>
    public static Task<int> RunCameraAsync(string outputPath) {
        ArgumentException.ThrowIfNullOrEmpty(outputPath);

        var rom = CameraRom.Build(title: "PUCKCAM");

        WriteRom(outputPath: outputPath, rom: rom);
        VerifyBoot(rom: rom, outputPath: outputPath);

        Console.WriteLine(value: $"camera forge | wrote {outputPath} ({rom.Length} bytes) | Pocket Camera (0xFC) viewfinder | boot it with: --rom {outputPath}");

        return Task.FromResult(result: 0);
    }

    /// <summary>Authors the sprite: a blobby, smooth-unioned creature — SDF's home turf, legible once crushed to 16×16.</summary>
    private static SdfProgram BuildCreatureScene() {
        var builder = new SdfProgramBuilder();
        var body = builder.AddMaterial(material: new SdfMaterial(Albedo: new Vector3(0.88f, 0.44f, 0.20f)));
        var belly = builder.AddMaterial(material: new SdfMaterial(Albedo: new Vector3(0.96f, 0.82f, 0.38f)));

        _ = builder.ResetPoint().Sphere(radius: 1.0f, material: body);
        _ = builder.ResetPoint().Translate(offset: new Vector3(0f, 0.95f, 0f)).Sphere(radius: 0.66f, material: body, blend: SdfBlendOp.SmoothUnion, smooth: 0.55f);
        _ = builder.ResetPoint().Translate(offset: new Vector3(-0.55f, -0.8f, 0.25f)).Sphere(radius: 0.34f, material: body, blend: SdfBlendOp.SmoothUnion, smooth: 0.3f);
        _ = builder.ResetPoint().Translate(offset: new Vector3(0.55f, -0.8f, 0.25f)).Sphere(radius: 0.34f, material: body, blend: SdfBlendOp.SmoothUnion, smooth: 0.3f);
        _ = builder.ResetPoint().Translate(offset: new Vector3(0f, 0.05f, 0.8f)).Sphere(radius: 0.42f, material: belly, blend: SdfBlendOp.SmoothUnion, smooth: 0.25f);

        return builder.Build();
    }

    private static void Forge(IGpuDeviceContext device, IGpuComputeServices gpu, string outputPath) {
        var room = SceneForge.ForgeRoom(device: device, gpu: gpu);

        var creatureCamera = CameraSnapshot.LookAt(
            position: new Vector3(0f, 0.15f, 4.4f),
            target: new Vector3(0f, 0.05f, 0f),
            fieldOfViewRadians: (42f * (MathF.PI / 180f)),
            viewportWidth: CreatureSupersample,
            viewportHeight: CreatureSupersample
        );
        var creature = SceneForge.ForgeSprite(device: device, gpu: gpu, program: BuildCreatureScene(), camera: creatureCamera, supersampleWidth: CreatureSupersample, supersampleHeight: CreatureSupersample, reduceFactor: CreatureReduceFactor);

        if ((room.TileCount + creature.TileCount) > MaxTiles) {
            throw new InvalidOperationException(message: $"The forged scene needs {room.TileCount + creature.TileCount} tiles, over the {MaxTiles}-tile VRAM budget.");
        }

        var objectTileBase = room.TileCount;
        var tileData = new byte[room.TileData.Length + creature.TileData.Length];

        room.TileData.CopyTo(array: tileData, index: 0);
        creature.TileData.CopyTo(array: tileData, index: room.TileData.Length);

        var rom = HgbCartridge.Build(
            title: "PUCKFORGE",
            backgroundPalette: HgbImage.EncodePalette(palette: room.Palette),
            objectPalette: HgbImage.EncodePalette(palette: creature.Palette),
            tileData: tileData,
            tileMap: room.TileMap,
            objectAttributes: BuildCreatureOam(objectTileBase: objectTileBase)
        );

        WriteRom(outputPath: outputPath, rom: rom);
        WritePreview(outputPath: outputPath, backgroundPalette: room.Palette, backgroundIndices: room.Indices, objectPalette: creature.Palette, creatureIndices: creature.Indices);
        VerifyBoot(rom: rom, outputPath: outputPath);

        Console.WriteLine(value: $"forge | wrote {outputPath} ({rom.Length} bytes) | {room.TileCount} BG tiles + {creature.TileCount} OBJ tiles | boot it with: --rom {outputPath}");
    }

    // Four 8x8 sprites forming the 16x16 metasprite, centred on the 160x144 screen.
    private static byte[] BuildCreatureOam(int objectTileBase) {
        const int screenLeft = ((SceneForge.ScreenWidth - CreatureSize) / 2);
        const int screenTop = ((SceneForge.ScreenHeight - CreatureSize) / 2);

        (int Dx, int Dy, int Tile)[] sprites = [
            (0, 0, objectTileBase + 0),
            (8, 0, objectTileBase + 1),
            (0, 8, objectTileBase + 2),
            (8, 8, objectTileBase + 3),
        ];

        var oam = new byte[sprites.Length * 4];

        for (var index = 0; (index < sprites.Length); index++) {
            oam[(index * 4) + 0] = (byte)(screenTop + sprites[index].Dy + 16);
            oam[(index * 4) + 1] = (byte)(screenLeft + sprites[index].Dx + 8);
            oam[(index * 4) + 2] = (byte)sprites[index].Tile;
            oam[(index * 4) + 3] = 0x00;
        }

        return oam;
    }

    internal static void WriteRom(string outputPath, byte[] rom) {
        var directory = Path.GetDirectoryName(path: Path.GetFullPath(path: outputPath));

        if (!string.IsNullOrEmpty(value: directory)) {
            _ = Directory.CreateDirectory(path: directory);
        }

        File.WriteAllBytes(path: outputPath, bytes: rom);
    }

    // A 160x144 RGBA preview of what the ROM should display: the quantized background with the creature composited.
    private static void WritePreview(string outputPath, HgbImage.Rgb[] backgroundPalette, byte[] backgroundIndices, HgbImage.Rgb[] objectPalette, byte[] creatureIndices) {
        var preview = new byte[SceneForge.ScreenWidth * SceneForge.ScreenHeight * 4];

        for (var pixel = 0; (pixel < backgroundIndices.Length); pixel++) {
            var colour = backgroundPalette[backgroundIndices[pixel]];
            var offset = (pixel * 4);

            preview[offset] = colour.R;
            preview[offset + 1] = colour.G;
            preview[offset + 2] = colour.B;
            preview[offset + 3] = 0xFF;
        }

        const int screenLeft = ((SceneForge.ScreenWidth - CreatureSize) / 2);
        const int screenTop = ((SceneForge.ScreenHeight - CreatureSize) / 2);

        for (var y = 0; (y < CreatureSize); y++) {
            for (var x = 0; (x < CreatureSize); x++) {
                var index = creatureIndices[(y * CreatureSize) + x];

                if (index == 0) {
                    continue;
                }

                var colour = objectPalette[index];
                var offset = ((((screenTop + y) * SceneForge.ScreenWidth) + (screenLeft + x)) * 4);

                preview[offset] = colour.R;
                preview[offset + 1] = colour.G;
                preview[offset + 2] = colour.B;
                preview[offset + 3] = 0xFF;
            }
        }

        PngEncoder.Write(height: SceneForge.ScreenHeight, path: Path.ChangeExtension(path: outputPath, extension: ".preview.png"), rgba: preview, width: SceneForge.ScreenWidth);
    }

    // Self-verification (the "two worlds meet at a file" round-trip): boot the freshly-forged .gbc on a real Humble CGB
    // machine — the SAME core the demo's --rom path uses, seeded post-boot with A = 0x11 — advance it a few frames, and
    // dump its native 160×144 framebuffer so <out>.emulated.png confirms the ROM boots and renders in colour.
    private static void VerifyBoot(byte[] rom, string outputPath) {
        using var machine = MachineFactory.Create(
            configuration: new MachineConfiguration(model: ConsoleModel.Cgb, cartridgeRom: rom),
            compose: static services => services.AddHumbleGamingBrickComponents()
        );

        machine.Machine.Run(tCycles: (70224UL * 60UL));

        PngEncoder.Write(height: Framebuffer.ScreenHeight, path: Path.ChangeExtension(path: outputPath, extension: ".emulated.png"), rgba: FramebufferToRgba(machine: machine), width: Framebuffer.ScreenWidth);
    }

    /// <summary>Packs a machine's native ARGB framebuffer into the RGBA8 bytes the PNG encoder / strip builder expects.</summary>
    internal static byte[] FramebufferToRgba(MachineInstance machine) {
        var pixels = machine.GetRequiredService<IFramebuffer>().Pixels;
        var rgba = new byte[Framebuffer.ScreenWidth * Framebuffer.ScreenHeight * 4];

        for (var index = 0; (index < pixels.Length); index++) {
            var pixel = pixels[index];
            var offset = (index * 4);

            rgba[offset] = (byte)(pixel >> 16);
            rgba[offset + 1] = (byte)(pixel >> 8);
            rgba[offset + 2] = (byte)pixel;
            rgba[offset + 3] = 0xFF;
        }

        return rgba;
    }
}
