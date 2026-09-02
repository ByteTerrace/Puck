using System.Numerics;
using System.Text;
using Puck.Assets.Documents;
using Puck.World.Authoring;
using Puck.SignedDistance;
using Xunit;

namespace Puck.ShaderVm.Tests;

// null.world.json's creations emitted through the domain-specific SDF ISA, using the same calls the live renderer's
// WorldStampPool.EmitShape makes, so the instruction count is the one Puck.SdfVm would actually interpret.
public sealed class SdfIsaDensity {
    [Fact]
    public void Measures() {
        var directory = Environment.GetEnvironmentVariable(variable: "PUCK_SKY_PREVIEW_DIR");

        Assert.SkipWhen(condition: string.IsNullOrEmpty(value: directory), reason: "Opt-in harness: set PUCK_SKY_PREVIEW_DIR to the directory the report is written to.");

        var builder = new SdfProgramBuilder();
        var slot = 0;

        // The flattened palette NullWorldScene uses, so both sides name the same materials.
        foreach (var albedo in NullWorldScene.Palette) {
            _ = builder.AddMaterial(material: new SdfMaterial(Albedo: albedo));
        }

        foreach (var shape in Shapes()) {
            EmitShape(builder: builder, shape: shape, slot: slot++);
        }

        var program = builder.Build();
        var report = new StringBuilder();

        _ = report.AppendLine(value: "null.world.json creations through the domain-specific SDF ISA");
        _ = report.AppendLine(value: $"  shapes                          {Shapes().Count}");
        _ = report.AppendLine(value: $"  instructions                    {program.Instructions.Count}");
        _ = report.AppendLine(value: $"  packed words                    {program.Words.Length}  ({(program.Words.Length * 4)} bytes)");
        _ = report.AppendLine(value: "");
        _ = report.AppendLine(value: "The document places these creations 10 times. A placement rides the instance table and its own");
        _ = report.AppendLine(value: "dynamic-transform slot, so the instruction stream above is emitted ONCE regardless of placement count.");

        File.WriteAllText(
            contents: report.ToString(),
            path: Path.Combine(path1: directory!, path2: "sdf-density.txt")
        );
        Assert.True(condition: (program.Instructions.Count > 0));
    }

