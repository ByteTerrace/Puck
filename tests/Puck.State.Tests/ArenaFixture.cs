using Puck.Assets.Documents;
using Puck.Testing;
using Xunit;

namespace Puck.State.Tests;

/// <summary>The one section the arena law suites store: a slot, a token and code pair feeding a derived board, two
/// ordered piles, a ring, a text slot, a vector slot, a host-owned lattice, an evicting table, a draw site, an
/// envelope-bounded table, a symbolic table, a symbolic ring, a bounded third pile, an accumulating slot, a phase
/// row, and two participant slot lanes.</summary>
public static class ArenaFixture {
    /// <summary>The catalog ordinal of the evicting keyed table.</summary>
    public const int Bag = 10;
    /// <summary>The catalog ordinal of the derived board.</summary>
    public const int Board = 3;
    /// <summary>The catalog ordinal of the accumulating slot.</summary>
    public const int Clock = 16;
    /// <summary>The catalog ordinal of the codes row.</summary>
    public const int Codes = 2;
    /// <summary>The catalog ordinal of the participant counter slot.</summary>
    public const int Coins = 18;
    /// <summary>The catalog ordinal of the draw site.</summary>
    public const int Deal = 11;
    /// <summary>The catalog ordinal of the first ordered pile.</summary>
    public const int Deck = 4;
    /// <summary>The catalog ordinal of the vector slot.</summary>
    public const int Embed = 8;
    /// <summary>The catalog ordinal of the host-owned lattice.</summary>
    public const int Field = 9;
    /// <summary>The catalog ordinal of the second ordered pile.</summary>
    public const int Hand = 5;
    /// <summary>The catalog ordinal of the ring row.</summary>
    public const int History = 6;
    /// <summary>The catalog ordinal of the text slot.</summary>
    public const int Label = 7;
    /// <summary>The catalog ordinal of the numeric slot.</summary>
    public const int Score = 0;
    /// <summary>The catalog ordinal of the symbolic ring.</summary>
    public const int Phases = 14;
    /// <summary>The catalog ordinal of the envelope-bounded keyed table.</summary>
    public const int Purse = 12;
    /// <summary>The catalog ordinal of the symbolic keyed table.</summary>
    public const int Suits = 13;
    /// <summary>The catalog ordinal of the participant timer slot.</summary>
    public const int Timer = 19;
    /// <summary>The catalog ordinal of the tokens row.</summary>
    public const int Tokens = 1;
    /// <summary>The catalog ordinal of the phase row.</summary>
    public const int Turn = 17;
    /// <summary>The catalog ordinal of the envelope-bounded ordered pile.</summary>
    public const int Vault = 15;

