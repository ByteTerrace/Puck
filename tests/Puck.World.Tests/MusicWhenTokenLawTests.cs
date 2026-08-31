using Xunit;

using Puck.Forge.Authoring;
using Puck.World.Server;

namespace Puck.World.Tests;

/// <summary>
/// CONTRACT UNDER TEST: <c>WorldAudioCue.MusicWhenTokens</c> single-sources the <c>puck.music.v1</c> <c>when</c>
/// vocabulary — <c>WorldDefinitionValidator</c> refuses a transition/layer/embellishment <c>when</c> outside it, and
/// <c>MusicDirectorFactory.ParseFamily</c> maps every token in it — so a cue-only token (one with no sense family
/// behind it) can never validate cleanly and then compile to a lane that cannot fire.
/// </summary>
public sealed class MusicWhenTokenLawTests {
    [Fact]
    public void MusicWhenTokensAreAPublishedEventTokenSubset() {
        foreach (var token in WorldAudioCue.MusicWhenTokens) {
            Assert.True(condition: WorldAudioCue.IsEventToken(token: token), userMessage: $"'{token}' is not a published event token");
        }
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
                Assert.True(condition: WorldDefinitionValidator.TryValidate(definition: document, neighbours: null, reason: out var reason), userMessage: reason);
            },
            segment: new MusicSegmentDocument(
                Id: "calm",
                Transitions: [.. WorldAudioCue.MusicWhenTokens.Select(selector: static token => new MusicTransitionDocument(To: "calm", When: token, At: null))],
                Layers: [.. WorldAudioCue.MusicWhenTokens.Select(selector: static token => new MusicLayerDocument(TuneId: "bed-tune", GainThousandths: null, When: token))],
                Embellishments: [.. WorldAudioCue.MusicWhenTokens.Select(selector: static token => new MusicEmbellishmentDocument(PatchId: "stinger", When: token, GainThousandths: null))]
            )
        );
    }
    [Fact]
    public void CueOnlyTransitionWhenRefusesByName() {
        AssertWhenRefuses(
            path: "transitions.when",
            segment: new MusicSegmentDocument(
                Id: "calm",
                Transitions: [new MusicTransitionDocument(To: "calm", When: WorldAudioCue.PlayerJump, At: null)]
            ),
            token: WorldAudioCue.PlayerJump
        );
    }
    [Fact]
    public void CueOnlyLayerWhenRefusesByName() {
        AssertWhenRefuses(
            path: "layers.when",
            segment: new MusicSegmentDocument(
                Id: "calm",
                Transitions: null,
                Layers: [new MusicLayerDocument(TuneId: "bed-tune", GainThousandths: null, When: WorldAudioCue.MutationApplied)]
            ),
            token: WorldAudioCue.MutationApplied
        );
    }
    [Fact]
    public void CueOnlyEmbellishmentWhenRefusesByName() {
        AssertWhenRefuses(
            path: "embellishments.when",
            segment: new MusicSegmentDocument(
                Id: "calm",
                Transitions: null,
                Embellishments: [new MusicEmbellishmentDocument(PatchId: "stinger", When: WorldAudioCue.GrantDenied, GainThousandths: null)]
            ),
            token: WorldAudioCue.GrantDenied
        );
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
                Layers: [new MusicLayerDocument(TuneId: "bed-tune", GainThousandths: null, When: " ")]
            ),
            token: " "
        );
    }

    private static void AssertWhenRefuses(MusicSegmentDocument segment, string path, string token) {
        WithDocument(
            assert: document => {
                Assert.False(condition: WorldDefinitionValidator.TryValidate(definition: document, neighbours: null, reason: out var reason), userMessage: $"'{token}' was expected to refuse");
                Assert.Contains(actualString: reason, comparisonType: StringComparison.Ordinal, expectedSubstring: path);
                Assert.Contains(actualString: reason, comparisonType: StringComparison.Ordinal, expectedSubstring: $"'{token}' is not a sense-mappable when token");
            },
            segment: segment
        );
    }
    private static void WithDocument(Action<WorldDefinition> assert, MusicSegmentDocument segment) {
        var directory = Directory.CreateTempSubdirectory(prefix: "puck-music-when-law-").FullName;

        try {
            var music = MusicCanonicalizer.Canonicalize(document: new MusicDocument(
                Schema: MusicDocument.CurrentSchema,
                Name: "when-law",
                Tempo: new MusicTempoDocument(BeatsPerBar: 4, TicksPerBeat: 2100),
                Segments: [segment]
            ));
            var tune = AudioCanonicalizer.Canonicalize(document: new AudioDocument(Schema: AudioDocument.CurrentSchema, Name: "bed", Tempo: null, Patterns: null, Order: null, Effects: null));
            var patch = SynthPatchCanonicalizer.Canonicalize(document: new SynthPatchDocument(Schema: SynthPatchDocument.CurrentSchema, Name: "stinger", Oscillator: null, DutyThousandths: null, Polynomial: null, AttackFrames: null, DecayFrames: null, SustainThousandths: null, ReleaseFrames: null, PitchMillihertz: 440_000));
            var musicPath = Path.Combine(path1: directory, path2: "when-law.puck.music.v1.json");
            var tunePath = Path.Combine(path1: directory, path2: "bed-tune.puck.audio.v1.json");
            var patchPath = Path.Combine(path1: directory, path2: "stinger.puck.synth.v1.json");

            File.WriteAllBytes(path: musicPath, bytes: music.Bytes);
            File.WriteAllBytes(path: tunePath, bytes: tune.Bytes);
            File.WriteAllBytes(path: patchPath, bytes: patch.Bytes);

            assert(obj: Fixtures.BuildDocument() with {
                Music = [new WorldMusicRow(Name: "when-law", Source: musicPath, Hash: music.Hash)],
                PatchesRaw = [new WorldPatch(Name: "stinger", Source: patchPath, Hash: patch.Hash)],
                TunesRaw = [new WorldTune(Name: "bed-tune", Source: tunePath, Hash: tune.Hash)],
            });
        } finally {
            Directory.Delete(path: directory, recursive: true);
        }
    }
}
