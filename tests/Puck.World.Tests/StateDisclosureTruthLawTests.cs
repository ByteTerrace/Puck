using Puck.Commands;
using Puck.World.Protocol;
using Xunit;

namespace Puck.World.Tests;

/// <summary>Proves state disclosure — <see cref="WorldStateDisclosure.Compose"/> and the rule host's
/// <see cref="WorldQuery.StateObservations"/> answer — discloses the stored truth of a cell carrying a
/// <see cref="StateDynamics"/> trait, not the follower's eased sample.</summary>
public sealed class StateDisclosureTruthLawTests {
    [Fact]
    public void StateDisclosureDisclosesStoredTruthForEasedCell() {
        var slow = new DynamicsRow(
            Damping: 1f,
            Frequency: 0.25f,
            Name: "slow",
            Response: 0f
        );
        var stateRow = new WorldStateRow(
            Name: CellName.Parse(candidate: "eased"),
            Kind: CellKind.Int,
            Capacity: 4,
            Cells: [new StateCell(
                Key: CellName.Parse(candidate: "0"),
                Value: CellValue.Int(value: 0L),
                Dynamics: new StateDynamics(Row: "slow")
            )],
            Visibility: new StateVisibility()
        );
        var document = Fixtures.BuildDocument().WithWorldState(rows: [stateRow]) with {
            DynamicsRaw = [.. Fixtures.StandardDynamics, slow],
        };

        using var fixture = Fixtures.FreshServer(definition: document);
        var server = fixture.Server;

        // A console write through the server's own door kicks the follower, so the cell is mid-ease on the next step.
        server.EnqueueMutation(mutation: new WorldMutation.UpsertStateCell(
            Principal: Principal.Console,
            Row: "eased",
            Key: "0",
            Value: 100,
            Kind: WorldDocumentWriteKind.Set
        ));
        fixture.Step();
        fixture.Step();

        var time = server.Time;
        var disclosed = WorldStateDisclosure.Compose(
            arena: server.Arena,
            definition: server.Definition,
            recipient: null,
            time: in time
        );

        Assert.NotNull(@object: disclosed);
        var observedRow = Assert.Single(collection: disclosed);
        var observedCell = Assert.Single(collection: observedRow.Cells);

        Assert.Equal(
            actual: observedCell.Value,
            expected: 100L
        );


        var answer = server.RuleHost.AnswerSubmittedQuery(
            principal: Principal.Console,
            query: new WorldQuery.StateObservations(Row: "eased")
        );

        Assert.False(
            condition: answer.Refused,
            userMessage: answer.Text
        );
        Assert.NotNull(@object: answer.Payload);
        var queryRows = Assert.IsAssignableFrom<IReadOnlyList<WorldObservedRow>>(@object: answer.Payload);
        var queryRow = Assert.Single(collection: queryRows);
        var queryCell = Assert.Single(collection: queryRow.Cells);

        Assert.Equal(
            actual: queryCell.Value,
            expected: 100L
        );
    }
}

