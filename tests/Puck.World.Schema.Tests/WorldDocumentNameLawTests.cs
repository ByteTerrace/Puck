using Xunit;

namespace Puck.World.Schema.Tests;

/// <summary>A world document is referenced by its name, and <see cref="WorldDocumentName"/> is the one mapping from a
/// name to the files that carry it: a file-form spelling is refused by name at every door that validates a reference,
/// and the mapping round-trips.</summary>
public sealed class WorldDocumentNameLawTests {
    [InlineData("klondike.world.json", "klondike")]
    [InlineData("games/klondike.world.json", "games/klondike")]
    [InlineData("../avatars/moth.puck", "../avatars/moth")]
    [InlineData("standard.basis.json", "standard.basis")]
    [Theory]
    public void AFileFormSpellingIsRefusedNamingTheDocument(string authored, string document) {
        Assert.False(condition: WorldDocumentName.TryValidate(
            name: authored,
            reason: out var reason
        ));
        Assert.Contains(
            actualString: reason,
            expectedSubstring: $"names a file; a document reference names the document ('{document}')"
        );
    }
    [InlineData("klondike")]
    [InlineData("games/klondike")]
    [InlineData("../puck")]
    [InlineData("parlor.basis")]
    [Theory]
    public void ADocumentNameIsAdmittedAndMapsToItsTwoFiles(string name) {
        Assert.True(
            condition: WorldDocumentName.TryValidate(
                name: name,
                reason: out var reason
            ),
            userMessage: reason
        );
        Assert.Equal(
            actual: WorldDocumentName.DocumentFile(name: name),
            expected: $"{name}.world.json"
        );
        Assert.Equal(
            actual: WorldDocumentName.SourceFile(name: name),
            expected: $"{name}.puck"
        );
        Assert.Equal(
            actual: WorldDocumentName.OfDocumentFile(path: WorldDocumentName.DocumentFile(name: name)),
            expected: name
        );
    }
    [InlineData("")]
    [InlineData(" amber")]
    [InlineData("amber ")]
    [Theory]
    public void AnEmptyOrPaddedNameIsRefused(string name) {
        Assert.False(condition: WorldDocumentName.TryValidate(
            name: name,
            reason: out _
        ));
    }
    [Fact]
    public void AFlatNamespaceNameIsAnOwnedWorldIdWhoseFileIsItsDocumentFile() {
        Assert.True(
            condition: WorldDocumentName.TryParseId(
                id: out var id,
                name: "amber",
                reason: out var reason
            ),
            userMessage: reason
        );
        Assert.Equal(
            actual: WorldDocumentName.For(id: id),
            expected: "amber.world.json"
        );
        Assert.False(condition: WorldDocumentName.TryParseId(
            id: out _,
            name: "basis/amber",
            reason: out _
        ));
        Assert.False(condition: WorldDocumentName.TryParseId(
            id: out _,
            name: "amber.world.json",
            reason: out _
        ));
    }
    [InlineData("games/klondike.puck", "games/klondike")]
    [InlineData("games/klondike.PUCK", "games/klondike")]
    [InlineData("games/klondike.world.json", "games/klondike")]
    [InlineData("games/klondike.World.Json", "games/klondike")]
    [Theory]
    public void EitherCarryingFileNamesItsDocument(string path, string name) {
        Assert.Equal(
            actual: WorldDocumentName.OfCarrierFile(path: path),
            expected: name
        );

        if (WorldDocumentName.IsSourceFile(path: path)) {
            Assert.Equal(
                actual: WorldDocumentName.OfSourceFile(path: path),
                expected: name
            );
        } else {
            Assert.Throws<ArgumentException>(testCode: () => WorldDocumentName.OfSourceFile(path: path));
        }
    }
    [Fact]
    public void AFileCarryingNoDocumentIsRefusedByName() {
        var refusal = Assert.Throws<ArgumentException>(testCode: static () => WorldDocumentName.OfCarrierFile(path: "games/klondike.assets.json"));

        Assert.Contains(
            actualString: refusal.Message,
            expectedSubstring: "'games/klondike.assets.json' carries no world document"
        );
    }
    [InlineData("worlds/moth.puck", "worlds/moth.assets.json")]
    [InlineData("worlds/moth.PUCK", "worlds/moth.assets.json")]
    [InlineData("worlds/moth.world.json", "worlds/moth.assets.json")]
    [InlineData("worlds/moth.assets.json", "worlds/moth.assets.json")]
    [InlineData("worlds/moth.txt", "worlds/moth.txt.assets.json")]
    [Theory]
    public void ASidecarSitsBesideItsSourceUnderTheDocumentName(string sourcePath, string sidecar) {
        Assert.Equal(
            actual: WorldDocumentName.SidecarFile(
                sourcePath: sourcePath,
                suffix: ".assets.json"
            ),
            expected: Path.GetFullPath(path: sidecar)
        );
    }
    // Both lock kinds once derived their sidecar by hand, one guarding only null: an empty path then named a lock
    // in the working directory rather than being refused.
    [InlineData("")]
    [InlineData(" ")]
    [Theory]
    public void ASidecarOfAnEmptyPathOrSuffixIsRefused(string blank) {
        Assert.ThrowsAny<ArgumentException>(testCode: () => WorldDocumentName.SidecarFile(
            sourcePath: blank,
            suffix: ".assets.json"
        ));
        Assert.ThrowsAny<ArgumentException>(testCode: () => WorldDocumentName.SidecarFile(
            sourcePath: "moth.puck",
            suffix: blank
        ));
        Assert.Throws<ArgumentNullException>(testCode: static () => WorldDocumentName.SidecarFile(
            sourcePath: null!,
            suffix: ".assets.json"
        ));
    }
    [Fact]
    public void AReferencesRowSpelledAsAFileIsRefusedByTheValidator() {
        var refusal = Assert.Throws<InvalidDataException>(testCode: static () => WorldDefinitionSerialization.Deserialize(utf8Json: """
            { "schema": "puck.world.definition.v1", "references": [ { "name": "dive", "document": "modules/dive.world.json" } ] }
            """u8.ToArray()));

        Assert.Contains(
            actualString: refusal.Message,
            expectedSubstring: "references[0].document 'modules/dive.world.json' names a file; a document reference names the document ('modules/dive')"
        );
    }
}
