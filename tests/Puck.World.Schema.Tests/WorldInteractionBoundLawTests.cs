using Xunit;

using RuleWorkBudget = Puck.State.Rules.RuleWorkBudget;

namespace Puck.World.Schema.Tests;

/// <summary>CONTRACT UNDER TEST: a distance interaction pays for gathering both carrier sets and for testing every
/// left against every right whatever its neighbour limit; the limit bounds only the evaluations that follow, and
/// adds the selection it makes. Tag rows have one carrier per body; pools can bind several instances to one body.</summary>
public sealed class WorldInteractionBoundLawTests {
    private const int Population = 48;
    private const int Tagged = 12;

    private static WorldStateRow Keyed(string name, int capacity) => new(
        CellName.Parse(candidate: name),
        CellKind.Int,
        Capacity: capacity,
        Cells: [new StateCell(
                CellName.Parse(candidate: "0"),
                CellValue.Int(value: 0L)
            )]
    );
    private static WorldDefinition Document(int? neighbours) => new(
        Simulation: new WorldSimulationDefaults(RateHz: 240),
        PopulationRaw: new WorldBodiesDefaults(CapacityRaw: Population),
        Properties: new WorldPropertyRegistrySection(Names: ["hot", "wet"]),
        StateRaw: new WorldStateSection(World: [Keyed(
                capacity: Tagged,
                name: "hot"
            ), Keyed(
                capacity: 4096,
                name: "wet"
            ), new WorldStateRow(
                CellName.Parse(candidate: "count"),
                CellKind.Int,
                Cells: [new StateCell(
                        WorldStateRow.SlotKey,
                        CellValue.Int(value: 0L)
                    )]
            )]),
        Interactions: new WorldInteractionsSection(Interactions: [new WorldInteraction(
                Name: CellName.Parse(candidate: "steams"),
                Left: "hot",
                Right: "wet",
                CoOccurrence: WorldInteractionCoOccurrence.Distance,
                Range: 2m,
                Effects: [new ActionEffect.AddState(
                        State: "count",
                        Value: 1m
                    )],
                Neighbours: neighbours
            )])
    );
    private static WorldInteractionBound Bound(int? neighbours) {
        var definition = Document(neighbours: neighbours);

        return WorldInteractionBound.Of(
            context: WorldFactsCompiler.Context(definition: definition),
            interaction: ((CompiledWorldFactsRule)Assert.Single(collection: WorldFactsCompiler.CompileAllInteractions(definition: definition))).Interaction!.Value
        );
    }

