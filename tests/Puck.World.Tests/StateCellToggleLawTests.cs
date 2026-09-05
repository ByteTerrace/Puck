using Puck.World.Protocol;
using Xunit;

namespace Puck.World.Tests;

/// <summary>An authored state cycle is decided atomically at the destination mutation compose boundary: the client
/// carries every token and never guesses from a potentially stale or differently routed document. The cell becomes
/// the token after the one it currently equals (wrapping); a value matching none becomes the first.</summary>
public sealed class StateCellToggleLawTests {
    private const string CellKey = "value";
    private const string RowName = "toggleProbe";

    [Fact]
    public void CycleTokens_TwoValues_ToggleAgainstDestinationValueAcrossWireRoundTrip() {
        using var fixture = Fixtures.FreshServer(definition: Fixtures.BuildDocument().WithWorldState(rows: [NumericRow(initial: 3L)]));
        var transport = new LoopbackTransport(server: fixture.Server);

        Cycle(tokens: ["3", "7"], transport: transport);
        fixture.Step();
        Assert.Equal(expected: 7L, actual: ReadNumeric(fixture: fixture));

        Cycle(tokens: ["3", "7"], transport: transport);
        fixture.Step();
        Assert.Equal(expected: 3L, actual: ReadNumeric(fixture: fixture));
    }
    [Fact]
    public void CycleTokens_ThreeValues_WrapAndRestartFromTheFirstWhenNoneMatch() {
        using var fixture = Fixtures.FreshServer(definition: Fixtures.BuildDocument().WithWorldState(rows: [NumericRow(initial: 99L)]));
        var transport = new LoopbackTransport(server: fixture.Server);

        // 99 matches none -> the first.
        Cycle(tokens: ["1", "2", "3"], transport: transport);
        fixture.Step();
        Assert.Equal(expected: 1L, actual: ReadNumeric(fixture: fixture));

        Cycle(tokens: ["1", "2", "3"], transport: transport);
        fixture.Step();
        Assert.Equal(expected: 2L, actual: ReadNumeric(fixture: fixture));

        Cycle(tokens: ["1", "2", "3"], transport: transport);
        fixture.Step();
        Assert.Equal(expected: 3L, actual: ReadNumeric(fixture: fixture));

        // 3 is the last -> wraps to the first.
        Cycle(tokens: ["1", "2", "3"], transport: transport);
        fixture.Step();
        Assert.Equal(expected: 1L, actual: ReadNumeric(fixture: fixture));
    }
    [Fact]
    public void CycleTokens_TextRow_CyclesByTextAtTheDestination() {
        var row = new WorldStateRow(
            Name: CellName.Parse(candidate: RowName),
            Kind: CellKind.Text,
            Capacity: 1,
            Cells: [new WorldStateCell(Key: CellName.Parse(candidate: CellKey), Text: "crossbar")]
        );
        using var fixture = Fixtures.FreshServer(definition: Fixtures.BuildDocument().WithWorldState(rows: [row]));
        var transport = new LoopbackTransport(server: fixture.Server);

        Cycle(tokens: ["crossbar", "linear"], transport: transport);
        fixture.Step();
        Assert.Equal(expected: "linear", actual: ReadText(fixture: fixture));

        Cycle(tokens: ["crossbar", "linear"], transport: transport);
        fixture.Step();
        Assert.Equal(expected: "crossbar", actual: ReadText(fixture: fixture));
    }
    [Fact]
    public void CycleTokens_OneToken_IsRefusedAndLeavesTheCell() {
        using var fixture = Fixtures.FreshServer(definition: Fixtures.BuildDocument().WithWorldState(rows: [NumericRow(initial: 3L)]));
        var transport = new LoopbackTransport(server: fixture.Server);

        Cycle(tokens: ["7"], transport: transport);
        fixture.Step();
        Assert.Equal(expected: 3L, actual: ReadNumeric(fixture: fixture));
    }

    private static WorldStateRow NumericRow(long initial) => new(
        Name: CellName.Parse(candidate: RowName),
        Kind: CellKind.Int,
        Capacity: 1,
        Cells: [new WorldStateCell(Key: CellName.Parse(candidate: CellKey), Value: initial)]
    );
    private static long ReadNumeric(WorldFixture fixture) {
        Assert.True(condition: WorldStateReader.TryRead(
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
    private static string ReadText(WorldFixture fixture) {
        Assert.True(condition: WorldStateReader.TryRead(
            definition: fixture.Server.Definition,
            rowName: RowName,
            key: CellKey,
            tick: fixture.Server.NextInputTick,
            row: out _,
            rawValue: out _,
            text: out var text
        ));

        return Assert.IsType<string>(@object: text);
    }
    private static void Cycle(LoopbackTransport transport, string[] tokens) => transport.SubmitWorldMutation(mutation: new WorldMutation.UpsertStateCell(
        Principal: WorldPrincipal.Console,
        Row: RowName,
        Key: CellKey,
        Value: 0L,
        Kind: WorldDocumentWriteKind.Set,
        CycleTokens: tokens
    ));
}
