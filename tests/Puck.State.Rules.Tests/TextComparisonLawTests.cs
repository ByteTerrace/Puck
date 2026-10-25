using Xunit;

namespace Puck.State.Rules.Tests;

/// <summary>Text comparisons use ordinal equality, live reads and the same absence rules as numeric gates.</summary>
public sealed class TextComparisonLawTests {
    private static CellName Name(string value) => CellName.Parse(value);
    private static (StateArena Arena, RuleCompileContext Context, ArenaEffectHost Reader) Arrange(string left, string right) {
        var section = new StateSection(Rows: [
            new StateRow(Name("left"), CellKind.Text, Cells: [new StateCell(StateRow.SlotKey, CellValue.Text(left))]),
            new StateRow(Name("right"), CellKind.Text, Cells: [new StateCell(StateRow.SlotKey, CellValue.Text(right))]),
            new StateRow(Name("number"), CellKind.Int, Cells: [new StateCell(StateRow.SlotKey, CellValue.Int(0))]),
        ]);
        var catalog = StateCatalog.Compile(section);
        var arena = new StateArena(catalog: catalog, section: section, time: ArenaTime.Origin, options: null);
        var context = new RuleCompileContext(section, catalog, null, null, null, 60, RuleVocabulary.Core);
        return (arena, context, new ArenaEffectHost(arena));
    }
    [Theory]
    [InlineData("", "", true)]
    [InlineData("king", "king", true)]
    [InlineData("king", "King", false)]
    [InlineData("é", "e\u0301", false)]
    [InlineData("灯", "灯", true)]
    public void LiteralAndRowComparandsReadTheSameOrdinalText(string left, string right, bool expected) {
        var (_, context, reader) = Arrange(left, right);
        foreach (var comparison in new[] { ExpressionOp.Equal, ExpressionOp.NotEqual }) {
            foreach (var gate in new ActionPredicate[] {
                new ActionPredicate.CompareState("left", comparison, Text: right),
                new ActionPredicate.CompareState("left", comparison, ComparandState: "right"),
            }) {
                var compiled = RuleCompiler.Compile(new Rule(Name("compare"), [], Gate: gate), context, requireEffects: false);
                var trace = new List<string>();
                Assert.Equal(comparison == ExpressionOp.Equal ? expected : !expected,
                    RuleEvaluation.GateHolds(reader, compiled.Gate, out var fault, trace));
                Assert.Equal(ExpressionFault.None, fault);
                Assert.Single(trace);
            }
        }
    }
    [Fact]
    public void MixedKindsOrderingAndAmbiguousComparandsRefuseAtCompileTime() {
        var (_, context, _) = Arrange("a", "b");
        foreach (var gate in new ActionPredicate[] {
            new ActionPredicate.CompareState("left", ExpressionOp.Equal, Value: 0),
            new ActionPredicate.CompareState("number", ExpressionOp.Equal, Text: "0"),
            new ActionPredicate.CompareState("left", ExpressionOp.Less, Text: "b"),
            new ActionPredicate.CompareState("left", ExpressionOp.Equal, Value: 0, Text: "0"),
            new ActionPredicate.CompareState("left", ExpressionOp.Equal, ComparandState: "right", Text: "b"),
            new ActionPredicate.CompareState("left", ExpressionOp.Equal, Text: new string('x', StateCapacity.MaxTextValueLength + 1)),
        }) {
            Assert.Throws<RuleException>(() => RuleCompiler.Compile(new Rule(Name("refused"), [], Gate: gate), context, requireEffects: false));
        }
    }
}
