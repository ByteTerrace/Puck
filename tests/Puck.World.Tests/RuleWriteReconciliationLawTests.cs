using Puck.Commands;
using Puck.Maths;
using Puck.World.Protocol;
using Xunit;

namespace Puck.World.Tests;

/// <summary>CONTRACT UNDER TEST: a state value reaches the installed document through two doors, a value mutation and
/// the export of what the tick's rules wrote, and what lies outside the arena and keeps its own copy of a value is
/// brought up to date by both. A drive gate a rule closes refuses drive exactly as one a command closes, and a body
/// scale a rule writes reaches the body exactly as one a command writes.</summary>
public sealed class RuleWriteReconciliationLawTests {
    private static readonly FixedQ4816 Half = FixedQ4816.FromDouble(value: 0.5);

    private static WorldDefinition Document(WorldRule[] rules, bool scaled) {
        var source = Fixtures.BuildDocument();

        return source with {
            PopulationRaw = (scaled
                ? (source.Population with { ScaleRow = "scale" })
                : source.Population),
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
                    CellName.Parse(candidate: "scale"),
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

    // What a publication costs follows what was written: a row no rule moved is carried across as the instance the
    // installed document already held, and only the row that moved is read back out of the arena.
    [Fact]
    public void APublicationCarriesTheRowsThatMovedAndKeepsTheRest() {
        using var fixture = Fixtures.FreshServer(definition: Document(
            rules: [new WorldRule(
                    CellName.Parse(candidate: "count"),
                    [new ActionEffect.AddState(
                            Key: "0",
                            State: "stunned",
                            Value: 1m
                        )]
                )],
            scaled: false
        ));

        static WorldStateRow Row(WorldFixture fixture, string name) => WorldDefinitionRows.FindStateRow(
            name: name,
            rows: fixture.Server.Definition.State
        )!;

        // The first publication after the arena is seeded carries every row, so the comparison starts after it.
        fixture.Step();

        var quiet = Row(
            fixture: fixture,
            name: "scale"
        );
        var written = Row(
            fixture: fixture,
            name: "stunned"
        );

        fixture.Step();

        Assert.Same(
            actual: Row(
                fixture: fixture,
                name: "scale"
            ),
            expected: quiet
        );
        Assert.NotSame(
            actual: Row(
                fixture: fixture,
                name: "stunned"
            ),
            expected: written
        );
        Assert.Equal(
            actual: Row(
                fixture: fixture,
                name: "stunned"
            ).Cells![0].Value.AsInt,
            expected: 2L
        );
    }
    [InlineData(false)]
    [InlineData(true)]
    [Theory]
    public void AClosedDriveGateRefusesDriveWhicheverDoorClosedIt(bool byRule) {
        using var fixture = Fixtures.FreshServer(definition: Document(
            rules: (byRule
                ? [Write(
                    row: "stunned",
                    value: 1m
                )]
                : []),
            scaled: false
        ));

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
    public void AWrittenBodyScaleReachesTheBodyWhicheverDoorWroteIt(bool byRule) {
        using var fixture = Fixtures.FreshServer(definition: Document(
            rules: (byRule
                ? [Write(
                    row: "scale",
                    value: 0.5m
                )]
                : []),
            scaled: true
        ));

        Join(fixture: fixture);

        if (!byRule) {
            fixture.Server.EnqueueMutation(mutation: new WorldMutation.UpsertStateCell(
                Key: "0",
                Kind: WorldDocumentWriteKind.Set,
                Principal: Principal.Console,
                Row: "scale",
                Value: Half.Value
            ));
        }

        fixture.Step();

        Assert.Equal(
            actual: fixture.Server.Body(index: 0)!.Scale,
            expected: Half
        );
    }
}
