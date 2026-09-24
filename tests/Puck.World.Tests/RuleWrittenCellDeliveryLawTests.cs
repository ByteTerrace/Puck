using Puck.Commands;
using Puck.World.Client;
using Puck.World.Protocol;

using Xunit;

namespace Puck.World.Tests;

/// <summary>THE LAW: a cell a rule writes reaches an attached client's own definition on the tick that wrote it,
/// through the same <see cref="IClientSink.DeliverState"/> door a console write delivers through.</summary>
/// <remarks>The control is the console write: one arm writes the row through
/// <see cref="WorldMutation.UpsertStateCell"/> and the other through a rule's own effect, and both are read back off
/// <see cref="WorldClient.Definition"/> rather than the server's.</remarks>
public sealed class RuleWrittenCellDeliveryLawTests {
    private const string TallyRow = "tally";

    // The base fixture plus one Int slot and, optionally, the rule that adds to it every tick.
    private static WorldDefinition Document(bool rule) {
        var document = Fixtures.BuildDocument().WithWorldState(rows: [new WorldStateRow(
            Name: CellName.Parse(candidate: TallyRow),
            Kind: CellKind.Int,
            Max: 1000L,
            Min: 0L,
            Cells: [new StateCell(
                Key: WorldStateRow.SlotKey,
                Value: CellValue.Int(value: 0L)
            )]
        )]);

        return (rule
            ? (document with {
                Rules = [new WorldRule(
                    Name: CellName.Parse(candidate: "count"),
                    Mode: ActionTriggerMode.Level,
                    Effects: [new ActionEffect.AddState(
                        State: TallyRow,
                        Value: 1m
                    )]
                )],
            })
            : document
        );
    }
    private static long Tally(WorldDefinition definition) => definition.State
        .Single(predicate: static row => (row.Name.Value == TallyRow))
        .Cells!
        .Single(predicate: static cell => (cell.Key.Value == WorldStateRow.SlotKey.Value))
        .Value
        .AsInt;

    [Fact]
    public void ACellARuleWroteReachesTheAttachedClientsDefinition() {
        using var fixture = Fixtures.FreshServer(definition: Document(rule: true));
        var client = ClientFixtures.Client(definition: fixture.Server.Definition);

        using var lease = fixture.Server.AttachSink(sink: client);

        Assert.Equal(
            actual: Tally(definition: client.Definition),
            expected: 0L
        );

        fixture.Step();
        fixture.Step();

        var served = Tally(definition: fixture.Server.Definition);

        Assert.True(
            condition: (served > 0L),
            userMessage: "the rule never wrote the row on the server, so the delivery claim is untested"
        );
        Assert.Equal(
            actual: Tally(definition: client.Definition),
            expected: served
        );
    }
    /// <summary>The control: the same row, the same client, written by the console instead of by a rule.</summary>
    [Fact]
    public void ACellTheConsoleWroteReachesTheAttachedClientsDefinition() {
        using var fixture = Fixtures.FreshServer(definition: Document(rule: false));
        var client = ClientFixtures.Client(definition: fixture.Server.Definition);

        using var lease = fixture.Server.AttachSink(sink: client);

        fixture.Server.EnqueueMutation(mutation: new WorldMutation.UpsertStateCell(
            Principal: Principal.Console,
            Row: TallyRow,
            Key: WorldStateRow.SlotKey.Value,
            Value: 7,
            Kind: WorldDocumentWriteKind.Set
        ));
        fixture.Step();

        Assert.Equal(
            actual: Tally(definition: fixture.Server.Definition),
            expected: 7L
        );
        Assert.Equal(
            actual: Tally(definition: client.Definition),
            expected: 7L
        );
    }

}