    [Fact]
    public void PoolInstancesSharingABodyStillEachSpendAnEvaluation() {
        static CellName Name(string value) => CellName.Parse(candidate: value);
        var definition = Document(neighbours: null) with {
            PopulationRaw = new WorldBodiesDefaults(CapacityRaw: 2, LocalSeatsRaw: 2),
            StateRaw = new WorldStateSection(
                World: [Keyed(capacity: 1, name: "hot"), new WorldStateRow(Name(value: "count"), CellKind.Int, Cells: [new StateCell(WorldStateRow.SlotKey, CellValue.Int(value: 0))])],
                Enums: [new StateEnum(Name(value: "Body"), [Name(value: "First"), Name(value: "Second")])],
                Records: [new StateRecord(Name(value: "Item"), [new StatePoolField(Name(value: "body"), Default: CellValue.Int(value: 1), Enum: Name(value: "Body"))])],
                Pools: [new StatePool(Name(value: "wet"), Name(value: "Item"), 8)]),
            Properties = new WorldPropertyRegistrySection(["hot"], [new WorldPoolBodyCarrier(Name(value: "wet"), Name(value: "body"), [
                new WorldPoolBodyBinding(Name(value: "First"), Seat: 0), new WorldPoolBodyBinding(Name(value: "Second"), Seat: 1),
            ])]),
        };
        var interaction = ((CompiledWorldFactsRule)Assert.Single(collection: WorldFactsCompiler.CompileAllInteractions(definition: definition))).Interaction!.Value;
        var bound = WorldInteractionBound.Of(interaction, WorldFactsCompiler.Context(definition: definition));

        Assert.Equal(1L, bound.Lefts);
        Assert.Equal(8L, bound.Rights);
        Assert.Equal(8L, bound.Evaluations);

        var regionDefinition = definition with {
            PlacementRowsRaw = [new WorldPlacement("zone", "fixture", new Puck.Assets.Documents.DocumentVector3(x: 0f, y: 0f, z: 0f), 0f, 1f, Region: new WorldPlacementRegion(Radius: 1f))],
            Interactions = new WorldInteractionsSection(Interactions: [definition.Interactions!.Interactions[0] with {
                Left = "wet", Right = "zone", CoOccurrence = WorldInteractionCoOccurrence.Region,
            }]),
        };
        var regionRule = ((CompiledWorldFactsRule)Assert.Single(collection: WorldFactsCompiler.CompileAllInteractions(definition: regionDefinition)));
        var regionBound = WorldInteractionBound.Of(regionRule.Interaction!.Value, WorldFactsCompiler.Context(definition: regionDefinition));

        Assert.Equal(8L, regionBound.Evaluations);
        Assert.Contains("8 logical pool instances", WorldRuleWorkBudget.DescribeMultiplier(definition: regionDefinition, rule: regionRule));
    }
    [Fact]
    public void ACarrierSetIsNoLargerThanItsTagRowOrThePopulation() {
        var bound = Bound(neighbours: null);

        Assert.Equal(
            Tagged,
            bound.Lefts
        );
        Assert.Equal(
            Population,
            bound.Rights
        );
    }
    [Fact]
    public void OneNeighbourStillTestsEveryLeftAgainstEveryRight() {
        var every = Bound(neighbours: null);
        var one = Bound(neighbours: 1);

        Assert.Equal(
            (Tagged * (Population - 1L)),
            every.Evaluations
        );
        Assert.Equal(
            Tagged,
            one.Evaluations
        );
        // The limit removes no distance test; it adds the comparison and the move that keep the nearest right.
        Assert.Equal(
            ((Tagged * ((long)Population)) * 2L),
            (one.Setup.Units - every.Setup.Units)
        );
        Assert.True(condition: (every.Setup.Units >= ((Tagged * ((long)Population)) * 4L)));
    }
    [Fact]
    public void ALeftDrawnFromTheRightsOwnRowHasOnePartnerFewer() {
        var definition = (Document(neighbours: null) with {
            Interactions = new WorldInteractionsSection(Interactions: [Document(neighbours: null).Interactions!.Interactions[0] with { Right = "hot" }]),
        });
        var bound = WorldInteractionBound.Of(
            context: WorldFactsCompiler.Context(definition: definition),
            interaction: ((CompiledWorldFactsRule)Assert.Single(collection: WorldFactsCompiler.CompileAllInteractions(definition: definition))).Interaction!.Value
        );

        Assert.Equal(
            (Tagged * (Tagged - 1L)),
            bound.Evaluations
        );
    }
    [Fact]
    public void TheSweepIsSetupOnTheSheetAndIsPaidOnceWhateverTheEvaluationsNumber() {
        var definition = Document(neighbours: 1);
        var line = Assert.Single(collection: WorldRuleWorkBudget.Contributors(definition: definition));
        var bound = Bound(neighbours: 1);

        Assert.Equal(
            bound.Setup,
            line.Cost.Setup
        );
        Assert.Equal(
            bound.Evaluations,
            line.Multiplier
        );
        Assert.Equal(
            (line.Cost.Setup + (line.Multiplier * (line.Cost.Check + line.Cost.Effects))),
            WorldRuleWorkBudget.Measure(definition: definition).WorkUnitsPerTick
        );
    }
    [Fact]
    public void GatheringPaysForTheScanOfTheWholeTagRowAndTheSortOfItsCarriers() {
        var bound = Bound(neighbours: null);
        var gathering = (((Tagged * 4L) + RuleWorkBudget.IntrosortWork(count: Tagged).Units) + ((4096L * 4L) + RuleWorkBudget.IntrosortWork(count: Population).Units));

        Assert.Equal(
            (gathering + ((Tagged * ((long)Population)) * 4L)),
            bound.Setup.Units
        );
    }
}