    // ResetPoint, the transform, the ordered domain ops, the local pose, then the primitive.
    private static void EmitShape(SdfProgramBuilder builder, AuthoredShape shape, int slot) {
        var chain = builder.ResetPoint();

        if (shape.Domain.Count > 0) {
            chain = ShapeDomainOps
                .Apply(chain: chain.TransformDynamic(slot: slot), domain: shape.Domain)
                .Translate(offset: shape.Position)
                .Rotate(rotation: Quaternion.Identity);
        } else {
            chain = chain.TransformDynamic(slot: slot);
        }

        _ = SdfSolidGeometry.AppendScaledPrimitive(
            blend: shape.Blend,
            chain: chain,
            material: shape.Material,
            scale: shape.Scale,
            smooth: shape.Smooth,
            type: shape.Type
        );
    }
    private static IReadOnlyList<AuthoredShape> Shapes() {
        var mirrorX = new ShapeDomainOp.Symmetry(Normal: new DocumentVector3(x: 1f, y: 0f, z: 0f));
        var mirrorZ = new ShapeDomainOp.Symmetry(Normal: new DocumentVector3(x: 0f, y: 0f, z: 1f));
        var ones = Vector3.One;
        var shapes = new List<AuthoredShape>();

        void Ground(float cell, int material, bool wallpaper, bool cuts) {
            shapes.Add(item: new AuthoredShape(
                Blend: SdfBlendOp.Union,
                Domain: (wallpaper
                    ? [new ShapeDomainOp.Wallpaper(Cell: new DocumentVector2(value: new Vector2(x: cell, y: cell)), Group: SdfWallpaperGroup.P1, MaterialStride: 1)]
                    : []),
                Material: material,
                Position: new Vector3(x: 0f, y: 0.001f, z: 0f),
                Scale: ones,
                Smooth: 0f,
                Type: SdfSolidPrimitive.Plane
            ));

            for (var cut = 0; (cut < (cuts ? 2 : 0)); cut++) {
                shapes.Add(item: new AuthoredShape(
                    Blend: SdfBlendOp.Subtraction,
                    Domain: [],
                    Material: material,
                    Position: Vector3.Zero,
                    Scale: ones,
                    Smooth: 0f,
                    Type: SdfSolidPrimitive.Plane
                ));
            }
        }

        Ground(cell: 1f, cuts: true, material: NullWorldScene.Paper, wallpaper: true);
        Ground(cell: 1f, cuts: true, material: NullWorldScene.Sage, wallpaper: false);
        Ground(cell: 2f, cuts: true, material: NullWorldScene.BlueDeep, wallpaper: true);
        Ground(cell: 1f, cuts: false, material: NullWorldScene.Stone, wallpaper: false);

        foreach (var (type, position, scale, material, smooth) in (((SdfSolidPrimitive, Vector3, Vector3, int, float)[])[
            (SdfSolidPrimitive.Cylinder, new Vector3(x: 6f, y: 0.3f, z: 6f), new Vector3(x: 0.8f, y: 0.3f, z: 0.8f), NullWorldScene.PillarTrim, 0f),
            (SdfSolidPrimitive.Cylinder, new Vector3(x: 6f, y: 3.3f, z: 6f), new Vector3(x: 0.45f, y: 2.9f, z: 0.45f), NullWorldScene.PillarStone, 0.25f),
            (SdfSolidPrimitive.Cylinder, new Vector3(x: 6f, y: 6.35f, z: 6f), new Vector3(x: 0.8f, y: 0.3f, z: 0.8f), NullWorldScene.PillarTrim, 0.25f),
            (SdfSolidPrimitive.Sphere, new Vector3(x: 6f, y: 7f, z: 6f), new Vector3(value: 0.45f), NullWorldScene.PillarLamp, 0.1f),
        ])) {
            shapes.Add(item: new AuthoredShape(
                Blend: SdfBlendOp.Union,
                Domain: [mirrorX, mirrorZ],
                Material: material,
                Position: position,
                Scale: scale,
                Smooth: smooth,
                Type: type
            ));
        }

        shapes.Add(item: new AuthoredShape(
            Blend: SdfBlendOp.Union,
            Domain: [],
            Material: NullWorldScene.PlanetoidCrust,
            Position: Vector3.Zero,
            Scale: new Vector3(value: 1.5f),
            Smooth: 0f,
            Type: SdfSolidPrimitive.Sphere
        ));
        shapes.Add(item: new AuthoredShape(
            Blend: SdfBlendOp.Union,
            Domain: [new ShapeDomainOp.Polar(Count: 6)],
            Material: NullWorldScene.PlanetoidRock,
            Position: new Vector3(x: 1.32f, y: 0f, z: 0f),
            Scale: new Vector3(value: 0.3f),
            Smooth: 0.3f,
            Type: SdfSolidPrimitive.Sphere
        ));
        shapes.Add(item: new AuthoredShape(
            Blend: SdfBlendOp.Union,
            Domain: [],
            Material: NullWorldScene.PipBody,
            Position: new Vector3(x: 0f, y: 0.5f, z: 0f),
            Scale: new Vector3(x: 0.35f, y: 1.75f, z: 0.35f),
            Smooth: 0f,
            Type: SdfSolidPrimitive.Capsule
        ));
        shapes.Add(item: new AuthoredShape(
            Blend: SdfBlendOp.Union,
            Domain: [mirrorX],
            Material: NullWorldScene.PipEye,
            Position: new Vector3(x: 0.2f, y: 1.1f, z: 0.42f),
            Scale: new Vector3(value: 0.14f),
            Smooth: 0f,
            Type: SdfSolidPrimitive.Sphere
        ));
        shapes.Add(item: new AuthoredShape(
            Blend: SdfBlendOp.Union,
            Domain: [mirrorX],
            Material: NullWorldScene.PipPupil,
            Position: new Vector3(x: 0.2f, y: 1.1f, z: 0.54f),
            Scale: new Vector3(value: 0.055f),
            Smooth: 0f,
            Type: SdfSolidPrimitive.Sphere
        ));

        return shapes;
    }

    private sealed record AuthoredShape(SdfSolidPrimitive Type, Vector3 Position, Vector3 Scale, int Material, float Smooth, SdfBlendOp Blend, IReadOnlyList<ShapeDomainOp> Domain);
}
