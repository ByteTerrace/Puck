using Puck.Assets.Documents;
using Xunit;

namespace Puck.World.Schema.Tests;

/// <summary>Proves <see cref="WorldDefinition.WithWorldState"/> keeps a value-only edit's compiled catalog, fields
/// composite, and field program identical by shape test rather than compile-then-compare, and still recompiles when
/// a row's shape actually changes.</summary>
public sealed class WorldDefinitionCompilationShapeLawTests(ITestOutputHelper output) {
    [Fact]
    public void ValueOnlyChange_PreservesCompiledViewsAndAllocatesUnder2KiB() {
        var original = new WorldDefinition(StateRaw: BuildState());
        var catalog = original.StateCatalog;
        var fields = Assert.IsType<WorldFieldsSection>(@object: original.Fields);
        var program = Assert.IsType<WorldFieldProgram>(@object: original.FieldProgram);

        var rows = original.StateRaw!.World!.Select(selector: row => (
            string.Equals(a: row.Name, b: "season", comparisonType: StringComparison.Ordinal)
                ? row with { Cells = [new StateCell(Key: WorldStateRow.SlotKey, Value: 42L)] }
                : row
        )).ToArray();

        var before = GC.GetAllocatedBytesForCurrentThread();
        var updated = original.WithWorldState(rows: rows);
        var allocated = (GC.GetAllocatedBytesForCurrentThread() - before);

        output.WriteLine(message: $"WithWorldState value-only change allocated {allocated} bytes.");

        Assert.Same(expected: catalog, actual: updated.StateCatalog);
        Assert.Same(expected: fields, actual: updated.Fields);
        Assert.Same(expected: program, actual: updated.FieldProgram);
        Assert.True(condition: (allocated < 2048), userMessage: $"expected under 2048 bytes, measured {allocated}");
    }
    [Fact]
    public void ShapeChange_ANewRowYieldsANewCatalog() {
        var original = new WorldDefinition(StateRaw: BuildState());
        var catalog = original.StateCatalog;

        var rows = original.StateRaw!.World!
            .Append(element: new WorldStateRow(Name: CellName.Parse(candidate: "round"), Kind: CellKind.Int))
            .ToArray();
        var updated = original.WithWorldState(rows: rows);

        Assert.NotSame(expected: catalog, actual: updated.StateCatalog);
        Assert.True(condition: updated.StateCatalog.TryResolve(handle: out _, lane: StateLane.Document, name: "round"));
    }

    private static WorldStateSection BuildState() => new(
        Lattices: [new WorldFieldTopology(
            Name: "ground",
            Origin: new DocumentVector3(x: 0f, y: 0f, z: 0f),
            CellSize: 1f,
            Width: 2,
            Depth: 2
        )],
        World: [
            new WorldStateRow(
                Name: CellName.Parse(candidate: "heat"),
                Kind: CellKind.Fixed,
                Domain: new StateDomain.CellsOf(Topology: "ground"),
                Field: new WorldStateFieldTrait(Initial: 0f, Min: 0f, Max: 10f)
            ),
            new WorldStateRow(
                Name: CellName.Parse(candidate: "season"),
                Kind: CellKind.Int,
                Cells: [new StateCell(Key: WorldStateRow.SlotKey, Value: 0L)]
            ),
        ]
    );
}
