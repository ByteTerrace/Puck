using Xunit;

namespace Puck.World.Transpiler.Tests;

public sealed class StateAuthoringReviewProbeTests {
    [InlineData("state { world { slot health = 10 } }")]
    [InlineData("let initial = 10\nstate { world { slot health = initial } }")]
    [InlineData("let cap = 8\nstate { world { table health capacity(cap) { hp = 10 } } }")]
    [Theory]
    public void DeclarationAcceptsCompileTimeConstants(string body) {
        var source = ("schema: \"puck.world.definition.v1\"\n" + body);
        var diagnostics = WorldCompiler.Compile(cancellationToken: TestContext.Current.CancellationToken, source: source).Diagnostics;

        Assert.False(condition: diagnostics.HasErrors, userMessage: diagnostics.FormatReport(source));
    }
}
