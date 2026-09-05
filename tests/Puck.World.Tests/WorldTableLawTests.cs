using Puck.Physics.Motion;
using System.Text.Json;

using Puck.Assets.Documents;
using Puck.World.Authoring;
using Xunit;

namespace Puck.World.Tests;

/// <summary>A static table is a referenced, hash-pinned document read through <c>$table:</c>: a literal key is
/// proven present at compile, a dynamic key reads at evaluation and a missing one never holds a gate, a stale hash or
/// a duplicate key refuses at validation, and the table is not simulation state.</summary>
public sealed class WorldTableLawTests {
    private static WorldStateRow Slot(string name, long value) =>
        new(WorldCellName.Parse(name), CellKind.Int, Cells: [new WorldStateCell(WorldStateRow.SlotKey, value)]);
    private static long Value(WorldFixture fixture, string row) =>
        WorldDefinitionRows.FindCell(WorldDefinitionRows.FindStateRow(fixture.Server.Definition.State, row)!.Cells, WorldStateRow.SlotKey)!.Value;

    private static (string Source, string Hash) WriteTable(string kind, params (long Key, decimal Value)[] entries) =>
        Write(new TableDocument(TableDocument.CurrentSchema, kind, [.. entries.Select(e => new TableEntryDocument(e.Key, Value: e.Value))]));
    private static (string Source, string Hash) WriteColumns(string kind, string[] columns, params (long Key, decimal[] Values)[] entries) =>
        Write(new TableDocument(TableDocument.CurrentSchema, kind, [.. entries.Select(e => new TableEntryDocument(e.Key, Values: e.Values))], Columns: columns));
    private static (string Source, string Hash) Write(TableDocument document) {
        var source = $"tables-{Guid.NewGuid():N}.table.json";
        File.WriteAllBytes(Path.Combine(AppContext.BaseDirectory, source), JsonSerializer.SerializeToUtf8Bytes(document, DocumentJsonOptions.Shared));
        return (source, TableCanonicalizer.Validate(document).Count == 0 ? TableCanonicalizer.Canonicalize(document).Hash : string.Empty);
    }

    [Fact]
    public void ALiteralAndADynamicKeyReadTheTableAndAMissingKeyNeverHolds() {
        var (source, hash) = WriteTable(TableDocument.IntKind, (1, 60m), (2, 90m), (250, 5m));
        var document = Fixtures.BuildDocument() with {
            Tables = [new WorldTableRow("power", source, hash)],
            StateRaw = new WorldStateSection(World: [Slot("move", 2L), Slot("literal", 0L), Slot("dynamic", 0L), Slot("missing", 0L)]),
            Rules = [
                new WorldRule(WorldCellName.Parse("lit"), [new ActionEffect.SetState(State: "literal", FromState: "$table:power:250")]),
                new WorldRule(WorldCellName.Parse("dyn"), [new ActionEffect.SetState(State: "dynamic", FromState: "$table:power:$cell:move:$value")]),
                new WorldRule(
                    WorldCellName.Parse("gap"),
                    [new ActionEffect.SetState(State: "missing", Value: 1m)],
                    Gate: new ActionPredicate.CompareState(State: "$table:power:$cell:missing:$value", Comparison: ActionStateComparison.GreaterOrEqual, Value: 0m)
                ),
            ],
        };
        Assert.True(WorldDefinitionValidator.TryValidateLocally(definition: document, reason: out var reason), reason);
        using var fixture = Fixtures.FreshServer(definition: document);
        fixture.Step();
        Assert.Equal(5L, Value(fixture, "literal"));
        Assert.Equal(90L, Value(fixture, "dynamic"));
        Assert.Equal(0L, Value(fixture, "missing"));
        Assert.Contains("power kind=int entries=3", fixture.Server.DescribeTables(), StringComparison.Ordinal);

        var control = document with { StateRaw = document.StateRaw! with { World = [Slot("move", 2L), Slot("literal", 0L), Slot("dynamic", 0L), Slot("missing", 1L)] } };
        using var held = Fixtures.FreshServer(definition: control);
        held.Step();
        Assert.Equal(1L, Value(held, "missing"));
    }

