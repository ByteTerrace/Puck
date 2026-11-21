using Puck.Assets.Documents;
using Puck.Audio.Mixing;
using Puck.World.Client;
using Puck.World.Protocol;
using Xunit;

namespace Puck.World.Tests;

public sealed class WorldSpeakerGainLawTests {
    [InlineData(false)]
    [InlineData(true)]
    [Theory]
    public void PublishedGainFollowsTheMirrorWithoutRecreatingTheEmitterOrRetriggeringItsVoice(bool bed) {
        using var scratch = new Puck.Testing.TemporaryDirectory(prefix: "puck-speaker-gain-");
        var patch = new SynthPatchDocument(Schema: "puck.synthesizer-patch.v1", Name: "signal",
            Oscillator: Puck.Assets.Documents.SynthOscillator.Saw, DutyThousandths: null, Polynomial: null,
            AttackFrames: 0, DecayFrames: 0, SustainThousandths: 1000, ReleaseFrames: 0, PitchMillihertz: 240000);

        File.WriteAllText(Path.Combine(path1: scratch.RootPath, path2: "signal.json"),
            System.Text.Json.JsonSerializer.Serialize(patch, DocumentJsonOptions.Shared));
        var feed = new WorldSpeakerFeed(new WorldSpeakerSource.Synth(PatchId: "signal"), "mix", new BindableScalar(binding: "state.level"));
        WorldSpeaker speaker = (bed
            ? new WorldSpeaker.Bed("signal", new DocumentVector3(x: 0, y: 0, z: 0), 8, 2, feed, 0)
            : new WorldSpeaker.Fixed("signal", new DocumentVector3(x: 0, y: 0, z: 0), feed));
        var definition = (Fixtures.BuildDocument() with {
            DocumentDirectory = scratch.RootPath,
            SpeakersRaw = [speaker],
            PatchesRaw = [new WorldPatch("signal", "signal.json", SynthPatchCanonicalizer.Canonicalize(document: patch, source: "signal").Hash)],
            AudioRaw = new WorldAudioDefaults(Cues: [], DefaultBedFadeSeconds: 0, DefaultCurve: "linear", DefaultSpeakerRadius: 8, Listener: "focus", MasterGain: 1),
        }).WithWorldState(rows: [StateFixtures.FixedSlot(name: "level", rawBits: 32768)]);
        var client = ClientFixtures.Client(definition: definition);
        var director = new WorldAudioDirector(client, null);

        client.DeliverDefinition(definition: definition, version: default, stream: default);
        director.ReconcileSpeakers(definition: definition);
        var mixer = new AudioMixer();
        var mixed = new short[400];

        director.AttachMixer(mixer: mixer);
        // Publish alone must resolve gain; no inspection command runs before these snapshot and PCM assertions.
        var first = director.Publish([], []);
        var id = Assert.Single(collection: first.Emitters.ToArray()).Id;

        Assert.Equal(32768, first.Emitters[0].GainQ16);
        Assert.Single(collection: first.Triggers.ToArray());
        Assert.True(condition: director.TryMixBlock(stereoInterleaved: mixed));
        Assert.Equal(1, mixer.Synth.ActiveVoiceCount);
        // Let the original arrival trigger's retention expire before changing the gain.
        for (var index = 0; (index < WorldAudioDirector.TriggerPublishRetention); index++) {
            director.Publish([], []);
            Assert.True(condition: director.TryMixBlock(stereoInterleaved: mixed));
        }

        void Present(long raw, int expected) {
            definition = definition.WithWorldState(rows: [StateFixtures.FixedSlot(name: "level", rawBits: raw)]);
            client.DeliverState(definition, default, new WorldStateStamp(EngineTick: 1680, Everything: true, MovedRows: ReadOnlyMemory<int>.Empty, Tick: 1), stream: default);
            var published = director.Publish([], []);

            Assert.Equal(id, Assert.Single(collection: published.Emitters.ToArray()).Id);
            Assert.Equal(expected, published.Emitters[0].GainQ16);
            Assert.Empty(collection: published.Triggers.ToArray());
            Assert.True(condition: director.TryMixBlock(stereoInterleaved: mixed));
            Assert.Equal(1, mixer.Synth.ActiveVoiceCount);
        }

        Present(expected: 49152, raw: 49152);
        Present(expected: 0, raw: -65536);
        // The block ramps down from the preceding gain instead of dropping the voice or cutting its coefficient.
        Assert.NotEqual(0, mixed[0]);
        Assert.Equal(0, mixed[^1]);
        Present(expected: ((int)(Puck.World.Authoring.CreationSoundDocument.MaxLevel * 65536)), raw: (999L * 65536));
        Present(expected: 32768, raw: 32768);

        // A literal edit is also a property change, retaining the same emitter and sustained voice.
        definition = definition with { SpeakersRaw = [speaker with { Feed = feed with { Gain = 0.25f } }] };
        client.DeliverDefinition(definition: definition, version: default, stream: default);
        director.ReconcileSpeakers(definition: definition);
        var literal = director.Publish([], []);

        Assert.Equal(id, literal.Emitters[0].Id);
        Assert.Equal(16384, literal.Emitters[0].GainQ16);
        Assert.Empty(collection: literal.Triggers.ToArray());
    }
    [Fact]
    public void OfflinePublicationResolvesKeyedGainThroughTheSameClockAndMirror() {
        var definition = (Fixtures.BuildDocument() with {
            TimelineRaw = new WorldTimelineSection(Clocks: [new WorldClock("level", State: "phase")]),
            SpeakersRaw = [new WorldSpeaker.Fixed("signal", new DocumentVector3(x: 0, y: 0, z: 0),
                new WorldSpeakerFeed(new WorldSpeakerSource.None(), "mix", new BindableScalar(keys: new WorldKeyTrack<float>(
                    clock: "level", keys: [new WorldKey<float>(At: 0, Ease: WorldEase.Linear, Value: 0), new WorldKey<float>(At: 0.5, Ease: WorldEase.Linear, Value: 1)]))),
                new WorldSpeakerAttenuation(Radius: 8f, Curve: null))],
        }).WithWorldState(rows: [StateFixtures.FixedSlot(name: "phase", rawBits: 16384)]);

        Assert.True(condition: WorldDefinitionValidator.TryValidateLocally(definition: definition, reason: out var reason), userMessage: reason);
        var director = new WorldAudioDirector(null, null);

        director.ReconcileSpeakers(definition: definition);
        var snapshot = director.Publish([], []);

        Assert.Equal(32768, Assert.Single(collection: snapshot.Emitters.ToArray()).GainQ16);
    }

