using Puck.Cli.Official;
using Puck.Testing;
using Xunit;

namespace Puck.Cli.Release.Tests.Official;

/// <summary>Channel names cannot redirect an official build outside its channel directory.</summary>
public sealed class OfficialChannelLawTests {
    [InlineData("../x")]
    [InlineData("..")]
    [InlineData(".")]
    [InlineData("/root")]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    [InlineData("C:/x")]
    [InlineData("NUL")]
    [InlineData(" ")]
    [Theory]
    public void AnInvalidChannelIsRefusedBeforeAnyTreeIsWritten(string channel) {
        using var scratch = new TemporaryDirectory();
        var tree = scratch.PathOf(name: "tree");

        var (exit, _, error) = ConsoleCapture.RunSplit(run: () => OfficialBuildCommand.Create().Parse(args: [
            "--channel", channel, "--tree", tree, "--engine", scratch.RootPath, "--worlds", scratch.RootPath,
        ]).Invoke());

        Assert.Equal(actual: exit, expected: CliExit.Refused);
        Assert.Contains(actualString: error, expectedSubstring: "--channel");
        Assert.Contains(actualString: error, expectedSubstring: "single portable directory name");
        Assert.False(condition: Directory.Exists(path: tree));
    }
}
