using Puck.Audio.Mixing;
using Puck.Overlays;
using Puck.World.Audio;
using Puck.World.Client;
using Xunit;

namespace Puck.World.Tests;

public sealed class WorldHudSpeechLawTests {
    private static readonly WorldVoiceProfile Voice = new(CadenceTicks: 16, FormantThousandths: 1150, PaceThousandths: 1250, PatchId: "voice", PitchThousandths: 1100);
    private static readonly WorldHudRect Rect = new(Height: 1, Width: 1, X: 0, Y: 0);

    [InlineData(false)]
    [InlineData(true)]
    [Theory]
    public void VisibleHudTextReachesTheLiveMixerAndTheAudioClockIgnoresFrameCadence(bool sampled) {
        using var scratch = new Puck.Testing.TemporaryDirectory(prefix: "puck-hud-speech-");
        var patch = new Puck.Assets.Documents.SynthPatchDocument(
            Schema: "puck.synthesizer-patch.v1", Name: "voice", Oscillator: Puck.Assets.Documents.SynthOscillator.Saw,
            DutyThousandths: null, Polynomial: null, AttackFrames: 96, DecayFrames: 240,
            SustainThousandths: 650, ReleaseFrames: 240, PitchMillihertz: 240000);

        File.WriteAllText(Path.Combine(path1: scratch.RootPath, path2: "voice.patch.json"),
            System.Text.Json.JsonSerializer.Serialize(patch, Puck.Assets.Documents.DocumentJsonOptions.Shared));
        var definition = Fixtures.BuildDocument() with {
            DocumentDirectory = scratch.RootPath,
            PatchesRaw = [new WorldPatch("voice", "voice.patch.json", Puck.Assets.Documents.SynthPatchCanonicalizer.Canonicalize(document: patch, source: "voice").Hash)],
            AudioRaw = new WorldAudioDefaults(Cues: [], DefaultBedFadeSeconds: 0, DefaultCurve: "linear", DefaultSpeakerRadius: 8, Listener: "focus", MasterGain: 1),
        };
        var voice = Voice;

        if (sampled) {
            var pin = WriteSampleBank(directory: scratch.RootPath, patch: WorldVoicePatchFactory.FromDocument(document: Puck.Assets.Documents.SynthPatchCanonicalizer.Normalize(document: patch)));
            voice = Voice with { Hash = pin, PatchId = null, SampleSet = "set.json" };
        }
        var element = new WorldHudElement("note", WorldHudElementKind.Text, Rect, WorldHudStyleToken.Primary,
            Text: "Ae, b!", Voice: voice);
        var panel = new WorldHudPanel("guide", Rect, WorldHudLayer.Over, WorldHudPanelStyle.Panel, [element]);

        short[] Mix(float frameDelta) {
            var director = new WorldAudioDirector(null, null);
            var mixer = new AudioMixer();

            director.ReconcileSpeakers(definition: definition);
            var hud = new WorldHudSpeechFeed(bindings: new Bindings(), speak: director.SpeakText, stop: director.StopText);

            hud.BeginFrame();
            hud.Observe(panel: panel, seat: -1);
            hud.EndFrame();
            // A late device attach binds the already scheduled utterance without changing its schedule.
            director.AttachMixer(mixer: mixer);
            var output = new short[(24000 * 2)];

            for (var frame = 0; (frame < 24000); frame += 200) {
                director.Publish(deltaSeconds: frameDelta, seats: [], transforms: []);
                Assert.True(condition: director.TryMixBlock(stereoInterleaved: output.AsSpan(length: 400, start: (frame * 2))));
            }
            Assert.True(condition: (output.AsSpan().IndexOfAnyExcept(value: ((short)0)) >= 0));
            Assert.Contains("textStarted=1", director.DescribeVoiceState());
            hud.BeginFrame();
            hud.EndFrame();
            Assert.Equal(0, mixer.BoundSourceCount);
            director.DetachMixer();
            return output;
        }
        Assert.Equal(Mix(frameDelta: (1f / 60)), Mix(frameDelta: (1f / 144)));
    }
    [Fact]
    public void AVisibleTextArrivalOrChangeSpeaksOnceAndHidingStopsIt() {
        var resolver = new Bindings { Value = "A sphere." };
        var spoken = new List<string>();
        var stopped = new List<string>();
        var feed = new WorldHudSpeechFeed(bindings: resolver, speak: (key, voice, text) => spoken.Add(item: $"{key}:{text}:{voice.PitchThousandths}"), stop: stopped.Add);
        var element = new WorldHudElement("note", WorldHudElementKind.Text, Rect, WorldHudStyleToken.Primary,
            Template: "Guide: {state.note}", Voice: Voice);
        var panel = new WorldHudPanel("guide", Rect, WorldHudLayer.Over, WorldHudPanelStyle.Panel, [element]);

        void Frame(bool visible = true) {
            feed.BeginFrame();
            if (visible) {
                feed.Observe(panel: panel, seat: -1);
            }
            feed.EndFrame();
        }
        Frame();
        Frame();
        Assert.Equal(actual: spoken, expected: ["-1/guide/note:Guide: A sphere.:1100"]);
        resolver.Value = "A box.";
        Frame();
        Assert.Equal(2, spoken.Count);
        Frame(visible: false);
        Assert.Equal(actual: stopped, expected: ["-1/guide/note"]);
        Frame();
        Assert.Equal(3, spoken.Count);
        panel = panel with { Elements = [element with { Voice = Voice with { PitchThousandths = 800 } }] };
        Frame();
        Assert.EndsWith(":800", spoken[^1]);
    }
    [Fact]
    public void EmptyBoundTextStopsAndLiteralTextAndSeatsResolveIndependently() {
        var resolver = new Bindings();
        var spoken = new List<string>();
        var stopped = new List<string>();
        var feed = new WorldHudSpeechFeed(bindings: resolver, speak: (key, _, text) => spoken.Add(item: $"{key}:{text}"), stop: stopped.Add);
        var bound = new WorldHudElement("note", WorldHudElementKind.Text, Rect, WorldHudStyleToken.Primary, Binding: "state.note", Voice: Voice);
        var literal = bound with { Id = "sign", Binding = null, Text = "Welcome!" };
        var panel = new WorldHudPanel("guide", Rect, WorldHudLayer.Over, WorldHudPanelStyle.Panel, [bound, literal]);

        feed.BeginFrame();
        feed.Observe(panel: panel, seat: 0);
        feed.Observe(panel: panel, seat: 1);
        feed.EndFrame();
        Assert.Equal(actual: spoken, expected: ["0/guide/sign:Welcome!", "1/guide/sign:Welcome!"]);
        Assert.Equal(actual: stopped, expected: ["0/guide/note", "1/guide/note"]);
        Assert.Equal("Welcome!", WorldHudSpeechFeed.ResolveText(bindings: resolver, element: literal, seat: 0));
    }
    [Fact]
    public void OfflineWaveExportsAreByteIdenticalAndCarryTheCompleteSchedule() {
        var patch = new VoicePatch(AttackFrames: 96, DecayFrames: 240, DurationFrames: 2400, DutyDcOffsetQ16: 0, DutyThresholdQ32: 0, FilterCoefficientQ16: 0, FilterDampingQ16: 65536, FilterMode: VoiceFilterMode.Bypass,
            Oscillator: SynthOscillator.Saw, PitchMillihertz: 240000, Polynomial: 0, ReleaseFrames: 240, SustainQ16: 42598, SweepMillihertzPerFrame: 0, VibratoDepthMillihertz: 0, VibratoRateMillihertz: 0);
        using var first = new MemoryStream();
        using var second = new MemoryStream();
        var source = new TextSpeechSource("Ae, b!", patch, 16);

        WorldSpeechRenderer.WriteWave(output: first, source: source);
        WorldSpeechRenderer.WriteWave(output: second, source: new TextSpeechSource("Ae, b!", patch, 16));
        Assert.Equal(first.ToArray(), second.ToArray());
        Assert.Equal((44 + (source.TotalFrames * 4)), first.Length);
        Assert.Equal("RIFF", System.Text.Encoding.ASCII.GetString(first.ToArray(), 0, 4));
        Assert.Equal((source.TotalFrames * 4), BitConverter.ToUInt32(first.ToArray(), 40));
    }

