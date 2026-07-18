using System.Numerics;
using Puck.Abstractions.Gpu;
using Puck.Cameras;
using Puck.Demo.Forge.Bake;
using Puck.SdfVm;

namespace Puck.Demo.Forge;

/// <summary>
/// Bakes Chroma's SDF-authored title screen — a glowing well of stacked colour blocks under a night sky, one tilted
/// block dripping into the gap. The scene bakes BOLD as a CGB background, round-trips through the bake's own
/// <c>PBAK</c> wire form (<see cref="Framework.PbakBundle.Parse"/> — the linker consumes exactly what an external
/// assembler would receive), and installs the parsed background through <see cref="ChromaTables.SetTitleArt"/>; the
/// game's manifest links it as the art-backed title screen (tiles after the font, palettes into slots 1..7 — slot 0
/// stays gameplay's — and the menu prompts overlaid, all owned by the linker). The scene is authored on a
/// one-world-unit = one-8-pixel-tile camera so unit-aligned blocks land ON the tile grid and the flat-emissive fills
/// dedupe hard. Install is best-effort by design: any failure (no GPU, a blown budget) narrates and leaves the
/// hand-authored title in place.
/// </summary>
internal static class ChromaTitleBake {
    /// <summary>The baked title's tile ceiling: the title shares VRAM's single-byte tile ids with the game + font
    /// tiles and the cartridge's 16 KiB data window with every other table — 120 keeps both comfortable.</summary>
    public const int TileBudget = 120;

    // Background palette slots 1..7 — slot 0 belongs to the gameplay palette the play screen renders with.
    private const int PaletteBudget = 7;
    private const int NativeHeight = 144;
    private const int NativeWidth = 160;
    private const float TanHalfFov = 0.41421356f; // tan(45°/2)

    /// <summary>Bakes the title scene and installs it as the cartridge's title art. Never throws.</summary>
    /// <param name="device">The live (or one-shot) GPU device.</param>
    /// <param name="gpu">The compute services.</param>
    /// <returns>Whether the baked title is installed (false = the hand-authored title stays).</returns>
    public static bool TryInstall(IGpuDeviceContext device, IGpuComputeServices gpu) {
        try {
            var style = (BakeStyles.Bold with { MaxBackgroundPalettes = PaletteBudget });
            var plan = new BakePlan(
                Budget: new BakeBudget(MaxBackgroundPalettes: PaletteBudget, MaxTiles: TileBudget),
                Intent: BakeIntent.Background,
                NativeHeight: NativeHeight,
                NativeWidth: NativeWidth,
                Style: style,
                Target: BakeTarget.Cgb,
                Views: [TitleView(style: style)]
            );
            var result = BakePipeline.Run(device: device, gpu: gpu, plan: plan);
            var bundle = Framework.PbakBundle.Parse(blob: result.Assets.ToBlob());

            if (bundle.Background is not { AttributeMap: not null } background) {
                Console.Error.WriteLine(value: "chroma title bake | no background/attributes produced; using the hand-authored title");

                return false;
            }

            if ((background.TileCount > TileBudget) || (background.PaletteCount > PaletteBudget)) {
                Console.Error.WriteLine(value: $"chroma title bake | over budget ({background.TileCount} tiles vs {TileBudget}, {background.PaletteCount} palettes vs {PaletteBudget}); using the hand-authored title");

                return false;
            }

            ChromaTables.SetTitleArt(art: background);
            Console.Error.WriteLine(value: $"chroma title bake | baked title installed | {background.TileCount} tiles (budget {TileBudget}) | {background.PaletteCount} palettes → slots 1..{background.PaletteCount} | style {result.Assets.StyleName}");

            return true;
        } catch (Exception exception) {
            Console.Error.WriteLine(value: $"chroma title bake | failed ({exception.Message}); using the hand-authored title");

            return false;
        }
    }

    // The head-on camera framing exactly 20×18 world units onto 160×144 pixels (one unit = one tile).
    private static BakeView TitleView(BakeStyle style) {
        var camera = CameraSnapshot.LookAt(
            fieldOfViewRadians: (45f * (MathF.PI / 180f)),
            position: new Vector3(x: 0f, y: 0f, z: ((NativeHeight / 16f) / TanHalfFov)),
            target: Vector3.Zero,
            viewportHeight: (uint)(NativeHeight * style.SupersampleFactor),
            viewportWidth: (uint)(NativeWidth * style.SupersampleFactor)
        );

        return new BakeView(Camera: camera, Name: "title", Program: BuildTitleScene());
    }

