using Xunit;

namespace Puck.State.Tests;

/// <summary>CONTRACT UNDER TEST: exporting an arena and loading the result into a fresh arena over the same catalog
/// reproduces every field of every <see cref="StateRow"/> and <see cref="StateCell"/>, a construction is that same
/// load of the section's own rows, and a load refuses a row whose shape, kind, vector dimensions, or runtime-state
/// fields disagree with the layout — by name, never silently, and without interning a key.</summary>
public sealed class ArenaSerializationLawTests {
    [Fact]
    public void ExportThenImportIsTheIdentityOverEveryRowAndCellField() {
        var section = ArenaFixture.Section();
        var catalog = StateCatalog.Compile(section: section);
        var arena = new StateArena(
            catalog: catalog,
            options: null,
            section: section,
            time: ArenaTime.Origin
        );

        Populate(
            arena: arena,
            catalog: catalog
        );

        var exported = arena.ToRows();
        var loaded = new StateArena(
            catalog: catalog,
            options: null,
            section: section,
            time: ArenaTime.Origin
        );

        Assert.True(condition: loaded.TryLoad(
            reason: out var reason,
            rows: exported,
            time: ArenaTime.Origin
        ), userMessage: reason);
        AssertSameRows(
            actual: loaded.ToRows(),
            expected: exported
        );
        Assert.Equal(
            actual: loaded.ComputeHash(),
            expected: arena.ComputeHash()
        );
    }
    [Fact]
    public void ALoadRefusesAKindDisagreementByName() {
        var section = ArenaFixture.Section();
        var catalog = StateCatalog.Compile(section: section);
        var arena = new StateArena(
            catalog: catalog,
            options: null,
            section: section,
            time: ArenaTime.Origin
        );
        var before = arena.ComputeHash();

        Assert.False(condition: arena.TryLoad(
            reason: out var reason,
            rows: [new StateRow(
                    Name: ArenaFixture.Name(value: "score"),
                    Kind: CellKind.Text
                )],
            time: ArenaTime.Origin
        ));
        Assert.Contains(
            actualString: reason,
            expectedSubstring: "score"
        );
        Assert.Equal(
            actual: arena.ComputeHash(),
            expected: before
        );
    }
    [Fact]
    public void ALoadRefusesAShapeDisagreementByName() {
        var section = ArenaFixture.Section();
        var catalog = StateCatalog.Compile(section: section);
        var arena = new StateArena(
            catalog: catalog,
            options: null,
            section: section,
            time: ArenaTime.Origin
        );

        Assert.False(condition: arena.TryLoad(
            reason: out var reason,
            rows: [new StateRow(
                    Name: ArenaFixture.Name(value: "score"),
                    Kind: CellKind.Int,
                    Capacity: 4
                )],
            time: ArenaTime.Origin
        ));
        Assert.Contains(
            actualString: reason,
            expectedSubstring: "score"
        );
    }
    [Fact]
    public void ALoadRefusesAVectorDimensionDisagreementByName() {
        var section = ArenaFixture.Section();
        var catalog = StateCatalog.Compile(section: section);
        var arena = new StateArena(
            catalog: catalog,
            options: null,
            section: section,
            time: ArenaTime.Origin
        );

        Assert.False(condition: arena.TryLoad(
            reason: out var reason,
            rows: [new StateRow(
                    Name: ArenaFixture.Name(value: "embed"),
                    Kind: CellKind.Vector,
                    Cells: [new StateCell(
                            Key: StateRow.SlotKey,
                            Value: CellValue.Vector(components: ArenaFixture.Unit(
                                axis: 0,
                                dimensions: 16
                            ).Memory)
                        )],
                    Space: "space8"
                )],
            time: ArenaTime.Origin
        ));
        Assert.Contains(
            actualString: reason,
            expectedSubstring: "embed"
        );
    }
    [Fact]
    public void ALoadRefusesACellNoPositionOfTheRowAddressesByName() {
        var section = ArenaFixture.Section();
        var catalog = StateCatalog.Compile(section: section);
        var arena = new StateArena(
            catalog: catalog,
            options: null,
            section: section,
            time: ArenaTime.Origin
        );

        Assert.False(condition: arena.TryLoad(
            reason: out var reason,
            rows: [new StateRow(
                    Name: ArenaFixture.Name(value: "board"),
                    Kind: CellKind.Int,
                    Cells: [new StateCell(
                            Key: ArenaFixture.Name(value: "elsewhere"),
                            Value: CellValue.Int(value: 1L)
                        )],
                    Domain: new StateDomain.CellsOf(
                        Empty: -1L,
                        Topology: "map"
                    ),
                    Inverse: new StateInverse(
                        Codes: ArenaFixture.Name(value: "codes"),
                        Tokens: ArenaFixture.Name(value: "tokens")
                    )
                )],
            time: ArenaTime.Origin
        ));
        Assert.Contains(
            actualString: reason,
            expectedSubstring: "elsewhere"
        );
    }
    [Fact]
    public void ALoadRefusesAValueTheRowsEnvelopeWouldNotAdmitByName() {
        var section = ArenaFixture.Section();
        var catalog = StateCatalog.Compile(section: section);
        var arena = new StateArena(
            catalog: catalog,
            options: null,
            section: section,
            time: ArenaTime.Origin
        );
        var before = arena.ComputeHash();

        Assert.False(condition: arena.TryLoad(
            reason: out var reason,
            rows: [new StateRow(
                    Name: ArenaFixture.Name(value: "purse"),
                    Kind: CellKind.Int,
                    Capacity: 4,
                    Cells: [new StateCell(
                            Key: ArenaFixture.Name(value: "gold"),
                            Value: CellValue.Int(value: 99L)
                        )],
                    Max: 10L,
                    Min: 0L
                )],
            time: ArenaTime.Origin
        ));
        Assert.Contains(
            actualString: reason,
            expectedSubstring: "purse"
        );
        Assert.Equal(
            actual: arena.ComputeHash(),
            expected: before
        );
    }
    [Fact]
    public void ALoadRefusesARowTheCatalogDoesNotDeclareByName() {
        var section = ArenaFixture.Section();
        var catalog = StateCatalog.Compile(section: section);
        var arena = new StateArena(
            catalog: catalog,
            options: null,
            section: section,
            time: ArenaTime.Origin
        );

        Assert.False(condition: arena.TryLoad(
            reason: out var reason,
            rows: [new StateRow(
                    Name: ArenaFixture.Name(value: "nowhere"),
                    Kind: CellKind.Int
                )],
            time: ArenaTime.Origin
        ));
        Assert.Contains(
            actualString: reason,
            expectedSubstring: "nowhere"
        );
    }
    [Fact]
    public void ASectionCarryingEveryRuntimeStateFieldExportsAndImportsAsItself() {
        var section = RuntimeStateSection();
        var catalog = StateCatalog.Compile(section: section);
        var arena = new StateArena(
            catalog: catalog,
            options: null,
            section: section,
            time: ArenaTime.Origin
        );
        var exported = arena.ToRows();
        var loaded = new StateArena(
            catalog: catalog,
            options: null,
            section: section,
            time: ArenaTime.Origin
        );

        Assert.True(condition: loaded.TryLoad(
            reason: out var reason,
            rows: exported,
            time: ArenaTime.Origin
        ), userMessage: reason);
        AssertSameRows(
            actual: loaded.ToRows(),
            expected: exported
        );
        Assert.Equal(
            actual: loaded.ComputeHash(),
            expected: arena.ComputeHash()
        );

        var authored = section.Rows![0];
        var round = exported[0];

        Assert.Equal(
            actual: round.HistoryCursor,
            expected: 0L
        );
        AssertSameCell(
            actual: round.Cells![0],
            expected: authored.Cells![0]
        );
    }
    [Fact]
    public void AnAuthoredValueOutsideTheRowsEnvelopeRefusesAtConstruction() {
        var section = ArenaFixture.Section();
        var rows = new List<StateRow>(collection: section.Rows!);

        rows[ArenaFixture.Purse] = (rows[ArenaFixture.Purse] with {
            Cells = [new StateCell(
                Key: ArenaFixture.Name(value: "gold"),
                Value: CellValue.Int(value: 99L)
            )],
        });

        var authored = (section with { Rows = rows });
        var catalog = StateCatalog.Compile(section: authored);
        var refusal = Assert.Throws<ArgumentException>(testCode: () => new StateArena(
            catalog: catalog,
            options: null,
            section: authored,
            time: ArenaTime.Origin
        ));

        Assert.Contains(
            actualString: refusal.Message,
            expectedSubstring: "purse"
        );
        Assert.Contains(
            actualString: refusal.Message,
            expectedSubstring: "gold"
        );
        Assert.False(condition: StateArena.TryCreate(
            arena: out var refused,
            catalog: catalog,
            options: null,
            reason: out var reason,
            section: authored,
            time: ArenaTime.Origin
        ));
        Assert.Null(@object: refused);
        Assert.Contains(
            actualString: reason,
            expectedSubstring: "purse"
        );
    }
    [Fact]
    public void ARefusedLoadInternsNothing() {
        var section = ArenaFixture.Section();
        var catalog = StateCatalog.Compile(section: section);
        var arena = new StateArena(
            catalog: catalog,
            options: null,
            section: section,
            time: ArenaTime.Origin
        );
        var interned = catalog.Keys.Count;

        Assert.False(condition: arena.TryLoad(
            reason: out var reason,
            rows: [
                new StateRow(
                    Name: ArenaFixture.Name(value: "tokens"),
                    Kind: CellKind.Int,
                    Capacity: 4,
                    Cells: [new StateCell(
                            Key: ArenaFixture.Name(value: "unreachable"),
                            Value: CellValue.Int(value: 1L)
                        )]
                ),
                new StateRow(
                    Name: ArenaFixture.Name(value: "purse"),
                    Kind: CellKind.Int,
                    Capacity: 4,
                    Cells: [new StateCell(
                            Key: ArenaFixture.Name(value: "gold"),
                            Value: CellValue.Int(value: 99L)
                        )],
                    Max: 10L,
                    Min: 0L
                ),
            ],
            time: ArenaTime.Origin
        ));
        Assert.Contains(
            actualString: reason,
            expectedSubstring: "purse"
        );
        Assert.Equal(
            actual: catalog.Keys.Count,
            expected: interned
        );
        Assert.False(condition: catalog.Keys.TryResolve(
            key: out _,
            name: ArenaFixture.Name(value: "unreachable")
        ));
    }
    [InlineData("historyCursor", "score")]
    [InlineData("negativeHistoryCursor", "history")]
    [InlineData("drawCursor", "score")]
    [InlineData("negativeDrawCursor", "deal")]
    [InlineData("phaseSequence", "score")]
    [InlineData("negativePhaseSequence", "turn")]
    [InlineData("drawnMasks", "deal")]
    [InlineData("advance", "score")]
    [InlineData("cellCycle", "tokens")]
    [Theory]
    public void ALoadRefusesARuntimeStateFieldItsRowDoesNotCarryByName(string field, string row) {
        var section = ArenaFixture.Section();
        var catalog = StateCatalog.Compile(section: section);
        var arena = new StateArena(
            catalog: catalog,
            options: null,
            section: section,
            time: ArenaTime.Origin
        );
        var before = arena.ComputeHash();

        Assert.False(condition: arena.TryLoad(
            reason: out var reason,
            rows: [Malformed(
                    field: field,
                    section: section
                )],
            time: ArenaTime.Origin
        ));
        Assert.Contains(
            actualString: reason,
            expectedSubstring: row
        );
        Assert.Equal(
            actual: arena.ComputeHash(),
            expected: before
        );
    }

