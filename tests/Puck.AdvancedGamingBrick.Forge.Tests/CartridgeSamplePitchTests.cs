using System.Collections.Concurrent;
using Puck.GamingBricks.Forge;

namespace Puck.AdvancedGamingBrick.Forge.Tests;

/// <summary>
/// Compiles the pitch cartridge once per playback rate, so every test that boots a rate boots the same image.
/// </summary>
public sealed class SamplePitchFixture {
    private readonly ConcurrentDictionary<int, CartridgeCompilation> m_compiled = new();

    /// <summary>Returns the pitch cartridge compiled for a rate, compiling it on the first request.</summary>
    /// <param name="rate">The authored playback rate, where 64 is the recording's own rate.</param>
    /// <returns>The compiled cartridge.</returns>
    public CartridgeCompilation At(int rate) => m_compiled.GetOrAdd(
        key: rate,
        valueFactory: static rate => new AgbCartridgeCompiler().Compile(document: Document(rate: rate))
    );

    // A recording fired on the third frame at the given rate.
    private static CartridgeDocument Document(int rate) {
        // A slow ramp rather than an alternating pair: resampling a two-sample cycle can land on one phase every time
        // and read as silence, which would pass this for the wrong reason.
        var sample = new int[CartridgeSamplePitchTests.SampleLength];

        for (var index = 0; (index < sample.Length); ++index) {
            sample[index] = (((index * 200) / CartridgeSamplePitchTests.SampleLength) - 100);
        }

        return CartridgeDocuments.Create(
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
                    comparison: ExpressionOp.Equal,
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
    }
}
/// <summary>
/// Covers resampling a recording as it plays, which is what turns one recording into an instrument rather than a
/// single fixed sound.
/// </summary>
public sealed class CartridgeSamplePitchTests(SamplePitchFixture pitch) : IClassFixture<SamplePitchFixture> {
    /// <summary>The recording's length in samples: four frames at its own rate.</summary>
    public const int SampleLength = 1152;

    private const uint BufferAddress = 0x02000160u;
    // The first voice's step word: the mix buffer's two halves, then the cursor, then the record's fourth word.
    private const uint FirstVoiceStepAddress = (((BufferAddress + (SamplesPerFrame * 2)) + 4) + 12);
    private const int SamplesPerFrame = 288;

    private string Mixed(int rate) {
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
    private AgbVerifyMachineDriver Run(int rate, int frames) {
        var machine = new AgbVerifyMachineDriver(
            rom: pitch.At(rate: rate).Rom,
            label: $"pitch{rate}"
        );

        machine.RunFrames(
            frames: frames,
            keys: AgbKeys.None
        );

        return machine;
    }

    // The recording is four frames long at its own rate (64): twice that rate retires it in two, half of it takes
    // eight, and a step of zero, which would never reach the recording's end, must leave the voice free instead.
    [InlineData(6, 64, true)]
    [InlineData(6, 128, false)]
    [InlineData(9, 64, false)]
    [InlineData(9, 32, true)]
    [InlineData(6, 0, false)]
    [Theory]
    public void TheRateSetsHowLongTheRecordingSounds(int frames, int rate, bool sounding) {
        using var machine = Run(
            frames: frames,
            rate: rate
        );
        var step = 0u;

        // The voice's own step rather than the buffer's contents: a retired voice leaves the half it last filled
        // intact for another frame, so buffer silence lags the voice by one.
        for (var index = 0u; (index < 4u); ++index) {
            step |= machine.ReadByte(address: (FirstVoiceStepAddress + index));
        }

        Assert.Equal(
            actual: (step != 0u),
            expected: sounding
        );
    }
    [Fact]
    public void TheRateChangesWhatIsMixedRatherThanOnlyHowLongItLasts() {
        var natural = Mixed(rate: 64);

        Assert.NotEqual(
            expected: natural,
            actual: Mixed(rate: 128)
        );
        Assert.Equal(
            expected: natural,
            actual: Mixed(rate: 64)
        );
    }
    [Fact]
    public void ValidationRefusesARateOnASoundThatIsNotARecording() => new CartridgeRefusal(
        Name: "a rate on music",
        Document: CartridgeDocuments.Create(
            target: "agb",
            title: "RATEBAD"
        ) with {
            Sounds = [new CartridgeSound(
                Name: "theme",
                Music: [CartridgeCostMeasurement.Lead]
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
        },
        Path: "rules[0].body[0].rate",
        Fragment: "is not a recording"
    ).Holds();
}
