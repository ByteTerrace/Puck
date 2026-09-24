using Puck.Abstractions.Counting;
using Puck.Assets.Documents;
using Xunit;

namespace Puck.State.Rules.Tests;

/// <summary>CONTRACT UNDER TEST: every arena transform's admitted price is at least the work one firing does over its
/// costliest operand, as the arena itself counts it — the journal entries its writes record, the vector components it
/// snapshots, the scratch elements it leases and clears, the lanes it visits, and the samples its draw site gives up —
/// and at most the fixed door plus a small multiple of that work, so a price that drifts far above the work fails as
/// surely as one below it. And the price is sized by the rows the transform addresses alone: rows it never touches
/// leave it unchanged.</summary>
/// <remarks>Each operand is the widest the kernel admits in its shape: a ray over a whole 64-cell line, a mask with
/// every bit set, a twenty-token pile moved one token at a time from its head, a reversed pile sorted, the last
/// arrangement of twenty tokens, and a push run through a full pool.</remarks>
public sealed class TransformWorkLawTests(ITestOutputHelper output) {
    private const int Members = 20;
    private const int Width = 64;
    // How far above the counted work a price may run beyond its fixed door: the lanes a worst-case price reserves
    // that the operand's rows leave unmaterialized, and the comparisons and walks a kernel makes over its own scratch.
    private const long Slack = 16L;

