using System.Numerics;

using Xunit;

namespace Puck.World.Tests;

/// <summary>Locks the rule host's mixed transaction boundary: state writes and a placement document write belong to
/// one preflighted branch, and a later document leaf refusal rolls the whole branch back.</summary>
public sealed class WorldRuleMixedDocumentTransactionLawTests {
    private const string PlacementId = "camera-seat-0";
    [Fact]
    public void MixedStateAndPlacementTransactionInstallsBothExactlyOnce() {
        using var fixture = Fixtures.FreshServer(definition: Scenario(includeBadSecondPlacement: false));
        var installs = 0;
        fixture.Server.MutationJournalTap = (_, _) => installs++;

        fixture.Step();

        Assert.Equal(expected: 1L, actual: State(fixture.Server.Definition, "stage"));
        Assert.Equal(expected: 1L, actual: State(fixture.Server.Definition, "prefix"));
        Assert.Equal(expected: "prefix", actual: Text(fixture.Server.Definition, "prefixLabel"));
        Assert.Equal(expected: 1L, actual: State(fixture.Server.Definition, "after"));
        Assert.Equal(expected: "after", actual: Text(fixture.Server.Definition, "label"));
        Assert.Equal(expected: 1.25f, actual: Placement(fixture.Server.Definition, PlacementId).Scale);
        Assert.Equal(1, installs);

        // The gate is closed by the state write. A second fixed step must not replay the document write.
        fixture.Step();
        Assert.Equal(expected: 1L, actual: State(fixture.Server.Definition, "stage"));
        Assert.Equal(expected: 1L, actual: State(fixture.Server.Definition, "prefix"));
        Assert.Equal(expected: "prefix", actual: Text(fixture.Server.Definition, "prefixLabel"));
        Assert.Equal(expected: 1L, actual: State(fixture.Server.Definition, "after"));
        Assert.Equal(expected: "after", actual: Text(fixture.Server.Definition, "label"));
        Assert.Equal(expected: 1.25f, actual: Placement(fixture.Server.Definition, PlacementId).Scale);
    }

    [Fact]
    public void FailedReloadedScopePreservesEarlierNumericReads() {
        var document = Scenario(includeBadSecondPlacement: true);
        var first = document.Rules![0];
        document = document with { Rules = [
            first with { Effects = first.Effects.Where(effect => effect is not ActionEffect.SetState { State: "prefixLabel" }).ToArray() },
            new WorldRule(CellName.Parse("read-retained-prefix"),
                Effects: [new ActionEffect.SetState(State: "after", FromState: "prefix")])
        ] };
        using var fixture = Fixtures.FreshServer(document);
        fixture.Step();
        Assert.Equal(1, State(fixture.Server.Definition, "prefix"));
        Assert.Equal(1, State(fixture.Server.Definition, "after"));
        Assert.Equal(1f, Placement(fixture.Server.Definition, PlacementId).Scale);
    }

    [Fact]
    public void RefusalOfALaterPlacementLeafRollsBackTheMixedTransaction() {
        using var fixture = Fixtures.FreshServer(definition: Scenario(includeBadSecondPlacement: true));

        fixture.Step();

        Assert.Equal(expected: 1L, actual: State(fixture.Server.Definition, "prefix"));
        Assert.Equal(expected: "prefix", actual: Text(fixture.Server.Definition, "prefixLabel"));
        Assert.Equal(expected: 0L, actual: State(fixture.Server.Definition, "stage"));
        Assert.Equal(expected: 0L, actual: State(fixture.Server.Definition, "after"));
        Assert.Equal(expected: "before", actual: Text(fixture.Server.Definition, "label"));
        Assert.Equal(expected: 1f, actual: Placement(fixture.Server.Definition, PlacementId).Scale);
        Assert.DoesNotContain(expected: "missing", collection: fixture.Server.Definition.Placements.Select(static placement => placement.Id));
    }