    // The declared row, given a runtime-state field its declaration does not carry or a value no door admits.
    private static StateRow Malformed(StateSection section, string field) {
        var rows = section.Rows!;

        return (field switch {
            "historyCursor" => (rows[ArenaFixture.Score] with { HistoryCursor = 1L }),
            "advance" => (rows[ArenaFixture.Score] with {
                Advance = new StateAdvance(
                    PerSecondDenominator: 1L,
                    PerSecondNumerator: 1L
                ),
            }),
            "cellCycle" => (rows[ArenaFixture.Tokens] with {
                Cells = [.. rows[ArenaFixture.Tokens].Cells!.Select(selector: static cell => ((cell.Key.Value == "a")
                    ? (cell with { Cycle = new StateCycle(TicksPerStep: 4L) })
                    : cell
                ))],
            }),
            "negativeHistoryCursor" => (rows[ArenaFixture.History] with { HistoryCursor = -1L }),
            "drawCursor" => (rows[ArenaFixture.Score] with { DrawCursor = 1L }),
            "negativeDrawCursor" => (rows[ArenaFixture.Deal] with { DrawCursor = -1L }),
            "phaseSequence" => (rows[ArenaFixture.Score] with { Phase = new StatePhase(Sequence: 1L) }),
            "negativePhaseSequence" => (rows[ArenaFixture.Turn] with { Phase = new StatePhase(Sequence: -1L) }),
            _ => (rows[ArenaFixture.Deal] with {
                DrawnMasks = [
                    default,
                    default,
                    new ClosedBitset256(
                        Word0: 1UL,
                        Word1: 0UL,
                        Word2: 0UL,
                        Word3: 0UL
                    ),
                ],
            }),
        });
    }
    // One of every shape that carries runtime state, with every per-row and per-cell field authored.
    private static StateSection RuntimeStateSection() => new(
        Rows: [
            new StateRow(
                Name: ArenaFixture.Name(value: "ledger"),
                Kind: CellKind.Int,
                Capacity: 4,
                Cells: [new StateCell(
                        Key: ArenaFixture.Name(value: "a"),
                        Value: CellValue.Int(value: 3L),
                        Provenance: "issuer",
                        Behavior: StateCellBehavior.None,
                        Clock: new StateCellClock(
                            EpochEngineTick: 4L,
                            EpochTick: 3L,
                            SubstepTicks: 7L,
                            V0: 6L,
                            Y0: 5L
                        ),
                        Visibility: new StateVisibility(
                            Hidden: HiddenCells.Count,
                            Readers: ["p1"],
                            ReadersFrom: "ledger"
                        ),
                        Observation: new StateObservation(
                            Tick: 12L,
                            Visible: true
                        )
                    )]
            ),
            new StateRow(
                Name: ArenaFixture.Name(value: "trail"),
                Kind: CellKind.Int,
                Cells: [new StateCell(
                        Key: ArenaFixture.Name(value: "1"),
                        Value: CellValue.Int(value: 8L)
                    )],
                Domain: new StateDomain.Ring(
                    Capacity: 3,
                    Empty: -1L
                ),
                HistoryCursor: 5L
            ),
            new StateRow(
                Name: ArenaFixture.Name(value: "spin"),
                Kind: CellKind.Int,
                Draw: new Draw(Source: ArenaFixture.Name(value: "pool")),
                DrawCursor: 9L,
                DrawnMasks: [new ClosedBitset256(
                        Word0: 3UL,
                        Word1: 0UL,
                        Word2: 0UL,
                        Word3: 8UL
                    )]
            ),
            new StateRow(
                Name: ArenaFixture.Name(value: "stage"),
                Kind: CellKind.Int,
                Capacity: 1,
                Phase: new StatePhase(Sequence: 6L)
            ),
        ]
    );