    private static CellName Name(string value) => CellName.Parse(candidate: value);
    private static StateCell Cell(string key, long value) => new(
        Key: Name(value: key),
        Value: CellValue.Int(value: value)
    );
    private static StateRow Board(string name, Func<int, long?> value, StateKnowledge? knowledge = null) => new(
        Name: Name(value: name),
        Kind: CellKind.Int,
        Domain: new StateDomain.CellsOf(
            Empty: 0L,
            Topology: "line"
        ),
        Cells: [.. Enumerable.Range(count: Width, start: 0).Where(predicate: cell => value(arg: cell).HasValue).Select(selector: cell => Cell(
            key: cell.ToString(provider: System.Globalization.CultureInfo.InvariantCulture),
            value: value(arg: cell)!.Value
        ))],
        Knowledge: knowledge
    );
    private static StateRow Pile(string name, bool full) => new(
        Name: Name(value: name),
        Kind: CellKind.Int,
        Capacity: Members,
        Domain: new StateDomain.KeysOf(
            Ordered: true,
            Row: Name(value: "tokens")
        ),
        Cells: (full
            ? [.. Enumerable.Range(count: Members, start: 0).Select(selector: token => Cell(key: $"t{token}", value: token))]
            : null)
    );
    private static StateRow Slot(string name, long value) => new(
        Name: Name(value: name),
        Kind: CellKind.Int,
        Cells: [Cell(key: StateRow.SlotKey.Value, value: value)]
    );
    private static StateSection Section(params StateRow[] unrelated) => new(
        Lattices: [new LatticeTopology.Grid(
            Name: "line",
            Origin: new DocumentVector3(x: 0f, y: 0f, z: 0f),
            CellSize: 1f,
            Width: Width,
            Depth: 1
        )],
        Rows: [
            // A 5 at the origin, then an unbroken run of 1s to the far edge: the whole line is the ray, and the run is
            // one enclosed group with no empty cell beside it.
            Board(name: "board", value: static cell => ((cell == 0) ? 5L : 1L)),
            Board(name: "left", value: static _ => 1L),
            Board(name: "right", value: static _ => 1L),
            Board(name: "target", value: static _ => null),
            Board(name: "truth", value: static cell => (cell + 1L)),
            Board(name: "sight", value: static _ => 1L),
            Board(name: "seen", value: static _ => null, knowledge: new StateKnowledge(Source: "truth", Mask: "sight")),
            new StateRow(
                Name: Name(value: "tokens"),
                Kind: CellKind.Int,
                Capacity: Members,
                Cells: [.. Enumerable.Range(count: Members, start: 0).Select(selector: token => Cell(key: $"t{token}", value: token))]
            ),
            Pile(full: true, name: "deck"),
            Pile(full: false, name: "hand"),
            // Ascending weights and ascending scores, each sorted descending: the reversed input an insertion sort
            // spends the most on.
            new StateRow(
                Name: Name(value: "weight"),
                Kind: CellKind.Int,
                Capacity: Members,
                Domain: new StateDomain.KeysOf(Row: Name(value: "tokens")),
                Cells: [.. Enumerable.Range(count: Members, start: 0).Select(selector: token => Cell(key: $"t{token}", value: token))]
            ),
            new StateRow(
                Name: Name(value: "scores"),
                Kind: CellKind.Int,
                Capacity: Members,
                Cells: [.. Enumerable.Range(count: Members, start: 0).Select(selector: token => Cell(key: $"s{token}", value: token))]
            ),
            new StateRow(
                Name: Name(value: "log"),
                Kind: CellKind.Int,
                Domain: new StateDomain.Ring(
                    Capacity: 8,
                    Empty: -1L
                )
            ),
            // The last of twenty tokens' arrangements: every token moves.
            Slot(name: "rankValue", value: 2_432_902_008_176_639_999L),
            Slot(name: "mask", value: -1L),
            new StateRow(
                Name: Name(value: "coin"),
                Kind: CellKind.Int,
                Draw: new Draw(
                    Source: Name(value: "uniform"),
                    Timing: DrawTiming.Event
                )
            ),
            .. unrelated,
        ]
    );
    private static GeneratorRow[] Generators() => [new GeneratorRow(
        Name: Name(value: "uniform"),
        Generator: new StateGenerator(Source: GeneratorSource.StreamDraw)
    )];
    private static RuleCompileContext Context(StateSection section) => new(
        catalog: StateCatalog.Compile(section: section),
        generators: Generators(),
        patterns: [new PatternRow(
            Name: Name(value: "ones"),
            Kind: CellKind.Int,
            Symbols: [new PatternSymbol(Name: Name(value: "one"), Min: 1L, Max: 1L)],
            Pattern: new PatternNode.Plus(Item: new PatternNode.Symbol(Name: "one"))
        )],
        section: section,
        simulationRateHz: 30,
        tables: null,
        vocabulary: RuleVocabulary.Core
    ) {
        Sets = [new CellSetRow(Name: Name(value: "everywhere"), Set: new CellSetExpression.Board(Row: Name(value: "left"), Low: 1L, High: 1L))],
    };
    private static TransformStateEffect Compile(RuleCompileContext context, StateTransform transform) => Assert.IsType<TransformStateEffect>(@object: Assert.Single(collection: RuleCompiler.Compile(
        context: context,
        rule: new Rule(
            Name: Name(value: "work"),
            Effects: [new ActionEffect.TransformState(Transform: transform)]
        )
    ).Effects));
    // One of the arena's work counts so far, read through its work counter source.
    private static long Count(IWorkCounterSource arena, WorkKind kind) {
        Assert.True(condition: arena.TryRead(kind: kind, value: out var count));

        return count;
    }
    // Every draw site's cursor summed: what one firing's draws consumed is the difference across it.
    private static long Cursors(StateArena arena) {
        var total = 0L;

        for (var ordinal = 0; (ordinal < arena.Rows.Count); ordinal++) {
            if (arena.Rows[ordinal].Draw is not null) {
                total += arena.DrawCursor(rowOrdinal: ordinal);
            }
        }

        return total;
    }
    // Applies the compiled transform once inside a scope it then rewinds, and counts what the arena recorded.
    private static long Observe(ArenaEffectHost host, ArenaTransform transform, in ArenaTransformBinding binding) {
        var arena = host.Arena;
        var mark = arena.BeginScope();
        var entries = arena.Journal.Length;
        var leased = Count(arena: arena, kind: ArenaWork.ScratchLeasedElements);
        var visits = Count(arena: arena, kind: ArenaWork.Visits);
        var cursors = Cursors(arena: arena);

        Assert.True(condition: host.TryTransform(
            binding: in binding,
            moved: out var moved,
            refusal: out var refusal,
            transform: transform
        ), userMessage: refusal.Reason);
        Assert.True(condition: moved);

        var observed = (((((arena.Journal.Length - entries) + arena.Journal.ComponentLength) + (Count(arena: arena, kind: ArenaWork.ScratchLeasedElements) - leased)) + (Count(arena: arena, kind: ArenaWork.Visits) - visits)) + (Cursors(arena: arena) - cursors));

        arena.Rewind(mark: mark);

        return observed;
    }
    private void Holds(string what, StateTransform transform, long least) {
        var section = Section();
        var context = Context(section: section);
        var effect = Compile(
            context: context,
            transform: transform
        );
        var host = new ArenaEffectHost(
            arena: new StateArena(
                catalog: context.Catalog,
                options: null,
                section: section,
                time: ArenaTime.Origin
            ),
            generators: Generators()
        );
        var observed = Observe(
            binding: ArenaTransformBinding.None,
            host: host,
            transform: effect.Arena
        );

        output.WriteLine(message: $"{what}: admitted {effect.Price.Units}, observed {observed}");
        // The operand really is the costly one, so the inequality below is not held by a firing that did nothing.
        Assert.True(condition: (observed >= least), userMessage: $"{what} observed {observed}, under the {least} its operand forces");
        Assert.True(condition: (effect.Price.Units >= observed), userMessage: $"{what} admitted {effect.Price.Units}, under the {observed} one firing did");
        Assert.True(condition: (effect.Price.Units <= (TransformWork.Call + (Slack * observed))), userMessage: $"{what} admitted {effect.Price.Units}, past the door and {Slack} times the {observed} one firing did");
    }