    // The emblem scene. World frame: x ∈ [-10, 10], y ∈ [-9, 9], y up; everything sits near z = 0 (thin in depth so
    // perspective cannot slide edges off the tile grid). High emissive flattens the lighting — flat fills survive the
    // bold dither as REPEATED 4×4 patterns, which the tile dedupe then collapses.
    private static SdfProgram BuildTitleScene() {
        var builder = new SdfProgramBuilder();
        var sky = builder.AddMaterial(material: new SdfMaterial(Albedo: new Vector3(x: 0.08f, y: 0.08f, z: 0.16f), Emissive: 1.4f));
        var wellWall = builder.AddMaterial(material: new SdfMaterial(Albedo: new Vector3(x: 0.16f, y: 0.17f, z: 0.28f), Emissive: 1.0f));
        var red = builder.AddMaterial(material: new SdfMaterial(Albedo: new Vector3(x: 0.93f, y: 0.38f, z: 0.41f), Emissive: 1.2f));
        var green = builder.AddMaterial(material: new SdfMaterial(Albedo: new Vector3(x: 0.47f, y: 0.82f, z: 0.51f), Emissive: 1.2f));
        var blue = builder.AddMaterial(material: new SdfMaterial(Albedo: new Vector3(x: 0.43f, y: 0.67f, z: 0.94f), Emissive: 1.2f));
        var faller = builder.AddMaterial(material: new SdfMaterial(Albedo: new Vector3(x: 0.98f, y: 0.50f, z: 0.52f), Emissive: 1.6f));

        // Backdrop + the two well walls flanking a 8-unit-wide well.
        _ = builder.ResetPoint().Translate(offset: new Vector3(x: 0f, y: 0f, z: -2f)).Box(halfExtents: new Vector3(x: 30f, y: 20f, z: 0.5f), round: 0f, material: sky);
        _ = builder.ResetPoint().Translate(offset: new Vector3(x: -5.5f, y: -3.0f, z: 0f)).Box(halfExtents: new Vector3(x: 0.5f, y: 6.0f, z: 0.2f), round: 0f, material: wellWall);
        _ = builder.ResetPoint().Translate(offset: new Vector3(x: 5.5f, y: -3.0f, z: 0f)).Box(halfExtents: new Vector3(x: 0.5f, y: 6.0f, z: 0.2f), round: 0f, material: wellWall);
        _ = builder.ResetPoint().Translate(offset: new Vector3(x: 0f, y: -8.5f, z: 0f)).Box(halfExtents: new Vector3(x: 6.0f, y: 0.5f, z: 0.2f), round: 0f, material: wellWall);

        // The settled blocks: unit cells stacked in the well, colours interleaved so no course reads as one slab, and
        // a one-column gap under the falling block.
        int[][] columns = [
            [0, 1, 2],    // x = -4.5: red, green, blue bottom-up.
            [1, 2],       // x = -3.5
            [2, 0, 1, 0], // x = -2.5
            [0, 2],       // x = -1.5
            [],           // x = -0.5: the gap the faller drops into.
            [2, 1],       // x = 0.5
            [1, 0, 2],    // x = 1.5
            [0, 1],       // x = 2.5
            [2, 0, 1],    // x = 3.5
            [1, 2, 0, 2], // x = 4.5
        ];
        int[] materials = [red, green, blue];

        for (var column = 0; (column < columns.Length); column++) {
            var x = (-4.5f + column);

            for (var level = 0; (level < columns[column].Length); level++) {
                _ = builder
                    .ResetPoint()
                    .Translate(offset: new Vector3(x: x, y: (-7.5f + level), z: 0f))
                    .Box(halfExtents: new Vector3(x: 0.5f, y: 0.5f, z: 0.2f), round: 0f, material: materials[columns[column][level]]);
            }
        }

        // The dripping block: tilted mid-fall over the gap — a bounded splash of unique tiles that reads as MOTION
        // against the locked stack.
        var tilt = Quaternion.CreateFromAxisAngle(axis: Vector3.UnitZ, angle: (-14f * (MathF.PI / 180f)));

        _ = builder.ResetPoint().Translate(offset: new Vector3(x: -0.5f, y: 3.6f, z: 0f)).Rotate(rotation: tilt).Box(halfExtents: new Vector3(x: 0.9f, y: 0.9f, z: 0.2f), round: 0.08f, material: faller);
        _ = builder.ResetPoint().Translate(offset: new Vector3(x: -0.2f, y: 6.2f, z: 0f)).Rotate(rotation: Quaternion.CreateFromAxisAngle(axis: Vector3.UnitZ, angle: (10f * (MathF.PI / 180f)))).Box(halfExtents: new Vector3(x: 0.5f, y: 0.5f, z: 0.2f), round: 0.08f, material: red);

        return builder.Build();
    }
}