    internal static void AssertSameCell(StateCell expected, StateCell actual) {
        Assert.Equal(
            actual: actual.Key,
            expected: expected.Key
        );
        Assert.Equal(
            actual: actual.Value,
            expected: expected.Value
        );
        Assert.Equal(
            actual: actual.Advance,
            expected: expected.Advance
        );
        Assert.Equal(
            actual: actual.Provenance,
            expected: expected.Provenance
        );
        Assert.Equal(
            actual: actual.Dynamics,
            expected: expected.Dynamics
        );
        Assert.Equal(
            actual: actual.Cycle,
            expected: expected.Cycle
        );
        Assert.Equal(
            actual: actual.Behavior,
            expected: expected.Behavior
        );
        Assert.Equal(
            actual: actual.Clock,
            expected: expected.Clock
        );
        Assert.Equal(
            actual: actual.Observation,
            expected: expected.Observation
        );
        AssertSameVisibility(
            actual: actual.Visibility,
            expected: expected.Visibility
        );
    }
    internal static void AssertSameRows(IReadOnlyList<StateRow> expected, IReadOnlyList<StateRow> actual) {
        Assert.Equal(
            actual: actual.Count,
            expected: expected.Count
        );

        for (var index = 0; (index < expected.Count); index++) {
            var first = expected[index];
            var second = actual[index];

            Assert.Equal(
                actual: second.Name,
                expected: first.Name
            );
            Assert.Equal(
                actual: second.Kind,
                expected: first.Kind
            );
            Assert.Equal(
                actual: second.Min,
                expected: first.Min
            );
            Assert.Equal(
                actual: second.Max,
                expected: first.Max
            );
            Assert.Equal(
                actual: second.Capacity,
                expected: first.Capacity
            );
            Assert.Equal(
                actual: second.Overflow,
                expected: first.Overflow
            );
            Assert.Equal(
                actual: second.Evicts,
                expected: first.Evicts
            );
            Assert.Equal(
                actual: second.Advance,
                expected: first.Advance
            );
            Assert.Equal(
                actual: second.Draw,
                expected: first.Draw
            );
            Assert.Equal(
                actual: second.DrawCursor,
                expected: first.DrawCursor
            );
            Assert.Equal(
                actual: second.Dynamics,
                expected: first.Dynamics
            );
            Assert.Equal(
                actual: second.Cycle,
                expected: first.Cycle
            );
            Assert.Equal(
                actual: second.Domain,
                expected: first.Domain
            );
            Assert.Equal(
                actual: second.ValuesFrom,
                expected: first.ValuesFrom
            );
            Assert.Equal(
                actual: second.Inverse,
                expected: first.Inverse
            );
            Assert.Equal(
                actual: second.Phase,
                expected: first.Phase
            );
            Assert.Equal(
                actual: second.Knowledge,
                expected: first.Knowledge
            );
            Assert.Equal(
                actual: second.PhaseOf,
                expected: first.PhaseOf
            );
            Assert.Equal(
                actual: second.HistoryCursor,
                expected: first.HistoryCursor
            );
            Assert.Equal(
                actual: second.Space,
                expected: first.Space
            );
            Assert.Equal(
                actual: second.Enum,
                expected: first.Enum
            );
            Assert.Equal(
                actual: second.Generated,
                expected: first.Generated
            );
            Assert.Equal(
                actual: second.HostOwned,
                expected: first.HostOwned
            );
            AssertSameVisibility(
                actual: second.Visibility,
                expected: first.Visibility
            );
            Assert.Equal(
                actual: (second.DrawnMasks?.Count ?? 0),
                expected: (first.DrawnMasks?.Count ?? 0)
            );

            for (var mask = 0; (mask < (first.DrawnMasks?.Count ?? 0)); mask++) {
                Assert.Equal(
                    actual: second.DrawnMasks![mask],
                    expected: first.DrawnMasks![mask]
                );
            }

            Assert.Equal(
                actual: (second.Cells?.Count ?? 0),
                expected: (first.Cells?.Count ?? 0)
            );

            for (var cell = 0; (cell < (first.Cells?.Count ?? 0)); cell++) {
                AssertSameCell(
                    actual: second.Cells![cell],
                    expected: first.Cells![cell]
                );
            }
        }
    }
    internal static void AssertSameVisibility(StateVisibility? expected, StateVisibility? actual) {
        if (expected is null) {
            Assert.Null(@object: actual);

            return;
        }

        Assert.NotNull(@object: actual);
        Assert.Equal(
            actual: actual!.Hidden,
            expected: expected.Hidden
        );
        Assert.Equal(
            actual: actual.ReadersFrom,
            expected: expected.ReadersFrom
        );
        Assert.Equal(
            actual: (actual.Readers?.Count ?? 0),
            expected: (expected.Readers?.Count ?? 0)
        );

        for (var index = 0; (index < (expected.Readers?.Count ?? 0)); index++) {
            Assert.Equal(
                actual: actual.Readers![index],
                expected: expected.Readers![index]
            );
        }
    }
    // Leaves at least one write in every column the arena carries, so the round trip is asked about all of them.
    internal static void Populate(StateArena arena, StateCatalog catalog) {
        var a = ArenaFixture.Key(
            catalog: catalog,
            value: "a"
        );
        var slot = ArenaFixture.SlotKey(catalog: catalog);

        Assert.True(condition: arena.TryWrite(
            key: slot,
            operand: 9L,
            reason: out var reason,
            rowOrdinal: ArenaFixture.Score,
            write: StateWriteKind.Set
        ), userMessage: reason);
        Assert.True(condition: arena.TryWriteText(
            key: slot,
            reason: out reason,
            rowOrdinal: ArenaFixture.Label,
            text: "bye"
        ), userMessage: reason);
        Assert.True(condition: arena.TryWriteVector(
            components: ArenaFixture.Unit(axis: 3).Components,
            key: slot,
            reason: out reason,
            rowOrdinal: ArenaFixture.Embed
        ), userMessage: reason);
        Assert.True(condition: arena.TryWriteClock(
            epochEngineTick: 4L,
            epochTick: 3L,
            key: a,
            reason: out reason,
            rowOrdinal: ArenaFixture.Tokens,
            substepTicks: 7L,
            v0: 6L,
            y0: 5L
        ), userMessage: reason);
        Assert.True(condition: arena.TryWriteProvenance(
            key: a,
            provenance: "issuer",
            rowOrdinal: ArenaFixture.Tokens
        ));
        Assert.True(condition: arena.TryWriteBehavior(
            behavior: StateCellBehavior.None,
            key: a,
            rowOrdinal: ArenaFixture.Tokens
        ));
        Assert.True(condition: arena.TryWriteVisibility(
            key: a,
            rowOrdinal: ArenaFixture.Tokens,
            visibility: new StateVisibility(
                Hidden: HiddenCells.Count,
                Readers: ["p1"],
                ReadersFrom: "label"
            )
        ));
        Assert.True(condition: arena.TryWriteObservation(
            key: a,
            observation: new StateObservation(
                Tick: 12L,
                Visible: true
            ),
            rowOrdinal: ArenaFixture.Tokens
        ));
        Assert.True(condition: arena.TryPush(
            reason: out reason,
            rowOrdinal: ArenaFixture.History,
            value: 42L
        ), userMessage: reason);
        Assert.True(condition: arena.TryMint(
            key: out _,
            name: ArenaFixture.Name(value: "minted"),
            reason: out reason,
            rowOrdinal: ArenaFixture.Hand,
            value: CellValue.Int(value: 3L)
        ), userMessage: reason);
        Assert.True(condition: arena.TryWriteDrawCursor(
            cursor: 5L,
            rowOrdinal: ArenaFixture.Deal
        ));
        Assert.True(condition: arena.TryWriteDrawnMask(
            index: 1,
            mask: new ClosedBitset256(
                Word0: 3UL,
                Word1: 0UL,
                Word2: 0UL,
                Word3: 8UL
            ),
            rowOrdinal: ArenaFixture.Deal
        ));
        Assert.True(condition: arena.TryWritePhaseSequence(
            rowOrdinal: ArenaFixture.Turn,
            sequence: 6L
        ));

        // Moving a token is what moves the derived board the export carries.
        Assert.True(condition: arena.TryWrite(
            key: a,
            operand: 3L,
            reason: out reason,
            rowOrdinal: ArenaFixture.Tokens,
            write: StateWriteKind.Set
        ), userMessage: reason);
    }
    // Two imported rows under one name would both load onto the same ordinal, the second over the first's member
    // tables, so the import refuses the pair by name and leaves the arena as it was.
    [Fact]
    public void ARowImportedTwiceIsRefusedByNameAndLeavesTheArenaUntouched() {
        var section = new StateSection(Rows: [new StateRow(
            Name: ArenaFixture.Name(value: "score"),
            Kind: CellKind.Int,
            Cells: [new StateCell(
                Key: StateRow.SlotKey,
                Value: CellValue.Int(value: 1L)
            )]
        )]);
        var catalog = StateCatalog.Compile(section: section);
        var arena = new StateArena(
            catalog: catalog,
            options: null,
            section: section,
            time: ArenaTime.Origin
        );
        var before = arena.ComputeHash();

        Assert.False(condition: arena.TryLoad(
            reason: out var reason,
            rows: [section.Rows![0], section.Rows[0]],
            time: ArenaTime.Origin
        ));
        Assert.Contains(
            actualString: reason,
            expectedSubstring: "'score' is imported twice"
        );
        Assert.Equal(
            actual: arena.ComputeHash(),
            expected: before
        );
    }
}
