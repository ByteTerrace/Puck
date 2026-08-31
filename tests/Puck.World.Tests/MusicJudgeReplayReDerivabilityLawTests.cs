using Xunit;

using Puck.Assets.Documents;
using Puck.World.Authoring;
using Puck.World.Protocol;
using Puck.World.Server;

namespace Puck.World.Tests;

/// <summary>
/// CONTRACT UNDER TEST: <c>MusicClock</c>/<c>MusicDirector</c>/<c>RhythmJudge</c>'s replay claim — neither is
/// captured directly in <c>WorldReplaySnapshot</c>, on the stated claim that both are purely RE-DERIVABLE from the
/// document plus tick 0, never taped. Proves it directly rather than assuming it: the IDENTICAL script against the
/// IDENTICAL document, run in TWO independent fresh <see cref="WorldServer"/> boots, must produce the
/// byte-identical <c>music.state</c>/<c>judge.state</c> stream at every checkpoint. A wall-clock read anywhere on
/// this path (the class the "no wall clock in simulation state" rule exists to catch) would make the two runs
/// diverge, since real time elapses differently across two sequential process-local runs.
/// </summary>
public sealed class MusicJudgeReplayReDerivabilityLawTests {
    [Fact]
    public void IdenticalScriptReDerivesTheIdenticalMusicAndJudgeStream() {
        var directoryA = Directory.CreateTempSubdirectory(prefix: "puck-replay-law-a-").FullName;
        var directoryB = Directory.CreateTempSubdirectory(prefix: "puck-replay-law-b-").FullName;

        try {
            var streamA = RunScriptAndCollect(assetDirectory: directoryA);
            var streamB = RunScriptAndCollect(assetDirectory: directoryB);

            Assert.Equal(actual: streamB, expected: streamA);
        } finally {
            Directory.Delete(path: directoryA, recursive: true);
            Directory.Delete(path: directoryB, recursive: true);
        }
    }

    /// <summary>Drives one fresh, independent boot through the identical script and collects its
    /// <c>music.state</c>/<c>judge.state</c> stream, in order. A fresh <paramref name="assetDirectory"/> per call —
    /// <see cref="ActionEffectJudgeLawTests.BuildJudgePressDocument"/> writes real Music/Judge asset files an
    /// absolute <c>Source</c> resolves against, so the SAME document content is authored twice, over two DIFFERENT
    /// directories, rather than two servers racing one shared file.</summary>
    private static IReadOnlyList<string> RunScriptAndCollect(string assetDirectory) {
        using var fixture = Fixtures.FreshServer(definition: ActionEffectJudgeLawTests.BuildJudgePressDocument(assetDirectory: assetDirectory));
        var actor = WorldPrincipal.Seat(slot: 0);

        Assert.True(condition: fixture.Server.ApplySession(request: new SessionRequest.Join(Principal: actor, Slot: actor.Index, IdentityName: null, WireProtocolKey: WorldProtocol.WireProtocolKey)).Accepted);

        var body = fixture.Server.Body(index: actor.Index)!;
        var stream = new List<string>();

        for (var step = 0; (step < 9); step++) {
            fixture.Step();
        }

        stream.Add(item: fixture.Server.Answer(query: new WorldQuery.MusicState(Index: 1)).Text);

        body.SubmitIntent(intent: default(PlayerIntent).WithChannel(ordinal: ActionEffectJudgeLawTests.JudgeOrdinal, value: Puck.Maths.FixedQ4816.One));
        fixture.Step();

        stream.Add(item: fixture.Server.Answer(query: new WorldQuery.JudgeState(Index: 1)).Text);

        for (var step = 0; (step < 10); step++) {
            fixture.Step();
        }

        body.SubmitIntent(intent: default(PlayerIntent).WithChannel(ordinal: ActionEffectJudgeLawTests.JudgeOrdinal, value: Puck.Maths.FixedQ4816.Zero));
        fixture.Step();
        body.SubmitIntent(intent: default(PlayerIntent).WithChannel(ordinal: ActionEffectJudgeLawTests.JudgeOrdinal, value: Puck.Maths.FixedQ4816.One));
        fixture.Step();

        stream.Add(item: fixture.Server.Answer(query: new WorldQuery.MusicState(Index: 1)).Text);
        stream.Add(item: fixture.Server.Answer(query: new WorldQuery.JudgeState(Index: 1)).Text);

        return stream;
    }

