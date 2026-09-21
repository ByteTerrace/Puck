using Puck.Assets.Documents;
using Xunit;

namespace Puck.State.Rules.Tests;

/// <summary>CONTRACT UNDER TEST: each <see cref="ArenaTransform"/> case applies its own semantics over a
/// <see cref="StateArena"/>, refuses by its own catalogued code, and writes only inside the caller's journal scope,
/// so a refused transform leaves the arena exactly as it found it.</summary>
public sealed class ArenaTransformLawTests {
    private static (ArenaEffectHost Host, RuleCompileContext Context) Arrange() {
        var section = TransformFixture.Section();
        var context = TransformFixture.Context(section: section);

        return (TransformFixture.Host(
            context: context,
            section: section
        ), context);
    }
    private static bool Apply(ArenaEffectHost host, RuleCompileContext context, StateTransform transform, out bool moved, out EffectRefusal refusal) {
        moved = false;

        if (!RuleCompiler.TryResolveTransform(
            context: context,
            reason: out var reason,
            resolved: out var resolved,
            transform: transform
        )) {
            refusal = EffectRefusal.Of(
                code: TransformRefusal.RowUnaddressable,
                reason: reason
            );

            return false;
        }

        return host.TryTransform(
            binding: ArenaTransformBinding.None,
            moved: out moved,
            refusal: out refusal,
            transform: resolved
        );
    }
    private static void Applies(ArenaEffectHost host, RuleCompileContext context, StateTransform transform) => Assert.True(
        condition: Apply(
            context: context,
            host: host,
            moved: out _,
            refusal: out var refusal,
            transform: transform
        ),
        userMessage: refusal.Reason
    );
    private static TRefusal Refuses<TRefusal>(ArenaEffectHost host, RuleCompileContext context, StateTransform transform) where TRefusal : struct, Enum {
        Assert.False(condition: Apply(
            context: context,
            host: host,
            moved: out _,
            refusal: out var refusal,
            transform: transform
        ));

        return Assert.IsType<TRefusal>(@object: refusal.Code);
    }

