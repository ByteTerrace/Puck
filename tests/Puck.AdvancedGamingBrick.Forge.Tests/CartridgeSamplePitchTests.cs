using Puck.GamingBricks.Forge;


namespace Puck.AdvancedGamingBrick.Forge.Tests;

/// <summary>
/// Covers resampling a recording as it plays, which is what turns one recording into an instrument rather than a
/// single fixed sound.
/// </summary>
public sealed class CartridgeSamplePitchTests {
    private const uint BufferAddress = 0x02000160u;
    // The first voice's step word: the mix buffer's two halves, then the cursor, then the record's fourth word.
    private const uint FirstVoiceStepAddress = (((BufferAddress + (SamplesPerFrame * 2)) + 4) + 12);
    private const int SampleLength = 1152;
    private const int SamplesPerFrame = 288;

    private static string Mixed(int rate) {
        using var machine = Run(
            frames: 5,
            rate: rate
        );
        var written = new System.Text.StringBuilder();

        for (var index = 0; (index < (SamplesPerFrame * 2)); ++index) {
            written.Append(value: machine.ReadByte(address: (BufferAddress + ((uint)index))).ToString(format: "X2"));
        }

        return written.ToString();
    }
    private static AgbVerifyMachineDriver Run(int rate, int frames) {
        // A slow ramp rather than an alternating pair: resampling a two-sample cycle can land on one phase every time
        // and read as silence, which would pass this for the wrong reason.
        var sample = new int[SampleLength];

        for (var index = 0; (index < sample.Length); ++index) {
            sample[index] = (((index * 200) / SampleLength) - 100);
        }

        var document = CartridgeDocuments.Create(
            target: "agb",
            title: "PITCH"
        ) with {
            Variables = [new CartridgeVariable(
                Name: "phase",
                Initial: 0
            )],
            Sounds = [new CartridgeSound(
                Name: "note",
                Sample: sample
            )],
            Rules = [
                new CartridgeRule(
                Name: "fire",
                When: CartridgeExpressions.Gate(
                    left: CartridgeExpressions.Of(state: "phase"),
                    comparison: ActionStateComparison.Equal,
                    right: CartridgeExpressions.Of(constant: 2)
                ),
                Body: [new CartridgeStatement(
                        Kind: "play",
                        Sound: "note",
                        Rate: CartridgeExpressions.Of(constant: rate)
                    )]
            ),
                new CartridgeRule(
                Name: "tick",
                When: CartridgeExpressions.Gate(
                    left: CartridgeExpressions.Of(state: "phase"),
                    comparison: ActionStateComparison.Less,
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
        var result = new AgbCartridgeCompiler().Compile(document: document);
        var machine = new AgbVerifyMachineDriver(
            rom: result.Rom,
            label: $"pitch{rate}"
        );

        machine.RunFrames(
            frames: frames,
            keys: AgbKeys.None
        );

        return machine;
    }
    // The voice's own step rather than the buffer's contents: a retired voice leaves the half it last filled intact
    // for another frame, so buffer silence lags the voice by one.
    private static bool Sounding(int rate, int frames) {
        using var machine = Run(
            frames: frames,
            rate: rate
        );

        for (var index = 0u; (index < 4u); ++index) {
            if (machine.ReadByte(address: (FirstVoiceStepAddress + index)) != 0) {
                return true;
            }
        }

        return false;
    }

    [Fact]
    public void ARateAboveNaturalRunsTheRecordingOutSooner() {
        // The recording is four frames long at its own rate, so twice that rate retires it in two.
        Assert.True(condition: Sounding(
            frames: 6,
            rate: 64
        ));
        Assert.False(condition: Sounding(
            frames: 6,
            rate: 128
        ));
    }
    [Fact]
    public void ARateBelowNaturalStretchesTheRecordingOut() {
        // Half rate takes eight frames, so it is still sounding where the natural rate has already finished.
        Assert.False(condition: Sounding(
            frames: 9,
            rate: 64
        ));
        Assert.True(condition: Sounding(
            frames: 9,
            rate: 32
        ));
    }
    [Fact]
    public void ARateOfZeroSoundsNothing() {
        // A step of zero would never reach the recording's end, so it must leave the voice free instead.
        Assert.False(condition: Sounding(
            frames: 6,
            rate: 0
        ));
    }
    [Fact]
    public void TheRateChangesWhatIsMixedRatherThanOnlyHowLongItLasts() {
        Assert.NotEqual(
            expected: Mixed(rate: 64),
            actual: Mixed(rate: 128)
        );
        Assert.Equal(
            expected: Mixed(rate: 64),
            actual: Mixed(rate: 64)
        );
    }
    [Fact]
    public void ValidationRefusesARateOnASoundThatIsNotARecording() {
        var document = CartridgeDocuments.Create(
            target: "agb",
            title: "RATEBAD"
        ) with {
            Sounds = [new CartridgeSound(
                Name: "theme",
                Music: [CartridgeCostMeasurement.Lead(part: CartridgeCostMeasurement.Track())]
            )],
            Rules = [new CartridgeRule(
                Name: "go",
                Body: [
                new CartridgeStatement(
                        Kind: "play",
                        Sound: "theme",
                        Rate: CartridgeExpressions.Of(constant: 64)
                    ),
            ]
            )],
        };
        var errors = CartridgeDocuments.Validate(document: document);

        Assert.Contains(
            collection: errors,
            filter: error => error.Message.Contains(
                comparisonType: StringComparison.Ordinal,
                value: "is not a recording"
            )
        );
    }
}
