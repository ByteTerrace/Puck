using Xunit;

namespace Puck.World.Schema.Tests;

/// <summary>Every expression the shipped worlds author prints to an infix spelling that parses back to the same
/// tokens, so an author can move any of them between the two spellings without changing what compiles.</summary>
public sealed class WorldExpressionAuthoringLawTests {
    [Fact]
    public void ARuleAuthoredInfixCompilesToTheSameProgramAsItsTokens() {
        var infix = Fixtures.BuildDocument() with {
            StateRaw = new WorldStateSection(World: [new WorldStateRow(
                CellName.Parse(candidate: "hp"),
                CellKind.Int,
                Cells: [new StateCell(
                        WorldStateRow.SlotKey,
                        CellValue.Int(value: 5L)
                    )]
            )]),
            Rules = [new WorldRule(
                CellName.Parse(candidate: "r"),
                [new ActionEffect.SetState(
                        State: "hp",
                        Expression: ExpressionProgram.Parse(text: "maximum(hp - 1, 0)")
                    )]
            )],
        };
        var tokens = infix with {
            Rules = [new WorldRule(
                CellName.Parse(candidate: "r"),
                [new ActionEffect.SetState(
                        State: "hp",
                        Expression: new ExpressionProgram(Instructions: [
                Instruction.Operand(name: "hp"), Instruction.Constant(value: 1m), Instruction.Of(operation: ExpressionOp.Subtract), Instruction.Constant(value: 0m), Instruction.Of(operation: ExpressionOp.Maximum),
            ])
                    )]
            )],
        };
        var fromInfix = WorldDefinitionSerialization.Deserialize(utf8Json: WorldDefinitionSerialization.Serialize(definition: infix));
        var fromTokens = WorldDefinitionSerialization.Deserialize(utf8Json: WorldDefinitionSerialization.Serialize(definition: tokens));

        Assert.Equal(
            WorldFactsCompiler.CompileAll(definition: fromTokens)[0].Effects[0].Describe,
            WorldFactsCompiler.CompileAll(definition: fromInfix)[0].Effects[0].Describe
        );
        Assert.Contains(
            "\"instructions\"",
            System.Text.Encoding.UTF8.GetString(bytes: WorldDefinitionSerialization.Serialize(definition: fromInfix)),
            StringComparison.Ordinal
        );
    }
}