    [Fact]
    public void ATransferMovesOneTokenAndPreservesTheRestOfThePile() {
        var (host, context) = Arrange();
        var deck = TransformFixture.Ordinal(
            context: context,
            name: "deck"
        );
        var hand = TransformFixture.Ordinal(
            context: context,
            name: "hand"
        );

        Applies(
            context: context,
            host: host,
            transform: new StateTransform.Transfer(
                From: "deck",
                Selector: ZoneSelector.Last,
                To: "hand"
            )
        );
        Assert.Equal(
            actual: TransformFixture.Listing(
                arena: host.Arena,
                rowOrdinal: deck
            ),
            expected: "a=1,b=2"
        );
        Assert.Equal(
            actual: TransformFixture.Listing(
                arena: host.Arena,
                rowOrdinal: hand
            ),
            expected: "c=3"
        );
    }
    [Fact]
    public void ASliceTransferMovesTheKeyedTokenAndEveryTokenAfterIt() {
        var (host, context) = Arrange();

        Applies(
            context: context,
            host: host,
            transform: new StateTransform.Transfer(
                From: "deck",
                Key: "b",
                Selector: ZoneSelector.Slice,
                To: "hand"
            )
        );
        Assert.Equal(
            actual: TransformFixture.Listing(
                arena: host.Arena,
                rowOrdinal: TransformFixture.Ordinal(
                    context: context,
                    name: "hand"
                )
            ),
            expected: "b=2,c=3"
        );
    }
    [Fact]
    public void ATransferOntoItsOwnZoneRotatesRatherThanRefusingForRoom() {
        var (host, context) = Arrange();

        Applies(
            context: context,
            host: host,
            transform: new StateTransform.Transfer(
                From: "deck",
                InsertFirst: true,
                Selector: ZoneSelector.Last,
                To: "deck"
            )
        );
        Assert.Equal(
            actual: TransformFixture.Listing(
                arena: host.Arena,
                rowOrdinal: TransformFixture.Ordinal(
                    context: context,
                    name: "deck"
                )
            ),
            expected: "c=3,a=1,b=2"
        );
    }
    [Fact]
    public void ATransferFromAnEmptyZoneRefusesByName() {
        var (host, context) = Arrange();

        Assert.Equal(
            actual: Refuses<TransformRefusal>(
                context: context,
                host: host,
                transform: new StateTransform.Transfer(
                    From: "hand",
                    Selector: ZoneSelector.First,
                    To: "deck"
                )
            ),
            expected: TransformRefusal.TransferSourceShort
        );
    }
    [Fact]
    public void ARandomTransferConsumesOneDrawPerTokenAndAdvancesTheSitesCursor() {
        var (host, context) = Arrange();
        var coin = TransformFixture.Ordinal(
            context: context,
            name: "coin"
        );

        Assert.Equal(
            actual: host.Arena.DrawCursor(rowOrdinal: coin),
            expected: 0L
        );
        Applies(
            context: context,
            host: host,
            transform: new StateTransform.Transfer(
                Count: 2,
                Draw: "coin",
                From: "deck",
                Selector: ZoneSelector.Random,
                To: "hand"
            )
        );
        Assert.Equal(
            actual: host.Arena.CellCount(rowOrdinal: TransformFixture.Ordinal(
                context: context,
                name: "hand"
            )),
            expected: 2
        );
        Assert.Equal(
            actual: host.Arena.DrawCursor(rowOrdinal: coin),
            expected: 2L
        );
    }
    // A draw a transform has already taken rides the caller's scope: rewinding the scope gives the samples back,
    // so the same transform draws the same tokens again.
    [Fact]
    public void ADrawTakenInsideARewoundScopeIsGivenBack() {
        var (host, context) = Arrange();
        var arena = host.Arena;
        var coin = TransformFixture.Ordinal(
            context: context,
            name: "coin"
        );
        var transfer = new StateTransform.Transfer(
            Count: 2,
            Draw: "coin",
            From: "deck",
            Selector: ZoneSelector.Random,
            To: "hand"
        );
        var before = arena.ComputeHash();
        var mark = arena.BeginScope();

        Applies(
            context: context,
            host: host,
            transform: transfer
        );
        Assert.Equal(
            actual: arena.DrawCursor(rowOrdinal: coin),
            expected: 2L
        );

        var drawn = arena.ComputeHash();

        arena.Rewind(mark: mark);
        Assert.Equal(
            actual: arena.DrawCursor(rowOrdinal: coin),
            expected: 0L
        );
        Assert.Equal(
            actual: arena.ComputeHash(),
            expected: before
        );

        mark = arena.BeginScope();
        Applies(
            context: context,
            host: host,
            transform: transfer
        );
        Assert.Equal(
            actual: arena.ComputeHash(),
            expected: drawn
        );
        arena.Commit(mark: mark);
    }
    [Fact]
    public void ARefusedTransformConsumesNoDrawAndLeavesTheArenaWhereItWas() {
        var (host, context) = Arrange();
        var arena = host.Arena;
        var coin = TransformFixture.Ordinal(
            context: context,
            name: "coin"
        );
        var before = arena.ComputeHash();
        var mark = arena.BeginScope();

        // Four tokens from a three-token pile: the source check refuses before a sample is drawn, and the scope
        // that would have carried the draw is rewound either way.
        Assert.False(condition: Apply(
            context: context,
            host: host,
            moved: out _,
            refusal: out _,
            transform: new StateTransform.Transfer(
                Count: 4,
                Draw: "coin",
                From: "deck",
                Selector: ZoneSelector.Random,
                To: "hand"
            )
        ));
        arena.Rewind(mark: mark);
        Assert.Equal(
            actual: arena.DrawCursor(rowOrdinal: coin),
            expected: 0L
        );
        Assert.Equal(
            actual: arena.ComputeHash(),
            expected: before
        );
    }
    [Fact]
    public void AShuffleConsumesOneSamplePerPositionButTheLast() {
        var (host, context) = Arrange();
        var coin = TransformFixture.Ordinal(
            context: context,
            name: "coin"
        );

        Applies(
            context: context,
            host: host,
            transform: new StateTransform.Shuffle(
                Draw: "coin",
                Row: "deck"
            )
        );
        Assert.Equal(
            actual: host.Arena.DrawCursor(rowOrdinal: coin),
            expected: 2L
        );
        Assert.Equal(
            actual: host.Arena.CellCount(rowOrdinal: TransformFixture.Ordinal(
                context: context,
                name: "deck"
            )),
            expected: 3
        );
    }
    [Fact]
    public void ASortZoneOrdersByItsAttributeRowAndItsDirection() {
        var (host, context) = Arrange();
        var deck = TransformFixture.Ordinal(
            context: context,
            name: "deck"
        );

        Applies(
            context: context,
            host: host,
            transform: new StateTransform.Sort(
                By: [new SortKey(Row: "rank")],
                Row: "deck"
            )
        );
        Assert.Equal(
            actual: TransformFixture.Listing(
                arena: host.Arena,
                rowOrdinal: deck
            ),
            expected: "b=2,c=3,a=1"
        );
        Applies(
            context: context,
            host: host,
            transform: new StateTransform.Sort(
                By: [new SortKey(
                        Descending: true,
                        Row: "rank"
                    )],
                Row: "deck"
            )
        );
        Assert.Equal(
            actual: TransformFixture.Listing(
                arena: host.Arena,
                rowOrdinal: deck
            ),
            expected: "a=1,c=3,b=2"
        );
    }
    [Fact]
    public void ASortKeyedOrdersByItsOwnValues() {
        var (host, context) = Arrange();

        Applies(
            context: context,
            host: host,
            transform: new StateTransform.Sort(Row: "scores", By: [new SortKey(Row: "scores")])
        );
        Assert.Equal(
            actual: TransformFixture.Listing(
                arena: host.Arena,
                rowOrdinal: TransformFixture.Ordinal(
                    context: context,
                    name: "scores"
                )
            ),
            expected: "y=2,x=5,z=9"
        );
    }
    [Fact]
    public void AnArrangeAtRankZeroIsTheTokenDomainsOwnOrder() {
        var (host, context) = Arrange();

        Applies(
            context: context,
            host: host,
            transform: new StateTransform.Shuffle(
                Draw: "coin",
                Row: "deck"
            )
        );
        Applies(
            context: context,
            host: host,
            transform: new StateTransform.Arrange(
                From: "rankValue",
                Row: "deck"
            )
        );
        Assert.Equal(
            actual: TransformFixture.Listing(
                arena: host.Arena,
                rowOrdinal: TransformFixture.Ordinal(
                    context: context,
                    name: "deck"
                )
            ),
            expected: "a=1,b=2,c=3"
        );
    }
    [Fact]
    public void AnArrangeRankAtOrPastFactorialRefusesByName() {
        var (host, context) = Arrange();

        Assert.True(condition: host.Arena.TryWrite(
            key: host.Arena.Catalog.Keys.Intern(name: StateRow.SlotKey),
            operand: 6L,
            reason: out _,
            rowOrdinal: TransformFixture.Ordinal(
                context: context,
                name: "rankValue"
            ),
            write: StateWriteKind.Set
        ));
        Assert.Equal(
            actual: Refuses<TransformRefusal>(
                context: context,
                host: host,
                transform: new StateTransform.Arrange(
                    From: "rankValue",
                    Row: "deck"
                )
            ),
            expected: TransformRefusal.ArrangeRankRange
        );
    }
    [Fact]
    public void APushWritesTheRingSlotTheCursorNamesAndAdvancesIt() {
        var (host, context) = Arrange();
        var log = TransformFixture.Ordinal(
            context: context,
            name: "log"
        );

        for (var value = 1L; (value <= 4L); value++) {
            Applies(
                context: context,
                host: host,
                transform: new StateTransform.Push(
                    Row: "log",
                    Value: value
                )
            );
        }

        Assert.Equal(
            actual: host.Arena.HistoryCursor(rowOrdinal: log),
            expected: 4L
        );
        Assert.Equal(
            actual: TransformFixture.PositionListing(
                arena: host.Arena,
                rowOrdinal: log
            ),
            expected: "4,2,3"
        );
    }
    [Fact]
    public void ABoardCombineWritesMembershipAlgebraOverTheWholeBoard() {
        var (host, context) = Arrange();
        var target = TransformFixture.Ordinal(
            context: context,
            name: "target"
        );

        Applies(
            context: context,
            host: host,
            transform: new StateTransform.BoardCombine(
                Left: "left",
                Operation: BoardCombineOp.And,
                Right: "right",
                Row: "target",
                Value: 7L
            )
        );
        Assert.Equal(
            actual: TransformFixture.BoardListing(
                arena: host.Arena,
                rowOrdinal: target
            ),
            expected: "0,0,7,0"
        );
        Applies(
            context: context,
            host: host,
            transform: new StateTransform.BoardCombine(
                Left: "left",
                Operation: BoardCombineOp.Xor,
                Right: "right",
                Row: "target",
                Value: 7L
            )
        );
        Assert.Equal(
            actual: TransformFixture.BoardListing(
                arena: host.Arena,
                rowOrdinal: target
            ),
            expected: "7,7,0,0"
        );
    }
    [Fact]
    public void ThirtyThreeByEighteenPerNounSetsStayAsBoardRows() {
        const int Width = 33;
        const int Depth = 18;
        const int Nouns = 6;
        var rows = new List<StateRow>();

        for (var noun = 0; (noun < Nouns); noun++) {
            var cells = new List<StateCell>();

            for (var cell = noun; (cell < (Width * Depth)); cell += Nouns) {
                cells.Add(item: TransformFixture.Cell(
                    key: cell.ToString(provider: System.Globalization.CultureInfo.InvariantCulture),
                    value: 1L
                ));
            }
            rows.Add(item: new StateRow(
                Name: TransformFixture.Name(value: $"noun{noun}"),
                Kind: CellKind.Int,
                Domain: new StateDomain.CellsOf(
                    Empty: 0L,
                    Topology: "level"
                ),
                Cells: cells
            ));
        }
        rows.Add(item: new StateRow(
            Name: TransformFixture.Name(value: "properties"),
            Kind: CellKind.Int,
            Domain: new StateDomain.CellsOf(
                Empty: 0L,
                Topology: "level"
            )
        ));
        var section = new StateSection(
            Rows: rows,
            Lattices: [new LatticeTopology.Grid(
                    Name: "level",
                    Origin: new DocumentVector3(
                        x: 0f,
                        y: 0f,
                        z: 0f
                    ),
                    CellSize: 1f,
                    Width: Width,
                    Depth: Depth
                )]
        );
        var context = TransformFixture.Context(section: section);
        var host = TransformFixture.Host(
            context: context,
            section: section
        );

        Applies(
            context: context,
            host: host,
            transform: new StateTransform.BoardCombine(
                Left: "noun0",
                Operation: BoardCombineOp.Copy,
                Row: "properties"
            )
        );
        for (var noun = 1; (noun < Nouns); noun++) {
            Applies(
                context: context,
                host: host,
                transform: new StateTransform.BoardCombine(
                    Left: "properties",
                    Operation: BoardCombineOp.Or,
                    Right: $"noun{noun}",
                    Row: "properties"
                )
            );
        }

        var values = new long[(Width * Depth)];

        Assert.True(condition: host.Arena.TryReadBoard(
            rowOrdinal: TransformFixture.Ordinal(
                context: context,
                name: "properties"
            ),
            values: values
        ));
        Assert.All(
            collection: values,
            action: value => Assert.Equal(
                actual: value,
                expected: 1L
            )
        );
        for (var noun = 0; (noun < Nouns); noun++) {
            Assert.True(condition: host.Arena.TryReadBoard(
                rowOrdinal: TransformFixture.Ordinal(
                    context: context,
                    name: $"noun{noun}"
                ),
                values: values
            ));
            for (var cell = 0; (cell < values.Length); cell++) {
                Assert.Equal(
                    expected: (((cell % Nouns) == noun) ? 1L : 0L),
                    actual: values[cell]
                );
            }
        }
    }
    [Fact]
    public void AWriteSetPaintsExactlyTheMaskedCellsAndSkipsABitPastTheTopology() {
        var (host, context) = Arrange();

        Applies(
            context: context,
            host: host,
            transform: new StateTransform.WriteSet(
                Row: "target",
                Set: "mask",
                Value: 3L
            )
        );
        Assert.Equal(
            actual: TransformFixture.BoardListing(
                arena: host.Arena,
                rowOrdinal: TransformFixture.Ordinal(
                    context: context,
                    name: "target"
                )
            ),
            expected: "3,3,0,3"
        );
    }
    [Fact]
    public void ASetRayWritesTheLongestAcceptedPrefixAndExcludesTheOrigin() {
        var (host, context) = Arrange();

        Applies(
            context: context,
            host: host,
            transform: new StateTransform.SetRay(
                Direction: CellName.Parse(candidate: "E"),
                From: "0",
                Pattern: "ones",
                Row: "board",
                Value: 9L
            )
        );
        Assert.Equal(
            actual: TransformFixture.BoardListing(
                arena: host.Arena,
                rowOrdinal: TransformFixture.Ordinal(
                    context: context,
                    name: "board"
                )
            ),
            expected: "5,9,9,0"
        );
    }
    [Fact]
    public void ASetRayWithNoAcceptedPrefixRefusesByName() {
        var (host, context) = Arrange();

        Assert.Equal(
            actual: Refuses<TransformRefusal>(
                context: context,
                host: host,
                transform: new StateTransform.SetRay(
                    Direction: CellName.Parse(candidate: "E"),
                    From: "0",
                    Pattern: "ones",
                    Row: "left",
                    Value: 9L
                )
            ),
            expected: TransformRefusal.SetRayEmptyPrefix
        );
    }
    [Fact]
    public void AClearEnclosedWritesEmptyOverTheEnclosedGroupAlone() {
        var (host, context) = Arrange();

        Applies(
            context: context,
            host: host,
            transform: new StateTransform.ClearEnclosed(
                From: "0",
                Lower: 1L,
                Row: "board",
                Upper: 1L
            )
        );
        Assert.Equal(
            actual: TransformFixture.BoardListing(
                arena: host.Arena,
                rowOrdinal: TransformFixture.Ordinal(
                    context: context,
                    name: "board"
                )
            ),
            expected: "5,1,1,0"
        );
    }
    [Fact]
    public void AnObserveCopiesTheMaskedCellsAndStampsTheHostsTick() {
        var (host, context) = Arrange();
        var known = TransformFixture.Ordinal(
            context: context,
            name: "known"
        );

        host.Advance(
            engineTick: 11UL,
            tick: 11UL
        );
        Applies(
            context: context,
            host: host,
            transform: new StateTransform.Observe(Row: "known")
        );
        Assert.Equal(4L, host.Arena.Read(known, context.Catalog.Keys.Intern(name: TransformFixture.Name(value: "a")))?.AsInt);
        Assert.Equal(6L, host.Arena.Read(known, context.Catalog.Keys.Intern(name: TransformFixture.Name(value: "b")))?.AsInt);
        Assert.Equal(
            actual: host.Arena.Observation(known, context.Catalog.Keys.Intern(name: TransformFixture.Name(value: "a"))),
            expected: new StateObservation(
                Tick: 11L,
                Visible: true
            )
        );
        Assert.Equal(new StateObservation(Tick: 11L, Visible: true), host.Arena.Observation(known, context.Catalog.Keys.Intern(name: TransformFixture.Name(value: "b"))));
    }
    [Fact]
    public void AnObserveDropsTheVisibleBitOfACellTheMaskNoLongerCovers() {
        var (host, context) = Arrange();
        var known = TransformFixture.Ordinal(
            context: context,
            name: "known"
        );

        host.Advance(
            engineTick: 3UL,
            tick: 3UL
        );
        Applies(
            context: context,
            host: host,
            transform: new StateTransform.Observe(Row: "known")
        );
        Assert.True(condition: host.Arena.TryWriteBoardCell(
            cell: 0,
            reason: out _,
            rowOrdinal: TransformFixture.Ordinal(
                context: context,
                name: "sight"
            ),
            value: 0L,
            write: StateWriteKind.Set
        ));
        host.Advance(
            engineTick: 4UL,
            tick: 4UL
        );
        Applies(
            context: context,
            host: host,
            transform: new StateTransform.Observe(Row: "known")
        );
        Assert.Equal(
            actual: host.Arena.Observation(known, context.Catalog.Keys.Intern(name: TransformFixture.Name(value: "a"))),
            expected: new StateObservation(
                Tick: 3L,
                Visible: false
            )
        );
    }
    [Fact]
    public void ATransformWritesInsideTheCallersScopeAndARewindUndoesIt() {
        var (host, context) = Arrange();
        var arena = host.Arena;
        var before = arena.ComputeHash();
        var mark = arena.BeginScope();

        Applies(
            context: context,
            host: host,
            transform: new StateTransform.Sort(Row: "scores", By: [new SortKey(Row: "scores")])
        );
        Assert.NotEqual(
            actual: arena.ComputeHash(),
            expected: before
        );
        arena.Rewind(mark: mark);
        Assert.Equal(
            actual: arena.ComputeHash(),
            expected: before
        );
    }
    [Fact]
    public void ATransformThatChangesNothingReportsThatItMovedNothing() {
        var (host, context) = Arrange();

        Assert.True(condition: Apply(
            context: context,
            host: host,
            moved: out var moved,
            refusal: out _,
            transform: new StateTransform.ClearEnclosed(
                From: "3",
                Lower: 1L,
                Row: "board",
                Upper: 1L
            )
        ));
        Assert.False(condition: moved);
    }
}
