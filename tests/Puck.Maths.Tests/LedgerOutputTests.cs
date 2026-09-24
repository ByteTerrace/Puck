using Xunit;

namespace Puck.Maths.Tests;

/// <summary>
/// A run leaves the checkout as it found it: every ledger artifact it writes lands in its own directory in the build
/// output, and the one writer the ledger uses refuses a committed file. Only <see cref="TestPaths.RecordCommand"/>
/// changes the committed artifacts, by promoting a fresh run's output over them.
/// </summary>
public sealed class LedgerOutputTests {
    [Fact]
    [Trait(name: "tier", value: "Default")]
    public void ARunWritesOnlyIntoTheBuildOutput() {
        var buildOutput = (Path.GetFullPath(path: AppContext.BaseDirectory).TrimEnd(trimChar: Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar);

        foreach (var name in TestPaths.LedgerArtifacts) {
            var output = Path.GetFullPath(path: TestPaths.Output(fileName: name));

            Assert.StartsWith(
                actualString: output,
                comparisonType: StringComparison.OrdinalIgnoreCase,
                expectedStartString: buildOutput
            );
            Assert.True(condition: TestPaths.MayWrite(path: output));
            Assert.False(
                condition: TestPaths.MayWrite(path: TestPaths.Artifact(fileName: name)),
                userMessage: $"a run may write the committed {name}."
            );
            Assert.Throws<InvalidOperationException>(testCode: () => ArtifactJson.WriteIfChanged(
                content: "not a ledger",
                path: TestPaths.Artifact(fileName: name)
            ));
        }
    }
}
