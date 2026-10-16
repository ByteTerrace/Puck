using Xunit;

namespace Puck.Cli.Worlds.Tests;

/// <summary>CONTRACT UNDER TEST: <c>puck test</c> refuses a path that is neither a world document, a <c>.puck</c>
/// source, nor a directory as a usage error.</summary>
/// <remarks>The run passes <c>--world-artifact</c> at the repository's own Release output rather than letting the verb
/// build <c>Puck.World</c> again: the test project already depends on that build.</remarks>
public sealed class TestCommandLawTests {
    private static string Artifact() => Path.Combine(
        path1: RepositoryPaths.RequireRoot(),
        path2: "src",
        path3: "Puck.World",
        path4: "bin/Release/net11.0/Puck.World.dll"
    );
    private static (int ExitCode, string Output) RunTest(params string[] arguments) =>
        ConsoleCapture.Run(run: () => SuiteRoot.Invoke(args: [
            "test",
            .. arguments,
            "--world-artifact",
            Artifact(),
        ]));

    [Fact]
    public void ADirectoryWithNoWorldDocumentIsAUsageError() {
        var (exitCode, output) = RunTest(Path.Combine(
            path1: RepositoryPaths.RequireRoot(),
            path2: "tests",
            path3: "Puck.World.Verdicts",
            path4: "nowhere"
        ));

        Assert.Equal(
            actual: exitCode,
            expected: 2
        );
        Assert.Contains(
            actualString: output,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "is neither a world document, a .puck source, nor a directory"
        );
    }
}
