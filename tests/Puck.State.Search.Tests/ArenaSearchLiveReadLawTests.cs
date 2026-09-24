using Puck.Maths;
using Puck.Testing;
using Xunit;

using static Puck.State.Search.Tests.SearchFixture;

namespace Puck.State.Search.Tests;

/// <summary>CONTRACT UNDER TEST: a search reads the rows its plan names live at the step's clocks — the value every
/// rule read of those cells answers — and moves a token through the live door. An advancing enable slot switches the
/// job on, an advancing revision slot is the revision the answer carries, and a token whose cell advances stands, and
/// is moved to, where a rule reads it now rather than at its stored base. Every trait here sits where a world
/// document may author it, which the fixture proves against the world validator.</summary>
public sealed class ArenaSearchLiveReadLawTests {
    private const int Cells = 4;

    private static readonly StateAdvance OnePerSecond = new(
        PerSecondDenominator: 1L,
        PerSecondNumerator: 1L
    );
    // Two seconds on: the token that booted on cell 0 stands on cell 2, and both slots read 2.
    private static readonly ArenaTime TwoSecondsIn = ArenaTime.At(
        engineTick: (2UL * ((ulong)FixedTickConversion.TicksPerSecond)),
        tick: 60UL
    );

    private static StateRow Advancing(StateRow row) => (row with { Advance = OnePerSecond });
    private static StateRow[] Rows() => [
        Keyed(name: "piece", ("t0", 0L)) with {
            Cells = [new StateCell(
                    Advance: OnePerSecond,
                    Key: Name(value: "t0"),
                    Value: CellValue.Int(value: 0L)
                )],
        },
        Slot(name: "turn", value: 0L),
        Slot(name: "verdict", value: 0L),
        Keyed(name: "legal", ("t0", 0L)),
        Keyed(name: "best", ("token", -1L), ("to", -1L), ("score", 0L), ("revision", -1L)),
        Advancing(row: Slot(name: "enabled", value: 0L)),
        Advancing(row: Slot(name: "revision", value: 0L)),
    ];
    // Accepts a candidate only while the moved token reads a cell of the board, so a move written beneath an
    // advancing clock (which would read past the board) is refused rather than kept.
    private static DelegateJudge OnBoard(Position position) {
        var piece = position.Ordinal(name: "piece");
        var token = position.Key(name: "t0");
        var turn = position.Ordinal(name: "turn");
        var verdict = position.Ordinal(name: "verdict");
        var slot = position.SlotKey;

        return new DelegateJudge(
            arena: position.Arena,
            judge: (in ArenaSearchView view) => {
                if (
                    !view.Arena.TryReadLiveNumber(
                    key: token,
                    rowOrdinal: piece,
                    time: ArenaTime.At(
                        engineTick: view.EngineTick,
                        tick: view.Tick
                    ),
                    value: out var cell
                ) ||
                    (((ulong)cell) >= Cells)
                ) {
                    return true;
                }

                _ = view.Arena.TryWrite(key: slot, operand: 1L, reason: out _, rowOrdinal: verdict, write: StateWriteKind.Set);
                _ = view.Arena.TryWrite(key: slot, operand: 1L, reason: out _, rowOrdinal: turn, write: StateWriteKind.Add);

                return true;
            },
            score: static (in ArenaSearchView _) => 0L
        );
    }
    // A controlled job names the enable and revision slots; an uncontrolled one runs whenever it is stepped.
    private static List<(string Row, string Key, long Value)> Land(bool controlled) {
        var position = new Position(rows: Rows());
        var search = Build(
            judge: OnBoard(position: position),
            plan: new SearchPlan(
                Name: "moves",
                Tokens: "piece",
                Topology: null,
                Zones: [],
                CellCount: Cells,
                Turn: "turn",
                Verdict: "verdict",
                Off: -1L,
                Nodes: 64,
                Work: SearchWork.NodeBounded(judge: 1L),
                Depth: 1,
                Best: "best",
                Shapes: [new SearchShapePlan(
                        Kind: SearchShapeKind.Relocate,
                        Displace: false,
                        Directions: [],
                        CompanionIndex: -1
                    )],
                Legal: "legal",
                Enabled: (controlled ? "enabled" : null),
                Revision: (controlled ? "revision" : null)
            ) {
                Scored = true,
            },
            position: position
        );

        return RunArena(
            catalog: position.Catalog,
            engineTick: TwoSecondsIn.EngineTick,
            search: search,
            tick: TwoSecondsIn.Tick
        );
    }

    [Fact]
    public void TheFixtureIsAShapeAWorldDocumentAdmits() => Assert.Equal(
        actual: WorldAdmission.Refusal(section: new StateSection(Rows: Rows())),
        expected: string.Empty
    );
    [Fact]
    public void AnAdvancingEnableSlotRunsTheJobAndAnAdvancingRevisionIsTheOneItCarries() => Assert.Contains(
        collection: Land(controlled: true),
        expected: ("best", "revision", 2L)
    );
    [Fact]
    public void ATokenMovesFromAndToWhereItsLiveCellSays() => Assert.Contains(
        collection: Land(controlled: false),
        // From cell 2: cells 0, 1 and 3, each read back on the board.
        expected: ("legal", "t0", 0b1011L)
    );
}
