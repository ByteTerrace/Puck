using Puck.Assets.Documents;
using Puck.GamingBricks.Forge;
using Puck.HumbleGamingBrick.Forge.Framework;

namespace Puck.AdvancedGamingBrick.Forge.Tests;

/// <summary>Covers authored music reaching the sound hardware, and the target gate on cartridge audio.</summary>
public sealed class CartridgeSoundTests {
    private const uint AdvancedStateAddress = 0x0200013Cu;

    private static readonly CartridgeRefusal[] GateRefusals = [
        new(
            Name: "an unknown sound",
            Document: Bad() with { Rules = [Rule(body: [new CartridgeStatement(
                        Kind: "play",
                        Sound: "nope"
                    )])] },
            Path: "rules[0].body[0].sound",
            Fragment: "Unknown sound"
        ),
        new(
            Name: "a stop without a sound",
            Document: Bad() with { Rules = [Rule(body: [new CartridgeStatement(Kind: "stop")])] },
            Path: "rules[0].body[0]",
            Fragment: "requires a declared sound"
        ),
    ];
    private static readonly CartridgeRefusal[] ShapeRefusals = [
        new(
            Name: "a sound with no body",
            Document: Bad() with { Sounds = [new CartridgeSound(Name: "s")] },
            Path: "sounds[0]",
            Fragment: "exactly one of music, effect or sample"
        ),
        new(
            Name: "a sound that is both music and an effect",
            Document: Bad() with { Sounds = [new CartridgeSound(
                    Name: "s",
                    Music: [Lead(part: Track())],
                    Effect: new AudioEffectDocument(
                        Rows: [],
                        Voice: "noise"
                    )
                )] },
            Path: "sounds[0]",
            Fragment: "exactly one of music, effect or sample"
        ),
        new(
            Name: "a frame count on music",
            Document: Bad() with { Sounds = [new CartridgeSound(
                    Name: "s",
                    Music: [Lead(part: Track())],
                    Frames: 4
                )] },
            Path: "sounds[0].frames",
            Fragment: "takes its pacing"
        ),
        new(
            Name: "an unknown effect voice",
            Document: Bad() with { Sounds = [Effect(
                    frames: 4,
                    voice: "sine"
                )] },
            Path: "sounds[0].effect.voice",
            Fragment: "Expected pulse1, noise or wave"
        ),
        new(
            // The wave voice is the only one that carries a waveform, and it must.
            Name: "a wave effect without a waveform",
            Document: Bad() with { Sounds = [Effect(
                    frames: 4,
                    voice: "wave"
                )] },
            Path: "sounds[0].waveform",
            Fragment: "carries a waveform"
        ),
        new(
            Name: "an effect without a frame count",
            Document: Bad() with { Sounds = [Effect(
                    frames: null,
                    voice: "noise"
                )] },
            Path: "sounds[0].frames",
            Fragment: "per-row frame count"
        ),
    ];

    public static TheoryData<string> GateRefusalNames => CartridgeRefusal.Names(table: GateRefusals);
    public static TheoryData<string> ShapeRefusalNames => CartridgeRefusal.Names(table: ShapeRefusals);

    private static CartridgeRule At(int phase, CartridgeStatement[] body) => new(
        Name: $"phase{phase}",
        When: CartridgeExpressions.Gate(
            left: CartridgeExpressions.Of(state: "phase"),
            comparison: ExpressionOp.Equal,
            right: CartridgeExpressions.Of(constant: phase)
        ),
        Body: body
    );
    private static CartridgeDocument Bad() => CartridgeDocuments.Create(
        target: "cgb",
        title: "SOUNDBAD"
    ) with {
        Variables = [new CartridgeVariable(
            Name: "x",
            Initial: 0
        )],
    };
    // A one-row effect on the named voice.
    private static CartridgeSound Effect(string voice, int? frames) => new(
        Name: "s",
        Effect: new AudioEffectDocument(
            Voice: voice,
            Rows: [new AudioRowDocument(
                    Duty: null,
                    Envelope: null,
                    Note: "C5"
                )]
        ),
        Frames: frames
    );
    private static CartridgeMusicVoice Lead(AudioDocument part) =>
        new(
            Voice: AudioEffectDocument.VoicePulse2,
            Part: part
        );
    // The second pulse voice's live sequencer pointer, wherever each machine keeps it: nonzero while a track plays.
    private static uint Playing(CartridgeProbe probe) => probe.Observe(
        advanced: agb => agb.ReadWord(address: AdvancedStateAddress),
        humble: hgb => ((uint)hgb.Read(address: ((ushort)((FrameworkMemoryMap.SoundPulse2State + FrameworkMemoryMap.SoundVoicePointerOffset) + 1))))
    );
    private static CartridgeRule Rule(CartridgeStatement[] body) => new(
        Name: "rule",
        Body: body
    );
    private static AudioDocument Track() => new(
        Schema: AudioDocument.CurrentSchema,
        Name: "theme",
        Tempo: 4,
        Patterns: [[
            new AudioRowDocument(
                    Duty: null,
                    Envelope: null,
                    Note: "C4"
                ),
            new AudioRowDocument(
                    Duty: null,
                    Envelope: null,
                    Note: "E4"
                ),
            new AudioRowDocument(
                    Duty: null,
                    Envelope: null,
                    Note: "G4"
                ),
        ]],
        Order: [0],
        Effects: null
    );
    // One cycle rising then falling, so the voice has something audible to play through.
    private static int[] Waveform() => [.. Enumerable.Range(
            count: 32,
            start: 0
        ).Select(selector: static step => ((step < 16)
        ? step
        : (31 - step)))];

