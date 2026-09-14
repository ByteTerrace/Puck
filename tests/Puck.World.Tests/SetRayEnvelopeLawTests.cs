using Puck.Assets.Documents;
using Puck.World.Protocol;
using Puck.World.Server;
using Xunit;

namespace Puck.World.Tests;

/// <summary>A <c>setRay</c> transform's write value goes through the SAME <see cref="StateRow.TryAdmitWrite"/> door
/// as every other write (ENGINE CONTRACT A): on a bounded row declaring <see cref="StateOverflow.Saturate"/> the
/// whole accepted prefix is clamped to the declared ceiling, and on a bounded row declaring the default
/// <see cref="StateOverflow.Refuse"/> an out-of-range value refuses the write outright — never a half-written ray —
/// regardless of how long the accepted prefix would otherwise have been.</summary>
public sealed class SetRayEnvelopeLawTests {
    private static CellName Name(string value) => CellName.Parse(candidate: value);
    private static StateCell Cell(string key, long value) => new(
        Key: Name(value: key),
        Value: value
    );
    // A pattern whose single symbol covers every representable value, so the whole ray is always the accepted
    // prefix — the law under test is the write's admission, never the pattern's own reach.
    private static PatternRow AcceptAnyPattern() => new(
        Name: Name(value: "any"),
        Kind: CellKind.Int,
        Symbols: [new PatternSymbol(
                Name: Name(value: "value"),
                Min: long.MinValue,
                Max: long.MaxValue
            )],
        Pattern: new PatternNode.Star(Item: new PatternNode.Symbol(Name: "value"))
    );
    private static LatticeTopology.Grid Row1x4() => new(
        Name: "map",
        Origin: new DocumentVector3(
            x: 0,
            y: 0,
            z: 0
        ),
        CellSize: 1,
        Width: 4,
        Depth: 1
    );
    private static WorldDefinition Document(WorldStateRow board) => Fixtures.BuildDocument() with {
        StateRaw = new WorldStateSection(
            Lattices: [Row1x4()],
            World: [board]
        ),
        PatternsRaw = [AcceptAnyPattern()],
        Rules = [],
    };
    private static WorldStateRow Find(WorldDefinition document, string row) => WorldDefinitionRows.FindStateRow(
        rows: document.State,
        name: row
    )!;
    private static bool TrySetRay(WorldDefinition definition, long value, out WorldDefinition changed, out string reason) {
        Assert.True(condition: CompiledPatterns.TryCompileAll(
            definition.Patterns,
            out var patterns,
            []
        ));

        return WorldStateTransforms.TryApply(
            definition,
            new StateTransform.SetRay(
                Direction: "E",
                From: "0",
                Pattern: "any",
                Row: "board",
                Value: value
            ),
            WorldPrincipal.World,
            1,
            "test",
            out changed,
            out reason,
            patterns
        );
    }

    [Fact]
    public void ASaturatingBoardClampsEveryAffectedCellToTheDeclaredCeiling() {
        var document = Document(board: new WorldStateRow(
            Name: Name(value: "board"),
            Kind: CellKind.Int,
            Min: 0,
            Max: 5,
            Overflow: StateOverflow.Saturate,
            Cells: [Cell(
                    key: "0",
                    value: 0
                ), Cell(
                    key: "1",
                    value: 0
                ), Cell(
                    key: "2",
                    value: 0
                ), Cell(
                    key: "3",
                    value: 0
                )],
            Domain: new StateDomain.CellsOf("map")
        ));

        Assert.True(
            condition: TrySetRay(
                definition: document,
                value: 100,
                changed: out var changed,
                reason: out var reason
            ),
            userMessage: reason
        );
        // The ray writes every cell the walk VISITS after its origin — "0" itself names only where the walk
        // starts and is never itself rewritten.
        Assert.Equal(
            expected: 0L,
            actual: Find(
                document: changed,
                row: "board"
            ).Cells!.Single(predicate: cell => (cell.Key.Value == "0")).Value
        );
        Assert.All(
            collection: Find(
                document: changed,
                row: "board"
            ).Cells!.Where(predicate: cell => (cell.Key.Value != "0")),
            action: cell => Assert.Equal(
                expected: 5L,
                actual: cell.Value
            )
        );

        // The mirror: saturating at the FLOOR.
        Assert.True(
            condition: TrySetRay(
                definition: document,
                value: -100,
                changed: out var floored,
                reason: out var flooredReason
            ),
            userMessage: flooredReason
        );
        Assert.All(
            collection: Find(
                document: floored,
                row: "board"
            ).Cells!.Where(predicate: cell => (cell.Key.Value != "0")),
            action: cell => Assert.Equal(
                expected: 0L,
                actual: cell.Value
            )
        );
    }
    [Fact]
    public void ARefusingBoardRefusesTheWholeRayByNameAndLeavesEveryCellUntouched() {
        var document = Document(board: new WorldStateRow(
            Name: Name(value: "board"),
            Kind: CellKind.Int,
            Min: 0,
            Max: 5,
            Cells: [Cell(
                    key: "0",
                    value: 1
                ), Cell(
                    key: "1",
                    value: 2
                ), Cell(
                    key: "2",
                    value: 3
                ), Cell(
                    key: "3",
                    value: 4
                )],
            Domain: new StateDomain.CellsOf("map")
        ));

        Assert.False(condition: TrySetRay(
            definition: document,
            value: 100,
            changed: out _,
            reason: out var reason
        ));
        Assert.Contains(
            actualString: reason,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "does not admit"
        );

        // Control: the same ray with an in-range value succeeds and writes every affected cell.
        Assert.True(
            condition: TrySetRay(
                definition: document,
                value: 5,
                changed: out var changed,
                reason: out var controlReason
            ),
            userMessage: controlReason
        );
        Assert.Equal(
            expected: 1L,
            actual: Find(
                document: changed,
                row: "board"
            ).Cells!.Single(predicate: cell => (cell.Key.Value == "0")).Value
        );
        Assert.All(
            collection: Find(
                document: changed,
                row: "board"
            ).Cells!.Where(predicate: cell => (cell.Key.Value != "0")),
            action: cell => Assert.Equal(
                expected: 5L,
                actual: cell.Value
            )
        );
    }
}