    [Fact]
    public void PublishCullsOutsideSupportAndBindsMachinesOnEvent() {
        var near = new WorldSpeaker.Fixed(
            Name: "near",
            Position: new DocumentVector3(x: 1, y: 0, z: 0),
            Feed: new WorldSpeakerFeed(new WorldSpeakerSource.Machine(Instance: "cab", Output: "audio"), "mix", 1f),
            Attenuation: new WorldSpeakerAttenuation(Radius: 8f, Curve: "linear")
        );
        var far = new WorldSpeaker.Fixed(
            Name: "far",
            Position: new DocumentVector3(x: 50, y: 0, z: 0),
            Feed: new WorldSpeakerFeed(new WorldSpeakerSource.None(), "mix", 1f),
            Attenuation: new WorldSpeakerAttenuation(Radius: 8f, Curve: "linear")
        );
        var definition = Fixtures.BuildDocument() with {
            SpeakersRaw = [near, far],
            AudioRaw = new WorldAudioDefaults(Cues: [], DefaultBedFadeSeconds: 0, DefaultCurve: "linear", DefaultSpeakerRadius: 8, Listener: "focus", MasterGain: 1),
        };
        var resolverCalls = 0;
        var director = new WorldAudioDirector(client: null, animator: null) {
            MachineSourceResolver = (_, _) => {
                resolverCalls++;

                return null;
            },
        };

        director.ReconcileSpeakers(definition: definition);
        director.AttachMixer(mixer: new AudioMixer());

        var first = director.Publish([], []);

        // Only the in-support speaker enters the slab; far speaker is culled before building an emitter.
        var emitter = Assert.Single(collection: first.Emitters.ToArray());
        Assert.True(first.ContainsEmitter(emitter.Id));
        Assert.True(first.TryFindEmitterIndex(emitter.Id, out var foundIndex));
        Assert.Equal(0, foundIndex);
        Assert.False(first.ContainsEmitter(emitter.Id + 999));
        Assert.Equal(1, resolverCalls);

        // Steady frames do not poll the machine source resolver.
        for (var frame = 0; (frame < 16); frame++) {
            _ = director.Publish([], []);
        }
        Assert.Equal(1, resolverCalls);

        // An invalidation event re-syncs machine bindings once on the next publish.
        director.InvalidateMachineBindings();
        _ = director.Publish([], []);
        _ = director.Publish([], []);
        Assert.Equal(2, resolverCalls);
    }
}