    [Fact]
    public void StandalonePlacementKeepsOrderedStateAndDocumentEffects() {
        using var fixture = Fixtures.FreshServer(definition: Scenario(includeBadSecondPlacement: false, transaction: false));

        fixture.Step();

        Assert.Equal(expected: 1L, actual: State(fixture.Server.Definition, "prefix"));
        Assert.Equal(expected: "prefix", actual: Text(fixture.Server.Definition, "prefixLabel"));
        Assert.Equal(expected: 1L, actual: State(fixture.Server.Definition, "stage"));
        Assert.Equal(expected: 1.25f, actual: Placement(fixture.Server.Definition, PlacementId).Scale);
        Assert.Equal(expected: 1L, actual: State(fixture.Server.Definition, "after"));
        Assert.Equal(expected: "after", actual: Text(fixture.Server.Definition, "label"));
    }

    private static WorldDefinition Scenario(bool includeBadSecondPlacement, bool transaction = true) {
        var document = Fixtures.BuildCameraBodyDocument();
        var prefix = new WorldStateRow(
            Name: CellName.Parse(candidate: "prefix"),
            Kind: CellKind.Int,
            Cells: [new StateCell(Key: WorldStateRow.SlotKey, Value: 0L)]
        );
        var prefixLabel = new WorldStateRow(
            Name: CellName.Parse(candidate: "prefixLabel"),
            Kind: CellKind.Text,
            Capacity: 1,
            Cells: [new StateCell(Key: CellName.Parse(candidate: "entry"), Text: "before")]
        );
        var stage = new WorldStateRow(
            Name: CellName.Parse(candidate: "stage"),
            Kind: CellKind.Int,
            Cells: [new StateCell(Key: WorldStateRow.SlotKey, Value: 0L)]
        );
        var after = new WorldStateRow(
            Name: CellName.Parse(candidate: "after"),
            Kind: CellKind.Int,
            Cells: [new StateCell(Key: WorldStateRow.SlotKey, Value: 0L)]
        );
        var label = new WorldStateRow(
            Name: CellName.Parse(candidate: "label"),
            Kind: CellKind.Text,
            Capacity: 1,
            Cells: [new StateCell(Key: CellName.Parse(candidate: "entry"), Text: "before")]
        );
        var updatedBall = Placement(document, PlacementId) with { Scale = 1.25f };
        var effects = new List<ActionEffect> {
            new ActionEffect.SetState(State: "stage", Value: 1m),
            new WorldEffect.UpsertPlacement(Placement: updatedBall),
        };
        if (includeBadSecondPlacement) {
            effects.Add(new WorldEffect.UpsertPlacement(new WorldPlacement(
                Id: "missing",
                PrototypeId: "missing",
                Position: Vector3.Zero,
                YawDegrees: 0f,
                Scale: 1f,
                Solid: new WorldSolid(Margin: 0f)
            )));
        }

        var ruleEffects = new List<ActionEffect> {
            new ActionEffect.AddState(State: "prefix", Value: 1m),
            new ActionEffect.SetState(State: "prefixLabel", Key: "entry", Text: "prefix"),
        };
        if (transaction) {
            ruleEffects.Add(new ActionEffect.Transaction(Effects: effects));
        } else {
            ruleEffects.AddRange(collection: effects);
        }
        if (!includeBadSecondPlacement) {
            ruleEffects.Add(new ActionEffect.SetState(State: "after", FromState: "stage"));
            ruleEffects.Add(new ActionEffect.SetState(State: "label", Key: "entry", Text: "after"));
        }

        return document with {
            StateRaw = new WorldStateSection(World: [prefix, prefixLabel, stage, after, label]),
            Rules = [new WorldRule(
                Name: CellName.Parse(candidate: "mixed-document-transaction"),
                Gate: new ActionPredicate.CompareState(
                    State: "stage",
                    Comparison: ActionStateComparison.Equal,
                    Value: 0m
                ),
                Effects: ruleEffects
            )],
        };
    }

    private static long State(WorldDefinition definition, string row) =>
        definition.State.Single(state => state.Name.Value == row).Cells!.Single().Value;

    private static string? Text(WorldDefinition definition, string row) =>
        definition.State.Single(state => state.Name.Value == row).Cells!.Single().Text;

    private static WorldPlacement Placement(WorldDefinition definition, string id) =>
        definition.Placements.Single(placement => placement.Id == id);
}
