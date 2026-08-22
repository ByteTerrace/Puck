using Puck.World.Protocol;
using Xunit;

namespace Puck.World.Tests;

/// <summary>An authored two-value state toggle is decided atomically at the destination mutation compose boundary.
/// The client carries both raw tokens and never guesses from a potentially stale or differently routed document.</summary>
public sealed class StateCellToggleLawTests {
    private const string RowName = "toggleProbe";
    private const string CellKey = "value";

    [Fact]
    public void AlternateRawToken_TogglesAgainstDestinationValueAcrossWireRoundTrip() {
        var row = new WorldStateRow(
            Name: WorldCellName.Parse(candidate: RowName),
            Kind: CellKind.Int,
            Capacity: 1,
            Cells: [new WorldStateCell(Key: WorldCellName.Parse(candidate: CellKey), Value: 3L)]
        );
        using var fixture = Fixtures.FreshServer(definition: Fixtures.BuildDocument().WithWorldState(rows: [row]));
        var transport = new LoopbackTransport(server: fixture.Server);

        Toggle(transport: transport);
        fixture.Step();
        Assert.Equal(expected: 7L, actual: Read(fixture: fixture));

        Toggle(transport: transport);
        fixture.Step();
        Assert.Equal(expected: 3L, actual: Read(fixture: fixture));
    }

    private static long Read(WorldFixture fixture) {
        Assert.True(WorldStateReader.TryRead(
            definition: fixture.Server.Definition,
            rowName: RowName,
            key: CellKey,
            tick: fixture.Server.NextInputTick,
            row: out _,
            rawValue: out var raw,
            text: out _
        ));

        return Assert.IsType<long>(@object: raw);
    }

    private static void Toggle(LoopbackTransport transport) => transport.SubmitWorldMutation(mutation: new WorldMutation.UpsertStateCell(
        Principal: WorldPrincipal.Console,
        Row: RowName,
        Key: CellKey,
        Value: 0L,
        Kind: WorldDocumentWriteKind.Set,
        RawToken: "3",
        AlternateRawToken: "7"
    ));
}
