using Puck.Cli.Transpiler;
using Xunit;

namespace Puck.Cli.Tests;

/// <summary><c>puck lint --strict</c> passes every source the repository ships: the asset packages under
/// <c>worlds</c>, the World's own worlds and cartridges under <c>src/Puck.World/Assets</c>, the transpiler's samples under
/// <c>src/Puck.World.Transpiler/Samples</c>, and the verdict worlds
/// under <c>tests/Puck.World.Verdicts</c>. Each source runs through the verb itself, so an error or a warning in any of
/// them fails here with the report the verb printed.</summary>
public sealed class ShippedSourceLintLawTests {
    public static TheoryData<string> Sources() => TrackedPuckSources.Under(
        "src/Puck.World.Transpiler/Samples",
        "src/Puck.World/Assets",
        "tests/Puck.World.Verdicts",
        "worlds"
    );
    [MemberData(nameof(Sources))]
    [Theory]
    public void AShippedSourceLintsCleanUnderStrict(string relativePath) {
        var (exitCode, output) = ConsoleCapture.Run(run: () => LintCommand.Execute(
            path: RepositoryPaths.Resolve(relativePath: relativePath),
            strict: true
        ));

        Assert.True(
            condition: (exitCode == 0),
            userMessage: $"puck lint --strict {relativePath} exited {exitCode}:{Environment.NewLine}{output}"
        );
    }
    // The control: a root with a seat and no kit to embody it, the fault a shared basis once carried, is red.
    [Fact]
    public void ARootWithASeatAndNoKitFailsTheLint() {
        var directory = Path.Combine(
            path1: Path.GetTempPath(),
            path2: ("puck-lint-gate-" + Guid.NewGuid().ToString(format: "N"))
        );

        Directory.CreateDirectory(path: directory);
        try {
            var path = Path.Combine(
                path1: directory,
                path2: "seat.puck"
            );

            File.WriteAllText(
                contents: "schema: \"puck.world.definition.v1\"\n\nbodies {\n  localSeats: 1\n}\n",
                path: path
            );

            var (exitCode, output) = ConsoleCapture.Run(run: () => LintCommand.Execute(
                path: path,
                strict: true
            ));

            Assert.Equal(
                actual: exitCode,
                expected: 1
            );
            Assert.Contains(
                actualString: output,
                comparisonType: StringComparison.Ordinal,
                expectedSubstring: "PUCK030"
            );
        } finally {
            Directory.Delete(
                path: directory,
                recursive: true
            );
        }
    }
}
