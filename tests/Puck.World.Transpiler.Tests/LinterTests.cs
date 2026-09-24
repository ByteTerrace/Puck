using Puck.Transpiler.Diagnostics;
using Puck.Transpiler.Parsing;
using Puck.World.Transpiler.Validation;
using Xunit;

namespace Puck.World.Transpiler.Tests;

public class LinterTests {
    [Fact]
    public void CompositionReferencesCountAsUses() {
        var source = """
            let seed = 7
            let island = "island"
            let endpoint = "gate"
            let width = 4
            module terrain(value: Int) { host { width: value } }
            world island = terrain(value: seed)
            border island.arch(1), neighbour.under(endpoint) { width: width }
            """;
        var diagnostics = new DiagnosticBag();

        PuckLinter.Lint(PuckParser.ParseDocument(source), diagnostics);

        Assert.DoesNotContain(collection: diagnostics, filter: diagnostic => (diagnostic.Code == "PUCK_LINT_001"));
    }
    [Fact]
    public void TestLinterPassesWhenConstantUsed() {
        var source = @"puck: 1
let USED_VAL = ""alpha""
host {
    authority: USED_VAL
}
";
        var doc = PuckParser.ParseDocument(source);

        Assert.NotNull(@object: doc);

        var diags = new DiagnosticBag();

        PuckLinter.Lint(
            diagnostics: diags,
            document: doc
        );

        Assert.DoesNotContain(
            collection: diags,
            filter: d => (d.Code == "PUCK_LINT_001")
        );
    }
    // A source, the lint finding it draws, and the name that finding must mention.
    [InlineData("puck: 1\nentities [\n    {\n        name: \"Box\"\n        color: \"#FF0000\"\n    }\n]\n", "PUCK_LINT_003", "#FF0000")]
    [InlineData("puck: 1\nlet UNUSED_VAL = 42\nhost {\n    authority: \"test\"\n}\n", "PUCK_LINT_001", "UNUSED_VAL")]
    [InlineData("puck: 1\ntemplate UnusedTemplate() {\n    components []\n}\nhost {\n    authority: \"test\"\n}\n", "PUCK_LINT_001", "UnusedTemplate")]
    [Theory]
    public void TheLinterReportsItsFindingByName(string source, string code, string mentions) {
        var diagnostics = new DiagnosticBag();

        PuckLinter.Lint(
            diagnostics: diagnostics,
            document: PuckParser.ParseDocument(source)
        );

        Assert.Contains(
            collection: diagnostics,
            filter: d => ((d.Code == code) && d.Message.Contains(value: mentions))
        );
    }
}
