using Puck.Testing;
using Xunit;

using Puck.World.Authoring;

namespace Puck.World.Tests;

/// <summary>
/// CONTRACT UNDER TEST: a <c>puck.music.v1</c> layer/embellishment <c>gainThousandths</c> rides the same
/// <c>CreationSoundDocument.MaxLevel</c> ceiling <c>WorldDefinitionValidator.ValidateCues</c> already enforces on a
/// cue row — <c>WorldDefinitionValidator</c>'s <c>CheckMusic</c> refuses a negative or over-ceiling value rather
/// than letting it validate clean and canonicalize into the document unchecked.
/// </summary>
public sealed class MusicGainThousandthsRangeLawTests {
    private static readonly int MaxGainThousandths = ((int)(CreationSoundDocument.MaxLevel * 1000f));

    private static void WithDocument(Action<WorldDefinition> assert, MusicSegmentDocument segment) {
        using var directory = new TemporaryDirectory();

        assert(obj: AudioAssetFixtures.ScoredDocument(
            directory: directory,
            musicName: "gain-law",
            segment: segment
        ));
    }

    [Fact]
    public void BoundaryAndUnauthoredGainControl() {
        // The control for the refusals above: the ceiling value itself, plus an unauthored (null) gain on the
        // sibling row, both validate clean — a refusal above is the range check firing, never a coincidental fault.
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
                Transitions: null,
                Layers: [new MusicLayerDocument(
                        GainThousandths: MaxGainThousandths,
                        TuneId: "bed-tune",
                        When: null
                    )],
                Embellishments: [new MusicEmbellishmentDocument(
                        GainThousandths: null,
                        PatchId: "stinger",
                        When: "region.enter"
                    )]
            )
        );
    }
    [Fact]
    public void NegativeLayerGainRefusesByName() {
        WithDocument(
            assert: static document => {
                Assert.False(
                    condition: WorldDefinitionValidator.TryValidate(
                        definition: document,
                        neighbours: null,
                        reason: out var reason
                    ),
                    userMessage: "a negative layer gainThousandths was expected to refuse"
                );
                Assert.Contains(
                    actualString: reason,
                    comparisonType: StringComparison.Ordinal,
                    expectedSubstring: "layers.gainThousandths"
                );
            },
            segment: new MusicSegmentDocument(
                Id: "calm",
                Transitions: null,
                Layers: [new MusicLayerDocument(
                        GainThousandths: -1,
                        TuneId: "bed-tune",
                        When: null
                    )]
            )
        );
    }
    [Fact]
    public void OutOfRangeEmbellishmentGainRefusesByName() {
        WithDocument(
            assert: static document => {
                Assert.False(
                    condition: WorldDefinitionValidator.TryValidate(
                        definition: document,
                        neighbours: null,
                        reason: out var reason
                    ),
                    userMessage: "an out-of-range embellishment gainThousandths was expected to refuse"
                );
                Assert.Contains(
                    actualString: reason,
                    comparisonType: StringComparison.Ordinal,
                    expectedSubstring: "embellishments.gainThousandths"
                );
            },
            segment: new MusicSegmentDocument(
                Id: "calm",
                Transitions: null,
                Embellishments: [new MusicEmbellishmentDocument(
                        GainThousandths: (MaxGainThousandths + 1),
                        PatchId: "stinger",
                        When: "region.enter"
                    )]
            )
        );
    }
    [Fact]
    public void OutOfRangeLayerGainRefusesByName() {
        WithDocument(
            assert: static document => {
                Assert.False(
                    condition: WorldDefinitionValidator.TryValidate(
                        definition: document,
                        neighbours: null,
                        reason: out var reason
                    ),
                    userMessage: "an out-of-range layer gainThousandths was expected to refuse"
                );
                Assert.Contains(
                    actualString: reason,
                    comparisonType: StringComparison.Ordinal,
                    expectedSubstring: "layers.gainThousandths"
                );
            },
            segment: new MusicSegmentDocument(
                Id: "calm",
                Transitions: null,
                Layers: [new MusicLayerDocument(
                        GainThousandths: (MaxGainThousandths + 1),
                        TuneId: "bed-tune",
                        When: null
                    )]
            )
        );
    }
}
