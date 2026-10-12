using Puck.Testing;
using Xunit;

namespace Puck.Cli.Worlds.Tests;

/// <summary>CONTRACT UNDER TEST: <c>puck compile</c> writes each world document's compiled world beside it. A source and
/// the document it compiles to derive the same compiled world, a <c>.world.json</c> path compiles to its compiled world
/// alone, and a module fragment, which does not draw as a world on its own, has none and is not a failure.</summary>
public sealed class CompiledWorldCompileTests {
    private static int Compile(params string[] arguments) => ConsoleCapture.Run(run: () => SuiteRoot.Invoke(args: ["compile", .. arguments])).ExitCode;

    [Fact]
    public void AFragmentHasNoCompiledWorldAndCompiles() {
        using var directory = new TemporaryDirectory();
        var source = directory.WriteBytes(bytes: "state { ints [] }"u8, name: "fragment.puck");

        Assert.Equal(expected: 0, actual: Compile(source));
        Assert.True(condition: File.Exists(path: directory.PathOf(name: "fragment.world.json")));
        Assert.False(condition: File.Exists(path: directory.PathOf(name: "fragment.puckb")));
    }
}
