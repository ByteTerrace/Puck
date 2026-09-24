using Puck.Maths;
using Puck.Testing;
using Xunit;

namespace Puck.State.Rules.Tests;

/// <summary>CONTRACT UNDER TEST: a <c>$match</c> word reads each letter's live value at the evaluation's
/// <see cref="ArenaTime"/> — the value every other rule read of that cell answers — so a zone read through an
/// attribute row whose cells advance, and a token-keyed row whose own cells advance, match on what the cells hold now
/// rather than on their stored bases. The traits sit on the cells of a token-keyed row, the shape a world document
/// authors, which the fixture proves against the world validator.</summary>
public sealed class PatternLiveWordLawTests {
    private static readonly string[] Tokens = ["a", "b", "c"];
    private static readonly StateAdvance OnePerSecond = new(
        PerSecondDenominator: 1L,
        PerSecondNumerator: 1L
    );

    private static PatternRow Ones(string name, string? attribute) => new(
        Attribute: attribute,
        Kind: CellKind.Int,
        Name: RulesFixture.Name(value: name),
        Pattern: new PatternNode.Plus(Item: new PatternNode.Symbol(Name: "one")),
        Symbols: [new PatternSymbol(
                Max: 1L,
                Min: 1L,
                Name: RulesFixture.Name(value: "one")
            )]
    );

    private static readonly PatternRow[] Patterns = [
        Ones(
            attribute: "level",
            name: "throughLevel"
        ),
        Ones(
            attribute: null,
            name: "ownLevel"
        ),
    ];

    // An ordered zone holds true membership cells; an unordered token-keyed row holds each token's own number.
    private static StateRow OverTokens(string name, bool ordered) => new(
        Name: RulesFixture.Name(value: name),
        Kind: (ordered ? CellKind.Bool : CellKind.Int),
        Capacity: Tokens.Length,
        Domain: new StateDomain.KeysOf(
            Ordered: ordered,
            Row: RulesFixture.Name(value: "tokens")
        ),
        Cells: [.. Tokens.Select(selector: key => new StateCell(
                Advance: (ordered ? null : OnePerSecond),
                Key: RulesFixture.Name(value: key),
                Value: (ordered ? CellValue.Bool(value: true) : CellValue.Int(value: 0L))
            ))]
    );
    private static StateSection Section() => new(Rows: [
        new StateRow(
            Name: RulesFixture.Name(value: "tokens"),
            Kind: CellKind.Int,
            Capacity: Tokens.Length,
            Cells: [.. Tokens.Select(selector: key => new StateCell(
                    Key: RulesFixture.Name(value: key),
                    Value: CellValue.Int(value: 0L)
                ))]
        ),
        OverTokens(
            name: "pile",
            ordered: true
        ),
        OverTokens(
            name: "level",
            ordered: false
        ),
        // The row the fixture rule's effect writes.
        new StateRow(
            Name: RulesFixture.Name(value: "score"),
            Kind: CellKind.Int,
            Cells: [new StateCell(
                    Key: StateRow.SlotKey,
                    Value: CellValue.Int(value: 0L)
                )]
        ),
    ]);
    private static (PatternOperand Operand, ArenaEffectHost Host, int Level) Arrange(string spelling) {
        var section = Section();
        var context = new RuleCompileContext(
            catalog: StateCatalog.Compile(section: section),
            generators: null,
            patterns: Patterns,
            section: section,
            simulationRateHz: 30,
            tables: null,
            vocabulary: RuleVocabulary.Core
        );
        var compiled = RuleCompiler.Compile(
            context: context,
            rule: RulesFixture.Rule(
                gate: new ActionPredicate.CompareState(
                    Comparison: ExpressionOp.GreaterOrEqual,
                    Key: null,
                    State: spelling,
                    Value: 0m
                ),
                name: "live"
            )
        );
        var host = new ArenaEffectHost(arena: new StateArena(
            catalog: context.Catalog,
            options: null,
            section: section,
            time: ArenaTime.Origin
        ));

        return (Assert.IsType<PatternOperand>(@object: compiled.Gate[0].LeftSource.Operand), host, RuleCompiler.ResolveRowOrdinal(
            context: context,
            name: "level"
        ));
    }

    [Fact]
    public void TheFixtureIsAShapeAWorldDocumentAdmits() => Assert.Equal(
        actual: WorldAdmission.Refusal(
            patterns: Patterns,
            section: Section()
        ),
        expected: string.Empty
    );
    [InlineData("$match:throughLevel:pile")]
    [InlineData("$match:ownLevel:level")]
    [Theory]
    public void AWordReadsItsLettersLiveAtTheEvaluationsTime(string spelling) {
        var (operand, host, level) = Arrange(spelling: spelling);

        Assert.Equal(
            actual: operand.Read(reader: host).Value,
            expected: 0L
        );

        host.Advance(
            engineTick: ((ulong)FixedTickConversion.TicksPerSecond),
            tick: 30UL
        );

        foreach (var token in Tokens) {
            Assert.True(condition: host.Arena.TryReadLiveNumber(
                key: host.Arena.Keys.Intern(name: RulesFixture.Name(value: token)),
                rowOrdinal: level,
                time: host.Time,
                value: out var live
            ));
            Assert.Equal(
                actual: live,
                expected: 1L
            );
        }

        Assert.Equal(
            actual: operand.Read(reader: host).Value,
            expected: 1L
        );
    }
}
