using System.Numerics;

using Puck.SignedDistance;
using Puck.World.Authoring;
using Puck.World.Protocol;

using Xunit;

namespace Puck.World.Schema.Tests;

/// <summary>A placement effect's cost derives from what its install actually rebuilds: the document write every
/// mutation pays (<see cref="WorldPlacementEffectCost.DocumentCost"/>, 512), one admission cost per live body an
/// inhabit facet could ever add (<see cref="WorldPlacementEffectCost.PopulationEntryCost"/>, 512, times
/// <see cref="WorldPlacementInhabit.DeclaredMax"/>), and one shape cost per shape the row's prototype folds into the
/// solid contact field when it carries <see cref="WorldSolid"/> (<see cref="WorldPlacementEffectCost.SolidShapeCost"/>,
/// 256) — never a flat number independent of the row. Expected sums are written as literals, not re-derived from the
/// same constants under test, so a change to a constant's value moves only the actual side.</summary>
public sealed class WorldPlacementEffectCostLawTests {
    private static WorldPrototype BoxPrototype(string id, int shapeCount) {
        var shapes = new ShapeDocument[shapeCount];

        for (var index = 0; index < shapeCount; index++) {
            shapes[index] = new ShapeDocument(
                Id: index,
                Name: null,
                Type: SdfSolidPrimitive.Box,
                Position: Vector3.Zero,
                Rotation: Quaternion.Identity,
                Scale: new Vector3(x: 0.1f, y: 0.1f, z: 0.1f),
                Material: 0,
                Blend: SdfBlendOp.Union,
                Smooth: 0f,
                Group: 0
            );
        }

        var document = new CreationDocument(Schema: CreationDocument.CurrentSchema, Name: id, Palette: null, Shapes: shapes, Frames: null);
        var canonical = CreationCanonicalizer.Canonicalize(document: document, source: id);

        return new WorldPrototype(Id: id, Document: canonical.Document, HashRaw: canonical.Hash);
    }

    private static WorldDefinition Document(WorldPrototype prototype, WorldPlacement[]? placements = null, int populationCapacity = 16) => new(
        Simulation: new WorldSimulationDefaults(RateHz: 240),
        PopulationRaw: new WorldBodiesDefaults(CapacityRaw: populationCapacity),
        CreationsRaw: [prototype],
        PlacementsRaw: ((placements is null) ? null : new WorldPlacementsSection(Policy: null, Rows: placements))
    );

    [Fact]
    public void ADecorationOrAttachOnlyRowCostsOnlyTheDocumentWrite() {
        var definition = Document(prototype: BoxPrototype(id: "prop", shapeCount: 3));
        var context = WorldRuleCompiler.Context(definition: definition);
        var placement = new WorldPlacement(Id: "row", PrototypeId: "prop", Position: Vector3.Zero, YawDegrees: 0f, Scale: 1f);
        var effect = new UpsertPlacementEffect(placement: placement, describe: "upsertPlacement row");

        Assert.Equal(512L, effect.Cost(context: context));
    }

    [Fact]
    public void AnInhabitedRowsCostScalesWithItsDeclaredMaxLiveBodies() {
        var definition = Document(prototype: BoxPrototype(id: "critter", shapeCount: 1), populationCapacity: 32);
        var context = WorldRuleCompiler.Context(definition: definition);
        var placement = new WorldPlacement(
            Id: "row",
            PrototypeId: "critter",
            Position: Vector3.Zero,
            YawDegrees: 0f,
            Scale: 1f,
            Inhabit: new WorldPlacementInhabit(Kit: null, Look: null, Source: IntentSource.Idle, Count: 5)
        );
        var effect = new UpsertPlacementEffect(placement: placement, describe: "upsertPlacement row");

        Assert.Equal(3_072L, effect.Cost(context: context)); // 512 + 5 * 512

        // The bound is the tighter of the literal count and the peer capacity, never the population capacity alone.
        var overLiteral = new WorldPlacementInhabit(Kit: null, Look: null, Source: IntentSource.Idle, Count: 9_000);
        var overEffect = new UpsertPlacementEffect(placement: (placement with { Inhabit = overLiteral }), describe: "upsertPlacement row");

        Assert.Equal(16_896L, overEffect.Cost(context: context)); // 512 + 32 * 512
    }

    [Fact]
    public void ASolidRowsCostScalesWithItsPrototypesShapeCount() {
        var definition = Document(prototype: BoxPrototype(id: "statue", shapeCount: 4));
        var context = WorldRuleCompiler.Context(definition: definition);
        var placement = new WorldPlacement(Id: "row", PrototypeId: "statue", Position: Vector3.Zero, YawDegrees: 0f, Scale: 1f, Solid: new WorldSolid(Margin: 0f));
        var effect = new UpsertPlacementEffect(placement: placement, describe: "upsertPlacement row");

        Assert.Equal(1_536L, effect.Cost(context: context)); // 512 + 4 * 256
    }

    [Fact]
    public void RemovingADeclaredRowChargesWhatItsCurrentShapeWouldRebuild_AnUndeclaredIdChargesTheFloor() {
        var prototype = BoxPrototype(id: "statue", shapeCount: 2);
        var placement = new WorldPlacement(Id: "row", PrototypeId: "statue", Position: Vector3.Zero, YawDegrees: 0f, Scale: 1f, Solid: new WorldSolid(Margin: 0f));
        var definition = Document(prototype: prototype, placements: [placement]);
        var context = WorldRuleCompiler.Context(definition: definition);

        Assert.Equal(1_024L, new RemovePlacementEffect(id: "row", describe: "removePlacement row").Cost(context: context)); // 512 + 2 * 256
        Assert.Equal(512L, new RemovePlacementEffect(id: "ghost", describe: "removePlacement ghost").Cost(context: context));
    }
}
