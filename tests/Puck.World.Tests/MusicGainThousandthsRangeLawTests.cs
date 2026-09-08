using Xunit;

using Puck.Assets.Documents;
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

    [Fact]
    public void OutOfRangeLayerGainRefusesByName() {
        WithDocument(
            assert: static document => {
                Assert.False(condition: WorldDefinitionValidator.TryValidate(definition: document, neighbours: null, reason: out var reason), userMessage: "an out-of-range layer gainThousandths was expected to refuse");
                Assert.Contains(actualString: reason, comparisonType: StringComparison.Ordinal, expectedSubstring: "layers.gainThousandths");
            },
            segment: new MusicSegmentDocument(
                Id: "calm",
                Transitions: null,
                Layers: [new MusicLayerDocument(GainThousandths: (MaxGainThousandths + 1), TuneId: "bed-tune", When: null)]
            )
        );
    }
    [Fact]
    public void NegativeLayerGainRefusesByName() {
        WithDocument(
            assert: static document => {
                Assert.False(condition: WorldDefinitionValidator.TryValidate(definition: document, neighbours: null, reason: out var reason), userMessage: "a negative layer gainThousandths was expected to refuse");
                Assert.Contains(actualString: reason, comparisonType: StringComparison.Ordinal, expectedSubstring: "layers.gainThousandths");
            },
            segment: new MusicSegmentDocument(
                Id: "calm",
                Transitions: null,
                Layers: [new MusicLayerDocument(GainThousandths: -1, TuneId: "bed-tune", When: null)]
            )
        );
    }
    [Fact]
    public void OutOfRangeEmbellishmentGainRefusesByName() {
        WithDocument(
            assert: static document => {
                Assert.False(condition: WorldDefinitionValidator.TryValidate(definition: document, neighbours: null, reason: out var reason), userMessage: "an out-of-range embellishment gainThousandths was expected to refuse");
                Assert.Contains(actualString: reason, comparisonType: StringComparison.Ordinal, expectedSubstring: "embellishments.gainThousandths");
            },
            segment: new MusicSegmentDocument(
                Id: "calm",
                Transitions: null,
                Embellishments: [new MusicEmbellishmentDocument(GainThousandths: (MaxGainThousandths + 1), PatchId: "stinger", When: "region.enter")]
            )
        );
    }
    [Fact]
    public void BoundaryAndUnauthoredGainControl() {
        // The control for the refusals above: the ceiling value itself, plus an unauthored (null) gain on the
        // sibling row, both validate clean — a refusal above is the range check firing, never a coincidental fault.
        WithDocument(
            assert: static document => {
                Assert.True(condition: WorldDefinitionValidator.TryValidate(definition: document, neighbours: null, reason: out var reason), userMessage: reason);
            },
            segment: new MusicSegmentDocument(
                Id: "calm",
                Transitions: null,
                Layers: [new MusicLayerDocument(GainThousandths: MaxGainThousandths, TuneId: "bed-tune", When: null)],
                Embellishments: [new MusicEmbellishmentDocument(GainThousandths: null, PatchId: "stinger", When: "region.enter")]
            )
        );
    }

    private static void WithDocument(Action<WorldDefinition> assert, MusicSegmentDocument segment) {
        var directory = Directory.CreateTempSubdirectory(prefix: "puck-music-gain-law-").FullName;

        try {
            var music = MusicCanonicalizer.Canonicalize(document: new MusicDocument(
                Schema: MusicDocument.CurrentSchema,
                Name: "gain-law",
                Tempo: new MusicTempoDocument(BeatsPerBar: 4, TicksPerBeat: 2100),
                Segments: [segment]
            ));
            var tune = AudioCanonicalizer.Canonicalize(document: new AudioDocument(Effects: null, Name: "bed", Order: null, Patterns: null, Schema: AudioDocument.CurrentSchema, Tempo: null));
            var patch = SynthPatchCanonicalizer.Canonicalize(document: new SynthPatchDocument(Schema: SynthPatchDocument.CurrentSchema, Name: "stinger", Oscillator: null, DutyThousandths: null, Polynomial: null, AttackFrames: null, DecayFrames: null, SustainThousandths: null, ReleaseFrames: null, PitchMillihertz: 440_000));
            var musicPath = Path.Combine(path1: directory, path2: "gain-law.puck.music.v1.json");
            var tunePath = Path.Combine(path1: directory, path2: "bed-tune.puck.audio.v1.json");
            var patchPath = Path.Combine(path1: directory, path2: "stinger.puck.synth.v1.json");

            File.WriteAllBytes(path: musicPath, bytes: music.Bytes);
            File.WriteAllBytes(path: tunePath, bytes: tune.Bytes);
            File.WriteAllBytes(path: patchPath, bytes: patch.Bytes);

            assert(obj: Fixtures.BuildDocument() with {
                Music = [new WorldMusicRow(Name: "gain-law", Source: musicPath, Hash: music.Hash)],
                PatchesRaw = [new WorldPatch(Name: "stinger", Source: patchPath, Hash: patch.Hash)],
                TunesRaw = [new WorldTune(Name: "bed-tune", Source: tunePath, Hash: tune.Hash)],
            });
        } finally {
            Directory.Delete(path: directory, recursive: true);
        }
    }
}
