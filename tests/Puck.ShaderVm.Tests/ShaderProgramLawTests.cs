using System.Numerics;
using Xunit;

namespace Puck.ShaderVm.Tests;

public sealed class ShaderProgramLawTests {
    [Fact]
    public void DirectionalGradientFixture_IsExpressedWithGenericStackOperations() {
        var program = DirectionalGradientProgram();
        var operations = Enumerable
            .Range(count: program.InstructionCount, start: 0)
            .Select(selector: index => program.Instruction(index: index).Op)
            .ToArray();

        Assert.Equal(actual: operations[^1], expected: ShaderOp.Halt);
        Assert.Contains(collection: operations, expected: ShaderOp.Swizzle);
        Assert.Contains(collection: operations, expected: ShaderOp.Lerp);
        Assert.DoesNotContain(collection: operations, expected: ShaderOp.Jump);
    }
    [Fact]
    public void DirectionalGradientFixture_InterpolatesTheThreeStopsByElevation() {
        var program = DirectionalGradientProgram();
        var parameters = new Vector4[] {
            new(x: 1f, y: 0f, z: 0f, w: 0f),
            new(x: 0f, y: 1f, z: 0f, w: 0f),
            new(x: 0f, y: 0f, z: 1f, w: 0f),
        };

        Assert.Equal(actual: Evaluate(elevation: 1f, parameters: parameters, program: program), expected: parameters[2]);
        Assert.Equal(actual: Evaluate(elevation: 0f, parameters: parameters, program: program), expected: parameters[1]);
        Assert.Equal(actual: Evaluate(elevation: -1f, parameters: parameters, program: program), expected: parameters[0]);
    }
    [Fact]
    public void Hash3_AgreesWithThePinnedPcgLattice() {
        var expected = ShaderIsa.Pcg3d(x: 7u, y: 9u, z: 1337u);
        var program = ShaderExpressionCompiler.Compile(root: ShaderMath.Unit(value: ShaderMath.Hash3(value: ShaderExpression.Input(input: ShaderInput.Coordinate))));
        var coordinate = new Vector4(
            w: 0f,
            x: BitConverter.UInt32BitsToSingle(7u),
            y: BitConverter.UInt32BitsToSingle(9u),
            z: BitConverter.UInt32BitsToSingle(1337u)
        );
        var context = new ShaderContext(Coordinate: coordinate);

        Assert.Equal(
            actual: ShaderInterpreter.Evaluate(context: in context, parameters: [], program: program).X,
            expected: (((float)expected.X) * ShaderIsa.InverseTwoPow32)
        );
    }
    [Fact]
    public void Fbm2_StaysWithinTheUnitInterval() {
        var program = ShaderExpressionCompiler.Compile(root: ShaderMath.Fbm2(octaves: 4, position: ShaderExpression.Input(input: ShaderInput.Coordinate)));

        for (var step = 0; (step < 512); step++) {
            var context = new ShaderContext(Coordinate: new Vector4(x: (step * 0.37f), y: (step * -0.11f), z: 0f, w: 0f));
            var value = ShaderInterpreter.Evaluate(context: in context, parameters: [], program: program).X;

            Assert.InRange(actual: value, high: 1f, low: 0f);
        }
    }
    [Fact]
    public void Select_TakesEachLaneIndependently() {
        var program = ShaderExpressionCompiler.Compile(root: ShaderMath.Select(
            condition: ShaderExpression.Input(input: ShaderInput.Coordinate),
            whenFalse: ShaderExpression.Constant(value: -1f),
            whenTrue: ShaderExpression.Constant(value: 1f)
        ));
        var context = new ShaderContext(Coordinate: new Vector4(x: 1f, y: 0f, z: 1f, w: 0f));

        Assert.Equal(
            actual: ShaderInterpreter.Evaluate(context: in context, parameters: [], program: program),
            expected: new Vector4(x: 1f, y: -1f, z: 1f, w: -1f)
        );
    }
    [Fact]
    public void Compile_EvaluatesASharedNodeOnce() {
        var shared = ShaderMath.Sqrt(value: ShaderExpression.Input(input: ShaderInput.Coordinate));
        var program = ShaderExpressionCompiler.Compile(root: ((shared + shared) + shared));
        var roots = Enumerable
            .Range(count: program.InstructionCount, start: 0)
            .Count(predicate: index => (program.Instruction(index: index).Op == ShaderOp.SquareRoot));

        Assert.Equal(actual: roots, expected: 1);
    }
    [Fact]
    public void FromWords_RejectsAnUndeclaredOperation() {
        var words = DirectionalGradientProgram().Words.ToArray();

        words[ShaderIsa.HeaderWordCount] = 0x0000_00FEu;

        var exception = Assert.Throws<ArgumentException>(testCode: () => ShaderProgram.FromWords(words: words));

        Assert.Contains(actualString: exception.Message, comparisonType: StringComparison.Ordinal, expectedSubstring: nameof(ShaderOp));
    }
    [Fact]
    public void FromWords_CopiesTheValidatedInput() {
        var words = DirectionalGradientProgram().Words.ToArray();
        var program = ShaderProgram.FromWords(words: words);
        var first = program.Words.Span[ShaderIsa.HeaderWordCount];

        words[ShaderIsa.HeaderWordCount] = 0xFFFF_FFFFu;

        Assert.Equal(actual: program.Words.Span[ShaderIsa.HeaderWordCount], expected: first);
    }
    [Fact]
    public void Build_RejectsAStackUnderflow() {
        var builder = new ShaderProgramBuilder()
            .Append(op: ShaderOp.Add)
            .Append(op: ShaderOp.Halt);

        var exception = Assert.Throws<ArgumentException>(testCode: () => builder.Build());

        Assert.Contains(actualString: exception.Message, comparisonType: StringComparison.Ordinal, expectedSubstring: "underflows");
    }
    [Fact]
    public void Build_RefusesABackwardJump() {
        var builder = new ShaderProgramBuilder();

        _ = builder.LoadConstant(value: 1f);
        _ = builder.Append(op: ShaderOp.Jump, operand: 0u);

        var exception = Assert.Throws<ArgumentException>(testCode: () => builder.Build());

        Assert.Contains(actualString: exception.Message, comparisonType: StringComparison.Ordinal, expectedSubstring: "forward jumps only");
    }
    [Fact]
    public void Build_RefusesJoiningPathsThatDisagreeOnStackDepth() {
        var builder = new ShaderProgramBuilder();

        _ = builder.LoadConstant(value: 1f);

        var jump = builder.AppendJump(op: ShaderOp.JumpIfZero);

        _ = builder.LoadConstant(value: 2f);
        builder.PatchJump(jumpIndex: jump);

        var exception = Assert.Throws<ArgumentException>(testCode: () => builder.Build());

        Assert.Contains(actualString: exception.Message, comparisonType: StringComparison.Ordinal, expectedSubstring: "stack depth");
    }
    private static Vector4 Evaluate(ShaderProgram program, float elevation, Vector4[] parameters) {
        var context = new ShaderContext(Coordinate: new Vector4(x: 0f, y: elevation, z: 0f, w: 0f));

        return ShaderInterpreter.Evaluate(context: in context, parameters: parameters, program: program);
    }
    // The three-stop directional ramp: ground beneath the horizon, horizon to zenith above it.
    private static ShaderProgram DirectionalGradientProgram() {
        var elevation = ShaderExpression.Input(input: ShaderInput.Coordinate).Y;

        return ShaderExpressionCompiler.Compile(root: ShaderMath.Select(
            condition: ShaderMath.Step(edge: 0f, value: elevation),
            whenFalse: ShaderMath.Lerp(
                amount: ShaderMath.Saturate(value: (elevation + 1f)),
                from: ShaderExpression.Parameter(index: 0),
                to: ShaderExpression.Parameter(index: 1)
            ),
            whenTrue: ShaderMath.Lerp(
                amount: ShaderMath.Saturate(value: elevation),
                from: ShaderExpression.Parameter(index: 1),
                to: ShaderExpression.Parameter(index: 2)
            )
        ));
    }
}
