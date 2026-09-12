using System.Text.Json.Nodes;
using Puck.Transpiler.Diagnostics;
using Puck.Transpiler.Formatting;
using Puck.Transpiler.Parsing;
using Puck.World.Transpiler.Lowering;
using Xunit;

namespace Puck.World.Transpiler.Tests;

public sealed class ProductionLoweringTests {
    [Theory]
    [InlineData("remainder(9007199254740993, 2)", "1")]
    [InlineData("9007199254740993 % 2", "1")]
    [InlineData("9007199254740993 + 2", "9007199254740995")]
    [InlineData("9007199254740993 > 9007199254740992", "1")]
    [InlineData("minimum(9007199254740993, 9007199254740995)", "9007199254740993")]
    [InlineData("round(9007199254740993)", "9007199254740993")]
    [InlineData("sort([9007199254740993, 9007199254740992])", "[9007199254740992,9007199254740993]")]
    [InlineData("distinct([9007199254740993, 9007199254740992, 9007199254740993])", "[9007199254740993,9007199254740992]")]
    [InlineData("distinct([{a:1,b:2}, {b:2,a:1}, {a:2,b:1}])", "[{\"a\":1,\"b\":2},{\"a\":2,\"b\":1}]")]
    public void ExactNumbersAndStructuralCollections(string expression, string expected) {
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(expected), Lower($"value: {expression}")["value"]));
    }

    [Theory]
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
    public void InvalidOrUnboundedInputHasLocatedDiagnostic(string source) {
        var parsed = PuckParser.ParseDocumentWithDiagnostics(source);
        if (parsed.Value is { } document) { WorldDocumentEmitter.LowerWithDiagnostics(document, diagnostics: parsed.Diagnostics, cancellationToken: TestContext.Current.CancellationToken); }
        Assert.True(parsed.Diagnostics.HasErrors);
        Assert.All(parsed.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error), d => Assert.True(d.Span.Line > 0));
    }

    [Fact]
    public void TemplateArgumentsCaptureEachCallerIteration() {
        var json = Lower("template make(v) { rows [v + 1] }\nfor i in [4,9] { make(i) }");
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse("[5,10]"), json["rows"]));
    }

    [Fact]
    public void TemplateParametersCannotRebindDocumentConstants() {
        var json = Lower("let seed = 3\nlet copy = seed\ntemplate make(seed, next = seed + 1) { first: copy\nsecond: next }\nmake(7)");
        Assert.Equal(3L, json["first"]!.GetValue<long>());
        Assert.Equal(8L, json["second"]!.GetValue<long>());
    }

    [Fact]
    public void CancellationStopsLoweringBeforeExpansion() {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var document = PuckParser.ParseDocument("value: range(0, 1000)");
        Assert.Throws<OperationCanceledException>(() => WorldDocumentEmitter.LowerWithDiagnostics(document, cancellationToken: cancellation.Token));
    }

    [Fact]
    public void RepeatedCopiesCannotBypassTheWorkBudget() {
        var document = PuckParser.ParseDocument("let a = range(0, 10000)\nvalue: map(range(0, 1000), i => a)");
        var result = WorldDocumentEmitter.LowerWithDiagnostics(document, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Contains(result.Diagnostics, d => d.Code == PuckDiagnosticCodes.EvaluationLimit);
    }

    [Theory]
    [InlineData("position [d,0,0]\ndelaySeconds: d")]
    [InlineData("delaySeconds: d\nposition [d,0,0]")]
    public void ConstantUnitsAreCheckedAtEveryDestination(string body) {
        var result = WorldDocumentEmitter.LowerWithDiagnostics(PuckParser.ParseDocument("let d = 1m\n" + body), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Single(result.Diagnostics, d => d.Code == PuckDiagnosticCodes.UnitNotAdmitted);
    }

    [Fact]
    public void FormatterPreservesRawAndInterpolatedLiteralValues() {
        const string source = "value: \"\"\"a:     b\n  indent\n\n{}[]\"\"\"\nother: $\"\"\"a  {1+2}\n   b\"\"\"";
        var formatted = PuckFormatter.Format(source);
        Assert.Equal(formatted, PuckFormatter.Format(formatted));
        Assert.True(JsonNode.DeepEquals(Lower(source), Lower(formatted)));
    }

    [Fact]
    public void ExcessiveNestingIsRefusedBeforeRecursiveParsing() {
        var result = PuckParser.ParseDocumentWithDiagnostics("value: " + new string('[', 10000) + "0" + new string(']', 10000));
        Assert.Contains(result.Diagnostics, d => d.Code == PuckDiagnosticCodes.EvaluationLimit);
    }

    [Fact]
    public void FailedConvenienceCompilationCannotEmitPartialData() {
        Assert.Throws<InvalidOperationException>(() => WorldDocumentEmitter.CompileToJson(PuckParser.ParseDocument("missing()")));
    }

    private static JsonObject Lower(string source) => WorldDocumentEmitter.Lower(PuckParser.ParseDocument(source));
}
