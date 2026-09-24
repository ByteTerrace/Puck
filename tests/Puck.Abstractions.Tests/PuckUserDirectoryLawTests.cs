namespace Puck.Abstractions.Tests;

public sealed class PuckUserDirectoryLawTests {
    [Fact]
    public void AnOwnersSubdirectoryIsOneSegmentUnderTheRoot() {
        var root = PuckUserDirectory.Root;
        var compilations = PuckUserDirectory.Resolve(name: "compilations");

        Assert.Equal(expected: "Puck", actual: Path.GetFileName(path: root));
        Assert.True(condition: Path.IsPathRooted(path: root), userMessage: root);
        Assert.Equal(expected: root, actual: Path.GetDirectoryName(path: compilations));
        Assert.Equal(expected: "compilations", actual: Path.GetFileName(path: compilations));
    }
    [InlineData("World")]
    [InlineData("world/builds")]
    [InlineData("world\\builds")]
    [InlineData("..")]
    [InlineData(".")]
    [InlineData("c:")]
    [InlineData(" ")]
    [Theory]
    public void ANameThatIsNotOneLowerCaseSegmentIsRefused(string name) {
        _ = Assert.ThrowsAny<ArgumentException>(testCode: () => PuckUserDirectory.Resolve(name: name));
    }
}
