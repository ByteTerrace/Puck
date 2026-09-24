using Puck.Assets.Documents;
using Puck.Maths;
using Puck.Testing;
using Xunit;

namespace Puck.State.Rules.Tests;

/// <summary>CONTRACT UNDER TEST: a transform that reads cell values reads each one live at the firing's
/// <see cref="ArenaTime"/> — the value every other rule read of that cell answers — and a transform that writes a
/// cell able to carry its own trait writes through the live door. A sort key, a knowledge source and position, an
/// arrangement rank and a written mask that advance are taken at what they read now rather than at their stored
/// bases, and an observed value lands in a remembered cell that advances as the value it holds at the firing's time.
/// Every trait here sits where a world document may author it, which the fixture proves against the world
/// validator.</summary>
public sealed class TransformLiveReadLawTests {
    private const ulong Seconds = 4UL;

    private static readonly StateAdvance OnePerSecond = PerSecond(
        denominator: 1L,
        numerator: 1L
    );

    private static CellName Name(string value) => CellName.Parse(candidate: value);
    private static StateCell Cell(string key, long value, StateAdvance? advance = null) => new(
        Advance: advance,
        Key: Name(value: key),
        Value: CellValue.Int(value: value)
    );
    // An ordered zone holds its members as true membership cells.
    private static StateCell Member(string key) => new(
        Key: Name(value: key),
        Value: CellValue.Bool(value: true)
    );
    private static StateAdvance PerSecond(long numerator, long denominator) => new(
        PerSecondDenominator: denominator,
        PerSecondNumerator: numerator
    );
    private static StateRow OverTokens(string name, params StateCell[] cells) => new(
        Name: Name(value: name),
        Kind: CellKind.Int,
        Capacity: 3,
        Domain: new StateDomain.KeysOf(Row: Name(value: "tokens")),
        Cells: cells
    );
    private static StateRow Slot(string name, long value) => new(
        Name: Name(value: name),
        Kind: CellKind.Int,
        Advance: OnePerSecond,
        Cells: [new StateCell(
                Key: StateRow.SlotKey,
                Value: CellValue.Int(value: value)
            )]
    );
    private static StateRow Board(string name, CellKind kind, params StateCell[] cells) => new(
        Name: Name(value: name),
        Kind: kind,
        Domain: new StateDomain.CellsOf(
            Empty: 0L,
            Topology: "map"
        ),
        Cells: cells
    );
    // A remembered cell carries its own stamp; the one that advances is the one the live write must re-anchor.
    private static StateCell Remembered(string key, StateAdvance? advance) => (Cell(
        advance: advance,
        key: key,
        value: 0L
    ) with {
        Observation = new StateObservation(
            Tick: 0L,
            Visible: false
        ),
    });
    private static StateSection Section() => new(
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
                Capacity: 3,
                Cells: [Cell(key: "a", value: 1L), Cell(key: "b", value: 2L), Cell(key: "c", value: 3L)]
            ),
            new StateRow(
                Name: Name(value: "deck"),
                Kind: CellKind.Bool,
                Capacity: 3,
                Domain: new StateDomain.KeysOf(
                    Ordered: true,
                    Row: Name(value: "tokens")
                ),
                Cells: [Member(key: "a"), Member(key: "b"), Member(key: "c")]
            ),
            OverTokens("rank", Cell(key: "a", value: 3L), Cell(advance: OnePerSecond, key: "b", value: 1L), Cell(key: "c", value: 2L)),
            new StateRow(
                Name: Name(value: "scores"),
                Kind: CellKind.Int,
                Capacity: 3,
                Cells: [Cell(key: "x", value: 5L), Cell(advance: OnePerSecond, key: "y", value: 2L), Cell(key: "z", value: 9L)]
            ),
            OverTokens("truth", Cell(advance: OnePerSecond, key: "a", value: 4L), Cell(key: "b", value: 6L)),
            OverTokens("positions", Cell(key: "a", value: 0L), Cell(advance: PerSecond(denominator: 4L, numerator: 1L), key: "b", value: 1L)),
            Board("sight", CellKind.Bool, new StateCell(Key: Name(value: "0"), Value: CellValue.Bool(value: true)), new StateCell(Key: Name(value: "2"), Value: CellValue.Bool(value: true))),
            OverTokens("known", Remembered(advance: null, key: "a"), Remembered(advance: OnePerSecond, key: "b")) with {
                Knowledge = new StateKnowledge(
                    Mask: "sight",
                    Positions: "positions",
                    Source: "truth"
                ),
                Visibility = new StateVisibility(Readers: ["seat1"]),
            },
            // Rank 0 at boot is the deck's own order; four seconds on the rank reads 4.
            Slot(
                name: "order",
                value: 0L
            ),
            // Cell 0 alone at boot; four seconds on the mask reads 0b101, cells 0 and 2.
            Slot(
                name: "mask",
                value: 1L
            ),
            Board("target", CellKind.Int),
        ]
    );
    private static (ArenaEffectHost Host, RuleCompileContext Context) Arrange() {
        var section = Section();
        var context = new RuleCompileContext(
            catalog: StateCatalog.Compile(section: section),
            generators: null,
            patterns: null,
            section: section,
            simulationRateHz: 30,
            tables: null,
            vocabulary: RuleVocabulary.Core
        );
        var host = new ArenaEffectHost(arena: new StateArena(
            catalog: context.Catalog,
            options: null,
            section: section,
            time: ArenaTime.Origin
        ));

        Advance(
            host: host,
            seconds: Seconds
        );

        return (host, context);
    }
    private static void Advance(ArenaEffectHost host, ulong seconds) => host.Advance(
        engineTick: (seconds * ((ulong)FixedTickConversion.TicksPerSecond)),
        tick: (seconds * 30UL)
    );
    private static void Apply(ArenaEffectHost host, RuleCompileContext context, StateTransform transform) {
        Assert.True(
            condition: RuleCompiler.TryResolveTransform(
                context: context,
                reason: out var reason,
                resolved: out var resolved,
                transform: transform
            ),
            userMessage: reason
        );
        Assert.True(
            condition: host.TryTransform(
                binding: ArenaTransformBinding.None,
                moved: out _,
                refusal: out var refusal,
                transform: resolved
            ),
            userMessage: refusal.Reason
        );
    }
    private static long Live(ArenaEffectHost host, RuleCompileContext context, string row, string key) {
        Assert.True(condition: host.Arena.TryReadLiveNumber(
            key: host.Arena.Keys.Intern(name: Name(value: key)),
            rowOrdinal: RuleCompiler.ResolveRowOrdinal(
                context: context,
                name: row
            ),
            time: host.Time,
            value: out var value
        ));

        return value;
    }
    private static string Listing(ArenaEffectHost host, RuleCompileContext context, string row) => TransformFixture.Listing(
        arena: host.Arena,
        rowOrdinal: RuleCompiler.ResolveRowOrdinal(
            context: context,
            name: row
        )
    );

    [Fact]
    public void TheFixtureIsAShapeAWorldDocumentAdmits() => Assert.Equal(
        actual: WorldAdmission.Refusal(section: Section()),
        expected: string.Empty
    );

    public static TheoryData<string, string, string> Orderings => new() {
        // The attribute b advanced from 1 to 5, so it sorts last rather than first.
        { "sort by a zone's attribute", "deck", "c=1,a=1,b=1" },
        // The row's own y advanced from 2 to 6, so it sorts between x and z rather than first.
        { "sort a keyed row by itself", "scores", "x=5,y=2,z=9" },
        // Rank 4 of three tokens is the permutation (2, 0, 1); the stored rank 0 would leave the deck as it is.
        { "arrange by an advancing rank", "deck", "c=1,a=1,b=1" },
    };

    [MemberData(nameof(Orderings))]
    [Theory]
    public void AReorderingTakesEachKeyAtItsLiveValue(string transform, string row, string expected) {
        var (host, context) = Arrange();

        Apply(
            context: context,
            host: host,
            transform: transform switch {
                "sort by a zone's attribute" => new StateTransform.Sort(By: [new SortKey(Row: "rank")], Row: "deck"),
                "sort a keyed row by itself" => new StateTransform.Sort(By: [new SortKey(Row: "scores")], Row: "scores"),
                _ => new StateTransform.Arrange(From: "order", Row: "deck"),
            }
        );
        Assert.Equal(
            actual: Listing(
                context: context,
                host: host,
                row: row
            ),
            expected: expected
        );
    }
    [Fact]
    public void AWriteSetPaintsTheCellsItsMasksLiveValueNames() {
        var (host, context) = Arrange();
        var target = RuleCompiler.ResolveRowOrdinal(
            context: context,
            name: "target"
        );

        Apply(
            context: context,
            host: host,
            transform: new StateTransform.WriteSet(
                Row: "target",
                Set: "mask",
                Value: 2L
            )
        );

        var painted = new long[4];

        Assert.True(condition: host.Arena.TryReadBoard(
            rowOrdinal: target,
            values: painted
        ));
        Assert.Equal(
            actual: painted,
            expected: [2L, 0L, 2L, 0L]
        );
    }
    [Fact]
    public void AnObserveReadsLiveSourcesAndRemembersThroughTheLiveDoor() {
        var (host, context) = Arrange();

        Apply(
            context: context,
            host: host,
            transform: new StateTransform.Observe(Row: "known")
        );

        // a stands on cell 0 and its source advanced from 4; b's position advanced from 1 onto the visible cell 2.
        Assert.Equal(
            actual: Live(context: context, host: host, key: "a", row: "known"),
            expected: (4L + ((long)Seconds))
        );
        Assert.Equal(
            actual: Live(context: context, host: host, key: "b", row: "known"),
            expected: 6L
        );

        // The remembered b advances on its own from what it was told at the firing, not from its boot epoch.
        Advance(
            host: host,
            seconds: (Seconds + 1UL)
        );
        Assert.Equal(
            actual: Live(context: context, host: host, key: "b", row: "known"),
            expected: 7L
        );
    }
}
