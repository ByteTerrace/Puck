using Puck.Cli.Transpiler;
using Puck.Testing;
using Puck.Transpiler.Diagnostics;
using Xunit;

namespace Puck.Cli.Worlds.Tests;

/// <summary><c>puck lint --strict</c> passes every source the repository ships: the asset packages under
/// <c>worlds</c>, their worlds, cartridges and cartridge modules among them, the transpiler's samples under
/// <c>src/Puck.World.Transpiler/Samples</c>, and the verdict worlds
/// under <c>tests/Puck.World.Verdicts</c>. Each source runs through the verb itself, so an error or a warning in any of
/// them fails here with the report the verb printed. The one warning a shipped source may carry is PUCK122, a blend
/// joining shapes that move independently, which the debug world's shape gallery exhibits on purpose.</summary>
public sealed class ShippedSourceLintLawTests {
    public static TheoryData<string> Sources() => TrackedPuckSources.Under(
        "src/Puck.World.Transpiler/Samples",
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

        var findings = output.Split(separator: '\n').Where(predicate: static line => (line.Contains(comparisonType: StringComparison.Ordinal, value: ": warning ") || line.Contains(comparisonType: StringComparison.Ordinal, value: ": error "))).ToArray();

        Assert.True(
            condition: ((exitCode == 0) || ((findings.Length > 0) && findings.All(predicate: static line => line.Contains(comparisonType: StringComparison.Ordinal, value: $": warning {PuckDiagnosticCodes.MovingBlend}:")))),
            userMessage: $"puck lint --strict {relativePath} exited {exitCode}:{Environment.NewLine}{output}"
        );
    }
    // The control: a root with a seat and no kit to embody it, the fault a shared basis once carried, is red.
    [Fact]
    public void ARootWithASeatAndNoKitFailsTheLint() {
        using var scratch = new TemporaryDirectory(prefix: "puck-lint-gate-");
        var directory = scratch.RootPath;

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
    }
}
