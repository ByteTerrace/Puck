using System.Text.Json.Nodes;
using Puck.Transpiler.Diagnostics;
using Puck.Transpiler.Parsing;
using Xunit;

namespace Puck.World.Transpiler.Tests;

public sealed class ProductionLoweringTests {
    private static JsonObject Lower(string source) => WorldCompiler.Compile(source: source).RequireJson();

    [Fact]
    public void CancellationStopsLoweringBeforeExpansion() {
        using var cancellation = new CancellationTokenSource();

        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(testCode: () => WorldCompiler.Compile(
            cancellationToken: cancellation.Token,
            source: "value: range(0, 1000)"
        ));
    }
    [InlineData("position [d,0,0]\ndelaySeconds: d")]
    [InlineData("delaySeconds: d\nposition [d,0,0]")]
    [Theory]
    public void ConstantUnitsAreCheckedAtEveryDestination(string body) {
        var result = WorldCompiler.Compile(
            cancellationToken: TestContext.Current.CancellationToken,
            source: ("let d = 1m\n" + body)
        );

        Assert.Single(
            collection: result.Diagnostics,
            predicate: d => (d.Code == PuckDiagnosticCodes.UnitNotAdmitted)
        );
    }
    [InlineData("floorModulo(9007199254740993, 2)", "1")]
    [InlineData("9007199254740993 % 2", "1")]
    [InlineData("9007199254740993 + 2", "9007199254740995")]
    [InlineData("9007199254740993 > 9007199254740992", "1")]
    [InlineData("minimum(9007199254740993, 9007199254740995)", "9007199254740993")]
    [InlineData("round(9007199254740993)", "9007199254740993")]
    [InlineData("sort([9007199254740993, 9007199254740992])", "[9007199254740992,9007199254740993]")]
    [InlineData("distinct([9007199254740993, 9007199254740992, 9007199254740993])", "[9007199254740993,9007199254740992]")]
    [InlineData("distinct([{a:1,b:2}, {b:2,a:1}, {a:2,b:1}])", "[{\"a\":1,\"b\":2},{\"a\":2,\"b\":1}]")]
    [Theory]
    public void ExactNumbersAndStructuralCollections(string expression, string expected) {
        Assert.True(condition: JsonNode.DeepEquals(
            node1: JsonNode.Parse(expected),
            node2: Lower(source: $"value: {expression}")["value"]
        ));
    }
    [Fact]
    public void ExcessiveNestingIsRefusedBeforeRecursiveParsing() {
        var result = PuckParser.ParseDocumentWithDiagnostics(((("value: " + new string(
            c: '[',
            count: 10000
        )) + "0") + new string(
            c: ']',
            count: 10000
        )));

        Assert.Contains(
            collection: result.Diagnostics,
            filter: d => (d.Code == PuckDiagnosticCodes.EvaluationLimit)
        );
    }
    [Fact]
    public void FailedConvenienceCompilationCannotEmitPartialData() {
        Assert.Throws<InvalidOperationException>(testCode: () => WorldCompiler.Compile(
            cancellationToken: TestContext.Current.CancellationToken,
            source: "missing()"
        ).RequireJson());
    }
    [Fact]
    public void FormatterPreservesRawAndInterpolatedLiteralValues() {
        const string Source = "value: \"\"\"a:     b\n  indent\n\n{}[]\"\"\"\nother: $\"\"\"a  {1+2}\n   b\"\"\"";
        var formatted = PuckFormat.Format(source: Source);

        Assert.Equal(
            formatted,
            PuckFormat.Format(source: formatted)
        );
        Assert.True(condition: JsonNode.DeepEquals(
            node1: Lower(source: Source),
            node2: Lower(source: formatted)
        ));
    }
    [InlineData("value: clamp(1, 5, 2)")]
    [InlineData("value: squareRoot(-1)")]
    [InlineData("value: 1e999")]
    [InlineData("let a = b\nlet b = a\nvalue: a")]
    [InlineData("let a = 1\nlet a = 2\nvalue: a")]
    [InlineData("missing()")]
    [InlineData("template make(v) { value: v }\nmake()")]
    [InlineData("template make(v) { value: v }\nmake(1, 2)")]
    [InlineData("template make() { make() }\nmake()")]
    [InlineData("value: range(0, 1000001)")]
    [InlineData("value: range(9223372036854775807, 2)")]
    [Theory]
    public void InvalidOrUnboundedInputHasLocatedDiagnostic(string source) {
        var parsed = WorldCompiler.Compile(
            cancellationToken: TestContext.Current.CancellationToken,
            source: source
        );

        Assert.True(condition: parsed.Diagnostics.HasErrors);
        Assert.All(
            parsed.Diagnostics.Where(predicate: d => (d.Severity == DiagnosticSeverity.Error)),
            d => Assert.True(condition: (d.Span.Line > 0))
        );
    }
    [Fact]
    public void RepeatedCopiesCannotBypassTheWorkBudget() {
        var result = WorldCompiler.Compile(
            cancellationToken: TestContext.Current.CancellationToken,
            source: "let a = range(0, 10000)\nvalue: map(range(0, 1000), i => a)"
        );

        Assert.Contains(
            collection: result.Diagnostics,
            filter: d => (d.Code == PuckDiagnosticCodes.EvaluationLimit)
        );
    }
    [Fact]
    public void TemplateArgumentsCaptureEachCallerIteration() {
        var json = Lower(source: "template make(v) { rows [v + 1] }\nfor i in [4,9] { make(i) }");

        Assert.True(condition: JsonNode.DeepEquals(
            node1: JsonNode.Parse("[5,10]"),
            node2: json["rows"]
        ));
    }
    [Fact]
    public void TemplateParametersCannotRebindDocumentConstants() {
        var json = Lower(source: "let seed = 3\nlet copy = seed\ntemplate make(seed, next = seed + 1) { first: copy\nsecond: next }\nmake(7)");

        Assert.Equal(
            3L,
            json["first"]!.GetValue<long>()
        );
        Assert.Equal(
            8L,
            json["second"]!.GetValue<long>()
        );
    }
}
