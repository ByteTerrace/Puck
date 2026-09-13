using System.Numerics;

using Xunit;

namespace Puck.World.Schema.Tests;

/// <summary>The work sheet's lines account for its total exactly, costliest first, and an over-budget refusal names
/// the lines that put it there.</summary>
public sealed class WorldRuleWorkBudgetContributorLawTests {
    private static WorldDefinition Document(params WorldRule[] rules) => new(
        Simulation: new WorldSimulationDefaults(RateHz: 240),
        StateRaw: new WorldStateSection(World: [Slot(name: "phase"), Slot(name: "count"), Keyed(
                capacity: 64,
                name: "many"
            ), Keyed(
                capacity: 4096,
                name: "more"
            )]),
        Rules: rules
    );
    private static WorldStateRow Keyed(string name, int capacity) =>
        new(
            CellName.Parse(candidate: name),
            CellKind.Int,
            Capacity: capacity,
            Cells: [new StateCell(
                    CellName.Parse(candidate: "0"),
                    0L
                )]
        );
    private static ActionPredicate PhaseIs(long value) =>
        new ActionPredicate.CompareState(
            State: "phase",
            Comparison: ActionStateComparison.Equal,
            Value: value
        );
    private static WorldDefinition RegionDocument(float regionRadius, params WorldKit[] kits) => new(
        Simulation: new WorldSimulationDefaults(RateHz: 240),
        PopulationRaw: new WorldBodiesDefaults(CapacityRaw: 144),
        Properties: new WorldPropertyRegistrySection(Names: ["tagged"]),
        StateRaw: new WorldStateSection(World: [Keyed(
                capacity: 144,
                name: "tagged"
            ), Slot(name: "count")]),
        KitsRaw: new WorldKitsSection(Rows: kits),
        PlacementsRaw: new WorldPlacementsSection(
            Policy: null,
            Rows: [RegionPlacement(
                    id: "spot",
                    radius: regionRadius
                )]
        ),
        Interactions: new WorldInteractionsSection(Interactions: [RegionInteraction(right: "spot")])
    );
    private static WorldInteraction RegionInteraction(string right) => new(
        Name: CellName.Parse(candidate: "touches"),
        Left: "tagged",
        Right: right,
        CoOccurrence: WorldInteractionCoOccurrence.Region,
        Range: 0m,
        Effects: [new ActionEffect.SetState(
                State: "count",
                Value: 1m
            )]
    );
    private static WorldPlacement RegionPlacement(string id, float radius) =>
        new(
            Id: id,
            PrototypeId: id,
            Position: Vector3.Zero,
            YawDegrees: 0f,
            Scale: 1f,
            Region: new WorldPlacementRegion(Radius: radius)
        );
    private static WorldRule Rule(string name, string? forEach, ActionPredicate? gate = null) =>
        new(
            CellName.Parse(candidate: name),
            [new ActionEffect.AddState(
                    State: "count",
                    Value: 1m
                )],
            Gate: gate,
            ForEach: forEach
        );
    private static WorldStateRow Slot(string name) =>
        new(
            CellName.Parse(candidate: name),
            CellKind.Int,
            Cells: [new StateCell(
                    WorldStateRow.SlotKey,
                    0L
                )]
        );
    private static WorldKit SolidKit(string name, float radius) => new(
        Name: name,
        BodyMotionProgram: "walk",
        Motion: new WorldMotion(
            Speed: new WorldSpeed(Value: 4f),
            Turn: new WorldTurn(Rate: 2.5f)
        ),
        Collider: new WorldCollider.Capsule(
            Endpoint: new Vector3(
                x: 0f,
                y: 1f,
                z: 0f
            ),
            Radius: radius
        ),
        BodyContact: WorldBodyContactMode.Solid
    );

