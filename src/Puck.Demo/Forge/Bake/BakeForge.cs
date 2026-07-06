using System.Numerics;
using Puck.Abstractions.Gpu;
using Puck.Capture;
using Puck.Demo.Creator;
using Puck.SdfVm;

namespace Puck.Demo.Forge.Bake;

/// <summary>
/// The <c>--forge-bake</c> tool mode — the pipeline's headless proof. It bakes two subjects (the default avatar as a
/// SPRITE, an authored scene as a BACKGROUND) in both styles × both targets (8 results), writing each preview PNG to
/// the output directory plus one diagnostics line to stderr. <c>--forge-bake-stress</c> instead bakes a
/// rainbow-striped synthetic scene whose per-tile palettes blow past the 8-palette budget, proving the greedy merge
/// path and the report-only warning. Fully deterministic: two runs write byte-identical PNGs.
/// </summary>
internal static class BakeForge {
    /// <summary>Runs the bake tool inside the shared one-shot GPU host; returns 0 on success.</summary>
    /// <param name="outputDirectory">Where the preview PNGs land.</param>
    /// <param name="stress">Whether to bake the palette-pressure stress scene instead of the standard 8.</param>
    /// <param name="args">The host args (backend selection etc.).</param>
    /// <returns>The process exit code.</returns>
    public static Task<int> RunAsync(string outputDirectory, bool stress, string[] args) {
        ArgumentException.ThrowIfNullOrEmpty(outputDirectory);

        return ForgeHost.RunAsync(args: args, work: (device, gpu) => Run(device: device, gpu: gpu, outputDirectory: outputDirectory, stress: stress));
    }

    private static int Run(IGpuDeviceContext device, IGpuComputeServices gpu, string outputDirectory, bool stress) {
        _ = Directory.CreateDirectory(path: outputDirectory);

        var subjects = (stress
            ? new List<(string Name, CreationDocument Document)> { ("stress", StressSubject()) }
            : [("avatar", AvatarSubject()), ("scene", SceneSubject())]);

        foreach (var (name, document) in subjects) {
            foreach (var style in new[] { BakeStyles.Classic, BakeStyles.Bold }) {
                foreach (var target in new[] { BakeTarget.Dmg, BakeTarget.Cgb }) {
                    // The stress bake IS the diagnostics showcase, so it writes overlay mode 1 (the palette strip +
                    // the warning ticks) — the standard 8 stay bare preview pixels.
                    BakeOne(device: device, document: document, gpu: gpu, name: name, outputDirectory: outputDirectory, overlayMode: (stress ? 1 : 0), style: style, target: target);
                }
            }
        }

        return 0;
    }

    private static void BakeOne(IGpuDeviceContext device, CreationDocument document, IGpuComputeServices gpu, string name, string outputDirectory, int overlayMode, BakeStyle style, BakeTarget target) {
        var plan = CreationBakePlanner.Plan(document: document, style: style, target: target);
        var result = BakePipeline.Run(device: device, gpu: gpu, overlayMode: overlayMode, plan: plan);
        var targetName = ((target == BakeTarget.Dmg) ? "dmg" : "cgb");
        var path = Path.Combine(path1: outputDirectory, path2: $"{name}-{style.Name}-{targetName}.png");

        PngEncoder.Write(height: result.PreviewHeight, path: path, rgba: result.PreviewRgba, width: result.PreviewWidth);
        Console.Error.WriteLine(value: result.Diagnostics.Summarize(target: target));

        foreach (var warning in result.Diagnostics.Warnings) {
            Console.Error.WriteLine(value: $"bake | warn: {warning}");
        }
    }

    // Subject (a): the built-in default avatar, lifted into a creation document as SPRITE-intent art (4 facings,
    // no timeline frames — the minimal honest metasprite).
    private static CreationDocument AvatarSubject() {
        var avatar = AvatarDefinition.Default();
        var shapes = new List<ShapeDocument>(capacity: avatar.Shapes.Count);

        for (var index = 0; (index < avatar.Shapes.Count); index++) {
            var shape = avatar.Shapes[index];

            shapes.Add(item: Shape(id: index, material: index, position: shape.Position, rotation: shape.Rotation, scale: shape.Scale, type: shape.Type));
        }

        return Document(intent: CreatorIntent.Sprite, name: "avatar", palette: null, shapes: shapes);
    }

