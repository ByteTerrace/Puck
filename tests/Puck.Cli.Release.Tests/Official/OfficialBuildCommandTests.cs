using Puck.Cli.Official;

using Xunit;

namespace Puck.Cli.Release.Tests.Official;

/// <summary>Exercises <see cref="OfficialBuildCommand.IsDirtyPorcelainOutput"/> as a pure unit in isolation. The tree state a
/// build is named by is held over checkouts of its own in <see cref="OfficialTreeStateLawTests"/>.</summary>
public sealed class OfficialBuildCommandTests {
    [InlineData("", false)]
    [InlineData("   \n  ", false)]
    [InlineData(" M src/Puck.Cli/Program.cs\n", true)]
    [InlineData("?? new-file.txt\n", true)]
    [Theory]
    public void IsDirtyPorcelainOutput_ReadsGitPorcelainCorrectly(string porcelain, bool expectedDirty) {
        Assert.Equal(
            expected: expectedDirty,
            actual: OfficialBuildCommand.IsDirtyPorcelainOutput(porcelainStdout: porcelain)
        );
    }
}
