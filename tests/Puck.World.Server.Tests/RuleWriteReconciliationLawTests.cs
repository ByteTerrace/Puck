using Puck.Commands;
using Puck.Maths;
using Puck.World.Protocol;
using Xunit;

namespace Puck.World.Server.Tests;

/// <summary>CONTRACT UNDER TEST: a state value reaches the installed document through two doors, a value mutation and
/// the export of what the tick's rules wrote, and what lies outside the arena and keeps its own copy of a value is
/// brought up to date by both. A drive gate a rule closes refuses drive exactly as one a command closes, and a body
/// scale written to its register reaches the body whichever writer wrote it.</summary>
public sealed class RuleWriteReconciliationLawTests {
    private static readonly FixedQ4816 Half = FixedQ4816.FromDouble(value: 0.5);

    private static WorldDefinition Document(WorldRule[] rules) {
        var source = Fixtures.BuildDocument();

        return source with {
            Rules = rules,
            StateRaw = ((source.StateRaw ?? new WorldStateSection()) with {
                World = [
                .. (source.StateRaw?.World ?? []),
                new WorldStateRow(
                    CellName.Parse(candidate: "stunned"),
                    CellKind.Int,
                    Capacity: 4,
                    Cells: [new StateCell(
                            CellName.Parse(candidate: "0"),
                            CellValue.Int(value: 0L)
                        )],
                    GatesDrive: true
                ),
                new WorldStateRow(
                    CellName.Parse(candidate: "quiet"),
                    CellKind.Fixed,
                    Capacity: 4,
                    Cells: [new StateCell(
                            CellName.Parse(candidate: "0"),
                            CellValue.Fixed(rawBits: FixedQ4816.One.Value)
                        )],
                    Max: FixedQ4816.One.Value,
                    Min: FixedQ4816.FromDouble(value: 0.05).Value
                ),
            ],
            }),
        };
    }
    private static WorldRule Write(string row, decimal value) => new(
        CellName.Parse(candidate: $"write-{row}"),
        [new ActionEffect.SetState(
                Key: "0",
                State: row,
                Value: value
            )]
    );
    private static void Join(WorldFixture fixture) {
        var actor = Principal.Seat(slot: 0);

        Assert.True(condition: fixture.Server.ApplySession(request: new SessionRequest.Join(
            IdentityName: null,
            Principal: actor,
            Slot: actor.Index,
            WireProtocolKey: WorldProtocol.WireProtocolKey
        )).Accepted);
    }

    [Fact]
    public void ADriveGateRuleCancelsHeldInputAtItsCommit() {
        var basis = Document(rules: [Write(row: "stunned", value: 1m) with {
            Gate = new ActionPredicate.CompareState("$inputMatch:1:gateChord", ExpressionOp.Equal, 1m),
        }]);
        var definition = basis with {
            ChannelsRaw = [.. basis.Channels, new("gateHold", ChannelShape.Binary, Composition: true), new("gateOther", ChannelShape.Binary, Composition: true)],
            InputPatterns = [new("gateChord", [new("gateHold", WorldInputEventKind.Press), new("gateOther", WorldInputEventKind.Press)])],
            InputHistory = new(EventCapacity: 16, MaximumEventsPerStep: 16),
        };
        using var fixture = Fixtures.FreshServer(definition: definition);

        Join(fixture: fixture);
        fixture.Step();
        var catalog = fixture.Server.Population.InputCatalog;

        Assert.True(condition: catalog.TryGetLogicalId(id: out var channel, name: "gateHold"));
        Assert.True(condition: catalog.TryGetLogicalId(id: out var other, name: "gateOther"));
        WorldInputSource[] sources = [new(0, channel, WorldInputContributionKind.Level, "key:held"), new(1, other, WorldInputContributionKind.Level, "key:other")];
        var owner = fixture.Server.Input.Admit(Principal.Seat(slot: 0), 0, sources);
        var producer = new WorldInputProducer(catalog, owner.Mapping, sources, 128, owner.Revision, owner.Epoch);
        var tick = fixture.Server.Input.Interval.Tick;

        Assert.True(condition: producer.Observe(0, FixedQ4816.One.Value, tick, default, 1));
        Assert.True(condition: producer.Observe(1, FixedQ4816.One.Value, tick, FixedQ4816.FromRawBits(value: 1), 2));
        Assert.True(condition: fixture.Server.Input.TryAdmit(Principal.Seat(slot: 0), producer.Publish(tick), out var reason), userMessage: reason);
        Assert.True(condition: owner.TryHeld(channel: channel, hold: out var origin));
        fixture.Step();
        Assert.True(condition: fixture.Server.Grants.TryGetDriveGate(bodyIndex: 0, gateRow: out _));
        Assert.False(condition: owner.TryHeld(channel: channel, hold: out _));
        Assert.True(condition: owner.TryEvent(channel: channel, kind: WorldInputEventKind.Cancel, occurrence: 0, record: out var cancelled));
        Assert.Equal(origin.Press, cancelled.HoldPress);
        Assert.Equal(new ulong[] { 1, 2 }, owner.LastSwitch.ConsumedActivations.ToArray());
    }
    // What a publication costs follows what was written: a row no rule moved is carried across as the instance the
    // installed document already held, and only the row that moved is read back out of the arena.
    [InlineData(false)]
    [InlineData(true)]
    [Theory]
    public void AClosedDriveGateRefusesDriveWhicheverDoorClosedIt(bool byRule) {
        using var fixture = Fixtures.FreshServer(definition: Document(rules: (byRule
                ? [Write(
                    row: "stunned",
                    value: 1m
                )]
                : [])));

        Assert.False(condition: fixture.Server.Grants.TryGetDriveGate(
            bodyIndex: 0,
            gateRow: out _
        ));

        if (!byRule) {
            fixture.Server.EnqueueMutation(mutation: new WorldMutation.UpsertStateCell(
                Key: "0",
                Kind: WorldDocumentWriteKind.Set,
                Principal: Principal.Console,
                Row: "stunned",
                Value: 1
            ));
        }

        fixture.Step();

        Assert.True(condition: fixture.Server.Grants.TryGetDriveGate(
            bodyIndex: 0,
            gateRow: out var gate
        ));
        Assert.Equal(
            actual: gate,
            expected: "stunned"
        );
    }
    [InlineData(false)]
    [InlineData(true)]
    [Theory]
    public void AWrittenBodyScaleReachesTheBodyWhicheverWriterWroteIt(bool byRule) {
        using var fixture = Fixtures.FreshServer(definition: Fixtures.WithBodyScale(
            initial: FixedQ4816.One,
            source: Document(rules: (byRule
                ? [new WorldRule(
                    CellName.Parse(candidate: "shrink"),
                    [new WorldEffect.SetBodyState(
                            Key: "0",
                            State: Fixtures.BodyScaleRegister,
                            Value: 0.5m
                        )]
                )]
                : []))
        ));

        Join(fixture: fixture);

        if (!byRule) {
            Fixtures.WriteBodyScale(
                body: 0,
                server: fixture.Server,
                value: Half
            );
        }

        fixture.Step();

        Assert.Equal(
            actual: fixture.Server.Body(index: 0)!.Scale,
            expected: Half
        );
    }
}
