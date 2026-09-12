using Puck.Assets.Documents;
using Puck.GamingBricks.Forge;
using Puck.HumbleGamingBrick;
using Puck.HumbleGamingBrick.Forge;
using Puck.HumbleGamingBrick.Forge.Framework;


namespace Puck.AdvancedGamingBrick.Forge.Tests;

/// <summary>Covers authored music reaching the sound hardware, and the target gate on cartridge audio.</summary>
public sealed class CartridgeSoundTests {
    [Theory]
    [InlineData("cgb")]
    [InlineData("agb")]
    public void PlayStartsTheMusicVoiceAndStopSilencesIt(string target) {
        var document = CartridgeDocuments.Create(target: target, title: "SOUND") with {
            Variables = [new CartridgeVariable(Name: "phase", Initial: 0)],
            Sounds = [new CartridgeSound(Name: "theme", Music: [Lead(part: Track())])],
            Rules = [
                At(phase: 2, body: [new CartridgeStatement(Kind: "play", Sound: "theme")]),
                At(phase: 10, body: [new CartridgeStatement(Kind: "stop")]),
                // The counter advances every frame, so the gates above fire on the frames they name.
                new CartridgeRule(Name: "tick", When: CartridgeExpressions.Gate(left: CartridgeExpressions.Of(state: "phase"), comparison: ActionStateComparison.Less, right: CartridgeExpressions.Of(constant: 200)),
                    Body: [new CartridgeStatement(Kind: "set", Target: new CartridgeTarget(State: "phase"), Operation: ExpressionOp.Add, Value: CartridgeExpressions.Of(constant: 1))]),
            ],
        };
        ICartridgeCompiler compiler = target == "agb" ? new AgbCartridgeCompiler() : new HgbCartridgeCompiler();
        var result = compiler.Compile(document: document);
        using var machine = new SoundProbe(result: result);

        // Boot costs a few frames before the rule loop runs, so the gates land after it.
        machine.Run(frames: 8);
        Assert.NotEqual(expected: 0u, actual: machine.Playing());
        Assert.NotEqual(expected: 0u, actual: machine.ChannelActive());

        machine.Run(frames: 14);
        Assert.Equal(expected: 0u, actual: machine.Playing());
        Assert.Equal(expected: 0u, actual: machine.ChannelActive());
    }

    [Theory]
    [InlineData("cgb")]
    [InlineData("agb")]
    public void EveryVoiceOfATrackPlaysAtOnceAndStopsTogether(string target) {
        var document = CartridgeDocuments.Create(target: target, title: "TRIO") with {
            Variables = [new CartridgeVariable(Name: "phase", Initial: 0)],
            Sounds = [new CartridgeSound(Name: "theme", Music: [
                new CartridgeMusicVoice(Voice: AudioEffectDocument.VoicePulse2, Part: Track()),
                new CartridgeMusicVoice(Voice: AudioEffectDocument.VoicePulse1, Part: Track()),
                new CartridgeMusicVoice(Voice: AudioEffectDocument.VoiceWave, Part: Track(), Waveform: Waveform()),
            ])],
            Rules = [
                At(phase: 2, body: [new CartridgeStatement(Kind: "play", Sound: "theme")]),
                At(phase: 30, body: [new CartridgeStatement(Kind: "stop")]),
                new CartridgeRule(Name: "tick", When: CartridgeExpressions.Gate(left: CartridgeExpressions.Of(state: "phase"), comparison: ActionStateComparison.Less, right: CartridgeExpressions.Of(constant: 200)),
                    Body: [new CartridgeStatement(Kind: "set", Target: new CartridgeTarget(State: "phase"), Operation: ExpressionOp.Add, Value: CartridgeExpressions.Of(constant: 1))]),
            ],
        };
        ICartridgeCompiler compiler = target == "agb" ? new AgbCartridgeCompiler() : new HgbCartridgeCompiler();
        var result = compiler.Compile(document: document);
        using var machine = new SoundProbe(result: result);

        machine.Run(frames: 8);

        // The status register's low nibble reports the channels that are sounding; the track claims three of them.
        Assert.Equal(expected: 0x07u, actual: machine.Status() & 0x07u);

        machine.Run(frames: 34);
        Assert.Equal(expected: 0u, actual: machine.Status() & 0x07u);
    }