    [Fact]
    public void ARegionInteractionsMultiplierAdmitsEveryCarrierWhoseCentreFitsTheRegion() {
        // Membership is by a carrier's own centre, so its footprint may hang over the rim. At radius 0.7 with a
        // 0.35 footprint the region's own centre plus a hexagonal ring of six at radius 0.7 is seven carriers,
        // every pair exactly 0.7 apart and every centre inside the region. A ratio taken over R alone admits
        // floor((R/r)^2) = 4 of them and under-prices the interaction; the bound over R + r admits
        // floor(((R + r)/r)^2) = 9.
        var document = RegionDocument(
            regionRadius: 0.7f,
            SolidKit(
                name: "walker",
                radius: 0.35f
            )
        );
        var line = WorldRuleWorkBudget.Contributors(definition: document).Single();

        Assert.Equal(
            9L,
            line.Multiplier
        );
        Assert.True(
            condition: (line.Multiplier >= 7L),
            userMessage: "seven carriers fit; a bound below that under-prices the interaction"
        );
    }
    [Fact]
    public void ARegionInteractionsMultiplierFallsBackToPopulationCapacityWhenAnyKitIsOverlap() {
        // The same radius/footprint pair as above, but an Overlap kit (of any footprint) defeats the packing bound —
        // two bodies depenetrate only when both kits are Solid, so an Overlap kit's own bodies could co-locate
        // without limit.
        var overlap = SolidKit(
            name: "ghost",
            radius: 0.05f
        ) with { BodyContact = WorldBodyContactMode.Overlap };
        var document = RegionDocument(
            regionRadius: 1.2f,
            SolidKit(
                name: "walker",
                radius: 0.35f
            ),
            overlap
        );
        var line = WorldRuleWorkBudget.Contributors(definition: document).Single();

        Assert.Equal(
            ((long)document.Population.Capacity),
            line.Multiplier
        );
    }
    [Fact]
    public void ARegionInteractionsMultiplierIsThePackedFootprintWhenEveryKitIsSolid() {
        // radius 1.2 packed with a 0.35 footprint: floor(((1.2 + 0.35)/0.35)^2) = 19, well under the 144-body
        // population.
        var document = RegionDocument(
            regionRadius: 1.2f,
            SolidKit(
                name: "walker",
                radius: 0.35f
            )
        );
        var line = WorldRuleWorkBudget.Contributors(definition: document).Single();

        Assert.Equal(
            19L,
            line.Multiplier
        );
        Assert.True(condition: (line.Multiplier < document.Population.Capacity));
    }
    [Fact]
    public void AnOverBudgetRefusalNamesTheCostliestLines() {
        var document = Document(
            Rule(
                "heavy",
                "more"
            ),
            Rule(
                "light",
                null
            )
        );

        Assert.False(condition: WorldDefinitionValidator.TryValidateLocally(
            definition: document,
            reason: out var reason
        ));
        Assert.Contains(
            actualString: reason,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "costliest: 'heavy' x4096 = "
        );
        Assert.Contains(
            actualString: reason,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "'light' x1 = "
        );
    }
    [Fact]
    public void LinesAccountForTheTotalAndSortCostliestFirst() {
        var document = Document(
            Rule(
                "plain",
                null
            ),
            Rule(
                "wide",
                "more"
            ),
            Rule(
                "narrow",
                "many"
            ),
            Rule(
                "p0",
                "many",
                PhaseIs(value: 0)
            ),
            Rule(
                "p1",
                "many",
                PhaseIs(value: 1)
            )
        );
        var lines = WorldRuleWorkBudget.Contributors(definition: document);

        Assert.Equal(
            ["wide", "p0", "p1", "narrow", "plain"],
            lines.Select(selector: line => line.Name)
        );
        Assert.Equal(
            4096L,
            lines[0].Multiplier
        );
        Assert.Equal(
            (lines[0].Multiplier * lines[0].UnitCost),
            lines[0].WorkUnits
        );
        Assert.Equal(
            [$"phase.{WorldStateRow.SlotKey}=0"],
            lines[1].Discriminators.Select(selector: pinned => pinned.Describe())
        );
        Assert.Equal(
            [$"phase.{WorldStateRow.SlotKey}=1"],
            lines[2].Discriminators.Select(selector: pinned => pinned.Describe())
        );
        Assert.Empty(collection: lines[0].Discriminators);

        // p0 and p1 are exclusive on phase, so all checks sum, but firing carries one of them, not both.
        var totalChecks = lines.Sum(selector: line => line.CheckUnits);
        var unconditionalFiring = lines.Where(predicate: line => (line.Discriminators.Count == 0)).Sum(selector: line => line.FiringUnits);
        var exclusiveFiring = lines.Where(predicate: line => (line.Discriminators.Count > 0)).Max(selector: line => line.FiringUnits);

        Assert.Equal(
            ((totalChecks + unconditionalFiring) + exclusiveFiring),
            WorldRuleWorkBudget.Measure(definition: document).WorkUnitsPerTick
        );
    }
}
