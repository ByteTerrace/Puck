using Puck.Commands;
using Puck.World.Protocol;
using Puck.World.Server;
using Xunit;

namespace Puck.World.Testing;

internal static partial class StateFixtures {
    /// <summary>Applies <paramref name="transform"/> to <paramref name="definition"/> through the arena kernels the
    /// live tick uses, as the world principal at revision one, asserting it is admitted.</summary>
    /// <param name="definition">The document to transform.</param>
    /// <param name="transform">The state transform.</param>
    /// <returns>The candidate document.</returns>
    public static WorldDefinition Apply(WorldDefinition definition, StateTransform transform) {
        Assert.True(
            condition: WorldArenaTransforms.TryApply(
                definition,
                transform,
                Principal.World,
                1,
                "test",
                out var candidate,
                out var reason
            ),
            userMessage: reason
        );

        return candidate!;
    }
    /// <summary>Enqueues a console write of <paramref name="value"/> to slot row <paramref name="row"/>, applied at
    /// the fixture's next step.</summary>
    /// <param name="fixture">The live fixture.</param>
    /// <param name="row">The slot row name.</param>
    /// <param name="value">The value to set.</param>
    public static void WriteSlot(this WorldFixture fixture, string row, long value) => fixture.Server.EnqueueMutation(mutation: new WorldMutation.UpsertStateCell(
        Principal: Principal.Console,
        Row: row,
        Key: WorldStateRow.SlotKey.Value,
        Value: value,
        Kind: WorldDocumentWriteKind.Set
    ));
    /// <summary>Reads the raw value of the cell under <paramref name="key"/> in row <paramref name="row"/> from the
    /// fixture's installed document.</summary>
    /// <param name="fixture">The live fixture.</param>
    /// <param name="row">The row name.</param>
    /// <param name="key">The cell key.</param>
    /// <returns>The cell's raw value.</returns>
    public static long KeyedValue(this WorldFixture fixture, string row, string key) => fixture.Row(name: row).Cells!.Single(predicate: cell => (cell.Key.Value == key)).Value.Raw;
    /// <summary>Reads a numeric cell through <see cref="WorldStateReader"/> at the fixture's next input tick, the
    /// read every console and rule reader takes, asserting it resolves to a whole number.</summary>
    /// <param name="fixture">The live fixture.</param>
    /// <param name="row">The row name.</param>
    /// <param name="key">The cell key.</param>
    /// <returns>The cell's value.</returns>
    public static long ReadNumeric(this WorldFixture fixture, string row, string key) {
        Assert.True(condition: WorldStateReader.TryRead(
            definition: fixture.Server.Definition,
            rowName: row,
            key: key,
            tick: fixture.Server.NextInputTick,
            engineTick: fixture.Server.CompletedEngineTicks,
            row: out _,
            rawValue: out var raw,
            text: out _
        ));

        return Assert.IsType<long>(@object: raw);
    }
    /// <summary>Returns the installed document's state row named <paramref name="name"/>.</summary>
    /// <param name="fixture">The live fixture.</param>
    /// <param name="name">The row name.</param>
    /// <returns>The row.</returns>
    public static WorldStateRow Row(this WorldFixture fixture, string name) => WorldDefinitionRows.FindStateRow(
        rows: fixture.Server.Definition.State,
        name: name
    )!;
    /// <summary>Reads the raw value of slot row <paramref name="row"/> from the fixture's installed document, as every
    /// reader outside the rule arena sees it after the tick publishes.</summary>
    /// <param name="fixture">The live fixture.</param>
    /// <param name="row">The slot row name.</param>
    /// <returns>The cell's raw value.</returns>
    public static long SlotValue(this WorldFixture fixture, string row) => StateRows.FindCell(
        cells: WorldDefinitionRows.FindStateRow(
            fixture.Server.Definition.State,
            row
        )!.Cells,
        key: WorldStateRow.SlotKey
    )!.Value.Raw;
}
