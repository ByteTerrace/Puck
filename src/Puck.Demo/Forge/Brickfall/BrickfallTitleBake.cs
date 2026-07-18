using System.Numerics;
using Puck.Abstractions.Gpu;
using Puck.Cameras;
using Puck.Demo.Forge.Bake;
using Puck.SdfVm;

namespace Puck.Demo.Forge;

/// <summary>
/// Bakes Brickfall's SDF-authored title screen — the sculpt→bake→forge pillars meeting inside a shipped cartridge.
/// Text is not SDF's home turf, so the title reads as an EMBLEM: a tilted falling brick (with two stray blocks
/// tumbling after it) dropping into the gap of a block-stacked skyline over a night sky. The scene bakes BOLD as a
/// CGB background, round-trips through the bake's own <c>PBAK</c> wire form (<see cref="Framework.PbakBundle.Parse"/>
/// — the linker consumes exactly what an external assembler would receive), and installs the parsed background
/// through <see cref="BrickfallTables.SetTitleArt"/>; the game's manifest links it as the art-backed title screen
/// (tiles after the font, palettes into slots 1..7 — slot 0 stays gameplay's — and the menu prompts overlaid, all
/// owned by the linker). The scene is authored on a one-world-unit = one-8-pixel-tile camera so unit-aligned blocks
/// land ON the tile grid and the flat-emissive fills dedupe hard. Install is best-effort by design: any failure (no
/// GPU, a blown budget) narrates and leaves the hand-authored title in place.
/// </summary>
internal static class BrickfallTitleBake {
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
                Console.Error.WriteLine(value: "brickfall title bake | no background/attributes produced; using the hand-authored title");

