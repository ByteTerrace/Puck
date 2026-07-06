using System.Numerics;
using Puck.Abstractions.Gpu;
using Puck.Cameras;
using Puck.Demo.Forge.Bake;
using Puck.Demo.Forge.Framework;
using Puck.SdfVm;

namespace Puck.Demo.Forge.Cards;

/// <summary>
/// The card layer's SDF authoring and bake helpers — the studio eating its own output for the card games. Scenes
/// are authored on the <see cref="BrickfallTitleBake"/> one-world-unit-per-tile camera convention (flat emissive
/// fills, thin in depth, unit-aligned blocks) so they crush cleanly; <see cref="BakeBackgroundArt"/> and
/// <see cref="BakeCursorSprites"/> run a plan through the pipeline, round-trip the result through the bake's own
/// <c>PBAK</c> wire form (the linker consumes exactly what an external assembler would receive), and enforce the
/// caller's tile/palette budgets — over budget or malformed means <see langword="null"/> and a narrated fallback,
/// never a throw. The suit-symbol and card-shape builders are shared authoring vocabulary: Solitaire's title bakes
/// with them today and Poker's bakes with them next.
/// </summary>
internal static class CardArtBake {
    private const float TanHalfFov = 0.41421356f; // tan(45°/2)

    /// <summary>Bakes a background scene and returns its parsed, budget-checked <c>PBAK</c> section.</summary>
    /// <param name="device">The live (or one-shot) GPU device.</param>
    /// <param name="gpu">The compute services.</param>
    /// <param name="scene">The authored scene.</param>
    /// <param name="name">The view's diagnostic name.</param>
    /// <param name="tileBudget">The section's tile ceiling (its share of the composed 256-tile bank).</param>
    /// <param name="paletteBudget">The section's background-palette-slot ceiling.</param>
    /// <returns>The parsed background, or <see langword="null"/> (narrated) on any failure.</returns>
    public static PbakBackground? BakeBackgroundArt(IGpuDeviceContext device, IGpuComputeServices gpu, SdfProgram scene, string name, int tileBudget, int paletteBudget) {
        try {
            var style = (BakeStyles.Classic with { MaxBackgroundPalettes = paletteBudget });
            var plan = new BakePlan(
                Budget: new BakeBudget(MaxBackgroundPalettes: paletteBudget, MaxTiles: tileBudget),
                Intent: BakeIntent.Background,
                NativeHeight: 144,
                NativeWidth: 160,
                Style: style,
                Target: BakeTarget.Cgb,
                Views: [new BakeView(Camera: BuildScreenCamera(style: style), Name: name, Program: scene)]
            );
            var result = BakePipeline.Run(device: device, gpu: gpu, plan: plan);
            var bundle = PbakBundle.Parse(blob: result.Assets.ToBlob());

            if (bundle.Background is not { AttributeMap: not null } background) {
                Console.Error.WriteLine(value: $"card art bake | '{name}' produced no background/attributes; using the hand-authored fallback");

                return null;
            }

            if ((background.TileCount > tileBudget) || (background.PaletteCount > paletteBudget)) {
                Console.Error.WriteLine(value: $"card art bake | '{name}' over budget ({background.TileCount} tiles vs {tileBudget}, {background.PaletteCount} palettes vs {paletteBudget}); using the hand-authored fallback");

                return null;
            }

            Console.Error.WriteLine(value: $"card art bake | '{name}' baked | {background.TileCount} tiles (budget {tileBudget}) | {background.PaletteCount} palettes (budget {paletteBudget})");

            return background;
        } catch (Exception exception) {
            Console.Error.WriteLine(value: $"card art bake | '{name}' failed ({exception.Message}); using the hand-authored fallback");

            return null;
        }
    }