    [Fact]
    public void AMissingLiteralKeyAStaleHashAndADuplicateKeyRefuse() {
        var (source, hash) = WriteTable(TableDocument.FixedKind, (7, 1.5m));
        var baseline = Fixtures.BuildDocument() with {
            Tables = [new WorldTableRow("rates", source, hash)],
            StateRaw = new WorldStateSection(World: [new WorldStateRow(WorldCellName.Parse("rate"), CellKind.Fixed, Cells: [new WorldStateCell(WorldStateRow.SlotKey, 0L)])]),
            Rules = [new WorldRule(WorldCellName.Parse("r"), [new ActionEffect.SetState(State: "rate", FromState: "$table:rates:7")])],
        };
        Assert.True(WorldDefinitionValidator.TryValidateLocally(definition: baseline, reason: out var okReason), okReason);

        var missing = baseline with { Rules = [new WorldRule(WorldCellName.Parse("r"), [new ActionEffect.SetState(State: "rate", FromState: "$table:rates:8")])] };
        Assert.False(WorldDefinitionValidator.TryValidateLocally(definition: missing, reason: out var missingReason));
        Assert.Contains("does not carry", missingReason, StringComparison.Ordinal);

        var stale = baseline with { Tables = [new WorldTableRow("rates", source, new string('0', 64))] };
        Assert.False(WorldDefinitionValidator.TryValidateLocally(definition: stale, reason: out var staleReason));
        Assert.Contains("hash", staleReason, StringComparison.Ordinal);

        var (duplicateSource, _) = WriteTable(TableDocument.IntKind, (1, 1m), (1, 2m));
        var duplicate = baseline with { Tables = [new WorldTableRow("rates", duplicateSource, hash)] };
        Assert.False(WorldDefinitionValidator.TryValidateLocally(definition: duplicate, reason: out var duplicateReason));
        Assert.Contains("declared twice", duplicateReason, StringComparison.Ordinal);
    }

    // A 2D chart flattened to a composite key computed by a binding: effectiveness[attack * 100 + defend].
    [Fact]
    public void AColumnTableIsReadByColumnAndABoundKeyComposesTheLookup() {
        var (source, hash) = WriteColumns(TableDocument.IntKind, ["power", "priority"], (1, [60m, 0m]), (2, [90m, 1m]));
        var (chart, chartHash) = WriteTable(TableDocument.IntKind, (102, 2m), (201, 0m));
        var document = Fixtures.BuildDocument() with {
            Tables = [new WorldTableRow("moves", source, hash), new WorldTableRow("chart", chart, chartHash)],
            StateRaw = new WorldStateSection(World: [Slot("move", 2L), Slot("attack", 1L), Slot("defend", 2L), Slot("power", 0L), Slot("priority", 0L), Slot("multiplier", 0L)]),
            Rules = [
                new WorldRule(WorldCellName.Parse("stats"), [
                    new ActionEffect.SetState(State: "power", FromState: "$table:moves:power:$cell:move:$value"),
                    new ActionEffect.SetState(State: "priority", FromState: "$table:moves:priority:$cell:move:$value"),
                ]),
                new WorldRule(
                    WorldCellName.Parse("effect"),
                    [new ActionEffect.SetState(State: "multiplier", FromState: "$table:chart:$bind:pair")],
                    Bindings: [new WorldRuleBinding(WorldCellName.Parse("pair"), CellKind.Int, new WorldValueExpression([
                        new WorldValueToken.State("attack"), new WorldValueToken.Constant(100m), new WorldValueToken.Multiply(), new WorldValueToken.State("defend"), new WorldValueToken.Add(),
                    ]))]
                ),
            ],
        };
        Assert.True(WorldDefinitionValidator.TryValidateLocally(definition: document, reason: out var reason), reason);
        using var fixture = Fixtures.FreshServer(definition: document);
        fixture.Step();
        Assert.Equal(90L, Value(fixture, "power"));
        Assert.Equal(1L, Value(fixture, "priority"));
        Assert.Equal(2L, Value(fixture, "multiplier"));

        var unnamed = document with { Rules = [new WorldRule(WorldCellName.Parse("stats"), [new ActionEffect.SetState(State: "power", FromState: "$table:moves:2")])] };
        Assert.False(WorldDefinitionValidator.TryValidateLocally(definition: unnamed, reason: out var columnReason));
        Assert.Contains("<column>", columnReason, StringComparison.Ordinal);
    }
}