                return false;
            }

            if ((background.TileCount > TileBudget) || (background.PaletteCount > PaletteBudget)) {
                Console.Error.WriteLine(value: $"brickfall title bake | over budget ({background.TileCount} tiles vs {TileBudget}, {background.PaletteCount} palettes vs {PaletteBudget}); using the hand-authored title");

                return false;
            }

            BrickfallTables.SetTitleArt(art: background);
            Console.Error.WriteLine(value: $"brickfall title bake | baked title installed | {background.TileCount} tiles (budget {TileBudget}) | {background.PaletteCount} palettes → slots 1..{background.PaletteCount} | style {result.Assets.StyleName}");

            return true;
        } catch (Exception exception) {
            Console.Error.WriteLine(value: $"brickfall title bake | failed ({exception.Message}); using the hand-authored title");

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
        var sky = builder.AddMaterial(material: new SdfMaterial(Albedo: new Vector3(x: 0.09f, y: 0.08f, z: 0.20f), Emissive: 1.4f));
        var ground = builder.AddMaterial(material: new SdfMaterial(Albedo: new Vector3(x: 0.15f, y: 0.16f, z: 0.24f), Emissive: 1.1f));
        var cyan = builder.AddMaterial(material: new SdfMaterial(Albedo: new Vector3(x: 0.30f, y: 0.70f, z: 0.80f), Emissive: 1.0f));
        var cyanDim = builder.AddMaterial(material: new SdfMaterial(Albedo: new Vector3(x: 0.20f, y: 0.48f, z: 0.56f), Emissive: 1.0f));
        var orange = builder.AddMaterial(material: new SdfMaterial(Albedo: new Vector3(x: 0.95f, y: 0.62f, z: 0.22f), Emissive: 1.0f));
        var orangeDim = builder.AddMaterial(material: new SdfMaterial(Albedo: new Vector3(x: 0.66f, y: 0.42f, z: 0.15f), Emissive: 1.0f));
        var white = builder.AddMaterial(material: new SdfMaterial(Albedo: new Vector3(x: 0.90f, y: 0.92f, z: 0.96f), Emissive: 1.0f));
        var whiteDim = builder.AddMaterial(material: new SdfMaterial(Albedo: new Vector3(x: 0.60f, y: 0.62f, z: 0.68f), Emissive: 1.0f));
        var brick = builder.AddMaterial(material: new SdfMaterial(Albedo: new Vector3(x: 1.00f, y: 0.72f, z: 0.28f), Emissive: 1.6f));

        // Backdrop + ground band (rows 16..17).
        _ = builder.ResetPoint().Translate(offset: new Vector3(x: 0f, y: 0f, z: -2f)).Box(halfExtents: new Vector3(x: 30f, y: 20f, z: 0.5f), round: 0f, material: sky);
        _ = builder.ResetPoint().Translate(offset: new Vector3(x: 0f, y: -8f, z: 0f)).Box(halfExtents: new Vector3(x: 12f, y: 1f, z: 0.2f), round: 0f, material: ground);

        // The skyline: block-stacked columns (unit blocks, courses alternating bright/dim so the stacks READ as
        // blocks), leaving a well-like gap around x ∈ [-1, 3] for the falling brick.
        (float X, int Height, int Bright, int Dim)[] columns = [
            (-9.5f, 4, cyan, cyanDim),
            (-8.5f, 6, orange, orangeDim),
            (-7.5f, 3, white, whiteDim),
            (-6.5f, 5, cyan, cyanDim),
            (-5.5f, 2, orange, orangeDim),
            (-4.5f, 7, white, whiteDim),
            (-3.5f, 4, cyan, cyanDim),
            (-2.5f, 2, orange, orangeDim),
            (-1.5f, 5, cyan, cyanDim),
            (3.5f, 3, orange, orangeDim),
            (4.5f, 6, white, whiteDim),
            (5.5f, 2, cyan, cyanDim),
            (6.5f, 5, orange, orangeDim),
            (7.5f, 3, cyan, cyanDim),
            (8.5f, 6, white, whiteDim),
            (9.5f, 4, orange, orangeDim),
        ];

        foreach (var (x, height, bright, dim) in columns) {
            for (var level = 0; (level < height); level++) {
                _ = builder
                    .ResetPoint()
                    .Translate(offset: new Vector3(x: x, y: (-6.5f + level), z: 0f))
                    .Box(halfExtents: new Vector3(x: 0.5f, y: 0.5f, z: 0.2f), round: 0f, material: (((level % 2) == 0) ? bright : dim));
            }
        }

        // The falling brick: an L of three unit-and-a-half blocks, tilted mid-tumble over the gap, with two stray
        // blocks tumbling after it. The tilt deliberately leaves the grid — a bounded splash of unique tiles that
        // reads as MOTION against the locked skyline.
        var tilt = Quaternion.CreateFromAxisAngle(axis: Vector3.UnitZ, angle: (-16f * (MathF.PI / 180f)));

        _ = builder.ResetPoint().Translate(offset: new Vector3(x: 1.0f, y: 4.4f, z: 0f)).Rotate(rotation: tilt).Box(halfExtents: new Vector3(x: 2.2f, y: 0.75f, z: 0.2f), round: 0.05f, material: brick);
        _ = builder.ResetPoint().Translate(offset: new Vector3(x: 1.0f, y: 4.4f, z: 0f)).Rotate(rotation: tilt).Translate(offset: new Vector3(x: -1.45f, y: 1.5f, z: 0f)).Box(halfExtents: new Vector3(x: 0.75f, y: 0.75f, z: 0.2f), round: 0.05f, material: brick);
        _ = builder.ResetPoint().Translate(offset: new Vector3(x: -0.8f, y: 7.2f, z: 0f)).Rotate(rotation: Quaternion.CreateFromAxisAngle(axis: Vector3.UnitZ, angle: (24f * (MathF.PI / 180f)))).Box(halfExtents: new Vector3(x: 0.5f, y: 0.5f, z: 0.2f), round: 0.05f, material: orange);
        _ = builder.ResetPoint().Translate(offset: new Vector3(x: 3.3f, y: 6.4f, z: 0f)).Rotate(rotation: Quaternion.CreateFromAxisAngle(axis: Vector3.UnitZ, angle: (-32f * (MathF.PI / 180f)))).Box(halfExtents: new Vector3(x: 0.5f, y: 0.5f, z: 0.2f), round: 0.05f, material: white);

        return builder.Build();
    }

}
