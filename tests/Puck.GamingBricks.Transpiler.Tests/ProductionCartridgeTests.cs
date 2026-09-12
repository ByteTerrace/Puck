using System.Text.Json.Nodes;
using Puck.GamingBricks.Forge;
using Puck.Transpiler.Diagnostics;
using Puck.Transpiler.Parsing;
using Xunit;

namespace Puck.GamingBricks.Transpiler.Tests;

public sealed class ProductionCartridgeTests {
    [Theory]
    [InlineData("sprite \"cursor\" { x: cursor + 1 }", "sprite \"cursor\" { x: \"cursor + 1\" }")]
    [InlineData("sprites [{ name: \"cursor\", x: cursor + 1 }]", "sprites [{ name: \"cursor\", x: \"cursor + 1\" }]")]
    [InlineData("rule \"move\" { map(row: cursor + 1, column: 0, tile: 0) }", "rule \"move\" { map(row: \"cursor + 1\", column: 0, tile: 0) }")]
    [InlineData("sprite \"cursor\" { x: cells[cursor + 1] }", "sprite \"cursor\" { x: \"cells[cursor + 1]\" }")]
    public void RuntimeExpressionsHaveOneMeaningInEveryPosition(string natural, string quoted) {
        Assert.True(JsonNode.DeepEquals(Lower(natural), Lower(quoted)));
    }

    [Fact]
    public void NestedTemplatesKeepCallerBindings() {
        var json = Lower("template inner(v) { variable \"test\" { initial: v } }\ntemplate outer(v) { inner(v) }\nfor i in [4,9] { outer(i) }");
        var values = json["variables"]!.AsArray().Select(row => row!["initial"]!.GetValue<long>()).ToArray();
        Assert.Equal(new long[] { 4, 9 }, values);
    }

    [Fact]
    public void RecursiveStringOperandsAreRefused() {
        var document = PuckParser.ParseDocument("let a = \"a\"\nsprite \"test\" { x: a }");
        Assert.True(CartridgeDocumentEmitter.LowerWithDiagnostics(document, cancellationToken: TestContext.Current.CancellationToken).Diagnostics.HasErrors);
    }

    [Fact]
    public void OversizedFloatingLiteralProducesADiagnostic() {
        var document = PuckParser.ParseDocument("sprite \"test\" { x: 1e300 }");
        Assert.True(CartridgeDocumentEmitter.LowerWithDiagnostics(document, cancellationToken: TestContext.Current.CancellationToken).Diagnostics.HasErrors);
    }

    [Fact]
    public void CompletionsBelongToTheSelectedSchema() {
        var document = PuckParser.ParseDocument("schema: \"puck.cartridge.v1\"");
        var items = CartridgeLanguageServices.Completions(document)!;
        Assert.Contains(items, item => item!["label"]!.GetValue<string>() == "sprite");
        Assert.DoesNotContain(items, item => item!["label"]!.GetValue<string>() == "seatRig");
        Assert.Null(CartridgeLanguageServices.Completions(PuckParser.ParseDocument("schema: \"puck.world.def.v1\"")));
    }

    [Theory]
    [InlineData("cgb")]
    [InlineData("agb")]
    public void BlankCartridgeRoundTripsAllRequiredSections(string target) {
        var original = JsonNode.Parse(CartridgeDocuments.Canonicalize(CartridgeDocuments.Create(target, "TEST")).Bytes)!.AsObject();
        Assert.True(JsonNode.DeepEquals(original, Lower(CartridgeDecompiler.Decompile(original))));
    }

    [Theory]
    [InlineData("cgb")]
    [InlineData("agb")]
    public void EditorValidationLocatesTheInvalidProperty(string target) {
        var json = JsonNode.Parse(CartridgeDocuments.Canonicalize(CartridgeDocuments.Create(target, "TEST")).Bytes)!;
        var source = CartridgeDecompiler.Decompile(json.AsObject());
        source += "\nvariable \"broken\" { initial: 99999 }\n";
        var document = PuckParser.ParseDocument(source);
        var diagnostics = new DiagnosticBag();
        Assert.True(CartridgeLanguageServices.Diagnose(document, null, diagnostics));
        Assert.Contains(diagnostics, error => error.Message.Contains("initial", StringComparison.Ordinal) && error.Span.Line > 1);
    }

    private static JsonObject Lower(string source) => CartridgeDocumentEmitter.LowerWithDiagnostics(PuckParser.ParseDocument(source), cancellationToken: TestContext.Current.CancellationToken).RequireValue();
}
