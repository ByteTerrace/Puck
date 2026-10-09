using Xunit;

namespace Puck.Cli.Tests;

/// <summary>CONTRACT UNDER TEST: <c>puck --version</c> names the commit the CLI was built at as semantic-version build
/// metadata, so an installed tool's revision can be compared with its checkout's.</summary>
public sealed class VersionLawTests {
    [Fact]
    public async Task TheVersionCarriesTheBuildsCommit() {
        var revision = Puck.World.WorldSchema.SourceRevision;

        Assert.False(condition: string.IsNullOrEmpty(value: revision), userMessage: "this build recorded no commit");

        var (exitCode, output, _) = await ConsoleCapture.RunSplitAsync(run: static () => PuckRootCommand.InvokeAsync(args: ["--version"]));

        Assert.Equal(actual: exitCode, expected: 0);
        Assert.EndsWith(expectedEndString: $"+{revision}", actualString: output.TrimEnd());
    }
}