    [Fact]
    public void ATrackIsRefusedTwoPartsOnOneVoice() {
        var document = CartridgeDocuments.Create(
            target: "cgb",
            title: "DOUBLE"
        ) with {
            Sounds = [new CartridgeSound(
                Name: "theme",
                Music: [
                new CartridgeMusicVoice(
                        Voice: AudioEffectDocument.VoicePulse2,
                        Part: Track()
                    ),
                new CartridgeMusicVoice(
                        Voice: AudioEffectDocument.VoicePulse2,
                        Part: Track()
                    ),
            ]
            )],
        };

        new CartridgeRefusal(
            Document: document,
            Fragment: "each voice at most one part",
            Name: "two parts on one voice",
            Path: "sounds[0].music[1].voice"
        ).Holds();
    }
    [Fact]
    public void AnEffectIsRefusedOnAVoiceTheMusicOccupies() {
        var document = CartridgeDocuments.Create(
            target: "cgb",
            title: "CLASH"
        ) with {
            Sounds = [
                new CartridgeSound(
                Name: "theme",
                Music: [
                    new CartridgeMusicVoice(
                        Voice: AudioEffectDocument.VoicePulse2,
                        Part: Track()
                    ),
                    new CartridgeMusicVoice(
                        Voice: AudioEffectDocument.VoicePulse1,
                        Part: Track()
                    ),
                ]
            ),
                new CartridgeSound(
                Name: "blip",
                Effect: new AudioEffectDocument(
                    Voice: "pulse1",
                    Rows: [
                    new AudioRowDocument(
                            Duty: null,
                            Envelope: null,
                            Note: "C5"
                        ),
                ]
                ),
                Frames: 4
            ),
            ],
        };

        new CartridgeRefusal(
            Document: document,
            Fragment: "carries a part of this cartridge's music",
            Name: "an effect on a music voice",
            Path: "sounds[1].effect.voice"
        ).Holds();
    }
    [InlineData("cgb", "pulse1")]
    [InlineData("cgb", "noise")]
    [InlineData("cgb", "wave")]
    [InlineData("agb", "pulse1")]
    [InlineData("agb", "noise")]
    [InlineData("agb", "wave")]
    [Theory]
    public void AnEffectSoundsOnItsOwnVoiceWithoutStoppingTheMusic(string target, string voice) {
        var document = CartridgeDocuments.Create(
            target: target,
            title: "SFX"
        ) with {
            Variables = [new CartridgeVariable(
                Name: "phase",
                Initial: 0
            )],
            Sounds = [
                new CartridgeSound(
                Name: "theme",
                Music: [Lead(part: Track())]
            ),
                new CartridgeSound(
                Name: "blip",
                Effect: new AudioEffectDocument(
                    Voice: voice,
                    Rows: [
                    new AudioRowDocument(
                            Duty: null,
                            Envelope: null,
                            Note: "C5"
                        ),
                    new AudioRowDocument(
                            Duty: null,
                            Envelope: null,
                            Note: "G5"
                        ),
                ]
                ),
                Frames: 6,
                Waveform: ((voice == "wave")
            ? Waveform()
            : null)
            ),
            ],
            Rules = [
                At(
                phase: 2,
                body: [new CartridgeStatement(
                        Kind: "play",
                        Sound: "theme"
                    )]
            ),
                At(
                phase: 6,
                body: [new CartridgeStatement(
                        Kind: "play",
                        Sound: "blip"
                    )]
            ),
                new CartridgeRule(
                Name: "tick",
                When: CartridgeExpressions.Gate(
                    left: CartridgeExpressions.Of(state: "phase"),
                    comparison: ExpressionOp.Less,
                    right: CartridgeExpressions.Of(constant: 200)
                ),
                Body: [new CartridgeStatement(
                        Kind: "set",
                        Target: new CartridgeTarget(State: "phase"),
                        Operation: ExpressionOp.Add,
                        Value: CartridgeExpressions.Of(constant: 1)
                    )]
            ),
            ],
        };
        var result = CartridgeProbe.Compile(document: document);
        using var machine = new CartridgeProbe(
            label: "sound",
            result: result
        );

        // Boot length varies with how much the image copies into video memory, so allow room past the gate.
        machine.Run(frames: 16);
        var mask = voice switch { "noise" => 8u, "wave" => 4u, _ => 1u };

        Assert.NotEqual(
            expected: 0u,
            actual: machine.SoundStatus() & 2u
        );
        Assert.NotEqual(
            expected: 0u,
            actual: machine.SoundStatus() & mask
        );

        // The one-shot ends on its own terminator; the music voice is still going.
        machine.RunUntil(
            awaited: "the effect's voice falling silent",
            limit: 40,
            until: probe => ((probe.SoundStatus() & mask) == 0u)
        );
        Assert.NotEqual(
            expected: 0u,
            actual: machine.SoundStatus() & 2u
        );
        Assert.Equal(
            expected: 0u,
            actual: machine.SoundStatus() & mask
        );
    }
    [InlineData("cgb")]
    [InlineData("agb")]
    [Theory]
    public void EveryVoiceOfATrackPlaysAtOnceAndStopsTogether(string target) {
        var document = CartridgeDocuments.Create(
            target: target,
            title: "TRIO"
        ) with {
            Variables = [new CartridgeVariable(
                Name: "phase",
                Initial: 0
            )],
            Sounds = [new CartridgeSound(
                Name: "theme",
                Music: [
                new CartridgeMusicVoice(
                        Voice: AudioEffectDocument.VoicePulse2,
                        Part: Track()
                    ),
                new CartridgeMusicVoice(
                        Voice: AudioEffectDocument.VoicePulse1,
                        Part: Track()
                    ),
                new CartridgeMusicVoice(
                        Voice: AudioEffectDocument.VoiceWave,
                        Part: Track(),
                        Waveform: Waveform()
                    ),
            ]
            )],
            Rules = [
                At(
                phase: 2,
                body: [new CartridgeStatement(
                        Kind: "play",
                        Sound: "theme"
                    )]
            ),
                At(
                phase: 30,
                body: [new CartridgeStatement(Kind: "stop")]
            ),
                new CartridgeRule(
                Name: "tick",
                When: CartridgeExpressions.Gate(
                    left: CartridgeExpressions.Of(state: "phase"),
                    comparison: ExpressionOp.Less,
                    right: CartridgeExpressions.Of(constant: 200)
                ),
                Body: [new CartridgeStatement(
                        Kind: "set",
                        Target: new CartridgeTarget(State: "phase"),
                        Operation: ExpressionOp.Add,
                        Value: CartridgeExpressions.Of(constant: 1)
                    )]
            ),
            ],
        };
        var result = CartridgeProbe.Compile(document: document);
        using var machine = new CartridgeProbe(
            label: "sound",
            result: result
        );

        machine.Run(frames: 8);

        // The status register's low nibble reports the channels that are sounding; the track claims three of them.
        Assert.Equal(
            expected: 0x07u,
            actual: machine.SoundStatus() & 0x07u
        );

        machine.Run(frames: 34);
        Assert.Equal(
            expected: 0u,
            actual: machine.SoundStatus() & 0x07u
        );
    }
    [InlineData("cgb")]
    [InlineData("agb")]
    [Theory]
    public void PlayStartsTheMusicVoiceAndStopSilencesIt(string target) {
        var document = CartridgeDocuments.Create(
            target: target,
            title: "SOUND"
        ) with {
            Variables = [new CartridgeVariable(
                Name: "phase",
                Initial: 0
            )],
            Sounds = [new CartridgeSound(
                Name: "theme",
                Music: [Lead(part: Track())]
            )],
            Rules = [
                At(
                phase: 2,
                body: [new CartridgeStatement(
                        Kind: "play",
                        Sound: "theme"
                    )]
            ),
                At(
                phase: 10,
                body: [new CartridgeStatement(Kind: "stop")]
            ),
                // The counter advances every frame, so the gates above fire on the frames they name.
                new CartridgeRule(
                Name: "tick",
                When: CartridgeExpressions.Gate(
                    left: CartridgeExpressions.Of(state: "phase"),
                    comparison: ExpressionOp.Less,
                    right: CartridgeExpressions.Of(constant: 200)
                ),
                Body: [new CartridgeStatement(
                        Kind: "set",
                        Target: new CartridgeTarget(State: "phase"),
                        Operation: ExpressionOp.Add,
                        Value: CartridgeExpressions.Of(constant: 1)
                    )]
            ),
            ],
        };
        var result = CartridgeProbe.Compile(document: document);
        using var machine = new CartridgeProbe(
            label: "sound",
            result: result
        );

        // Boot costs a few frames before the rule loop runs, so the gates land after it.
        machine.Run(frames: 8);
        Assert.NotEqual(
            expected: 0u,
            actual: Playing(probe: machine)
        );
        Assert.NotEqual(
            expected: 0u,
            actual: machine.SoundStatus() & 2u
        );

        machine.Run(frames: 14);
        Assert.Equal(
            expected: 0u,
            actual: Playing(probe: machine)
        );
        Assert.Equal(
            expected: 0u,
            actual: machine.SoundStatus() & 2u
        );
    }
    [MemberData(memberName: nameof(GateRefusalNames))]
    [Theory]
    public void ValidationGatesAudioOnTargetAndDeclaration(string refusal) => CartridgeRefusal.Holds(
        name: refusal,
        table: GateRefusals
    );
    [MemberData(memberName: nameof(ShapeRefusalNames))]
    [Theory]
    public void ValidationRefusesMalformedSounds(string refusal) => CartridgeRefusal.Holds(
        name: refusal,
        table: ShapeRefusals
    );
}