    /// <summary>Bakes the shared table cursor as a two-frame sprite set (point and grab) and returns the parsed,
    /// budget-checked bundle — the same one-sprite-set shape <see cref="CardTables.BuildFallbackCursorBundle"/>
    /// hand-authors, so a game's <see cref="GameManifest.DefineSpriteArt"/> call is identical either way.</summary>
    /// <param name="device">The live (or one-shot) GPU device.</param>
    /// <param name="gpu">The compute services.</param>
    /// <param name="tileBudget">The set's tile ceiling.</param>
    /// <param name="entryBudget">The per-frame OAM entry ceiling (the games reserve this many shadow slots).</param>
    /// <returns>The parsed bundle, or <see langword="null"/> (narrated) on any failure.</returns>
    public static PbakBundle? BakeCursorSprites(IGpuDeviceContext device, IGpuComputeServices gpu, int tileBudget, int entryBudget) {
        try {
            var style = (BakeStyles.Classic with { MaxObjectPalettes = 2 });
            var plan = new BakePlan(
                Budget: new BakeBudget(MaxObjectPalettes: 2, MaxTiles: tileBudget),
                Intent: BakeIntent.Sprite,
                NativeHeight: 32,
                NativeWidth: 32,
                Style: style,
                Target: BakeTarget.Cgb,
                Views: [
                    new BakeView(Camera: BuildCursorCamera(style: style), Name: "point", Program: BuildCursorScene(grab: false)),
                    new BakeView(Camera: BuildCursorCamera(style: style), Name: "grab", Program: BuildCursorScene(grab: true)),
                ]
            );
            var result = BakePipeline.Run(device: device, gpu: gpu, plan: plan);
            var bundle = PbakBundle.Parse(blob: result.Assets.ToBlob());

            if ((bundle.Sprites.Count != 1) || (bundle.Sprites[0].Frames.Count != 2)) {
                Console.Error.WriteLine(value: $"card art bake | cursor produced {bundle.Sprites.Count} sets; using the hand-authored fallback");

                return null;
            }

            var sprites = bundle.Sprites[0];

            if ((sprites.TileCount > tileBudget) || sprites.Frames.Any(predicate: frame => (frame.EntryCount > entryBudget))) {
                Console.Error.WriteLine(value: $"card art bake | cursor over budget ({sprites.TileCount} tiles vs {tileBudget}, worst frame {sprites.Frames.Max(selector: static frame => frame.EntryCount)} entries vs {entryBudget}); using the hand-authored fallback");

                return null;
            }

            Console.Error.WriteLine(value: $"card art bake | cursor baked | {sprites.TileCount} tiles | frames {string.Join(separator: "+", values: sprites.Frames.Select(selector: static frame => frame.EntryCount))} entries");

            return bundle;
        } catch (Exception exception) {
            Console.Error.WriteLine(value: $"card art bake | cursor failed ({exception.Message}); using the hand-authored fallback");

            return null;
        }
    }

    /// <summary>Authors the shared felt-table scene: a green baize field inside a darker rail, with a soft corner
    /// glow — deliberately flat so it dedupes hard and leaves the play area quiet under the cards.</summary>
    /// <returns>The scene.</returns>
    public static SdfProgram BuildFeltScene() {
        var builder = new SdfProgramBuilder();
        var felt = builder.AddMaterial(material: new SdfMaterial(Albedo: new Vector3(0.10f, 0.38f, 0.22f), Emissive: 1.2f));
        var feltDeep = builder.AddMaterial(material: new SdfMaterial(Albedo: new Vector3(0.07f, 0.28f, 0.16f), Emissive: 1.1f));
        var rail = builder.AddMaterial(material: new SdfMaterial(Albedo: new Vector3(0.24f, 0.14f, 0.08f), Emissive: 1.1f));

        // The baize field with a one-unit rail frame; a deep-felt band along the bottom anchors the table.
        _ = builder.ResetPoint().Translate(offset: new Vector3(0f, 0f, -2f)).Box(halfExtents: new Vector3(30f, 20f, 0.5f), round: 0f, material: rail);
        _ = builder.ResetPoint().Translate(offset: new Vector3(0f, 0f, -1f)).Box(halfExtents: new Vector3(9f, 8f, 0.4f), round: 0.2f, material: felt);
        _ = builder.ResetPoint().Translate(offset: new Vector3(0f, -8.2f, 0f)).Box(halfExtents: new Vector3(9f, 0.8f, 0.3f), round: 0f, material: feltDeep);

        return builder.Build();
    }

