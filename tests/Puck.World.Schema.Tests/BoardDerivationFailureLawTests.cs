using Puck.Assets.Documents;
using Xunit;

namespace Puck.World.Schema.Tests;

/// <summary>THE LAW: a derived board whose derivation cannot be computed is a validation line naming why, never a
/// check the walk skips. CONTROL: the same board over a section the arena does load is checked against its
/// derivation as usual.</summary>
public sealed class BoardDerivationFailureLawTests {
    private const string BoardRow = "board";
    private const string CodeRow = "pieceCode";
    private const string TokenRow = "pieceToken";

    private static CellName Name(string value) => CellName.Parse(candidate: value);
    // A tokens/codes pair over one keyed piece and the board they derive. `tokenValue` is the token's authored cell:
    // inside the row's envelope it loads, outside it the arena's import door refuses the whole section.
    private static WorldDefinition Document(long tokenValue) => new(
        StateRaw: new WorldStateSection(World: [
            new WorldStateRow(
            Name: Name(value: TokenRow),
            Kind: CellKind.Int,
            Domain: new StateDomain.Keys(),
            Capacity: 4,
            Max: 8L,
            Min: 0L,
            Cells: [new StateCell(
                    Key: Name(value: "p1"),
                    Value: CellValue.Int(value: tokenValue)
                )]
        ),
            new WorldStateRow(
            Name: Name(value: CodeRow),
            Kind: CellKind.Int,
            Domain: new StateDomain.Keys(),
            Capacity: 4,
            Cells: [new StateCell(
                    Key: Name(value: "p1"),
                    Value: CellValue.Int(value: 1L)
                )]
        ),
            new WorldStateRow(
            Name: Name(value: BoardRow),
            Kind: CellKind.Int,
            Domain: new StateDomain.CellsOf(Topology: "grid"),
            Inverse: new StateInverse(
                Codes: Name(value: CodeRow),
                Tokens: Name(value: TokenRow)
            ),
            Cells: [new StateCell(
                    Key: Name(value: "0"),
                    Value: CellValue.Int(value: 0L)
                )]
        ),
        ], Lattices: [new LatticeTopology.Grid(
            CellSize: 1f,
            Depth: 2,
            Name: "grid",
            Origin: new DocumentVector3(
                x: 0f,
                y: 0f,
                z: 0f
            ),
            Width: 2
        )])
    );
    private static List<string> Validate(WorldDefinition definition) {
        var errors = new List<string>();

        _ = WorldDefinitionValidator.TryValidateLocally(
            definition,
            errors,
            out _
        );

        return errors;
    }

    [Fact]
    public void ADerivationTheArenaCannotComputeIsNamedRatherThanSkipped() {
        var refused = Validate(definition: Document(tokenValue: 99L));

        Assert.Contains(
            collection: refused,
            filter: static line => line.Contains(
                comparisonType: StringComparison.Ordinal,
                value: $"state row '{BoardRow}': its inverse's derivation could not be computed"
            )
        );
        // The control: the same board whose token sits inside its envelope reaches the derivation comparison, so
        // the line above is about the derivation and not about the board's shape.
        Assert.DoesNotContain(
            collection: Validate(definition: Document(tokenValue: 1L)),
            filter: static line => line.Contains(
                comparisonType: StringComparison.Ordinal,
                value: "could not be computed"
            )
        );
    }
}
