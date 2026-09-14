using Puck.Transpiler.Diagnostics;
using Puck.Transpiler.Parsing;
using Puck.World.Transpiler.Lowering;
using Xunit;

namespace Puck.World.Transpiler.Tests;

public sealed class StateAuthoringReviewProbeTests {
    [Theory]
    [InlineData("state { world { slot health : Int = 10 } }")]
    [InlineData("let initial = 10\nstate { world { slot health : Int = initial } }")]
    [InlineData("let cap = 8\nstate { world { table health : Int capacity(cap) { hp = 10 } } }")]
    public void DeclarationAcceptsCompileTimeConstants(string body) {
        var source = "schema: \"puck.world.definition.v1\"\n" + body;
        var parsed = PuckParser.ParseDocumentWithDiagnostics(source);
        Assert.False(parsed.Diagnostics.HasErrors, parsed.Diagnostics.FormatReport(source));
        var diagnostics = new DiagnosticBag();
        _ = WorldDocumentEmitter.LowerWithDiagnostics(parsed.Value!, diagnostics: diagnostics, cancellationToken: TestContext.Current.CancellationToken);
        Assert.False(diagnostics.HasErrors, diagnostics.FormatReport(source));
    }
}
