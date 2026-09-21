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
    [Fact]
    public void TestLinterReportsHardcodedHexColor() {
        var source = @"puck: 1
entities [
    {
        name: ""Box""
        color: ""#FF0000""
    }
]
";
        var doc = PuckParser.ParseDocument(source);

        Assert.NotNull(@object: doc);

        var diags = new DiagnosticBag();

        PuckLinter.Lint(
            diagnostics: diags,
            document: doc
        );

        Assert.Contains(
            collection: diags,
            filter: d => ((d.Code == "PUCK_LINT_003") && d.Message.Contains(value: "#FF0000"))
        );
    }
    [Fact]
    public void TestLinterReportsUnusedConstant() {
        var source = @"puck: 1
let UNUSED_VAL = 42
host {
    authority: ""test""
}
";
        var doc = PuckParser.ParseDocument(source);

        Assert.NotNull(@object: doc);

        var diags = new DiagnosticBag();

        PuckLinter.Lint(
            diagnostics: diags,
            document: doc
        );

        Assert.Contains(
            collection: diags,
            filter: d => ((d.Code == "PUCK_LINT_001") && d.Message.Contains(value: "UNUSED_VAL"))
        );
    }
    [Fact]
    public void TestLinterReportsUnusedTemplate() {
        var source = @"puck: 1
template UnusedTemplate() {
    components []
}
host {
    authority: ""test""
}
";
        var doc = PuckParser.ParseDocument(source);

        Assert.NotNull(@object: doc);

        var diags = new DiagnosticBag();

        PuckLinter.Lint(
            diagnostics: diags,
            document: doc
        );

        Assert.Contains(
            collection: diags,
            filter: d => ((d.Code == "PUCK_LINT_001") && d.Message.Contains(value: "UnusedTemplate"))
        );
    }
}
