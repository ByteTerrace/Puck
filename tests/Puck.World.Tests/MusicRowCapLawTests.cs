using Xunit;

using Puck.Forge.Authoring;

namespace Puck.World.Tests;

/// <summary>
/// CONTRACT UNDER TEST: <c>WorldServer</c>'s constructor reads only <c>music[0]</c> (see <c>WorldServer.cs</c>'s
/// music-clock construction), so a document authoring a second <c>music</c> row must refuse at validation rather
/// than boot silently truncated.
/// </summary>
public sealed class MusicRowCapLawTests {
    [Fact]
    public void TwoMusicRowsRefuseByName() {
        var directory = Directory.CreateTempSubdirectory(prefix: "puck-music-cap-law-").FullName;

        try {
            var document = Fixtures.BuildDocument() with {
                Music = [BuildMusicRow(assetDirectory: directory, name: "score-a"), BuildMusicRow(assetDirectory: directory, name: "score-b")],
            };

            Assert.False(condition: WorldDefinitionValidator.TryValidate(definition: document, neighbours: null, reason: out var reason), userMessage: "a second authored music row was expected to refuse");
            Assert.Contains(actualString: reason, comparisonType: StringComparison.Ordinal, expectedSubstring: "music declares 2 rows");
            Assert.Contains(actualString: reason, comparisonType: StringComparison.Ordinal, expectedSubstring: "at most one");
        } finally {
            Directory.Delete(path: directory, recursive: true);
        }
    }
    [Fact]
    public void OneMusicRowControl() {
        // The identical row-building helper, called once instead of twice — isolates the refusal above to the cap
        // itself: this document is otherwise the same shape, so a clean validation here proves the denial is the
        // cap firing, never a coincidental fault a shared row can't resolve.
        var directory = Directory.CreateTempSubdirectory(prefix: "puck-music-cap-law-").FullName;

        try {
            var document = Fixtures.BuildDocument() with {
                Music = [BuildMusicRow(assetDirectory: directory, name: "score-a")],
            };

            Assert.True(condition: WorldDefinitionValidator.TryValidate(definition: document, neighbours: null, reason: out var reason), userMessage: reason);
        } finally {
            Directory.Delete(path: directory, recursive: true);
        }
    }

    private static WorldMusicRow BuildMusicRow(string assetDirectory, string name) {
        var music = MusicCanonicalizer.Canonicalize(document: new MusicDocument(
            Schema: MusicDocument.CurrentSchema,
            Name: name,
            Tempo: new MusicTempoDocument(BeatsPerBar: 4, TicksPerBeat: 2100),
            Segments: [new MusicSegmentDocument(Id: "calm", Transitions: null)]
        ));
        var path = Path.Combine(path1: assetDirectory, path2: $"{name}.puck.music.v1.json");

        File.WriteAllBytes(path: path, bytes: music.Bytes);

        return new WorldMusicRow(Name: name, Source: path, Hash: music.Hash);
    }
}
