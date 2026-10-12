using Xunit;

namespace Puck.World.Schema.Tests;

/// <summary>THE LAW: an inverse board can store every value its codes row can produce, including its empty value;
/// symbolic domains narrow that proof rather than bypassing it. The shipped world tree is enumerated mechanically
/// below so every inverse declaration remains covered as worlds are added.</summary>
public sealed class DerivedBoardDomainLawTests {
    private static CellName Name(string value) => CellName.Parse(candidate: value);

    [Fact]
    public void CodeRangeMustBeContainedByBoardRange() {
        var board = new StateRow(
            Name: Name(value: "board"),
            Kind: CellKind.Int,
            Min: 0L,
            Max: 1L,
            Domain: new StateDomain.CellsOf(Topology: "grid")
        );
        var codes = new StateRow(
            Name: Name(value: "codes"),
            Kind: CellKind.Int,
            Min: 0L,
            Max: 2L,
            Domain: new StateDomain.Keys()
        );

        Assert.False(condition: StateRow.TryProveDerivedDomain(board: board, boardSymbols: null, codeSymbols: null, codes: codes, reason: out var reason));
        Assert.Contains(actualString: reason, comparisonType: StringComparison.Ordinal, expectedSubstring: "can admit 0..2");

        var unboundedBoard = board with { Min = null, Max = null };
        var unboundedCodes = codes with { Min = null, Max = null };

        Assert.True(condition: StateRow.TryProveDerivedDomain(board: unboundedBoard, boardSymbols: null, codeSymbols: null, codes: unboundedCodes, reason: out reason), userMessage: reason);

        var boolBoard = unboundedBoard with { Kind = CellKind.Bool };
        var boolCodes = unboundedCodes with { Min = 0L, Max = 1L };

        Assert.True(condition: StateRow.TryProveDerivedDomain(board: boolBoard, boardSymbols: null, codeSymbols: null, codes: boolCodes, reason: out reason), userMessage: reason);
        Assert.False(condition: StateRow.TryProveDerivedDomain(board: boolBoard, boardSymbols: null, codeSymbols: null, codes: unboundedCodes, reason: out reason));
        Assert.Contains(actualString: reason, comparisonType: StringComparison.Ordinal, expectedSubstring: "can admit");
    }
    [Fact]
    public void EmptyAndEnumIntersectionsAreAdmittedAsTheirEffectiveDomains() {
        var board = new StateRow(
            Name: Name(value: "board"),
            Kind: CellKind.Int,
            Min: 0L,
            Max: 2L,
            Enum: Name(value: "boardValues"),
            Domain: new StateDomain.CellsOf(Topology: "grid")
        );
        var codes = new StateRow(
            Name: Name(value: "codes"),
            Kind: CellKind.Int,
            Min: -4L,
            Max: 5L,
            Enum: Name(value: "codeValues"),
            Domain: new StateDomain.Keys()
        );
        var boardValues = new StateEnum(Name(value: "boardValues"), [Name(value: "empty"), Name(value: "piece")]);
        var codeValues = new StateEnum(Name(value: "codeValues"), [Name(value: "piece"), Name(value: "other")]);

        Assert.True(condition: StateRow.TryProveDerivedDomain(board: board, boardSymbols: boardValues, codeSymbols: codeValues, codes: codes, reason: out var reason), userMessage: reason);

        var boardWithOneValue = board with { Enum = Name(value: "boardOne") };
        var boardOne = new StateEnum(Name(value: "boardOne"), [Name(value: "empty")]);
        var twoCodeValues = codes with { Min = 0L, Max = 1L };

        Assert.False(condition: StateRow.TryProveDerivedDomain(board: boardWithOneValue, boardSymbols: boardOne, codeSymbols: codeValues, codes: twoCodeValues, reason: out reason));
        Assert.Contains(actualString: reason, comparisonType: StringComparison.Ordinal, expectedSubstring: "admits 0..0");

        var emptyOutside = board with { Min = 0L, Max = 1L };
        var noEmpty = emptyOutside with { Domain = new StateDomain.CellsOf(Empty: 2L, Topology: "grid") };

        Assert.False(condition: StateRow.TryProveDerivedDomain(noEmpty, boardValues, codes with { Min = 0L, Max = 1L }, codeValues, out reason));
        Assert.Contains(actualString: reason, comparisonType: StringComparison.Ordinal, expectedSubstring: "empty value 2");
    }
}
