using Puck.Assets.Documents;
using Xunit;

namespace Puck.State.Rules.Tests;

/// <summary>CONTRACT UNDER TEST: a live jump destination is an in-range integer ordinal; it never aliases a
/// different cell through text conversion or a narrowing cast.</summary>
public sealed class BoardJumpDistanceOperandLawTests {
    private static StateSection Section(long destination, CellKind destinationKind = CellKind.Int) => new(
        Lattices: [new LatticeTopology.Grid(
                Name: "line",
                Origin: new DocumentVector3(
                    x: 0f,
                    y: 0f,
                    z: 0f
                ),
                CellSize: 1f,
                Width: 3,
                Depth: 1
            )],
        Rows: [
            new StateRow(
                Name: RulesFixture.Name(value: "board"),
                Kind: CellKind.Int,
                Domain: new StateDomain.CellsOf(
                    Empty: 0L,
                    Topology: "line"
                ),
                Cells: [
                    new StateCell(Key: RulesFixture.Name(value: "0"), Value: CellValue.Int(value: 1L)),
                    new StateCell(Key: RulesFixture.Name(value: "1"), Value: CellValue.Int(value: 2L)),
                ]
            ),
            new StateRow(
                Name: RulesFixture.Name(value: "destination"),
                Kind: destinationKind,
                Capacity: 1,
                Cells: [new StateCell(
                        Key: RulesFixture.Name(value: "to"),
                        Value: ((destinationKind == CellKind.Int)
                            ? CellValue.Int(value: destination)
                            : CellValue.Text(value: destination.ToString(provider: System.Globalization.CultureInfo.InvariantCulture)))
                    )]
            ),
        ]
    );
    private static Rule Rule() => RulesFixture.Rule(
        effects: [new ActionEffect.SetState(Key: "0", State: "board", Value: 1m)],
        gate: new ActionPredicate.CompareState(
            Comparison: ExpressionOp.Equal,
            Key: "0",
            State: "$board:jumpDistance:board:cell:destination:to",
            Value: 0m
        ),
        name: "jump"
    );

    [Fact]
    public void ATextLiveJumpDestinationIsRefusedAtCompileTime() {
        var context = EvaluatorFixture.Context(section: Section(destination: 2L, destinationKind: CellKind.Text));
        var exception = Assert.Throws<RuleException>(testCode: () => RuleCompiler.Compile(
            context: context,
            rule: Rule()
        ));

        Assert.Equal(
            actual: exception.Refusal,
            expected: RuleRefusal.StateCellUnaddressable
        );
    }
    [InlineData(-1L)]
    [InlineData(3L)]
    [InlineData(4_294_967_296L)]
    [InlineData(281_474_976_710_656L)]
    [Theory]
    public void AnInvalidLiveJumpDestinationDoesNotAliasCellZero(long destination) {
        var section = Section(destination: destination);
        var context = EvaluatorFixture.Context(section: section);
        var compiled = RuleCompiler.Compile(
            context: context,
            rule: Rule()
        );
        var operand = Assert.IsType<BoardOperand>(@object: compiled.Gate[0].LeftSource.Operand);
        var host = EvaluatorFixture.Host(
            context: context,
            section: section
        );

        Assert.Equal(
            actual: operand.Read(reader: host).Value,
            expected: -1L
        );
    }
    [Fact]
    public void AnAbsentLiveJumpDestinationIsUnreachable() {
        var section = Section(destination: 2L);
        var context = EvaluatorFixture.Context(section: section);
        var compiled = RuleCompiler.Compile(
            context: context,
            rule: Rule()
        );
        var operand = Assert.IsType<BoardOperand>(@object: compiled.Gate[0].LeftSource.Operand);
        var host = EvaluatorFixture.Host(
            context: context,
            section: section
        );

        Assert.True(condition: host.Arena.TryRemove(
            key: operand.TargetFrom!.Value.Key,
            reason: out _,
            rowOrdinal: EvaluatorFixture.Ordinal(
                host: host,
                row: "destination"
            )
        ));

        Assert.Equal(
            actual: operand.Read(reader: host).Value,
            expected: -1L
        );
    }
    [Fact]
    public void AnInRangeLiveJumpDestinationUsesItsExactOrdinal() {
        var section = Section(destination: 2L);
        var context = EvaluatorFixture.Context(section: section);
        var compiled = RuleCompiler.Compile(
            context: context,
            rule: Rule()
        );
        var operand = Assert.IsType<BoardOperand>(@object: compiled.Gate[0].LeftSource.Operand);
        var host = EvaluatorFixture.Host(
            context: context,
            section: section
        );

        Assert.Equal(
            actual: operand.Read(reader: host).Value,
            expected: 1L
        );
    }
}