    private static string WriteSampleBank(string directory, VoicePatch patch) {
        var pins = new Dictionary<char, string>();

        foreach (var letter in "aeioub") {
            var wavPath = Path.Combine(path1: directory, path2: $"{letter}.wav");
            using (var output = File.Create(path: wavPath)) {
                WorldSpeechRenderer.WriteWave(output: output, source: new TextSpeechSource(letter.ToString(), patch, 24));
            }
            pins[letter] = Puck.Assets.ContentPin.Compute(content: File.ReadAllBytes(path: wavPath)).Hex;
        }
        var set = new WorldVoiceSampleSet(
            Name: "test",
            A: "a.wav",
            E: "e.wav",
            I: "i.wav",
            O: "o.wav",
            U: "u.wav",
            Consonant: "b.wav",
            PlaybackRateThousandths: 1500,
            AHash: pins['a'],
            EHash: pins['e'],
            IHash: pins['i'],
            OHash: pins['o'],
            UHash: pins['u'],
            ConsonantHash: pins['b']
        );
        File.WriteAllText(
            path: Path.Combine(path1: directory, path2: "set.json"),
            contents: System.Text.Json.JsonSerializer.Serialize(value: set, options: Puck.Assets.Documents.DocumentJsonOptions.Shared)
        );
        Assert.True(condition: WorldVoiceSampleLoader.TryCanonicalHash(documentDirectory: directory, source: "set.json", pin: out var manifestPin, error: out var error), userMessage: error);
        return manifestPin;
    }