    [Theory]
    [InlineData("cgb", "pulse1")]
    [InlineData("cgb", "noise")]
    [InlineData("cgb", "wave")]
    [InlineData("agb", "pulse1")]
    [InlineData("agb", "noise")]
    [InlineData("agb", "wave")]
    public void AnEffectSoundsOnItsOwnVoiceWithoutStoppingTheMusic(string target, string voice) {
        var document = CartridgeDocuments.Create(target: target, title: "SFX") with {
            Variables = [new CartridgeVariable(Name: "phase", Initial: 0)],
            Sounds = [
                new CartridgeSound(Name: "theme", Music: [Lead(part: Track())]),
                new CartridgeSound(Name: "blip", Effect: new AudioEffectDocument(Voice: voice, Rows: [
                    new AudioRowDocument(Note: "C5", Duty: null, Envelope: null),
                    new AudioRowDocument(Note: "G5", Duty: null, Envelope: null),
                ]), Frames: 6, Waveform: voice == "wave" ? Waveform() : null),
            ],
            Rules = [
                At(phase: 2, body: [new CartridgeStatement(Kind: "play", Sound: "theme")]),
                At(phase: 6, body: [new CartridgeStatement(Kind: "play", Sound: "blip")]),
                new CartridgeRule(Name: "tick", When: CartridgeExpressions.Gate(left: CartridgeExpressions.Of(state: "phase"), comparison: ActionStateComparison.Less, right: CartridgeExpressions.Of(constant: 200)),
                    Body: [new CartridgeStatement(Kind: "set", Target: new CartridgeTarget(State: "phase"), Operation: ExpressionOp.Add, Value: CartridgeExpressions.Of(constant: 1))]),
            ],
        };
        ICartridgeCompiler compiler = target == "agb" ? new AgbCartridgeCompiler() : new HgbCartridgeCompiler();
        var result = compiler.Compile(document: document);
        using var machine = new SoundProbe(result: result);

        // Boot length varies with how much the image copies into video memory, so allow room past the gate.
        machine.Run(frames: 16);
        var mask = voice switch { "noise" => 8u, "wave" => 4u, _ => 1u };
        Assert.NotEqual(expected: 0u, actual: machine.ChannelActive());
        Assert.NotEqual(expected: 0u, actual: machine.Status() & mask);

        // The one-shot ends on its own terminator; the music voice is still going.
        machine.Run(frames: 40);
        Assert.NotEqual(expected: 0u, actual: machine.ChannelActive());
        Assert.Equal(expected: 0u, actual: machine.Status() & mask);
    }

    [Fact]
    public void ValidationRefusesMalformedSounds() {
        var document = CartridgeDocuments.Create(target: "cgb", title: "SFXBAD") with {
            Variables = [new CartridgeVariable(Name: "x", Initial: 0)],
        };
        Refuses(document: document with { Sounds = [new CartridgeSound(Name: "s")] }, fragment: "exactly one of music, effect or sample");
        Refuses(document: document with { Sounds = [new CartridgeSound(Name: "s", Music: [Lead(part: Track())], Effect: new AudioEffectDocument(Voice: "noise", Rows: []))] }, fragment: "exactly one of music, effect or sample");
        Refuses(document: document with { Sounds = [new CartridgeSound(Name: "s", Music: [Lead(part: Track())], Frames: 4)] }, fragment: "takes its pacing");
        Refuses(document: document with { Sounds = [new CartridgeSound(Name: "s", Effect: new AudioEffectDocument(Voice: "sine", Rows: [new AudioRowDocument(Note: "C5", Duty: null, Envelope: null)]), Frames: 4)] }, fragment: "Expected pulse1, noise or wave");
        // The wave voice is the only one that carries a waveform, and it must.
        Refuses(document: document with { Sounds = [new CartridgeSound(Name: "s", Effect: new AudioEffectDocument(Voice: "wave", Rows: [new AudioRowDocument(Note: "C5", Duty: null, Envelope: null)]), Frames: 4)] }, fragment: "carries a waveform");
        Refuses(document: document with { Sounds = [new CartridgeSound(Name: "s", Effect: new AudioEffectDocument(Voice: "noise", Rows: [new AudioRowDocument(Note: "C5", Duty: null, Envelope: null)]))] }, fragment: "per-row frame count");
    }

    [Fact]
    public void ValidationGatesAudioOnTargetAndDeclaration() {
        var document = CartridgeDocuments.Create(target: "cgb", title: "SOUNDBAD") with {
            Variables = [new CartridgeVariable(Name: "x", Initial: 0)],
        };
        Refuses(document: document with { Rules = [Rule(body: [new CartridgeStatement(Kind: "play", Sound: "nope")])] }, fragment: "Unknown sound");
        Refuses(document: document with { Rules = [Rule(body: [new CartridgeStatement(Kind: "stop")])] }, fragment: "requires a declared sound");

    }

    private static CartridgeMusicVoice Lead(AudioDocument part) =>
        new(Voice: AudioEffectDocument.VoicePulse2, Part: part);