    [Fact]
    public void ASetRayIsAdmittedAtLeastItsWholeLineRay() => Holds(
        least: (2L * (Width - 1)),
        transform: new StateTransform.SetRay(Row: "board", From: "0", Direction: Name(value: "E"), Pattern: "ones", Value: 2L),
        what: "setRay"
    );
    [Fact]
    public void ABoardCombineIsAdmittedAtLeastItsWholeBoard() => Holds(
        least: (2L * Width),
        transform: new StateTransform.BoardCombine(Row: "target", Operation: BoardCombineOp.Or, Left: "left", Right: "right"),
        what: "boardCombine"
    );
    [Fact]
    public void AMaskWriteSetIsAdmittedAtLeastEveryBitItStores() => Holds(
        least: (2L * Width),
        transform: new StateTransform.WriteSet(Row: "target", Set: "mask", Value: 1L),
        what: "writeSet mask"
    );
    [Fact]
    public void ADeclaredSetWriteSetIsAdmittedAtLeastEveryMemberItStores() => Holds(
        least: (2L * Width),
        transform: new StateTransform.WriteSet(Row: "target", Set: "everywhere", Value: 1L),
        what: "writeSet declared set"
    );
    [Fact]
    public void AClearEnclosedIsAdmittedAtLeastTheWholeGroupItEmpties() => Holds(
        least: (2L * (Width - 1)),
        transform: new StateTransform.ClearEnclosed(From: "0", Lower: 1L, Row: "board", Upper: 1L),
        what: "clearEnclosed"
    );
    [Fact]
    public void AnObserveIsAdmittedAtLeastEveryVisibleCellItStamps() => Holds(
        least: (3L * Width),
        transform: new StateTransform.Observe(Row: "seen"),
        what: "observe"
    );
    [Fact]
    public void AShuffleIsAdmittedAtLeastItsSwapsAndItsSamples() => Holds(
        least: Members,
        transform: new StateTransform.Shuffle(Draw: "coin", Row: "deck"),
        what: "shuffle"
    );
    [Fact]
    public void ASortByOwnValuesIsAdmittedAtLeastItsReversedReorder() => Holds(
        least: (2L * Members),
        transform: new StateTransform.Sort(Row: "scores", By: [new SortKey(Descending: true, Row: "scores")]),
        what: "sort own values"
    );
    [Fact]
    public void ASortByAnAttributeIsAdmittedAtLeastItsReversedReorder() => Holds(
        least: (2L * Members),
        transform: new StateTransform.Sort(Row: "deck", By: [new SortKey(Descending: true, Row: "weight")]),
        what: "sort by attribute"
    );
    [Fact]
    public void AnArrangeIsAdmittedAtLeastItsLastArrangement() => Holds(
        least: (2L * Members),
        transform: new StateTransform.Arrange(Row: "deck", From: "rankValue"),
        what: "arrange"
    );
    [Fact]
    public void ATransferOfAWholePileFromItsHeadIsAdmittedAtLeastEveryShift() => Holds(
        least: (Members * Members),
        transform: new StateTransform.Transfer(From: "deck", To: "hand", Selector: ZoneSelector.First, Count: Members),
        what: "transfer first"
    );
    [Fact]
    public void ARandomTransferOfAWholePileIsAdmittedAtLeastEveryShiftAndSample() => Holds(
        least: (2L * Members),
        transform: new StateTransform.Transfer(From: "deck", To: "hand", Selector: ZoneSelector.Random, Draw: "coin", Count: Members),
        what: "transfer random"
    );
    [Fact]
    public void ASliceOfAWholePileIsAdmittedAtLeastItsRun() => Holds(
        least: (Members * Members),
        transform: new StateTransform.Transfer(From: "deck", To: "hand", Selector: ZoneSelector.Slice, Key: "t0"),
        what: "transfer slice"
    );
    [Fact]
    public void ATransferOntoItsOwnPileIsAdmittedAtLeastItsReorder() => Holds(
        least: (2L * Members),
        transform: new StateTransform.Transfer(From: "deck", To: "deck", Selector: ZoneSelector.Key, Key: "t19", InsertFirst: true),
        what: "transfer within"
    );
    [Fact]
    public void APushRayThroughAFullPoolIsAdmittedAtLeastItsRun() {
        const int Tokens = 32;
        var cell = Name(value: "cell");
        var kind = Name(value: "kind");
        // Every token but the last is pushable and stands in a line from the origin; the last is passable and ends the
        // run at the far edge, so every pushable token moves.
        var section = new StateSection(
            Lattices: [new LatticeTopology.Grid(Name: "line", Origin: new DocumentVector3(x: 0f, y: 0f, z: 0f), CellSize: 1f, Width: Tokens, Depth: 1)],
            Records: [new StateRecord(Name: Name(value: "Piece"), Fields: [
                new StatePoolField(Name: cell, Kind: CellKind.Int, Min: 0, Max: (Tokens - 1)),
                new StatePoolField(Name: kind, Kind: CellKind.Int),
            ])],
            Pools: [new StatePool(Name: Name(value: "pieces"), Record: Name(value: "Piece"), Capacity: Tokens, Initial: [.. Enumerable.Range(count: Tokens, start: 0).Select(selector: slot => new StatePoolSeed(Slot: slot, Values: [
                new StatePoolValue(Field: cell, Value: CellValue.Int(value: slot)),
                new StatePoolValue(Field: kind, Value: CellValue.Int(value: ((slot == (Tokens - 1)) ? 3L : 1L))),
            ]))])]
        );
        var context = new RuleCompileContext(section: section, catalog: StateCatalog.Compile(section: section), tables: null, patterns: [
            new PatternRow(Name: Name(value: "pushable"), Kind: CellKind.Int, Symbols: [new PatternSymbol(Name: Name(value: "push"), Min: 1, Max: 1), new PatternSymbol(Name: Name(value: "empty"), Min: 0, Max: 0), new PatternSymbol(Name: Name(value: "passable"), Min: 3, Max: 3)],
                Pattern: new PatternNode.Sequence(Items: [new PatternNode.Plus(Item: new PatternNode.Symbol(Name: "push")), new PatternNode.Choice(Items: [new PatternNode.Symbol(Name: "empty"), new PatternNode.Plus(Item: new PatternNode.Symbol(Name: "passable"))])])),
            new PatternRow(Name: Name(value: "push"), Kind: CellKind.Int, Symbols: [new PatternSymbol(Name: Name(value: "push"), Min: 1, Max: 1)], Pattern: new PatternNode.Symbol(Name: "push")),
            new PatternRow(Name: Name(value: "stop"), Kind: CellKind.Int, Symbols: [new PatternSymbol(Name: Name(value: "stop"), Min: 2, Max: 2)], Pattern: new PatternNode.Symbol(Name: "stop")),
        ], generators: null, simulationRateHz: 60, vocabulary: RuleVocabulary.Core);
        var compiled = RuleCompiler.Compile(context: context, rule: new Rule(Name: Name(value: "push"), Effects: [new ActionEffect.ForEachPool(
            Pool: "pieces",
            Binding: Name(value: "mover"),
            Effects: [new ActionEffect.TransformState(Transform: new StateTransform.PushRay(
                Pool: Name(value: "pieces"),
                Cell: cell,
                Value: kind,
                From: StateChannelRef.OfBindingField(binding: "mover", field: "cell"),
                Topology: Name(value: "line"),
                Direction: Name(value: "E"),
                Pattern: "pushable",
                PushPattern: "push",
                StopPattern: "stop",
                Empty: 0
            ))]
        )]));
        var each = Assert.IsType<ForEachPoolEffect>(@object: Assert.Single(collection: compiled.Effects));
        var effect = Assert.IsType<TransformStateEffect>(@object: Assert.Single(collection: each.Effects));
        var arena = new StateArena(catalog: context.Catalog, options: null, section: section, time: ArenaTime.Origin);
        var host = new ArenaEffectHost(arena: arena, generators: null);
        var mover = arena.SnapshotPool(poolOrdinal: 0).First(predicate: handle => (arena.TryRead(fieldOrdinal: 0, handle: handle, value: out var value) && (value.AsInt == 0)));
        var observed = Observe(
            binding: new ArenaTransformBinding(bindsInstance: true, instance: mover),
            host: host,
            transform: effect.Arena
        );

        output.WriteLine(message: $"pushRay: admitted {effect.Price.Units}, observed {observed}");
        Assert.True(condition: (observed >= (2L * (Tokens - 1))), userMessage: $"pushRay observed {observed}");
        Assert.True(condition: (effect.Price.Units >= observed), userMessage: $"pushRay admitted {effect.Price.Units}, under the {observed} one firing did");
        Assert.True(condition: (effect.Price.Units <= (TransformWork.Call + (Slack * observed))), userMessage: $"pushRay admitted {effect.Price.Units}, past the door and {Slack} times the {observed} one firing did");
    }
    [Fact]
    public void RowsATransformNeverTouchesLeaveItsPriceUnchanged() {
        var unrelated = new StateRow(
            Name: Name(value: "archive"),
            Kind: CellKind.Int,
            Capacity: 4096,
            Cells: [.. Enumerable.Range(count: 4096, start: 0).Select(selector: index => Cell(key: $"a{index}", value: index))]
        );

        foreach (var transform in new StateTransform[] {
            new StateTransform.SetRay(Row: "board", From: "0", Direction: Name(value: "E"), Pattern: "ones", Value: 2L),
            new StateTransform.BoardCombine(Row: "target", Operation: BoardCombineOp.Or, Left: "left", Right: "right"),
            new StateTransform.Transfer(From: "deck", To: "hand", Selector: ZoneSelector.First, Count: Members),
            new StateTransform.Shuffle(Draw: "coin", Row: "deck"),
        }) {
            var plain = Compile(context: Context(section: Section()), transform: transform).Price.Units;
            var beside = Compile(context: Context(section: Section(unrelated)), transform: transform).Price.Units;

            Assert.Equal(actual: beside, expected: plain);
        }
    }
}
