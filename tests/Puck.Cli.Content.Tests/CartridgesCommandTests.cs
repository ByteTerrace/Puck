using Puck.HumbleGamingDeck.Forge;
using Puck.Testing;
using Xunit;

namespace Puck.Cli.Content.Tests;

public sealed class CartridgesCommandTests {
    [Fact]
    public void DeckWritesVerifiesAndRefusesAnOutputItCannotWrite() {
        using var directory = new TemporaryDirectory(prefix: "puck-cartridges tests ");

        Assert.Equal(
            expected: 1,
            actual: SuiteRoot.Invoke(args: ["cartridges", "deck", "--output", directory.RootPath, "--check"])
        );
        Assert.Equal(
            expected: 0,
            actual: SuiteRoot.Invoke(args: ["cartridges", "deck", "--output", directory.RootPath])
        );
        Assert.Equal(
            expected: DeckTestCartridges.All.Select(selector: static cartridge => cartridge.FileName).ToArray(),
            actual: Directory.GetFiles(path: directory.RootPath).Select(selector: static file => Path.GetFileName(path: file)).Order(comparer: StringComparer.Ordinal).ToArray()
        );
        Assert.Equal(
            expected: 0,
            actual: SuiteRoot.Invoke(args: ["cartridges", "deck", "--output", directory.RootPath, "--check"])
        );

        // An output that is a file cannot hold the images.
        var file = Path.Combine(
            path1: directory.RootPath,
            path2: DeckTestCartridges.All[0].FileName
        );

        Assert.Equal(
            expected: 1,
            actual: SuiteRoot.Invoke(args: ["cartridges", "deck", "--output", file])
        );
    }
    [Fact]
    public void TheShippedDeckCartridgesAreTheOnesTheForgeBuilds() =>
        Assert.Equal(
            expected: 0,
            actual: SuiteRoot.Invoke(args: ["cartridges", "deck", "--output", RepositoryPaths.Resolve(relativePath: "src/Puck.World/Assets/cartridges/deck"), "--check"])
        );
}
