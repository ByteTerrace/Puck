using System.Numerics;
using Puck.SignedDistance;
using Puck.World.Authoring;
using Xunit;

namespace Puck.World.Schema.Tests;

/// <summary>
/// THE LAW: a creation row differs in shading alone (<see cref="WorldPrototype.DiffersInShadingAlone"/>, the test a
/// live edit's presentation install rests on) only when nothing but its palette's colours and materials moved: a rebuilt
/// but equal row qualifies, and a shape, a palette length or a slot's glass does not.
/// </summary>
public sealed class WorldPrototypeShadingLawTests {
    private static PaletteEntryDocument Slot(float? roughness = null, PaletteGlassDocument? glass = null) => new(
        Color: "#D8D2C4",
        Emissive: null,
        Specular: null,
        Roughness: roughness,
        Glass: glass
    );
    private static WorldPrototype Row(IReadOnlyList<PaletteEntryDocument> palette, float scale = 0.5f) => new(
        Document: new CreationDocument(
            Schema: CreationDocument.CurrentSchema,
            Name: "row",
            Palette: palette,
            Shapes: [new ShapeDocument(
                Id: 0,
                Name: "torso",
                Type: SdfSolidPrimitive.Box,
                Position: Vector3.Zero,
                Rotation: Quaternion.Identity,
                Scale: new Vector3(x: scale, y: scale, z: scale),
                Material: 0,
                Blend: SdfBlendOp.Union,
                Smooth: 0f,
                Group: 0
            )],
            Frames: null
        ),
        Id: "row"
    );

    [Fact]
    public void OnlyAPaletteThatKeepsItsLengthAndGlassDiffersInShadingAlone() {
        var installed = Row(palette: [Slot(), Slot()]);

        Assert.True(condition: installed.DiffersInShadingAlone(other: Row(palette: [Slot(), Slot()])));
        Assert.True(condition: installed.DiffersInShadingAlone(other: Row(palette: [Slot(), Slot(roughness: 0.4f)])));
        Assert.False(condition: installed.DiffersInShadingAlone(other: Row(palette: [Slot(), Slot()], scale: 0.6f)));
        Assert.False(condition: installed.DiffersInShadingAlone(other: Row(palette: [Slot()])));
        Assert.False(condition: installed.DiffersInShadingAlone(other: Row(palette: [Slot(), Slot(glass: new PaletteGlassDocument(Depth: 0.1f))])));
    }
}
