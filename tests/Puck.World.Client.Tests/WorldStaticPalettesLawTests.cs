using System.Numerics;
using Puck.Assets.Documents;
using Puck.SignedDistance;
using Xunit;

namespace Puck.World.Client.Tests;

/// <summary>
/// THE LAW: a static emission registers each untinted creation's palette once however many placements show it, and an
/// owner that keeps its <see cref="WorldStaticPalettes"/> and its colors across rebuilds emits a document with only
/// literal colors without allocating once warm: no palette dictionary, no id array and no layer-color resolver per
/// emission.
/// </summary>
public sealed class WorldStaticPalettesLawTests {
    private const string PrototypeId = "orb";

    private static WorldPlacement Placement(string id, float x) => new(
        Id: id,
        Position: new DocumentVector3(value: new Vector3(
            x: x,
            y: 0f,
            z: 0f
        )),
        PrototypeId: PrototypeId,
        Scale: 1f,
        YawDegrees: 0f
    );
    private static WorldDefinition Document() => (Fixtures.BuildDocument() with {
        CreationsRaw = [CreationFixtures.Prototype(document: CreationFixtures.Document(
            name: PrototypeId,
            palette: CreationFixtures.GreyAndBlue,
            shapes: [CreationFixtures.UnitSphereShape]
        ))],
        PlacementRowsRaw = [
            Placement(id: "orb-a", x: -4f),
            Placement(id: "orb-b", x: 0f),
            Placement(id: "orb-c", x: 4f),
        ],
    });
    private static SdfProgramBuilder Builder() => new(
        instanceCapacity: 64,
        instructionCapacity: 4_096,
        materialCapacity: 64
    );
    private static void Emit(SdfProgramBuilder builder, WorldDefinition definition, WorldBakedColors colors, WorldStaticPalettes palettes) {
        colors.Begin();
        WorldPlacementStamper.EmitStatic(
            builder: builder,
            colors: colors,
            creations: definition.Creations,
            definition: definition,
            palettes: palettes,
            placements: definition.Placements
        );
    }

    [Fact]
    public void AnUntintedCreationsPaletteRegistersOncePerEmission() {
        var definition = Document();
        var colors = WorldBakedColors.Of(definition: definition);
        var palettes = new WorldStaticPalettes();

        for (var emission = 0; (emission < 2); emission++) {
            var builder = Builder();

            Emit(
                builder: builder,
                colors: colors,
                definition: definition,
                palettes: palettes
            );

            // The next material's id is the count the emission registered: the orb's two palette entries, once.
            Assert.Equal(
                actual: builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One)),
                expected: CreationFixtures.GreyAndBlue.Count
            );
        }
    }
    [Fact]
    public void AWarmEmissionOfLiteralColorsAllocatesNothing() {
        var definition = Document();
        var colors = WorldBakedColors.Of(definition: definition);
        var palettes = new WorldStaticPalettes();

        for (var warm = 0; (warm < 4); warm++) {
            Emit(
                builder: Builder(),
                colors: colors,
                definition: definition,
                palettes: palettes
            );
        }

        var builder = Builder();
        var before = GC.GetAllocatedBytesForCurrentThread();

        Emit(
            builder: builder,
            colors: colors,
            definition: definition,
            palettes: palettes
        );

        Assert.Equal(
            actual: (GC.GetAllocatedBytesForCurrentThread() - before),
            expected: 0L
        );
        Assert.False(condition: colors.IsMirrored);
    }
}
