using System.Text.Json.Nodes;
using Puck.GamingBricks.Forge;
using Puck.Transpiler.Diagnostics;
using Puck.Transpiler.Parsing;
using Xunit;

namespace Puck.GamingBricks.Transpiler.Tests;

public sealed class ProductionCartridgeTests {
    private static JsonObject Lower(string source) => CartridgeDocumentEmitter.LowerWithDiagnostics(
        PuckParser.ParseDocument(source),
        cancellationToken: TestContext.Current.CancellationToken
    ).RequireValue();

    [InlineData("cgb")]
    [InlineData("agb")]
    [Theory]
    public void BlankCartridgeRoundTripsAllRequiredSections(string target) {
        var original = JsonNode.Parse(CartridgeDocuments.Canonicalize(document: CartridgeDocuments.Create(
            target: target,
            title: "TEST"
        )).Bytes)!.AsObject();

        Assert.True(condition: JsonNode.DeepEquals(
            node1: original,
            node2: Lower(source: CartridgeDecompiler.Decompile(document: original))
        ));
    }
    [Fact]
    public void CompletionsBelongToTheSelectedSchema() {
        var document = PuckParser.ParseDocument("schema: \"puck.cartridge.v1\"");
        var items = CartridgeLanguageServices.Completions(document: document)!;

        Assert.Contains(
            collection: items,
            filter: item => (item!["label"]!.GetValue<string>() == "sprite")
        );
        Assert.DoesNotContain(
            collection: items,
            filter: item => (item!["label"]!.GetValue<string>() == "seatRig")
        );
        Assert.Null(@object: CartridgeLanguageServices.Completions(document: PuckParser.ParseDocument("schema: \"puck.world.definition.v1\"")));
    }
    [InlineData("cgb")]
    [InlineData("agb")]
    [Theory]
    public void EditorValidationLocatesTheInvalidProperty(string target) {
        var json = JsonNode.Parse(CartridgeDocuments.Canonicalize(document: CartridgeDocuments.Create(
            target: target,
            title: "TEST"
        )).Bytes)!;
        var source = CartridgeDecompiler.Decompile(document: json.AsObject());

        source += "\nvariable \"broken\" { initial: 99999 }\n";
        var document = PuckParser.ParseDocument(source);
        var diagnostics = new DiagnosticBag();

        Assert.True(condition: CartridgeLanguageServices.Diagnose(
            diagnostics: diagnostics,
            document: document,
            sourcePath: null
        ));
        Assert.Contains(
            collection: diagnostics,
            filter: error => (error.Message.Contains(
                comparisonType: StringComparison.Ordinal,
                value: "initial"
            ) && (error.Span.Line > 1))
        );
    }
    [Fact]
    public void NestedTemplatesKeepCallerBindings() {
        var json = Lower(source: "template inner(v) { variable \"test\" { initial: v } }\ntemplate outer(v) { inner(v) }\nfor i in [4,9] { outer(i) }");
        var values = json["variables"]!.AsArray().Select(selector: row => row!["initial"]!.GetValue<long>()).ToArray();

        Assert.Equal(
            actual: values,
            expected: new long[] { 4, 9 }
        );
    }
    [Fact]
    public void OversizedFloatingLiteralProducesADiagnostic() {
        var document = PuckParser.ParseDocument("sprite \"test\" { x: 1e300 }");

        Assert.True(condition: CartridgeDocumentEmitter.LowerWithDiagnostics(
            document,
            cancellationToken: TestContext.Current.CancellationToken
        ).Diagnostics.HasErrors);
    }
    [Fact]
    public void RecursiveStringOperandsAreRefused() {
        var document = PuckParser.ParseDocument("let a = \"a\"\nsprite \"test\" { x: a }");

        Assert.True(condition: CartridgeDocumentEmitter.LowerWithDiagnostics(
            document,
            cancellationToken: TestContext.Current.CancellationToken
        ).Diagnostics.HasErrors);
    }
    [InlineData("sprite \"cursor\" { x: cursor + 1 }", "sprite \"cursor\" { x: \"cursor + 1\" }")]
    [InlineData("sprites [{ name: \"cursor\", x: cursor + 1 }]", "sprites [{ name: \"cursor\", x: \"cursor + 1\" }]")]
    [InlineData("rule \"move\" { map(row: cursor + 1, column: 0, tile: 0) }", "rule \"move\" { map(row: \"cursor + 1\", column: 0, tile: 0) }")]
    [InlineData("sprite \"cursor\" { x: cells[cursor + 1] }", "sprite \"cursor\" { x: \"cells[cursor + 1]\" }")]
    [Theory]
    public void RuntimeExpressionsHaveOneMeaningInEveryPosition(string natural, string quoted) {
        Assert.True(condition: JsonNode.DeepEquals(
            node1: Lower(source: natural),
            node2: Lower(source: quoted)
        ));
    }
}