    /// <summary>Adds one suit symbol to a scene at the given centre and scale — spheres, boxes, and cones smooth-
    /// unioned into the four classic shapes. Shared authoring vocabulary for every card game's title emblem.</summary>
    /// <param name="builder">The scene builder.</param>
    /// <param name="suit">The suit (0 = spade, 1 = heart, 2 = diamond, 3 = club).</param>
    /// <param name="material">The symbol's material (ink or red).</param>
    /// <param name="centre">The symbol's centre in world units.</param>
    /// <param name="scale">The symbol's half-height in world units.</param>
    public static void AddSuitSymbol(SdfProgramBuilder builder, int suit, int material, Vector3 centre, float scale) {
        ArgumentNullException.ThrowIfNull(builder);

        var tilt = Quaternion.CreateFromAxisAngle(axis: Vector3.UnitZ, angle: (MathF.PI / 4f));

        switch (suit) {
            case 0: // Spade: an up-pointing diamond over two lobes, with a stem.
                _ = builder.ResetPoint().Translate(offset: (centre + new Vector3(0f, (0.25f * scale), 0f))).Rotate(rotation: tilt).Box(halfExtents: new Vector3((0.5f * scale), (0.5f * scale), 0.2f), round: (0.08f * scale), material: material);
                _ = builder.ResetPoint().Translate(offset: (centre + new Vector3((-0.34f * scale), (-0.18f * scale), 0f))).Sphere(radius: (0.36f * scale), material: material, blend: SdfBlendOp.SmoothUnion, smooth: (0.1f * scale));
                _ = builder.ResetPoint().Translate(offset: (centre + new Vector3((0.34f * scale), (-0.18f * scale), 0f))).Sphere(radius: (0.36f * scale), material: material, blend: SdfBlendOp.SmoothUnion, smooth: (0.1f * scale));
                _ = builder.ResetPoint().Translate(offset: (centre + new Vector3(0f, (-0.65f * scale), 0f))).Box(halfExtents: new Vector3((0.14f * scale), (0.3f * scale), 0.2f), round: 0f, material: material, blend: SdfBlendOp.SmoothUnion, smooth: (0.08f * scale));
                break;
            case 1: // Heart: two lobes over a down-pointing diamond.
                _ = builder.ResetPoint().Translate(offset: (centre + new Vector3((-0.34f * scale), (0.28f * scale), 0f))).Sphere(radius: (0.38f * scale), material: material);
                _ = builder.ResetPoint().Translate(offset: (centre + new Vector3((0.34f * scale), (0.28f * scale), 0f))).Sphere(radius: (0.38f * scale), material: material, blend: SdfBlendOp.SmoothUnion, smooth: (0.12f * scale));
                _ = builder.ResetPoint().Translate(offset: (centre + new Vector3(0f, (-0.18f * scale), 0f))).Rotate(rotation: tilt).Box(halfExtents: new Vector3((0.52f * scale), (0.52f * scale), 0.2f), round: (0.06f * scale), material: material, blend: SdfBlendOp.SmoothUnion, smooth: (0.12f * scale));
                break;
            case 2: // Diamond: one rotated, vertically stretched box.
                _ = builder.ResetPoint().Translate(offset: centre).Rotate(rotation: tilt).Box(halfExtents: new Vector3((0.55f * scale), (0.55f * scale), 0.2f), round: (0.06f * scale), material: material);
                break;
            default: // Club: three lobes and a stem.
                _ = builder.ResetPoint().Translate(offset: (centre + new Vector3(0f, (0.4f * scale), 0f))).Sphere(radius: (0.34f * scale), material: material);
                _ = builder.ResetPoint().Translate(offset: (centre + new Vector3((-0.36f * scale), (-0.08f * scale), 0f))).Sphere(radius: (0.34f * scale), material: material, blend: SdfBlendOp.SmoothUnion, smooth: (0.1f * scale));
                _ = builder.ResetPoint().Translate(offset: (centre + new Vector3((0.36f * scale), (-0.08f * scale), 0f))).Sphere(radius: (0.34f * scale), material: material, blend: SdfBlendOp.SmoothUnion, smooth: (0.1f * scale));
                _ = builder.ResetPoint().Translate(offset: (centre + new Vector3(0f, (-0.6f * scale), 0f))).Box(halfExtents: new Vector3((0.13f * scale), (0.32f * scale), 0.2f), round: 0f, material: material, blend: SdfBlendOp.SmoothUnion, smooth: (0.08f * scale));
                break;
        }
    }