    private static AudioDocument Track() => new(
        Schema: AudioDocument.CurrentSchema,
        Name: "theme",
        Tempo: 4,
        Patterns: [[
            new AudioRowDocument(Note: "C4", Duty: null, Envelope: null),
            new AudioRowDocument(Note: "E4", Duty: null, Envelope: null),
            new AudioRowDocument(Note: "G4", Duty: null, Envelope: null),
        ]],
        Order: [0],
        Effects: null);

    // One cycle rising then falling, so the voice has something audible to play through.
    private static int[] Waveform() => [.. Enumerable.Range(start: 0, count: 32).Select(selector: static step => step < 16 ? step : 31 - step)];

    [Fact]
    public void AnEffectIsRefusedOnAVoiceTheMusicOccupies() {
        var document = CartridgeDocuments.Create(target: "cgb", title: "CLASH") with {
            Sounds = [
                new CartridgeSound(Name: "theme", Music: [
                    new CartridgeMusicVoice(Voice: AudioEffectDocument.VoicePulse2, Part: Track()),
                    new CartridgeMusicVoice(Voice: AudioEffectDocument.VoicePulse1, Part: Track()),
                ]),
                new CartridgeSound(Name: "blip", Effect: new AudioEffectDocument(Voice: "pulse1", Rows: [
                    new AudioRowDocument(Note: "C5", Duty: null, Envelope: null),
                ]), Frames: 4),
            ],
        };

        Assert.Contains(
            collection: CartridgeDocuments.Validate(document: document),
            filter: error => error.Message.Contains(value: "carries a part of this cartridge's music", comparisonType: StringComparison.Ordinal));
    }

    [Fact]
    public void ATrackIsRefusedTwoPartsOnOneVoice() {
        var document = CartridgeDocuments.Create(target: "cgb", title: "DOUBLE") with {
            Sounds = [new CartridgeSound(Name: "theme", Music: [
                new CartridgeMusicVoice(Voice: AudioEffectDocument.VoicePulse2, Part: Track()),
                new CartridgeMusicVoice(Voice: AudioEffectDocument.VoicePulse2, Part: Track()),
            ])],
        };

        Assert.Contains(
            collection: CartridgeDocuments.Validate(document: document),
            filter: error => error.Message.Contains(value: "each voice at most one part", comparisonType: StringComparison.Ordinal));
    }

    private static CartridgeRule Rule(CartridgeStatement[] body) => new(Name: "rule", Body: body);

    private static CartridgeRule At(int phase, CartridgeStatement[] body) => new(
        Name: $"phase{phase}",
        When: CartridgeExpressions.Gate(left: CartridgeExpressions.Of(state: "phase"), comparison: ActionStateComparison.Equal, right: CartridgeExpressions.Of(constant: phase)),
        Body: body);

    // The sequencer's live pointer and the channel's envelope byte, wherever each machine keeps them.
    private sealed class SoundProbe : IDisposable {
        // Bit one of the master status register is set while the music channel is sounding, on either machine.
        private const uint AdvancedStatusAddress = 0x04000084u;
        private const ushort HumbleStatusAddress = 0xFF26;
        private const uint AdvancedStateAddress = 0x0200013Cu;
        private readonly AgbVerifyMachineDriver? m_agb;
        private readonly VerifyMachineDriver? m_hgb;
        public SoundProbe(CartridgeCompilation result) {
            if (result.Target == "agb") { m_agb = new AgbVerifyMachineDriver(rom: result.Rom, label: "sound"); }
            else { m_hgb = new VerifyMachineDriver(rom: result.Rom, label: "sound"); }
        }
        public void Run(int frames) {
            m_agb?.RunFrames(keys: AgbKeys.None, frames: frames);
            m_hgb?.RunFrames(buttons: JoypadButtons.None, frames: frames);
        }
        public uint Playing() => m_agb is { } agb
            ? agb.ReadWord(address: AdvancedStateAddress)
            : m_hgb!.Read(address: (ushort)(FrameworkMemoryMap.SoundPulse2State + FrameworkMemoryMap.SoundVoicePointerOffset + 1));
        public uint Status() => m_agb is { } agb
            ? agb.ReadHalf(address: AdvancedStatusAddress)
            : m_hgb!.Read(address: HumbleStatusAddress);
        public uint ChannelActive() => (m_agb is { } agb
            ? agb.ReadHalf(address: AdvancedStatusAddress)
            : m_hgb!.Read(address: HumbleStatusAddress)) & 2u;
        public void Dispose() { m_agb?.Dispose(); m_hgb?.Dispose(); }
    }

    private static void Refuses(CartridgeDocument document, string fragment) {
        var errors = CartridgeDocuments.Validate(document: document);
        Assert.Contains(collection: errors, filter: error => error.Message.Contains(value: fragment, comparisonType: StringComparison.Ordinal));
    }
}
