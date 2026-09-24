using Puck.Testing;
using Xunit;

using Puck.World.Authoring;
using Puck.World.Server;

namespace Puck.World.Tests;

/// <summary>
/// CONTRACT UNDER TEST: <c>WorldAudioCue.MusicWhenTokens</c> single-sources the <c>puck.music.v1</c> <c>when</c>
/// vocabulary — <c>WorldDefinitionValidator</c> refuses a transition/layer/embellishment <c>when</c> outside it, and
/// <c>MusicDirectorFactory.ParseFamily</c> maps every token in it — so a cue-only token (one with no sense family
/// behind it) can never validate cleanly and then compile to a lane that cannot fire.
/// </summary>
public sealed class MusicWhenTokenLawTests {
    private static void AssertWhenRefuses(MusicSegmentDocument segment, string path, string token) {
        WithDocument(
            assert: document => {
                Assert.False(
                    condition: WorldDefinitionValidator.TryValidate(
                        definition: document,
                        neighbours: null,
                        reason: out var reason
                    ),
                    userMessage: $"'{token}' was expected to refuse"
                );
                Assert.Contains(
                    actualString: reason,
                    comparisonType: StringComparison.Ordinal,
                    expectedSubstring: path
                );
                Assert.Contains(
                    actualString: reason,
                    comparisonType: StringComparison.Ordinal,
                    expectedSubstring: $"'{token}' is not a sense-mappable when token"
                );
            },
            segment: segment
        );
    }
    private static void WithDocument(Action<WorldDefinition> assert, MusicSegmentDocument segment) {
        using var directory = new TemporaryDirectory();

        assert(obj: AudioAssetFixtures.ScoredDocument(
            directory: directory,
            musicName: "when-law",
            segment: segment
        ));
    }

    // A cue-only token has no standing state, so neither an embellishment nor a layer may gate on it.
    [InlineData("embellishments.when", WorldAudioCue.GrantDenied)]
    [InlineData("layers.when", WorldAudioCue.MutationApplied)]
    [Theory]
    public void ACueOnlyWhenRefusesByName(string path, string token) =>
        AssertWhenRefuses(
            path: path,
            segment: ((path == "layers.when")
                ? new MusicSegmentDocument(
                    Id: "calm",
                    Transitions: null,
                    Layers: [new MusicLayerDocument(
                        GainThousandths: null,
                        TuneId: "bed-tune",
                        When: token
                    )]
                )
                : new MusicSegmentDocument(
                    Id: "calm",
                    Transitions: null,
                    Embellishments: [new MusicEmbellishmentDocument(
                        GainThousandths: null,
                        PatchId: "stinger",
                        When: token
                    )]
                )),
            token: token
        );
    [Fact]
    public void CueOnlyTransitionWhenRefusesByName() {
        AssertWhenRefuses(
            path: "transitions.when",
            segment: new MusicSegmentDocument(
                Id: "calm",
                Transitions: [new MusicTransitionDocument(
                        At: null,
                        To: "calm",
                        When: WorldAudioCue.PlayerJump
                    )]
            ),
            token: WorldAudioCue.PlayerJump
        );
    }
    [Fact]
    public void EveryMusicWhenTokenMapsToASenseFamily() {
        // The list↔mapping closure: a token added to MusicWhenTokens without a ParseFamily arm throws here, at test
        // time, instead of at the first world boot that authors it.
        foreach (var token in WorldAudioCue.MusicWhenTokens) {
            _ = MusicDirectorFactory.ParseFamily(token: token);
        }
    }
    [Fact]
    public void EveryMusicWhenTokenValidatesInAllThreeLanes() {
        // The control for the refusal cases below: the identical document shape, with every sense-mappable token
        // authored in every lane, validates cleanly — so a refusal there is the vocabulary check firing, never a
        // coincidental fault of the shared fixture.
        WithDocument(
            assert: static document => {
                Assert.True(
                    condition: WorldDefinitionValidator.TryValidate(
                        definition: document,
                        neighbours: null,
                        reason: out var reason
                    ),
                    userMessage: reason
                );
            },
            segment: new MusicSegmentDocument(
                Id: "calm",
                Transitions: [.. WorldAudioCue.MusicWhenTokens.Select(selector: static token => new MusicTransitionDocument(
                        At: null,
                        To: "calm",
                        When: token
                    ))],
                Layers: [.. WorldAudioCue.MusicWhenTokens.Select(selector: static token => new MusicLayerDocument(
                        GainThousandths: null,
                        TuneId: "bed-tune",
                        When: token
                    ))],
                Embellishments: [.. WorldAudioCue.MusicWhenTokens.Select(selector: static token => new MusicEmbellishmentDocument(
                        GainThousandths: null,
                        PatchId: "stinger",
                        When: token
                    ))]
            )
        );
    }
    [Fact]
    public void MusicWhenTokensAreAPublishedEventTokenSubset() {
        foreach (var token in WorldAudioCue.MusicWhenTokens) {
            Assert.True(
                condition: WorldAudioCue.IsEventToken(token: token),
                userMessage: $"'{token}' is not a published event token"
            );
        }
    }
    [Fact]
    public void WhitespaceLayerWhenRefuses() {
        // A whitespace layer When is neither the null unconditional case nor a sense-mappable token; nothing the
        // director compiler cannot arm may survive validation.
        AssertWhenRefuses(
            path: "layers.when",
            segment: new MusicSegmentDocument(
                Id: "calm",
                Transitions: null,
                Layers: [new MusicLayerDocument(
                        GainThousandths: null,
                        TuneId: "bed-tune",
                        When: " "
                    )]
            ),
            token: " "
        );
    }
}
