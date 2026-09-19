using Xunit;

using Puck.World.Protocol;

namespace Puck.World.Tests;

/// <summary>Proves a world rule's gate reads the stored truth of a cell carrying a <see cref="StateDynamics"/> easing
/// trait, not the follower's eased sample.</summary>
public sealed class RuleGateReadsTruthLawTests {
    private static readonly DynamicsRow Slow = new(
        Damping: 1f,
        Frequency: 0.25f,
        Name: "slow",
        Response: 0f
    );

    private static WorldStateRow Gauge(string name, bool eased) => new(
        Name: CellName.Parse(candidate: name),
        Kind: CellKind.Int,
        Capacity: 4,
        Cells: [new StateCell(
                Key: CellName.Parse(candidate: "0"),
                Value: CellValue.Int(value: 0L),
                Dynamics: (eased ? new StateDynamics(Row: "slow") : null)
            )]
    );
    private static WorldStateRow Flag(string name) => new(
        Name: CellName.Parse(candidate: name),
        Kind: CellKind.Int,
        Cells: [new StateCell(
                Key: WorldStateRow.SlotKey,
                Value: CellValue.Int(value: 0L)
            )]
    );
    private static WorldRule Watch(string name, string gauge, string flag) => new(
        Name: CellName.Parse(candidate: name),
        Gate: new ActionPredicate.CompareState(
            State: gauge,
            Comparison: ActionStateComparison.GreaterOrEqual,
            Value: 100m,
            Key: "0"
        ),
        Mode: ActionTriggerMode.Edge,
        Effects: [new ActionEffect.SetState(
                State: flag,
                Value: 1m
            )]
    );
    private static long Read(WorldFixture fixture, string row) => WorldDefinitionRows.FindStateRow(
        rows: fixture.Server.Definition.State,
        name: row
    )!.Cells![0].Value.AsInt;

    [Fact]
    public void AGateOverAnEasedCellOpensWhenTheTruthCrosses() {
        var document = (Fixtures.BuildDocument().WithWorldState(rows: [
            Gauge(eased: true, name: "eased"), Gauge(eased: false, name: "plain"), Flag(name: "easedFired"), Flag(name: "plainFired"),
        ]) with {
            DynamicsRaw = [.. Fixtures.StandardDynamics, Slow],
            Rules = [Watch(flag: "easedFired", gauge: "eased", name: "watchEased"), Watch(flag: "plainFired", gauge: "plain", name: "watchPlain")],
        });
        using var fixture = Fixtures.FreshServer(definition: document);

        foreach (var row in new[] { "eased", "plain" }) {
            fixture.Server.EnqueueMutation(mutation: new WorldMutation.UpsertStateCell(
                Principal: WorldPrincipal.Console,
                Row: row,
                Key: "0",
                Value: 300,
                Kind: WorldDocumentWriteKind.Set
            ));
        }

        var easedAt = -1;
        var plainAt = -1;

        for (var step = 1; (step <= 240); step++) {
            fixture.Step();

            if ((plainAt < 0) && (Read(fixture: fixture, row: "plainFired") == 1L)) {
                plainAt = step;
            }
            if ((easedAt < 0) && (Read(fixture: fixture, row: "easedFired") == 1L)) {
                easedAt = step;
            }
        }

        Assert.True(condition: (plainAt > 0), userMessage: "the control gate never opened");
        Assert.True(
            condition: (plainAt == easedAt),
            userMessage: $"plain gate opened at step {plainAt}; eased-cell gate opened at step {easedAt}"
        );
    }
}
