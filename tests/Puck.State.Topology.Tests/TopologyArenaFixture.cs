using Puck.Assets.Documents;

namespace Puck.State.Topology.Tests;

/// <summary>The section the arena-facing topology and pattern law suites store: a token domain, an ordered pile
/// over it, an attribute row keyed by the same tokens, a lattice board, a draw site, and a four-member family of
/// slots.</summary>
public static class TopologyArenaFixture {
    /// <summary>The catalog ordinal of the attribute row.</summary>
    public const int Attribute = 2;
    /// <summary>The catalog ordinal of the lattice board.</summary>
    public const int Board = 3;
    /// <summary>The catalog ordinal of the draw site.</summary>
    public const int Deal = 4;
    /// <summary>The catalog ordinal of the ring row.</summary>
    public const int History = 5;
    /// <summary>The catalog ordinal of the ordered pile.</summary>
    public const int Pile = 1;
    /// <summary>The catalog ordinal of the family's first member.</summary>
    public const int Slots = 6;
    /// <summary>The catalog ordinal of the token domain.</summary>
    public const int Tokens = 0;
    /// <summary>How many members the family holds.</summary>
    public const int FamilySize = 4;
    /// <summary>How many cells the board lays out.</summary>
    public const int BoardCells = 16;

    /// <summary>Returns a validated cell name.</summary>
    /// <param name="value">The name's text.</param>
    /// <returns>The name.</returns>
    public static CellName Name(string value) => CellName.Parse(candidate: value);
    /// <summary>Builds the fixture's section.</summary>
    /// <returns>The section.</returns>
    public static StateSection Section() {
        var tokens = new List<StateCell>();

        for (var index = 0; (index < 16); index++) {
            tokens.Add(item: new StateCell(
                Key: Name(value: $"t{index}"),
                Value: CellValue.Int(value: index)
            ));
        }

        var rows = new List<StateRow> {
            new(
                Name: Name(value: "tokens"),
                Kind: CellKind.Int,
                Capacity: 16,
                Cells: tokens
            ),
            new(
                Name: Name(value: "pile"),
                Kind: CellKind.Int,
                Capacity: 16,
                Domain: new StateDomain.KeysOf(
                    Ordered: true,
                    Row: Name(value: "tokens")
                )
            ),
            new(
                Name: Name(value: "attribute"),
                Kind: CellKind.Int,
                Capacity: 16,
                Cells: tokens
            ),
            new(
                Name: Name(value: "board"),
                Kind: CellKind.Int,
                Domain: new StateDomain.CellsOf(
                    Empty: -1L,
                    Topology: "map"
                )
            ),
            new(
                Name: Name(value: "deal"),
                Kind: CellKind.Int,
                Draw: new Draw(Generator: Bag())
            ),
            new(
                Name: Name(value: "history"),
                Kind: CellKind.Int,
                Domain: new StateDomain.Ring(
                    Capacity: 4,
                    Empty: -1L
                )
            ),
        };

        for (var member = 0; (member < FamilySize); member++) {
            rows.Add(item: new StateRow(
                Name: Name(value: $"slot{member}"),
                Kind: CellKind.Int,
                Cells: [new StateCell(
                        Key: StateRow.SlotKey,
                        Value: CellValue.Int(value: member)
                    )]
            ));
        }

        return new StateSection(
            Rows: rows,
            Families: [new StateFamily(
                    Name: Name(value: "slot"),
                    Size: FamilySize
                )],
            Lattices: [new LatticeTopology.Grid(
                    Name: "map",
                    Origin: new DocumentVector3(
                        x: 0f,
                        y: 0f,
                        z: 0f
                    ),
                    CellSize: 1f,
                    Width: 4,
                    Depth: 4
                )]
        );
    }
    /// <summary>An exhausting weighted numeric source, so a site drawing from it persists one mask.</summary>
    /// <returns>The generator.</returns>
    public static StateGenerator Bag() => new(
        Mode: GeneratorMode.WithoutReplacement,
        Source: GeneratorSource.WeightedNumeric,
        Weighted: [
            new GeneratorWeightedNumeric(
                Value: 1L,
                Weight: 1UL
            ),
            new GeneratorWeightedNumeric(
                Value: 2L,
                Weight: 1UL
            ),
            new GeneratorWeightedNumeric(
                Value: 3L,
                Weight: 1UL
            ),
        ]
    );
    /// <summary>Builds a catalog and an arena over the fixture's section.</summary>
    /// <returns>The catalog and its arena.</returns>
    public static (StateCatalog Catalog, StateArena Arena) Build() {
        var section = Section();
        var catalog = StateCatalog.Compile(section: section);

        return (catalog, new StateArena(
            catalog: catalog,
            options: null,
            section: section,
            time: ArenaTime.Origin
        ));
    }
}