    /// <summary>Builds an admissible unit vector pointing along one axis.</summary>
    /// <param name="axis">Which component carries the whole length.</param>
    /// <param name="dimensions">How many components the vector carries.</param>
    /// <returns>The vector.</returns>
    public static StateVector Unit(int axis, int dimensions = 8) {
        var components = new sbyte[dimensions];

        components[axis] = 127;

        Assert.True(
            condition: StateVector.TryCreate(
                components: components,
                error: out var error,
                vector: out var vector
            ),
            userMessage: error
        );

        return vector!;
    }
    /// <summary>Returns a validated cell name.</summary>
    /// <param name="value">The name's text.</param>
    /// <returns>The name.</returns>
    public static CellName Name(string value) => CellName.Parse(candidate: value);
    /// <summary>Builds the fixture's section.</summary>
    /// <returns>The section.</returns>
    public static StateSection Section() => new(
        Rows: [
            new StateRow(
                Name: Name(value: "score"),
                Kind: CellKind.Int,
                Max: 100L,
                Min: 0L,
                Cells: [new StateCell(
                        Key: StateRow.SlotKey,
                        Value: CellValue.Int(value: 5L)
                    )]
            ),
            new StateRow(
                Name: Name(value: "tokens"),
                Kind: CellKind.Int,
                Capacity: 4,
                Cells: [
                    new StateCell(
                        Key: Name(value: "a"),
                        Value: CellValue.Int(value: 0L)
                    ),
                    new StateCell(
                        Key: Name(value: "b"),
                        Value: CellValue.Int(value: 2L)
                    ),
                ]
            ),
            new StateRow(
                Name: Name(value: "codes"),
                Kind: CellKind.Int,
                Capacity: 4,
                Cells: [
                    new StateCell(
                        Key: Name(value: "a"),
                        Value: CellValue.Int(value: 7L)
                    ),
                    new StateCell(
                        Key: Name(value: "b"),
                        Value: CellValue.Int(value: 9L)
                    ),
                ]
            ),
            new StateRow(
                Name: Name(value: "board"),
                Kind: CellKind.Int,
                Domain: new StateDomain.CellsOf(
                    Empty: -1L,
                    Topology: "map"
                ),
                Inverse: new StateInverse(
                    Codes: Name(value: "codes"),
                    Tokens: Name(value: "tokens")
                )
            ),
            new StateRow(
                Name: Name(value: "deck"),
                Kind: CellKind.Int,
                Capacity: 4,
                Domain: new StateDomain.KeysOf(
                    Ordered: true,
                    Row: Name(value: "tokens")
                ),
                Cells: [
                    new StateCell(
                        Key: Name(value: "a"),
                        Value: CellValue.Int(value: 11L)
                    ),
                    new StateCell(
                        Key: Name(value: "b"),
                        Value: CellValue.Int(value: 22L)
                    ),
                ]
            ),
            new StateRow(
                Name: Name(value: "hand"),
                Kind: CellKind.Int,
                Capacity: 4,
                Domain: new StateDomain.KeysOf(
                    Ordered: true,
                    Row: Name(value: "tokens")
                )
            ),
            new StateRow(
                Name: Name(value: "history"),
                Kind: CellKind.Int,
                Domain: new StateDomain.Ring(
                    Capacity: 3,
                    Empty: -1L
                )
            ),
            new StateRow(
                Name: Name(value: "label"),
                Kind: CellKind.Text,
                Cells: [new StateCell(
                        Key: StateRow.SlotKey,
                        Value: CellValue.Text(value: "hi")
                    )]
            ),
            new StateRow(
                Name: Name(value: "embed"),
                Kind: CellKind.Vector,
                Space: "space8",
                Cells: [new StateCell(
                        Key: StateRow.SlotKey,
                        Value: CellValue.Vector(components: Unit(axis: 0).Memory)
                    )]
            ),
            new StateRow(
                Name: Name(value: "field"),
                Kind: CellKind.Fixed,
                Domain: new StateDomain.CellsOf(Topology: "map")
            ) {
                HostOwned = true,
            },
            new StateRow(
                Name: Name(value: "bag"),
                Kind: CellKind.Int,
                Capacity: 2,
                Evicts: true
            ),
            new StateRow(
                Name: Name(value: "deal"),
                Kind: CellKind.Int,
                Draw: new Draw(Generator: Walk())
            ),
            new StateRow(
                Name: Name(value: "purse"),
                Kind: CellKind.Int,
                Capacity: 4,
                Max: 10L,
                Min: 0L
            ),
            new StateRow(
                Name: Name(value: "suits"),
                Kind: CellKind.Int,
                Capacity: 4,
                Enum: Name(value: "suit")
            ),
            new StateRow(
                Name: Name(value: "phases"),
                Kind: CellKind.Int,
                Domain: new StateDomain.Ring(
                    Capacity: 3,
                    Empty: -1L
                ),
                Enum: Name(value: "suit")
            ),
            new StateRow(
                Name: Name(value: "vault"),
                Kind: CellKind.Int,
                Capacity: 4,
                Domain: new StateDomain.KeysOf(
                    Ordered: true,
                    Row: Name(value: "tokens")
                ),
                Max: 5L,
                Min: 0L
            ),
            new StateRow(
                Name: Name(value: "clock"),
                Kind: CellKind.Int,
                Cells: [new StateCell(
                        Key: StateRow.SlotKey,
                        Value: CellValue.Int(value: 10L)
                    )],
                Advance: new StateAdvance(
                    PerSecondDenominator: 1L,
                    PerSecondNumerator: 60L
                )
            ),
            new StateRow(
                Name: Name(value: "turn"),
                Kind: CellKind.Int,
                Capacity: 1,
                Phase: new StatePhase(Sequence: 0L)
            ),
        ],
        Enums: [new StateEnum(
                Name: Name(value: "suit"),
                Members: [
                    Name(value: "clubs"),
                    Name(value: "diamonds"),
                    Name(value: "hearts"),
                    Name(value: "spades"),
                ]
            )],
        Lattices: [new LatticeTopology.Grid(
                Name: "map",
                Origin: new DocumentVector3(
                    x: 0f,
                    y: 0f,
                    z: 0f
                ),
                CellSize: 1f,
                Width: 2,
                Depth: 2
            )],
        ParticipantSlots: [
            new StateSlot(
                Name: "coins",
                Role: StateParticipantRole.Counter
            ),
            new StateSlot(
                Name: "timer",
                Role: StateParticipantRole.Timer
            ),
        ],
        Spaces: [new StateSpace(
                name: Name(value: "space8"),
                model: "model",
                revision: "r1",
                dimensions: 8
            )]
    );
    /// <summary>An exhausting two-context Markov walk, so a site drawing from it persists two masks.</summary>
    /// <returns>The generator.</returns>
    public static StateGenerator Walk() => new(
        Bound: 4,
        Contexts: [
            new GeneratorContext(
                Alternatives: [new GeneratorAlternative(
                    Next: Name(value: "b"),
                    Token: "x",
                    Weight: 1UL
                )],
                Key: Name(value: "a")
            ),
            new GeneratorContext(Key: Name(value: "b")),
        ],
        Mode: GeneratorMode.WithoutReplacement,
        Source: GeneratorSource.Markov,
        Start: Name(value: "a")
    );
    /// <summary>Builds a catalog and an arena over the fixture's section.</summary>
    /// <returns>The catalog and its arena.</returns>
    public static (StateCatalog Catalog, StateArena Arena) Build() => TopologyArenaFixture.Build(section: Section());
    /// <summary>Resolves an interned cell key by name.</summary>
    /// <param name="catalog">The catalog that interned it.</param>
    /// <param name="value">The key's text.</param>
    /// <returns>The interned key.</returns>
    public static CellKey Key(StateCatalog catalog, string value) => catalog.Keys.Intern(name: Name(value: value));
    /// <summary>Resolves the reserved slot key.</summary>
    /// <param name="catalog">The catalog that interned it.</param>
    /// <returns>The interned slot key.</returns>
    public static CellKey SlotKey(StateCatalog catalog) => catalog.Keys.Intern(name: StateRow.SlotKey);
}
