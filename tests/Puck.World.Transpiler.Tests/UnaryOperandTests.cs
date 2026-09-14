using System.Text.Json.Nodes;
using Puck.Transpiler.Ast;
using Puck.Transpiler.Diagnostics;
using Puck.Transpiler.Formatting;
using Puck.World.Transpiler.Lowering;
using Puck.Transpiler.Parsing;
using Puck.World.Transpiler.Validation;
using Xunit;

namespace Puck.World.Transpiler.Tests;

// A sign in front of something that is not a number. Before this, only a NUMERIC LITERAL could carry one, so a
// mirrored pair had to be written as two constants (`leftX`/`rightX`) instead of one `spread` and its negation.
public class UnaryOperandTests {
    private static JsonObject LowerSource(string source) => WorldDocumentEmitter.Lower(PuckParser.ParseDocument(source));
    private static JsonArray LoweredOrigin(string source) {
        var placements = Assert.IsType<JsonArray>(@object: LowerSource(source: source)["screens"]);

        return Assert.IsType<JsonArray>(@object: Assert.IsType<JsonObject>(@object: placements[0])["origin"]);
    }

    [Fact]
    public void TestDoubleNegationCancels() {
        var origin = LoweredOrigin(source: """
            schema: "puck.world.definition.v1"
            let spread = 1.8
            screens [
                { origin [--spread, 0, 0] }
            ]
            """);

        Assert.Equal(
            1.8,
            origin[0]?.GetValue<double>()
        );
    }
    [Fact]
    public void TestFormatterKeepsTheSignAttachedToItsOperand() {
        var formatted = PuckFormatter.Format("""
            schema: "puck.world.definition.v1"
            let spread = 1.8
            screens [
            { origin [-spread, 0, 0] }
            ]
            """);

        Assert.Contains(
            actualString: formatted,
            expectedSubstring: "-spread"
        );

        var origin = LoweredOrigin(source: formatted);

        Assert.Equal(
            -1.8,
            origin[0]?.GetValue<double>()
        );
    }
    [Fact]
    public void TestNegatedConstantCountsAsAUseOfIt() {
        var document = PuckParser.ParseDocument("""
            schema: "puck.world.definition.v1"
            let spread = 1.8
            screens [
                { origin [-spread, 0, 0] }
            ]
            """);
        var diagnostics = new DiagnosticBag();

        PuckLinter.Lint(
            diagnostics: diagnostics,
            document: document
        );

        // PUCK_LINT_001 is the unused-constant report: a negated reference is still a reference.
        Assert.DoesNotContain(
            collection: diagnostics,
            filter: diagnostic => diagnostic.Message.Contains(value: "spread")
        );
    }
    [Fact]
    public void TestNegatedConstantLowersToTheNegatedValue() {
        var origin = LoweredOrigin(source: """
            schema: "puck.world.definition.v1"
            let spread = 1.8
            screens [
                { origin [-spread, 0, 0] }
            ]
            """);

        Assert.Equal(
            -1.8,
            origin[0]?.GetValue<double>()
        );
    }
    [Fact]
    public void TestNegatedConstantParsesAsAUnaryOverAnIdentifier() {
        const string Source = """
            schema: "puck.world.definition.v1"
            let spread = 1.8
            """;

        var document = PuckParser.ParseDocument(Source);
        var constant = Assert.IsType<LetNode>(@object: document.Statements[0]);

        Assert.Equal(
            "spread",
            constant.Name
        );

        var negated = PuckParser.ParseDocument($"{Source}\nlet mirrored = -spread");
        var mirrored = Assert.IsType<LetNode>(@object: negated.Statements[1]);
        var unary = Assert.IsType<UnaryExpressionNode>(@object: mirrored.Value);

        Assert.Equal(
            "-",
            unary.Operator
        );
        Assert.Equal(
            "spread",
            Assert.IsType<IdentifierExpressionNode>(@object: unary.Operand).Name
        );
    }
    [Fact]
    public void TestNegatedIntegralConstantStaysIntegral() {
        var origin = LoweredOrigin(source: """
            schema: "puck.world.definition.v1"
            let step = 4
            screens [
                { origin [-step, 0, 0] }
            ]
            """);

        // The same JSON kind a written `-4` reaches the document as — a sign must not turn an integer into a double.
        Assert.Equal(
            -4L,
            origin[0]?.GetValue<long>()
        );
    }
    [Fact]
    public void TestNegationAppliesToAParenthesizedExpression() {
        var origin = LoweredOrigin(source: """
            schema: "puck.world.definition.v1"
            let spread = 1.8
            let gap = 0.2
            screens [
                { origin [-(spread + gap), 0, 0] }
            ]
            """);

        // -(1.8 + 0.2) is integral, so it narrows to a long exactly as a written `-2` would.
        Assert.Equal(
            -2L,
            origin[0]?.GetValue<long>()
        );
    }
    [Fact]
    public void TestNegationReadsAsTheRightOperandOfASubtraction() {
        var origin = LoweredOrigin(source: """
            schema: "puck.world.definition.v1"
            let spread = 1.8
            screens [
                { origin [0 - -spread, 0, 0] }
            ]
            """);

        Assert.Equal(
            1.8,
            origin[0]?.GetValue<double>()
        );
    }
    [Fact]
    public void TestSignedNumericLiteralStillFoldsIntoTheLiteral() {
        var document = PuckParser.ParseDocument("""
            schema: "puck.world.definition.v1"
            let drop = -1.5
            """);
        var constant = Assert.IsType<LetNode>(@object: document.Statements[0]);
        var literal = Assert.IsType<LiteralExpressionNode>(@object: constant.Value);

        Assert.Equal(
            -1.5,
            Assert.IsType<double>(@object: literal.Value)
        );
    }
    [Fact]
    public void TestWholeNumberArithmeticEvaluates() {
        var origin = LoweredOrigin(source: """
            schema: "puck.world.definition.v1"
            let step = 4
            screens [
                { origin [step * 3, step + 1, step - 6] }
            ]
            """);

        // The sample's own `let doubleFps = 60 * 2` never reached a field, so nothing caught that a whole-number
        // product evaluated to nothing.
        Assert.Equal(
            12L,
            origin[0]?.GetValue<long>()
        );
        Assert.Equal(
            5L,
            origin[1]?.GetValue<long>()
        );
        Assert.Equal(
            -2L,
            origin[2]?.GetValue<long>()
        );
    }
}
