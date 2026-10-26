using System.Numerics;
using Puck.Assets.Documents;
using Puck.SignedDistance;
using Xunit;

namespace Puck.World.Client.Tests;

public sealed class StaticEmissionLawTests {
    [InlineData(false)]
    [InlineData(true)]
    [Theory]
    public void RetainedPlacementsMatchFreshEmissionAcrossMovedRowsAndPaletteOffsets(bool scoped) {
        var creation = CreationFixtures.Prototype(document: CreationFixtures.Document(name: "retained", shapes: [CreationFixtures.UnitSphereShape]), id: "retained");
        var row = new WorldPlacement(Id: "kept", Position: new DocumentVector3(value: Vector3.One), PrototypeId: creation.Id, Scale: 1, YawDegrees: 0);
        var palettes = new WorldStaticPalettes();
        var definition = Fixtures.BuildDocument() with { CreationsRaw = [creation], PlacementRowsRaw = [row] };

        for (var prefix = 0; (prefix < 3); prefix++) {
            foreach (var moved in new[] { row, row with { Position = new DocumentVector3(value: Vector3.UnitX) }, row }) {
                var current = definition with { PlacementRowsRaw = [moved] };

                SdfProgram Build(WorldStaticPalettes? cache) {
                    var builder = new SdfProgramBuilder();
                    var scope = (scoped ? builder.BeginMaterialScope() : null);

                    for (var i = 0; (i < prefix); i++) { builder.AddMaterial(material: new SdfMaterial(Vector3.One)); }
                    WorldPlacementStamper.EmitStatic(builder, current, current.Creations, current.Placements, palettes: cache);
                    scope?.Dispose();
                    return builder.Build();
                }
                Assert.Equal(Build(cache: null).Words.ToArray(), Build(cache: palettes).Words.ToArray());
                Assert.Equal(Build(cache: null).Words.ToArray(), Build(cache: palettes).Words.ToArray());
            }
        }
    }
}
