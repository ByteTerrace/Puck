using Xunit;

namespace Puck.State.Tests;

/// <summary>Compile-time arithmetic preserves raw kinds, eager domain refusals, and authored validation.</summary>
public sealed class ConstantFoldingLawTests {
    private static readonly Rules.RuleCompileContext Context = new(
        null,
        StateCatalog.Compile(section: null),
        null,
        null,
        null,
        240,
        Rules.RuleVocabulary.Core
    );

    private static Rules.CompiledExpressionToken[] Compile(string text, CellKind kind = CellKind.Int) =>
        Rules.RuleCompiler.CompileExpression(
            ExpressionProgram.Parse(text: text),
            kind,
            "fold-law",
            "fold-law",
            Context
        );

    [InlineData("1 / 0")]
    [InlineData("0 * (1 / 0)")]
    [InlineData("1 ? 7 : 1 / 0")]
    [InlineData("0 ? repeatBits(256, 8) : 3")]
    [InlineData("9223372036854775807 + 1")]
    [InlineData("replicationMask(65)")]
    [InlineData("1 << 64")]
    [Theory]
    public void ConstantDomainFaultsStillRefuseAtRuntime(string text) {
        Assert.True(condition: (Compile(text).Length > 1));
        Assert.False(condition: ExpressionFunctionLawTests.TryEvalPublic(text: text));
    }
    [Fact]
    public void FixedResultsKeepRawScalingAndIntegerConditions() {
        Assert.Equal(
            229376L,
            Assert.Single(collection: Compile(
                kind: CellKind.Fixed,
                text: "1.5 * 2.25 + 0.125"
            )).Constant
        );
        Assert.Equal(
            98304L,
            Assert.Single(collection: Compile(
                kind: CellKind.Fixed,
                text: "1 < 2 ? 1.5 : 2.5"
            )).Constant
        );
    }
    [Fact]
    public void FoldingCannotHideInvalidNamesKindsOrOversizedAuthoredPrograms() {
        Assert.Throws<RuleException>(testCode: () => Compile("1 ? 7 : missing"));
        Assert.Throws<RuleException>(testCode: () => Compile(
            kind: CellKind.Fixed,
            text: "replicationMask(8)"
        ));
        var tokens = new List<Instruction> { Instruction.Constant(value: 0) };

        for (var i = 0; (i < RuleCapacity.MaxExpressionTokens); i++) { tokens.Add(item: Instruction.Of(operation: ExpressionOp.BitNot)); }
        Assert.Throws<RuleException>(testCode: () => Rules.RuleCompiler.CompileExpression(
            new ExpressionProgram(Instructions: tokens),
            CellKind.Int,
            "fold-law",
            "fold-law",
            Context
        ));
    }
    [Fact]
    public void IndependentConstantsFoldWithoutElidingAnApparentlyRedundantLiveRead() {
        var program = Compile("$tick * 0 + repeatBits(1, 8)");

        Assert.Equal(
            new[] { ExpressionOp.Operand, ExpressionOp.Constant, ExpressionOp.Multiply, ExpressionOp.Constant, ExpressionOp.Add },
            program.Select(selector: token => token.Operation)
        );
        Assert.IsType<Rules.TickOperand>(@object: program[0].Operand);
        Assert.Equal(
            0x0101010101010101L,
            program[3].Constant
        );
    }
    [InlineData("replicationMask(8)", 0x0101010101010101L)]
    [InlineData("repeatBits(128, 8)", unchecked((long)0x8080808080808080UL))]
    [InlineData("byteSwap(0x5000)", 22517998136852480L)]
    [InlineData("(1 << 63) >>> 63", 1L)]
    [InlineData("bitInsert(0, 15, 4, 4)", 240L)]
    [InlineData("bitField(240, 4, 4)", 15L)]
    [InlineData("clamp(-5, 0, 10)", 0L)]
    [InlineData("1 ? 3 : 7", 3L)]
    [InlineData("pairX(pair(7, 3))", 7L)]
    [Theory]
    public void SuccessfulSubtreesBecomeOneRawConstant(string text, long expected) {
        var token = Assert.Single(collection: Compile(text));

        Assert.Equal(
            ExpressionOp.Constant,
            token.Operation
        );
        Assert.Equal(
            expected,
            token.Constant
        );
    }
}
