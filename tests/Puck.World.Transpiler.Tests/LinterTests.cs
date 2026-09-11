using Puck.World.Transpiler.Diagnostics;
using Puck.World.Transpiler.Parsing;
using Puck.World.Transpiler.Validation;
using Xunit;

namespace Puck.World.Transpiler.Tests;

public class LinterTests {
    [Fact]
    public void TestLinterReportsUnusedConstant() {
        var source = @"puck: 1
let UNUSED_VAL = 42
host: {
    authority: ""test""
}
";
        var doc = PuckParser.ParseDocument(source);
        Assert.NotNull(doc);

        var diags = new DiagnosticBag();
        PuckLinter.Lint(doc, diags);

        Assert.Contains(diags, d => d.Code == "PUCK_LINT_001" && d.Message.Contains("UNUSED_VAL"));
    }

    [Fact]
    public void TestLinterReportsUnusedTemplate() {
        var source = @"puck: 1
template UnusedTemplate() {
    components: []
}
host: {
    authority: ""test""
}
";
        var doc = PuckParser.ParseDocument(source);
        Assert.NotNull(doc);

        var diags = new DiagnosticBag();
        PuckLinter.Lint(doc, diags);

        Assert.Contains(diags, d => d.Code == "PUCK_LINT_001" && d.Message.Contains("UnusedTemplate"));
    }

    [Fact]
    public void TestLinterPassesWhenConstantUsed() {
        var source = @"puck: 1
let USED_VAL = ""alpha""
host: {
    authority: USED_VAL
}
";
        var doc = PuckParser.ParseDocument(source);
        Assert.NotNull(doc);

        var diags = new DiagnosticBag();
        PuckLinter.Lint(doc, diags);

        Assert.DoesNotContain(diags, d => d.Code == "PUCK_LINT_001");
    }

    [Fact]
    public void TestLinterReportsHardcodedHexColor() {
        var source = @"puck: 1
entities: [
    {
        name: ""Box""
        color: ""#FF0000""
    }
]
";
        var doc = PuckParser.ParseDocument(source);
        Assert.NotNull(doc);

        var diags = new DiagnosticBag();
        PuckLinter.Lint(doc, diags);

        Assert.Contains(diags, d => d.Code == "PUCK_LINT_003" && d.Message.Contains("#FF0000"));
    }
}
