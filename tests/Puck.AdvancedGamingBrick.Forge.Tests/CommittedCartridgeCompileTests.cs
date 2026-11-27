using Puck.GamingBricks.Forge;
using Puck.HumbleGamingBrick.Forge;

namespace Puck.AdvancedGamingBrick.Forge.Tests;

/// <summary>Holds every committed cartridge source to its native compile. Validation reads a document's shape; only the
/// compiler finds an image that outgrows its machine (<see cref="CartridgeCapacityException"/>), which would otherwise
/// surface first as a refused bind in a running world.</summary>
public sealed class CommittedCartridgeCompileTests {
    // Every target folder's sources; shared/ holds the modules they import, which are no document of their own.
    public static TheoryData<string> CommittedSources() => [.. Directory.EnumerateFiles(
        RepositoryPaths.Resolve(relativePath: "worlds/debug/cartridges"),
        "*.puck",
        SearchOption.AllDirectories
    )
        .Where(predicate: static path => (Path.GetFileName(path: Path.GetDirectoryName(path: path)) != "shared"))
        .Order(comparer: StringComparer.Ordinal)];

    [MemberData(nameof(CommittedSources))]
    [Theory]
    public void EveryCommittedCartridgeCompilesForItsTarget(string sourcePath) {
        var document = Puck.GamingBricks.Transpiler.CartridgeSource.Read(sourcePath: sourcePath);
        ICartridgeCompiler compiler = (document.Target switch {
            "agb" => new AgbCartridgeCompiler(),
            Puck.HumbleGamingDeck.Forge.DeckCartridgeCompiler.TargetName => new Puck.HumbleGamingDeck.Forge.DeckCartridgeCompiler(),
            _ => new HgbCartridgeCompiler(),
        });

        Assert.NotEmpty(collection: compiler.Compile(document: document).Rom);
    }
}
