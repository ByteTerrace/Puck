using Puck.Maths;
using Xunit;

namespace Puck.State.Tests;

/// <summary>CONTRACT UNDER TEST: a <see cref="CellSetExpression"/> source holds a position by the live value its
/// cell reads at the lowering's <see cref="ArenaTime"/> — the value every other rule read of that cell answers — so a
/// zone cell, a slot family member, and a token family member that advance join the set when their live value enters
/// the range while their stored bases stay outside it.</summary>
public sealed class CellSetLiveSourceLawTests {
    private static readonly string[] Tokens = ["t0", "t1", "t2"];
    private static readonly StateAdvance OnePerSecond = new(
        PerSecondDenominator: 1L,
        PerSecondNumerator: 1L
    );
    private static readonly ArenaTime TwoSecondsIn = ArenaTime.At(
        engineTick: (2UL * ((ulong)FixedTickConversion.TicksPerSecond)),
        tick: 60UL
    );

    private static CellName Name(string value) => CellName.Parse(candidate: value);
    // A token-keyed row whose cells hold zero, except that the cell named by advancing carries its own advance.
    private static StateRow OverTokens(string name, long value, string? advancing) => new(
        Name: Name(value: name),
        Kind: CellKind.Int,
        Capacity: Tokens.Length,
        Domain: new StateDomain.KeysOf(Row: Name(value: "tokens")),
        Cells: [.. Tokens.Select(selector: token => new StateCell(
                Advance: ((token == advancing) ? OnePerSecond : null),
                Key: Name(value: token),
                Value: CellValue.Int(value: value)
            ))]
    );
    private static StateRow Slot(string name, long value, StateAdvance? advance) => new(
        Name: Name(value: name),
        Kind: CellKind.Int,
        Advance: advance,
        Cells: [new StateCell(
                Key: StateRow.SlotKey,
                Value: CellValue.Int(value: value)
            )]
    );
    private static StateArena Arena() {
        var section = new StateSection(
            Rows: [
                new StateRow(
                    Name: Name(value: "tokens"),
                    Kind: CellKind.Int,
                    Capacity: Tokens.Length,
                    Cells: [.. Tokens.Select(selector: token => new StateCell(
                            Key: Name(value: token),
                            Value: CellValue.Int(value: 0L)
                        ))]
                ),
                OverTokens(
                    advancing: "t0",
                    name: "level",
                    value: 0L
                ),
                Slot(
                    advance: OnePerSecond,
                    name: "hp0",
                    value: 0L
                ),
                Slot(
                    advance: null,
                    name: "hp1",
                    value: 1L
                ),
                OverTokens(
                    advancing: "t1",
                    name: "lv0",
                    value: 0L
                ),
                OverTokens(
                    advancing: null,
                    name: "lv1",
                    value: 9L
                ),
            ],
            Families: [
                new StateFamily(
                    Name: Name(value: "hp"),
                    Size: 2
                ),
                new StateFamily(
                    Name: Name(value: "lv"),
                    Size: 2
                ),
            ]
        );

        return new StateArena(
            catalog: StateCatalog.Compile(section: section),
            options: null,
            section: section,
            time: ArenaTime.Origin
        );
    }
    private static int[] Members(StateArena arena, CellSetExpression expression, ArenaTime time) {
        Assert.True(
            condition: CellSetLowering.TryLower(
                arena: arena,
                expression: expression,
                reason: out var reason,
                set: out var set,
                time: in time
            ),
            userMessage: reason
        );

        return [.. Enumerable.Range(
            count: set.Length,
            start: 0
        ).Where(predicate: set.Contains)];
    }

    public static TheoryData<string, int> Sources => new() {
        { "zone", 0 },
        { "slotFamily", 0 },
        { "tokenFamily", 1 },
    };

    private static CellSetExpression Source(string source) => source switch {
        "zone" => new CellSetExpression.Zone(
            High: 2L,
            Low: 2L,
            Row: Name(value: "level")
        ),
        "slotFamily" => new CellSetExpression.Family(
            High: 2L,
            Low: 2L,
            Name: Name(value: "hp")
        ),
        _ => new CellSetExpression.Family(
            High: 2L,
            Low: 2L,
            Name: Name(value: "lv")
        ),
    };

    [MemberData(nameof(Sources))]
    [Theory]
    public void ASourceHoldsAPositionByItsLiveValueAtTheLoweringsTime(string source, int advancing) {
        var arena = Arena();
        var expression = Source(source: source);

        Assert.Empty(collection: Members(
            arena: arena,
            expression: expression,
            time: ArenaTime.Origin
        ));
        Assert.Equal(
            actual: Members(
                arena: arena,
                expression: expression,
                time: TwoSecondsIn
            ),
            expected: [advancing]
        );
    }
}