    /// <summary>Adds a tilted playing card (a rounded white slab with an ink rim) to a scene, returning the
    /// materials so the caller can dress its face with <see cref="AddSuitSymbol"/>.</summary>
    /// <param name="builder">The scene builder.</param>
    /// <param name="faceMaterial">The card-face material (author once per scene, pass to every card).</param>
    /// <param name="centre">The card's centre in world units.</param>
    /// <param name="tiltRadians">The card's roll around Z.</param>
    /// <param name="halfExtents">The card's half-size (a classic card reads well near 1.6 × 2.2).</param>
    public static void AddCardShape(SdfProgramBuilder builder, int faceMaterial, Vector3 centre, float tiltRadians, Vector2 halfExtents) {
        ArgumentNullException.ThrowIfNull(builder);

        var tilt = Quaternion.CreateFromAxisAngle(axis: Vector3.UnitZ, angle: tiltRadians);

        _ = builder.ResetPoint().Translate(offset: centre).Rotate(rotation: tilt).Box(halfExtents: new Vector3(halfExtents.X, halfExtents.Y, 0.15f), round: 0.12f, material: faceMaterial);
    }

    // The head-on camera framing exactly 20×18 world units onto 160×144 pixels (one unit = one tile).
    private static CameraSnapshot BuildScreenCamera(BakeStyle style) =>
        CameraSnapshot.LookAt(
            fieldOfViewRadians: (45f * (MathF.PI / 180f)),
            position: new Vector3(0f, 0f, ((144f / 16f) / TanHalfFov)),
            target: Vector3.Zero,
            viewportHeight: (uint)(144 * style.SupersampleFactor),
            viewportWidth: (uint)(160 * style.SupersampleFactor)
        );

    // The cursor camera: 4×4 world units onto the 32×32 native cell (8 pixels per unit).
    private static CameraSnapshot BuildCursorCamera(BakeStyle style) =>
        CameraSnapshot.LookAt(
            fieldOfViewRadians: (45f * (MathF.PI / 180f)),
            position: new Vector3(0f, 0f, (2f / TanHalfFov)),
            target: Vector3.Zero,
            viewportHeight: (uint)(32 * style.SupersampleFactor),
            viewportWidth: (uint)(32 * style.SupersampleFactor)
        );

    // The cursor's two poses: a gold pointer, and the same pointer pinching a small white card.
    private static SdfProgram BuildCursorScene(bool grab) {
        var builder = new SdfProgramBuilder();
        var gold = builder.AddMaterial(material: new SdfMaterial(Albedo: new Vector3(0.95f, 0.78f, 0.30f), Emissive: 1.3f));
        var white = builder.AddMaterial(material: new SdfMaterial(Albedo: new Vector3(0.92f, 0.93f, 0.95f), Emissive: 1.2f));
        var tilt = Quaternion.CreateFromAxisAngle(axis: Vector3.UnitZ, angle: (-30f * (MathF.PI / 180f)));

        // The pointer: a round cone (tip up-left) with a short tail.
        _ = builder.ResetPoint().Translate(offset: new Vector3(-0.2f, 0.2f, 0f)).Rotate(rotation: tilt).RoundCone(lowerRadius: 0.42f, upperRadius: 0.05f, height: 1.5f, material: gold);

        if (grab) {
            _ = builder.ResetPoint().Translate(offset: new Vector3(0.55f, -0.85f, 0f)).Rotate(rotation: tilt).Box(halfExtents: new Vector3(0.5f, 0.65f, 0.12f), round: 0.1f, material: white);
        }

        return builder.Build();
    }
}
