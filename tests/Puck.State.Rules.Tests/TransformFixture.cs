using Puck.Assets.Documents;

namespace Puck.State.Rules.Tests;

/// <summary>The section every transform law applies against: a token domain and three ordered piles over it, a
/// numeric attribute row, a keyed score table, a 4x1 board and two boards beside it, a knowledge board with its
/// source and visibility mask, a ring, a rank slot, a mask slot, and a redrawable integer draw site.</summary>
public static class TransformFixture {
    /// <summary>Returns a validated cell name.</summary>
    /// <param name="value">The name's text.</param>
    /// <returns>The name.</returns>
    public static CellName Name(string value) => CellName.Parse(candidate: value);
    /// <summary>Builds one cell.</summary>
    /// <param name="key">The cell key.</param>
    /// <param name="value">The cell's value.</param>
    /// <returns>The cell.</returns>
    public static StateCell Cell(string key, long value) => new(
        Key: Name(value: key),
        Value: CellValue.Int(value: value)
    );
    /// <summary>Builds the fixture's section.</summary>
    /// <returns>The section.</returns>
    public static StateSection Section() => new(
        Lattices: [new LatticeTopology.Grid(
                Name: "map",
                Origin: new DocumentVector3(
                    x: 0f,
                    y: 0f,
                    z: 0f
                ),
                CellSize: 1f,
                Width: 4,
                Depth: 1
            )],
        Rows: [
            new StateRow(
                Name: Name(value: "tokens"),
                Kind: CellKind.Int,
                Capacity: 4,
                Cells: [
                    Cell(
                        key: "a",
                        value: 1L
                    ),
                    Cell(
                        key: "b",
                        value: 2L
                    ),
                    Cell(
                        key: "c",
                        value: 3L
                    ),
                ]
            ),
            new StateRow(
                Name: Name(value: "rank"),
                Kind: CellKind.Int,
                Capacity: 4,
                Domain: new StateDomain.KeysOf(Row: Name(value: "tokens")),
                Cells: [
                    Cell(
                        key: "a",
                        value: 3L
                    ),
                    Cell(
                        key: "b",
                        value: 1L
                    ),
                    Cell(
                        key: "c",
                        value: 2L
                    ),
                ]
            ),
            Pile(
                cells: [
                    Cell(
                        key: "a",
                        value: 1L
                    ),
                    Cell(
                        key: "b",
                        value: 2L
                    ),
                    Cell(
                        key: "c",
                        value: 3L
                    ),
                ],
                name: "deck"
            ),
            Pile(name: "hand"),
            new StateRow(
                Name: Name(value: "scores"),
                Kind: CellKind.Int,
                Capacity: 4,
                Cells: [
                    Cell(
                        key: "x",
                        value: 5L
                    ),
                    Cell(
                        key: "y",
                        value: 2L
                    ),
                    Cell(
                        key: "z",
                        value: 9L
                    ),
                ]
            ),
            Board(
                cells: [
                    Cell(
                        key: "0",
                        value: 5L
                    ),
                    Cell(
                        key: "1",
                        value: 1L
                    ),
                    Cell(
                        key: "2",
                        value: 1L
                    ),
                ],
                name: "board"
            ),
            Board(
                cells: [
                    Cell(
                        key: "0",
                        value: 1L
                    ),
                    Cell(
                        key: "2",
                        value: 1L
                    ),
                ],
                name: "left"
            ),
            Board(
                cells: [
                    Cell(
                        key: "1",
                        value: 1L
                    ),
                    Cell(
                        key: "2",
                        value: 1L
                    ),
                ],
                name: "right"
            ),
            Board(name: "target"),
            Board(
                cells: [
                    Cell(
                        key: "0",
                        value: 4L
                    ),
                    Cell(
                        key: "1",
                        value: 5L
                    ),
                    Cell(
                        key: "2",
                        value: 6L
                    ),
                ],
                name: "truth"
            ),
            Board(
                cells: [
                    Cell(
                        key: "0",
                        value: 1L
                    ),
                    Cell(
                        key: "2",
                        value: 1L
                    ),
                ],
                name: "sight"
            ),
            Board(name: "known") with {
                Knowledge = new StateKnowledge(
                    Mask: Name(value: "sight"),
                    Source: Name(value: "truth")
                ),
            },
            new StateRow(
                Name: Name(value: "log"),
                Kind: CellKind.Int,
                Domain: new StateDomain.Ring(
                    Capacity: 3,
                    Empty: -1L
                )
            ),
            new StateRow(
                Name: Name(value: "rankValue"),
                Kind: CellKind.Int,
                Cells: [Cell(
                        key: StateRow.SlotKey.Value,
                        value: 0L
                    )]
            ),
            new StateRow(
                Name: Name(value: "pointer"),
                Kind: CellKind.Text,
                Cells: [new StateCell(
                        Key: StateRow.SlotKey,
                        Value: CellValue.Text(value: "x")
                    )]
            ),
            new StateRow(
                Name: Name(value: "mask"),
                Kind: CellKind.Int,
                Cells: [Cell(
                        key: StateRow.SlotKey.Value,
                        value: 0b1011L
                    )]
            ),
            new StateRow(
                Name: Name(value: "coin"),
                Kind: CellKind.Int,
                Draw: new Draw(
                    Source: Name(value: "uniform"),
                    Timing: DrawTiming.Event
                )
            ),
        ]
    );
    /// <summary>Builds the fixture's declared draw sources.</summary>
    /// <returns>The sources.</returns>
    public static GeneratorRow[] Generators() => [new GeneratorRow(
            Name: Name(value: "uniform"),
            Generator: new StateGenerator(Source: GeneratorSource.StreamDraw)
        )];
    /// <summary>Builds the fixture's declared patterns.</summary>
    /// <returns>The patterns.</returns>
    public static PatternRow[] Patterns() => [new PatternRow(
            Name: Name(value: "ones"),
            Kind: CellKind.Int,
            Symbols: [new PatternSymbol(
                    Name: Name(value: "one"),
                    Min: 1L,
                    Max: 1L
                )],
            Pattern: new PatternNode.Plus(Item: new PatternNode.Symbol(Name: "one"))
        )];
    /// <summary>Builds a compile context over a section.</summary>
    /// <param name="section">The section.</param>
    /// <returns>The context.</returns>
    public static RuleCompileContext Context(StateSection section) {
        ArgumentNullException.ThrowIfNull(argument: section);

        return new RuleCompileContext(
            catalog: StateCatalog.Compile(section: section),
            generators: Generators(),
            patterns: Patterns(),
            section: section,
            simulationRateHz: 30,
            tables: null,
            vocabulary: RuleVocabulary.Core
        );
    }
    /// <summary>Builds an arena host over the fixture's section, at tick zero.</summary>
    /// <param name="section">The section.</param>
    /// <param name="context">The context whose catalog the arena shares.</param>
    /// <returns>The host.</returns>
    public static ArenaEffectHost Host(StateSection section, RuleCompileContext context) {
        ArgumentNullException.ThrowIfNull(argument: context);

        return new ArenaEffectHost(
            arena: new StateArena(
                catalog: context.Catalog,
                options: null,
                section: section,
                time: ArenaTime.Origin
            ),
            generators: Generators()
        );
    }
    /// <summary>Returns a row's catalog ordinal.</summary>
    /// <param name="context">The context.</param>
    /// <param name="name">The row name.</param>
    /// <returns>The ordinal.</returns>
    public static int Ordinal(RuleCompileContext context, string name) => RuleCompiler.ResolveRowOrdinal(
        context: context,
        name: name
    );
    /// <summary>Returns a row's cells as a comma-joined <c>key=value</c> list, in the row's own order.</summary>
    /// <param name="arena">The arena.</param>
    /// <param name="rowOrdinal">The row's catalog ordinal.</param>
    /// <returns>The listing.</returns>
    public static string Listing(StateArena arena, int rowOrdinal) {
        ArgumentNullException.ThrowIfNull(argument: arena);

        var text = new System.Text.StringBuilder();

        for (var position = 0; (position < arena.CellCount(rowOrdinal: rowOrdinal)); position++) {
            _ = arena.TryKeyAt(
                key: out var key,
                position: position,
                rowOrdinal: rowOrdinal
            );
            _ = arena.TryReadRawAt(
                position: position,
                raw: out var raw,
                rowOrdinal: rowOrdinal
            );
            if (position > 0) {
                _ = text.Append(value: ',');
            }

            _ = text
                .Append(value: arena.Catalog.Keys[key].Value)
                .Append(value: '=')
                .Append(value: raw.ToString(provider: System.Globalization.CultureInfo.InvariantCulture));
        }

        return text.ToString();
    }
    /// <summary>Returns a row's values in position order, comma joined; a ring's slots carry no minted key, so a
    /// ring reads by position rather than by key.</summary>
    /// <param name="arena">The arena.</param>
    /// <param name="rowOrdinal">The row's catalog ordinal.</param>
    /// <returns>The listing.</returns>
    public static string PositionListing(StateArena arena, int rowOrdinal) {
        ArgumentNullException.ThrowIfNull(argument: arena);

        var values = new List<long>();

        for (var position = 0; (position < arena.Layout[rowOrdinal].CellCapacity); position++) {
            if (arena.TryReadRawAt(
                position: position,
                raw: out var raw,
                rowOrdinal: rowOrdinal
            )) {
                values.Add(item: raw);
            }
        }

        return string.Join(
            separator: ',',
            values: values
        );
    }
    /// <summary>Returns a board row's dense values, comma joined.</summary>
    /// <param name="arena">The arena.</param>
    /// <param name="rowOrdinal">The board row's catalog ordinal.</param>
    /// <returns>The listing.</returns>
    public static string BoardListing(StateArena arena, int rowOrdinal) {
        ArgumentNullException.ThrowIfNull(argument: arena);

        var values = new long[arena.Layout[rowOrdinal].CellCapacity];

        _ = arena.TryReadBoard(
            rowOrdinal: rowOrdinal,
            values: values
        );

        return string.Join(
            separator: ',',
            values: values
        );
    }

    private static StateRow Board(string name, IReadOnlyList<StateCell>? cells = null) => new(
        Name: Name(value: name),
        Kind: CellKind.Int,
        Domain: new StateDomain.CellsOf(
            Empty: 0L,
            Topology: "map"
        ),
        Cells: cells
    );
    private static StateRow Pile(string name, IReadOnlyList<StateCell>? cells = null) => new(
        Name: Name(value: name),
        Kind: CellKind.Int,
        Capacity: 4,
        Domain: new StateDomain.KeysOf(
            Ordered: true,
            Row: Name(value: "tokens")
        ),
        Cells: cells
    );
}
