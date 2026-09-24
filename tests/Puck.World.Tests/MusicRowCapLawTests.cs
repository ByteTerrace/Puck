using Puck.Testing;
using Xunit;

using Puck.World.Authoring;

namespace Puck.World.Tests;

/// <summary>
/// CONTRACT UNDER TEST: <c>WorldServer</c>'s constructor reads only <c>music[0]</c> (see <c>WorldServer.cs</c>'s
/// music-clock construction), so a document authoring a second <c>music</c> row must refuse at validation rather
/// than boot silently truncated.
/// </summary>
public sealed class MusicRowCapLawTests {
    // One row is the control: the same row-building path, so a clean validation proves the refusal is the cap
    // firing, never a fault a row cannot resolve.
    [InlineData(1, null)]
    [InlineData(2, "music declares 2 rows")]
    [Theory]
    public void MusicRowsPastOneRefuseByName(int rows, string? refusal) {
        using var directory = new TemporaryDirectory();
        var document = Fixtures.BuildDocument() with {
            Music = [.. Enumerable.Range(
                count: rows,
                start: 0
            ).Select(selector: index => AudioAssetFixtures.Write(
                directory: directory,
                document: AudioAssetFixtures.Score(
                    name: $"score-{index}",
                    segments: [new MusicSegmentDocument(
                        Id: "calm",
                        Transitions: null
                    )]
                )
            ))],
        };
        var valid = WorldDefinitionValidator.TryValidate(
            definition: document,
            neighbours: null,
            reason: out var reason
        );

        if (refusal is null) {
            Assert.True(
                condition: valid,
                userMessage: reason
            );
            return;
        }
        Assert.False(condition: valid);
        Assert.Contains(
            actualString: reason,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: refusal
        );
        Assert.Contains(
            actualString: reason,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "at most one"
        );
    }
}
