using System.Numerics;
using Puck.Abstractions.Gpu;
using Puck.Cameras;
using Puck.Demo.Forge.Bake;
using Puck.SdfVm;

namespace Puck.Demo.Forge;

/// <summary>
/// Bakes Volley's SDF-authored title screen — a floodlit night court seen head-on: the dashed net down the centre,
/// the two paddle bars mid-rally, and the amber ball tilted mid-flight between them. The scene bakes BOLD as a CGB
/// background, round-trips through the bake's own <c>PBAK</c> wire form (<see cref="Framework.PbakBundle.Parse"/> —
/// the linker consumes exactly what an external assembler would receive), and installs the parsed background through
/// <see cref="VolleyTables.SetTitleArt"/>; the game's manifest links it as the art-backed title screen (tiles after
/// the font, palettes into slots 1..7 — slot 0 stays gameplay's — and the menu prompts overlaid, all owned by the
/// linker). The scene is authored on a one-world-unit = one-8-pixel-tile camera so unit-aligned boxes land ON the
/// tile grid and the flat-emissive fills dedupe hard. Install is best-effort by design: any failure (no GPU, a blown
/// budget) narrates and leaves the hand-authored title in place.
/// </summary>
internal static class VolleyTitleBake {
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
                Console.Error.WriteLine(value: "volley title bake | no background/attributes produced; using the hand-authored title");

                return false;
            }

            if ((background.TileCount > TileBudget) || (background.PaletteCount > PaletteBudget)) {
                Console.Error.WriteLine(value: $"volley title bake | over budget ({background.TileCount} tiles vs {TileBudget}, {background.PaletteCount} palettes vs {PaletteBudget}); using the hand-authored title");

                return false;
            }

            VolleyTables.SetTitleArt(art: background);
            Console.Error.WriteLine(value: $"volley title bake | baked title installed | {background.TileCount} tiles (budget {TileBudget}) | {background.PaletteCount} palettes → slots 1..{background.PaletteCount} | style {result.Assets.StyleName}");

            return true;
        } catch (Exception exception) {
            Console.Error.WriteLine(value: $"volley title bake | failed ({exception.Message}); using the hand-authored title");

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
        var sky = builder.AddMaterial(material: new SdfMaterial(Albedo: new Vector3(x: 0.07f, y: 0.08f, z: 0.14f), Emissive: 1.4f));
        var court = builder.AddMaterial(material: new SdfMaterial(Albedo: new Vector3(x: 0.13f, y: 0.15f, z: 0.24f), Emissive: 1.1f));
        var cyan = builder.AddMaterial(material: new SdfMaterial(Albedo: new Vector3(x: 0.38f, y: 0.69f, z: 0.78f), Emissive: 1.2f));
        var white = builder.AddMaterial(material: new SdfMaterial(Albedo: new Vector3(x: 0.91f, y: 0.93f, z: 0.96f), Emissive: 1.3f));
        var whiteDim = builder.AddMaterial(material: new SdfMaterial(Albedo: new Vector3(x: 0.62f, y: 0.64f, z: 0.70f), Emissive: 1.0f));
        var amber = builder.AddMaterial(material: new SdfMaterial(Albedo: new Vector3(x: 0.97f, y: 0.84f, z: 0.36f), Emissive: 1.6f));
        var amberDim = builder.AddMaterial(material: new SdfMaterial(Albedo: new Vector3(x: 0.66f, y: 0.55f, z: 0.22f), Emissive: 1.0f));

        // Backdrop + the court band along the bottom (rows 15..17).
        _ = builder.ResetPoint().Translate(offset: new Vector3(x: 0f, y: 0f, z: -2f)).Box(halfExtents: new Vector3(x: 30f, y: 20f, z: 0.5f), round: 0f, material: sky);
        _ = builder.ResetPoint().Translate(offset: new Vector3(x: 0f, y: -7.5f, z: 0f)).Box(halfExtents: new Vector3(x: 12f, y: 1.5f, z: 0.2f), round: 0f, material: court);

        // The dashed net down the centre: unit dashes with unit gaps, on the grid.
        for (var dash = 0; (dash < 6); dash++) {
            _ = builder
                .ResetPoint()
                .Translate(offset: new Vector3(x: 0f, y: (-5.5f + (dash * 2f)), z: 0f))
                .Box(halfExtents: new Vector3(x: 0.5f, y: 0.5f, z: 0.2f), round: 0f, material: cyan);
        }

        // The two paddle bars mid-rally: tall unit-wide boxes with a dim inner course so the bars read as columns.
        _ = builder.ResetPoint().Translate(offset: new Vector3(x: -8.5f, y: 1.0f, z: 0f)).Box(halfExtents: new Vector3(x: 0.5f, y: 2.5f, z: 0.2f), round: 0f, material: white);
        _ = builder.ResetPoint().Translate(offset: new Vector3(x: -8.5f, y: 1.0f, z: 0.05f)).Box(halfExtents: new Vector3(x: 0.2f, y: 2.0f, z: 0.2f), round: 0f, material: whiteDim);
        _ = builder.ResetPoint().Translate(offset: new Vector3(x: 8.5f, y: -2.0f, z: 0f)).Box(halfExtents: new Vector3(x: 0.5f, y: 2.5f, z: 0.2f), round: 0f, material: white);
        _ = builder.ResetPoint().Translate(offset: new Vector3(x: 8.5f, y: -2.0f, z: 0.05f)).Box(halfExtents: new Vector3(x: 0.2f, y: 2.0f, z: 0.2f), round: 0f, material: whiteDim);

        // The ball mid-flight, tilted off the grid so it reads as MOTION, with two trailing echoes fading behind it.
        var tilt = Quaternion.CreateFromAxisAngle(axis: Vector3.UnitZ, angle: (18f * (MathF.PI / 180f)));

        _ = builder.ResetPoint().Translate(offset: new Vector3(x: 2.5f, y: 3.2f, z: 0f)).Rotate(rotation: tilt).Box(halfExtents: new Vector3(x: 0.9f, y: 0.9f, z: 0.2f), round: 0.35f, material: amber);
        _ = builder.ResetPoint().Translate(offset: new Vector3(x: 0.4f, y: 2.2f, z: 0f)).Rotate(rotation: tilt).Box(halfExtents: new Vector3(x: 0.6f, y: 0.6f, z: 0.2f), round: 0.25f, material: amberDim);
        _ = builder.ResetPoint().Translate(offset: new Vector3(x: -1.4f, y: 1.4f, z: 0f)).Rotate(rotation: tilt).Box(halfExtents: new Vector3(x: 0.4f, y: 0.4f, z: 0.2f), round: 0.18f, material: amberDim);

        return builder.Build();
    }
}