    [Fact]
    public void SampleLoaderAndOfflineFactoryUseDocumentRelativeAssetsAndRefuseBrokenBanks() {
        using var scratch = new Puck.Testing.TemporaryDirectory(prefix: "puck-voice-samples-");
        var patchDocument = new Puck.Assets.Documents.SynthPatchDocument(Schema: "puck.synthesizer-patch.v1", Name: "voice",
            Oscillator: Puck.Assets.Documents.SynthOscillator.Saw, DutyThousandths: null, Polynomial: null, PitchMillihertz: 240000, AttackFrames: 96, DecayFrames: 240,
            SustainThousandths: 650, ReleaseFrames: 240);

        var manifestPin = WriteSampleBank(directory: scratch.RootPath, patch: WorldVoicePatchFactory.FromDocument(document: Puck.Assets.Documents.SynthPatchCanonicalizer.Normalize(document: patchDocument)));
        var definition = Fixtures.BuildDocument() with { DocumentDirectory = scratch.RootPath };
        var voice = Voice with { Hash = manifestPin, PatchId = null, SampleSet = "set.json" };

        Assert.True(condition: WorldVoiceSampleLoader.TryLoad(scratch.RootPath, "set.json", out var bank, out var rate, out var error, hash: manifestPin), userMessage: error);
        Assert.Equal(6, bank.Length);
        Assert.Equal(actual: rate, expected: 1500);
        Assert.True(condition: WorldTextSpeechFactory.TryCreate(definition: definition, error: out error, source: out var first, text: "Ae, b!", voice: voice), userMessage: error);
        Assert.True(condition: WorldTextSpeechFactory.TryCreate(definition: definition, error: out error, source: out var second, text: "Ae, b!", voice: voice), userMessage: error);
        using var a = new MemoryStream();
        using var b = new MemoryStream();

        WorldSpeechRenderer.WriteWave(output: a, source: first!);
        WorldSpeechRenderer.WriteWave(output: b, source: second!);
        Assert.Equal(a.ToArray(), b.ToArray());
        Assert.Contains(collection: Puck.Assets.PcmWave.ReadMono(bytes: a.ToArray()), filter: value => (value != 0));
        Assert.False(condition: WorldVoiceSampleLoader.TryLoad(documentDirectory: null, error: out error, rate: out _, samples: out _, source: "set.json"));
        Assert.Contains(actualString: error, expectedSubstring: "no directory");
        File.WriteAllBytes(Path.Combine(path1: scratch.RootPath, path2: "a.wav"), [0, 1, 2]);
        Assert.False(condition: WorldVoiceSampleLoader.TryLoad(scratch.RootPath, "set.json", out _, out _, out error, hash: manifestPin));
        Assert.Contains(actualString: error, expectedSubstring: "content pin");
        Assert.False(condition: WorldTextSpeechFactory.TryCreate(definition: definition, error: out _, source: out _, text: "a", voice: voice));
    }
    [Fact]
    public void DeliveredWorldFetchesPinnedSampledVoiceThroughContentResolverAndSpeaksIdentically() {
        using var sender = new Puck.Testing.TemporaryDirectory(prefix: "puck-voice-sender-");
        using var recipientStoreDir = new Puck.Testing.TemporaryDirectory(prefix: "puck-voice-recipient-");
        var patchDocument = new Puck.Assets.Documents.SynthPatchDocument(Schema: "puck.synthesizer-patch.v1", Name: "voice",
            Oscillator: Puck.Assets.Documents.SynthOscillator.Saw, DutyThousandths: null, Polynomial: null, PitchMillihertz: 240000, AttackFrames: 88, DecayFrames: 210,
            SustainThousandths: 610, ReleaseFrames: 220);

        var manifestPin = WriteSampleBank(directory: sender.RootPath, patch: WorldVoicePatchFactory.FromDocument(document: Puck.Assets.Documents.SynthPatchCanonicalizer.Normalize(document: patchDocument)));
        var voice = Voice with { Hash = manifestPin, PatchId = null, SampleSet = "set.json" };
        var element = new WorldHudElement("note", WorldHudElementKind.Text, Rect, WorldHudStyleToken.Primary, Text: "Ae, b!", Voice: voice);
        var panel = new WorldHudPanel("guide", Rect, WorldHudLayer.Over, WorldHudPanelStyle.Panel, [element]);
        var authored = Fixtures.BuildDocument() with {
            DocumentDirectory = sender.RootPath,
            HudRaw = new WorldHudSection(Defaults: new WorldHudDefaults(Enabled: true), Panels: [panel]),
        };

        var senderObjects = new Dictionary<Puck.Assets.ContentPin, byte[]>();
        var canonicalManifest = WorldVoiceSampleLoader.CanonicalBytes(bytes: File.ReadAllBytes(path: Path.Combine(path1: sender.RootPath, path2: "set.json")))!;
        senderObjects[Puck.Assets.ContentPin.Compute(content: canonicalManifest)] = canonicalManifest;
        foreach (var letter in "aeioub") {
            var wavBytes = File.ReadAllBytes(path: Path.Combine(path1: sender.RootPath, path2: $"{letter}.wav"));
            senderObjects[Puck.Assets.ContentPin.Compute(content: wavBytes)] = wavBytes;
        }

        var delivered = authored with { DocumentDirectory = null };
        var recipientStore = new Puck.Assets.ContentAddressedStore(root: recipientStoreDir.RootPath);

        WorldContentIndex.Fetch(
            definition: delivered,
            held: recipientStore,
            fetch: pin => (WorldContentIndex.Names(definition: authored, maximumBytes: out _, pin: pin) && senderObjects.TryGetValue(key: pin, value: out var bytes)) ? bytes : null
        );
        Assert.True(condition: WorldDefinitionValidator.TryValidateDeliveredAudio(definition: delivered, reason: out var reason), userMessage: reason);
        Assert.True(condition: WorldTextSpeechFactory.TryCreate(definition: authored, error: out var authoredError, source: out var authoredSpeech, text: "Ae, b!", voice: voice), userMessage: authoredError);
        Assert.True(condition: WorldTextSpeechFactory.TryCreate(definition: delivered, error: out var deliveredError, source: out var deliveredSpeech, text: "Ae, b!", voice: voice), userMessage: deliveredError);

        using var expected = new MemoryStream();
        using var actual = new MemoryStream();
        WorldSpeechRenderer.WriteWave(output: expected, source: authoredSpeech!);
        WorldSpeechRenderer.WriteWave(output: actual, source: deliveredSpeech!);
        Assert.Equal(expected.ToArray(), actual.ToArray());
        Assert.Contains(collection: Puck.Assets.PcmWave.ReadMono(bytes: actual.ToArray()), filter: value => (value != 0));
    }
    [Fact]
    public void ComposedHudAndIdentityVoicesRelocateSampleManifests() {
        var node = System.Text.Json.Nodes.JsonNode.Parse("""
            { "identity": { "voice": { "sampleSet": "voices/set.json" } },
              "hud": { "panels": [{ "elements": [{ "voice": { "sampleSet": "voices/set.json" } }] }] },
              "patches": [{ "source": "patches/voice.json" }], "host": { "icon": "icon.png" },
              "machines": [{ "contentFolders": ["cartridges", "other"] }],
              "payload": { "voice": { "sampleSet": "leave-this-value-alone" } } }
            """)!.AsObject();

        WorldDocumentPaths.RelocateDocumentFields(node, "nested/guide.puck", "debug.puck");
        Assert.Equal("nested/voices/set.json", node["identity"]!["voice"]!["sampleSet"]!.GetValue<string>());
        Assert.Equal("nested/voices/set.json", node["hud"]!["panels"]![0]!["elements"]![0]!["voice"]!["sampleSet"]!.GetValue<string>());
        Assert.True(condition: WorldDocumentPaths.IsFileField(declaringType: typeof(WorldVoiceProfile), member: nameof(WorldVoiceProfile.SampleSet)));
        Assert.Equal("nested/patches/voice.json", node["patches"]![0]!["source"]!.GetValue<string>());
        Assert.Equal("nested/icon.png", node["host"]!["icon"]!.GetValue<string>());
        Assert.Equal("nested/cartridges", node["machines"]![0]!["contentFolders"]![0]!.GetValue<string>());
        Assert.Equal("leave-this-value-alone", node["payload"]!["voice"]!["sampleSet"]!.GetValue<string>());
    }

    private sealed class Bindings : IHudBindingResolver {
        public string Value { get; set; } = "";

        public bool TryResolve(string binding, int seat, out float fraction, out ReadOnlySpan<char> text) {
            fraction = 0;
            text = Value;
            return true;
        }
    }
}
