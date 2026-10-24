using Puck.Audio.Mixing;

namespace Puck.Audio.Tests;

public sealed class TextSpeechTests {
    private static readonly VoicePatch Patch = new(AttackFrames: 96, DecayFrames: 240, DurationFrames: 2400, DutyDcOffsetQ16: 0, DutyThresholdQ32: 0, FilterCoefficientQ16: 0, FilterDampingQ16: 65536, FilterMode: VoiceFilterMode.Bypass,
        Oscillator: SynthOscillator.Saw, PitchMillihertz: 240000, Polynomial: 0, ReleaseFrames: 240, SustainQ16: 42598, SweepMillihertzPerFrame: 0, VibratoDepthMillihertz: 12000, VibratoRateMillihertz: 7000);

    private static short[] Render(TextSpeechSource source, int blockSize) {
        var output = new short[checked((((int)source.TotalFrames) * 2))];
        var offset = 0;

        while (!source.Finished) {
            var frames = Math.Min(val1: blockSize, val2: ((output.Length - offset) / 2));
            var delivered = source.Pull(output.AsSpan(start: offset), frames);

            Assert.Equal(actual: delivered, expected: frames);
            offset += (delivered * 2);
        }
        return output;
    }

    [Fact]
    public void TextAndVoiceGiveTheSameScheduleAndBytesAcrossPullSizes() {
        const string Text = "Ae, b! A little gallery guide.";
        var first = new TextSpeechSource(cadenceTicks: 16, formantThousandths: 1150, paceThousandths: 1250, patch: Patch, pitchThousandths: 1100, text: Text);
        var second = new TextSpeechSource(cadenceTicks: 16, formantThousandths: 1150, paceThousandths: 1250, patch: Patch, pitchThousandths: 1100, text: Text);

        Assert.Equal(first.Schedule, second.Schedule);
        Assert.Equal(Render(blockSize: 200, source: first), Render(blockSize: 127, source: second));
    }
    [Fact]
    public void EveryCharacterHasItsOwnIntervalAndPunctuationReservesExactSilence() {
        var source = new TextSpeechSource("Ae, b!", Patch, 16, paceThousandths: 1250);

        Assert.Equal(new long[] { 0, 2560, 5120, 10240, 12800, 15360 }, source.Schedule.Select(selector: s => s.StartFrame));
        Assert.Equal(23040, source.TotalFrames);
        var pcm = Render(blockSize: 301, source: source);

        foreach (var syllable in source.Schedule) {
            var samples = pcm.AsSpan((((int)syllable.StartFrame) * 2), (syllable.Frames * 2));

            if (syllable.Rest) {
                Assert.True(condition: (samples.IndexOfAnyExcept(value: ((short)0)) < 0));
            } else {
                Assert.True(condition: (samples.IndexOfAnyExcept(value: ((short)0)) >= 0));
                // Even adjacent letters have their own release and quiet tail.
                Assert.True(condition: (samples[(((syllable.Frames * 3) / 4) * 2)..].IndexOfAnyExcept(value: ((short)0)) < 0));
            }
        }
    }
    [Fact]
    public void VowelsAndSpeakerParametersChangeTheSoundAndPaceChangesTheSchedule() {
        var normal = Render(new TextSpeechSource("a", Patch, 16), 256);

        Assert.NotEqual(normal, Render(new TextSpeechSource("e", Patch, 16), 256));
        Assert.NotEqual(normal, Render(new TextSpeechSource("a", Patch, 16, pitchThousandths: 1300), 256));
        Assert.NotEqual(normal, Render(new TextSpeechSource("a", Patch, 16, formantThousandths: 800), 256));
        var faster = new TextSpeechSource("a", Patch, 16, paceThousandths: 2000);

        Assert.Equal((normal.Length / 4), faster.TotalFrames);
    }
    [Fact]
    public void EmptyTextAndACompletedSourceReturnSilenceAndUnicodeUsesOneIntervalPerScalar() {
        var empty = new TextSpeechSource("", Patch, 16);
        short[] output = [1, 1, 1, 1];

        Assert.True(condition: empty.Finished);
        Assert.Equal(0, empty.Pull(frames: 2, interleavedStereo: output));
        Assert.Equal(actual: output, expected: new short[4]);
        var unicode = new TextSpeechSource("é𐐀🙂", Patch, 16);

        Assert.Equal(3, unicode.Schedule.Count);
        Assert.False(condition: unicode.Schedule[1].Rest);
        Assert.True(condition: unicode.Schedule[2].Rest);
        Render(blockSize: 97, source: unicode);
        Assert.Equal(0, unicode.Pull(frames: 2, interleavedStereo: output));
        Assert.Equal(actual: output, expected: new short[4]);
    }
    [InlineData(0, 1000, 1000, 1000)]
    [InlineData(241, 1000, 1000, 1000)]
    [InlineData(16, 0, 1000, 1000)]
    [InlineData(16, 1000, 5000, 1000)]
    [InlineData(16, 1000, 1000, 0)]
    [Theory]
    public void InvalidVoiceParametersRefuse(int cadence, int pitch, int formant, int pace) =>
        Assert.Throws<ArgumentOutOfRangeException>(testCode: () => new TextSpeechSource(cadenceTicks: cadence, formantThousandths: formant, paceThousandths: pace, patch: Patch, pitchThousandths: pitch, text: "a"));
    [Fact]
    public void SampledSpeechSharesTheSynthScheduleAndMapsClassesWithExactRests() {
        var bank = Enumerable.Range(count: 6, start: 1).Select(selector: value => Enumerable.Repeat(count: 4800, element: ((short)(value * 1000))).ToArray()).ToArray();
        const string Text = "AEiyouB7, !";
        var first = new TextSpeechSource(Text, default, 16, pitchThousandths: 1200, paceThousandths: 1250, samples: bank);
        var second = new TextSpeechSource(Text, default, 16, pitchThousandths: 1200, paceThousandths: 1250, samples: bank);
        var synth = new TextSpeechSource(Text, Patch, 16, pitchThousandths: 1200, paceThousandths: 1250);

        Assert.Equal(synth.Schedule, first.Schedule);
        var pcm = Render(blockSize: 256, source: first);

        Assert.Equal(pcm, Render(blockSize: 97, source: second));
        int[] classes = [1, 2, 3, 3, 4, 5, 6, 6];

        for (var index = 0; (index < classes.Length); index++) {
            Assert.Equal(((short)(classes[index] * 1000)), pcm[(((int)first.Schedule[index].StartFrame) * 2)]);
        }
        foreach (var syllable in first.Schedule) {
            var tail = pcm.AsSpan((((int)(syllable.StartFrame + (syllable.Rest ? 0 : ((syllable.Frames * 3) / 4)))) * 2),
                ((syllable.Rest ? syllable.Frames : (syllable.Frames - ((syllable.Frames * 3) / 4))) * 2));

            Assert.True(condition: (tail.IndexOfAnyExcept(value: ((short)0)) < 0));
        }
    }
    [Fact]
    public void SamplePitchAndBankRateChangePlaybackWithoutChangingCadence() {
        var bank = Enumerable.Range(count: 6, start: 0).Select(selector: _ => Enumerable.Range(count: 300, start: 0).Select(selector: value => ((short)(value * 50))).ToArray()).ToArray();
        var normal = new TextSpeechSource("a", default, 16, samples: bank, sampleRateThousandths: 1000);
        var high = new TextSpeechSource("a", default, 16, samples: bank, pitchThousandths: 2000, sampleRateThousandths: 1000);
        var fast = new TextSpeechSource("a", default, 16, samples: bank, sampleRateThousandths: 2000);

        Assert.Equal(normal.Schedule, high.Schedule);
        var original = Render(blockSize: 99, source: normal);
        var pitched = Render(blockSize: 99, source: high);

        Assert.NotEqual(actual: pitched, expected: original);
        Assert.Equal(pitched, Render(blockSize: 73, source: fast));
        Assert.Throws<ArgumentException>(testCode: () => new TextSpeechSource("a", default, 16, samples: []));
    }
}