    // Subject (b): a simple authored background — a hill, an emissive sun, a tower, an arch, and a two-shape
    // smooth-union tree — colourful enough that the CGB fit has real work and the DMG ramp reads in shades.
    private static CreationDocument SceneSubject() {
        var palette = new List<PaletteEntryDocument> {
            new(Albedo: new Vector3(0.30f, 0.62f, 0.32f), Emissive: null, Shininess: null, Specular: null),
            new(Albedo: new Vector3(0.98f, 0.85f, 0.25f), Emissive: 1.5f, Shininess: null, Specular: null),
            new(Albedo: new Vector3(0.62f, 0.32f, 0.22f), Emissive: null, Shininess: null, Specular: null),
            new(Albedo: new Vector3(0.55f, 0.35f, 0.75f), Emissive: null, Shininess: null, Specular: null),
            new(Albedo: new Vector3(0.45f, 0.30f, 0.18f), Emissive: null, Shininess: null, Specular: null),
            new(Albedo: new Vector3(0.25f, 0.55f, 0.28f), Emissive: null, Shininess: null, Specular: null),
        };
        var lean = Quaternion.CreateFromAxisAngle(axis: Vector3.UnitY, angle: (18f * (MathF.PI / 180f)));
        var tip = Quaternion.CreateFromAxisAngle(axis: Vector3.UnitX, angle: (90f * (MathF.PI / 180f)));
        var shapes = new List<ShapeDocument> {
            Shape(id: 0, material: 0, position: new Vector3(0f, 0.2f, 0f), rotation: Quaternion.Identity, scale: new Vector3(3f, 1.1f, 3f), type: AvatarPrimitive.Ellipsoid),
            Shape(id: 1, material: 1, position: new Vector3(1.8f, 2.4f, -1.0f), rotation: Quaternion.Identity, scale: new Vector3(0.8f), type: AvatarPrimitive.Sphere),
            Shape(id: 2, material: 2, position: new Vector3(-1.4f, 0.9f, 0.2f), rotation: lean, scale: new Vector3(0.8f, 2.2f, 0.8f), type: AvatarPrimitive.Box),
            Shape(id: 3, material: 3, position: new Vector3(0.6f, 1.0f, 0.6f), rotation: tip, scale: new Vector3(1.4f), type: AvatarPrimitive.Torus),
            Shape(id: 4, material: 4, position: new Vector3(1.2f, 0.6f, 0.8f), rotation: Quaternion.Identity, scale: Vector3.One, type: AvatarPrimitive.Capsule),
            (Shape(id: 5, material: 5, position: new Vector3(1.2f, 1.5f, 0.8f), rotation: Quaternion.Identity, scale: new Vector3(1.1f), type: AvatarPrimitive.Sphere)
                with { Blend = SdfBlendOp.SmoothUnion, Group = 1, Smooth = 0.25f }),
        };

        return Document(intent: CreatorIntent.Object, name: "scene", palette: palette, shapes: shapes);
    }

    // The stress subject: twelve tall rainbow pillars, each its own saturated hue and a slightly different roll, so
    // every pillar's tiles derive a DIFFERENT per-tile palette — >8 clusters guarantees the greedy merge runs and
    // the palette-pressure warning reports it.
    private static CreationDocument StressSubject() {
        const int pillarCount = 12;
        var palette = new List<PaletteEntryDocument>(capacity: pillarCount);
        var shapes = new List<ShapeDocument>(capacity: pillarCount);

        for (var index = 0; (index < pillarCount); index++) {
            palette.Add(item: new PaletteEntryDocument(Albedo: RainbowHue(index: index, count: pillarCount), Emissive: null, Shininess: null, Specular: null));
            shapes.Add(item: Shape(
                id: index,
                material: index,
                position: new Vector3((-3.3f + (index * 0.6f)), 1.2f, 0f),
                rotation: Quaternion.CreateFromAxisAngle(axis: Vector3.UnitZ, angle: (index * (4f * (MathF.PI / 180f)))),
                scale: new Vector3(0.4f, 3.0f, 0.6f),
                type: AvatarPrimitive.Box
            ));
        }

        return Document(intent: CreatorIntent.Object, name: "stress", palette: palette, shapes: shapes);
    }

    private static Vector3 RainbowHue(int index, int count) {
        var h6 = ((index * 6f) / count);
        var x = (1f - MathF.Abs(((h6 % 2f) - 1f)));
        var (r, g, b) = ((int)h6 switch {
            0 => (1f, x, 0f),
            1 => (x, 1f, 0f),
            2 => (0f, 1f, x),
            3 => (0f, x, 1f),
            4 => (x, 0f, 1f),
            _ => (1f, 0f, x),
        });

        return new Vector3((0.15f + (0.85f * r)), (0.15f + (0.85f * g)), (0.15f + (0.85f * b)));
    }

    private static ShapeDocument Shape(int id, int material, Vector3 position, Quaternion rotation, Vector3 scale, AvatarPrimitive type) =>
        new(
            Blend: SdfBlendOp.Union,
            Group: 0,
            Id: id,
            Material: material,
            Name: null,
            Position: position,
            Rotation: rotation,
            Scale: scale,
            Smooth: 0f,
            Type: type
        );

    private static CreationDocument Document(CreatorIntent intent, string name, IReadOnlyList<PaletteEntryDocument>? palette, IReadOnlyList<ShapeDocument> shapes) =>
        new(
            BakeStyle: null,
            Frames: null,
            Intent: intent,
            Name: name,
            Palette: palette,
            Schema: CreationDocument.CurrentSchema,
            Shapes: shapes
        );
}