    /// <summary>Proves <c>MusicDirector.ActiveLayerTuneIds</c>/<c>LastEmbellishmentPatchId</c>/
    /// <c>LastEmbellishmentTick</c> re-derive identically the same way the transition/judge stream above already
    /// does — an unconditional layer, a layer conditioned on the seat-join edge <see cref="SessionRequest.Join"/>
    /// itself fires, and an embellishment on the same edge, so the two independent boots' <c>music.state</c> text
    /// actually exercises the new fields rather than both trivially reading "none".</summary>
    [Fact]
    public void IdenticalScriptReDerivesTheIdenticalActiveLayerAndEmbellishmentStream() {
        var directoryA = Directory.CreateTempSubdirectory(prefix: "puck-replay-law-layers-a-").FullName;
        var directoryB = Directory.CreateTempSubdirectory(prefix: "puck-replay-law-layers-b-").FullName;

        try {
            var streamA = RunLayeredScriptAndCollect(assetDirectory: directoryA);
            var streamB = RunLayeredScriptAndCollect(assetDirectory: directoryB);

            Assert.Equal(actual: streamB, expected: streamA);
            Assert.Contains(actualString: streamA[0], comparisonType: StringComparison.Ordinal, expectedSubstring: "layers=ambient-tune,arrival-tune");
            Assert.Contains(actualString: streamA[0], comparisonType: StringComparison.Ordinal, expectedSubstring: "lastEmbellishment=stinger");
        } finally {
            Directory.Delete(path: directoryA, recursive: true);
            Directory.Delete(path: directoryB, recursive: true);
        }
    }

    private static IReadOnlyList<string> RunLayeredScriptAndCollect(string assetDirectory) {
        using var fixture = Fixtures.FreshServer(definition: BuildLayeredMusicDocument(assetDirectory: assetDirectory));
        var actor = WorldPrincipal.Seat(slot: 0);

        Assert.True(condition: fixture.Server.ApplySession(request: new SessionRequest.Join(Principal: actor, Slot: actor.Index, IdentityName: null, WireProtocolKey: WorldProtocol.WireProtocolKey)).Accepted);

        // The join lands as a seat.join sense edge on THIS step (WorldEventFeed.Collect runs inside StepCore) — the
        // conditional layer and the embellishment both arm/fire off it here, not at the join call itself.
        fixture.Step();

        var stream = new List<string> {
            fixture.Server.Answer(query: new WorldQuery.MusicState(Index: 1)).Text,
        };

        for (var step = 0; (step < 5); step++) {
            fixture.Step();
        }

        stream.Add(item: fixture.Server.Answer(query: new WorldQuery.MusicState(Index: 1)).Text);

        return stream;
    }
    private static WorldDefinition BuildLayeredMusicDocument(string assetDirectory) {
        var music = MusicCanonicalizer.Canonicalize(document: new MusicDocument(
            Schema: MusicDocument.CurrentSchema,
            Name: "layered-score",
            Tempo: new MusicTempoDocument(BeatsPerBar: 4, TicksPerBeat: ((int)ActionEffectJudgeLawTests.TicksPerBeat)),
            Segments: [
                new MusicSegmentDocument(
                    Id: "calm",
                    Transitions: null,
                    Layers: [
                        new MusicLayerDocument(TuneId: "ambient-tune", GainThousandths: null, When: null),
                        new MusicLayerDocument(TuneId: "arrival-tune", GainThousandths: null, When: WorldAudioCue.SeatJoin),
                    ],
                    Embellishments: [
                        new MusicEmbellishmentDocument(PatchId: "stinger", When: WorldAudioCue.SeatJoin, GainThousandths: null),
                    ]
                ),
            ]
        ));
        var musicPath = Path.Combine(path1: assetDirectory, path2: "layered-score.puck.music.v1.json");

        File.WriteAllBytes(path: musicPath, bytes: music.Bytes);

        var ambientTune = AudioCanonicalizer.Canonicalize(document: new AudioDocument(Schema: AudioDocument.CurrentSchema, Name: "ambient", Tempo: null, Patterns: null, Order: null, Effects: null));
        var arrivalTune = AudioCanonicalizer.Canonicalize(document: new AudioDocument(Schema: AudioDocument.CurrentSchema, Name: "arrival", Tempo: null, Patterns: null, Order: null, Effects: null));
        var stingerPatch = SynthPatchCanonicalizer.Canonicalize(document: new SynthPatchDocument(Schema: SynthPatchDocument.CurrentSchema, Name: "stinger", Oscillator: null, DutyThousandths: null, Polynomial: null, AttackFrames: null, DecayFrames: null, SustainThousandths: null, ReleaseFrames: null, PitchMillihertz: 440_000));

        var ambientTunePath = Path.Combine(path1: assetDirectory, path2: "ambient-tune.puck.audio.v1.json");
        var arrivalTunePath = Path.Combine(path1: assetDirectory, path2: "arrival-tune.puck.audio.v1.json");
        var stingerPatchPath = Path.Combine(path1: assetDirectory, path2: "stinger.puck.synth.v1.json");

        File.WriteAllBytes(path: ambientTunePath, bytes: ambientTune.Bytes);
        File.WriteAllBytes(path: arrivalTunePath, bytes: arrivalTune.Bytes);
        File.WriteAllBytes(path: stingerPatchPath, bytes: stingerPatch.Bytes);

        return Fixtures.BuildDocument() with {
            Music = [new WorldMusicRow(Name: "layered-score", Source: musicPath, Hash: music.Hash)],
            PatchesRaw = [new WorldPatch(Name: "stinger", Source: stingerPatchPath, Hash: stingerPatch.Hash)],
            TunesRaw = [
                new WorldTune(Name: "ambient-tune", Source: ambientTunePath, Hash: ambientTune.Hash),
                new WorldTune(Name: "arrival-tune", Source: arrivalTunePath, Hash: arrivalTune.Hash),
            ],
        };
    }
}
