using Puck.Audio.Mixing;
using Puck.Audio.Simulation;
using Puck.Maths;

namespace Puck.Audio.Tests;

/// <summary>
/// The 32-voice synth's determinism, allocation policy, and filter contracts — re-homed alongside
/// <see cref="WorldAudioMixerTests"/> from the quarantined offline harness.
/// </summary>
public sealed class WorldVoiceSynthTests {
    private const int UnityQ16 = 65536;

    [Fact]
    public void SeededVoiceReproducesBitwiseAcrossTwoFreshSynths() {
        var first = new VoiceSynth();
        var second = new VoiceSynth();
        var chirp = Chirp();

        first.Trigger(patch: in chirp, seed: 0xBEEFUL, gainQ16: UnityQ16);
        second.Trigger(patch: in chirp, seed: 0xBEEFUL, gainQ16: UnityQ16);

        var renderFirst = Render(synth: first, totalFrames: 12_000);
        var renderSecond = Render(synth: second, totalFrames: 12_000);

        Assert.True(condition: renderFirst.AsSpan().SequenceEqual(other: renderSecond));
    }
    [Fact]
    public void VoiceProducesSignalAndFreesItselfWhenItsEnvelopeCompletes() {
        var synth = new VoiceSynth();
        var chirp = Chirp();

        synth.Trigger(patch: in chirp, seed: 0xBEEFUL, gainQ16: UnityQ16);

        var energy = 0L;

        foreach (var sample in Render(synth: synth, totalFrames: 12_000)) {
            energy += Math.Abs(value: ((int)sample));
        }

        Assert.True(condition: (energy > 0));
        Assert.Equal(expected: 0, actual: synth.ActiveVoiceCount);
    }
    [Fact]
    public void SeededNoiseReproducesBitwiseFromTheSameSeed() {
        var first = new VoiceSynth();
        var second = new VoiceSynth();
        var bed = BedNoise();

        first.Trigger(patch: in bed, seed: 7UL, gainQ16: UnityQ16);
        second.Trigger(patch: in bed, seed: 7UL, gainQ16: UnityQ16);

        Assert.True(condition: Render(synth: first, totalFrames: 4800).AsSpan().SequenceEqual(
            other: Render(synth: second, totalFrames: 4800)));
    }
    [Fact]
    public void FortyTriggersStealTheQuietestVoiceAndPinAtCapacity() {
        var synth = new VoiceSynth();
        var hum = Hum();

        for (var i = 0; (i < 40); i++) {
            synth.Trigger(patch: in hum, seed: ((ulong)i), gainQ16: UnityQ16);
        }

        Assert.Equal(expected: VoiceSynth.VoiceCount, actual: synth.ActiveVoiceCount);
    }
    [Fact]
    public void LowPassFilterMeasurablyDarkensSeededNoise() {
        var open = new VoiceSynth();
        var dark = new VoiceSynth();
        var white = (BedNoise() with { Polynomial = 0 });
        var filtered = (white with { FilterMode = VoiceFilterMode.LowPass, FilterCoefficientQ16 = 8573, FilterDampingQ16 = 65536 });

        open.Trigger(patch: in white, seed: 99UL, gainQ16: UnityQ16);
        dark.Trigger(patch: in filtered, seed: 99UL, gainQ16: UnityQ16);

        var openRender = Render(synth: open, totalFrames: 9600);
        var darkRender = Render(synth: dark, totalFrames: 9600);
        long openRoughness = 0, darkRoughness = 0;

        for (var n = 4801; (n < 9600); n++) { // The settled tail, past the attack.
            openRoughness += Math.Abs(value: (openRender[n] - openRender[(n - 1)]));
            darkRoughness += Math.Abs(value: (darkRender[n] - darkRender[(n - 1)]));
        }

        Assert.True(condition: ((darkRoughness * 4) < openRoughness));
    }
    [Fact]
    public void TriggerFiresOnceWhenTheSameSnapshotIsMixedTwiceUnderHold() {
        var mixer = new AudioMixer();

        mixer.RegisterPatch(id: "hum", patch: Hum());

        var snapshot = new AudioSnapshot();
        var block = new short[(AudioMixer.FramesPerSimStep * 2)];

        snapshot.Reset(listener: new AudioListener(Position: default, Yaw: FixedComplex.MultiplicativeIdentity));
        snapshot.TryAddEmitter(emitter: new AudioEmitter(
            Id: 1, Kind: AudioEmitterKind.Point, Position: new FixedVector3(X: FixedQ4816.FromRawBits(value: 65536), Y: FixedQ4816.Zero, Z: FixedQ4816.Zero),
            MinRadius: FixedQ4816.FromRawBits(value: (2 * 65536)), MaxRadius: FixedQ4816.FromRawBits(value: (4 * 65536)), Curve: AudioAttenuationCurve.Smoothstep, FadeFrames: 0, GainQ16: UnityQ16,
            Channel: AudioChannel.Mix, Source: AudioSourceKey.Synth(patchId: "hum")
        ));
        snapshot.TryAddTrigger(trigger: new SynthTrigger(EmitterId: 1, GainQ16: UnityQ16, PatchId: "hum", Seed: 1UL, Sequence: 1));

        mixer.MixBlock(snapshot: snapshot, stereoInterleaved: block);
        mixer.MixBlock(snapshot: snapshot, stereoInterleaved: block);

        Assert.Equal(expected: 1, actual: mixer.Synth.ActiveVoiceCount);
    }
    [Fact]
    public void BedFadeFramesBoundsThePresenceSlewOnAFullPresenceBed() {
        short[] BedBlock(int fadeFrames) {
            var mixer = new AudioMixer();

            mixer.SetSource(key: AudioSourceKey.Tune(id: "const"), source: new ConstSource(value: 16384));

            var snapshot = new AudioSnapshot();
            var block = new short[(AudioMixer.FramesPerSimStep * 2)];

            snapshot.Reset(listener: new AudioListener(Position: default, Yaw: FixedComplex.MultiplicativeIdentity));
            snapshot.TryAddEmitter(emitter: new AudioEmitter(
                Id: 1, Kind: AudioEmitterKind.Bed, Position: default,
                MinRadius: FixedQ4816.FromRawBits(value: (2 * 65536)), MaxRadius: FixedQ4816.FromRawBits(value: (5 * 65536)), Curve: AudioAttenuationCurve.Smoothstep, FadeFrames: fadeFrames, GainQ16: UnityQ16,
                Channel: AudioChannel.Mix, Source: AudioSourceKey.Tune(id: "const")
            ));
            mixer.MixBlock(snapshot: snapshot, stereoInterleaved: block);

            return block;
        }

        var faded = BedBlock(fadeFrames: 4800);
        var instant = BedBlock(fadeFrames: 0);
        int fadedPeak = 0, instantPeak = 0;

        for (var n = 0; (n < AudioMixer.FramesPerSimStep); n++) {
            fadedPeak = Math.Max(val1: fadedPeak, val2: Math.Abs(value: ((int)faded[(2 * n)])));
            instantPeak = Math.Max(val1: instantPeak, val2: Math.Abs(value: ((int)instant[(2 * n)])));
        }

        // Slew bound: 65536*200/4800 = 2730 coefficient/block -> <= ~683 on a 16384 source; unbounded ramps to ~11585.
        Assert.True(condition: (fadedPeak <= 700));
        Assert.True(condition: (instantPeak >= 11_000));
    }
    [Fact]
    public void BabbleUtteranceFiresOneDistinctSeededTriggerPerSyllableBitIdenticalAcrossTwoFreshPairings() {
        var (slotsFirst, renderFirst) = RunBabbleUtterance(baseTick: 1_000UL, cadenceTicks: 600, identitySeed: 0x1234UL, syllableCount: 6, utteranceOrdinal: 3UL);
        var (slotsSecond, renderSecond) = RunBabbleUtterance(baseTick: 1_000UL, cadenceTicks: 600, identitySeed: 0x1234UL, syllableCount: 6, utteranceOrdinal: 3UL);

        Assert.Equal(expected: 6, actual: slotsFirst.Distinct().Count());
        Assert.True(condition: renderFirst.AsSpan().SequenceEqual(other: renderSecond));
        Assert.True(condition: slotsFirst.SequenceEqual(second: slotsSecond));
    }
    [InlineData(2)]
    [InlineData(5)]
    [InlineData(10)]
    [Theory]
    public void BabbleUtteranceNeverCollapsesToASingleSustainedToneForMultipleSyllables(int syllableCount) {
        var (slots, _) = RunBabbleUtterance(baseTick: 0UL, cadenceTicks: 500, identitySeed: 7UL, syllableCount: syllableCount, utteranceOrdinal: 1UL);

        Assert.Equal(expected: syllableCount, actual: slots.Count);
        Assert.NotEqual(expected: 1, actual: slots.Count);
        Assert.Equal(expected: syllableCount, actual: slots.Distinct().Count());
    }

    // Composes VoiceBabbler's deterministic tick schedule with VoiceSynth.Trigger: one seeded voice per syllable,
    // never a single collapsed trigger for the whole utterance. Each syllable's seed folds the identity seed, the
    // utterance ordinal, and the syllable index — never wall-clock or the tick value itself — mirroring
    // WorldAudioDirector.TriggerBabble's own seed-derivation discipline.
    private static (List<int> Slots, short[] Render) RunBabbleUtterance(int syllableCount, long cadenceTicks, ulong identitySeed, ulong utteranceOrdinal, ulong baseTick) {
        var synth = new VoiceSynth();
        var ticks = new ulong[syllableCount];

        VoiceBabbler.ComputeTriggerTicks(baseTick: baseTick, cadenceTicks: cadenceTicks, destination: ticks, identitySeed: identitySeed, syllableCount: syllableCount, utteranceOrdinal: utteranceOrdinal);

        var patch = Chirp();
        var slots = new List<int>(capacity: syllableCount);

        for (var syllableIndex = 0; (syllableIndex < syllableCount); syllableIndex++) {
            var seedHash = Fnv1aHash.Create();

            seedHash.Add(value: identitySeed);
            seedHash.Add(value: utteranceOrdinal);
            seedHash.Add(value: ((ulong)syllableIndex));

            slots.Add(item: synth.Trigger(patch: in patch, seed: seedHash.Value, gainQ16: UnityQ16));
        }

        return (Slots: slots, Render: Render(synth: synth, totalFrames: 4800));
    }
    private static VoicePatch Chirp() => new(
        AttackFrames: 480,
        DecayFrames: 4800,
        DurationFrames: 9_600,
        DutyDcOffsetQ16: ((int)((((2L * 250) - 1000L) * 65536L) / 1000L)),
        DutyThresholdQ32: ((uint)((250UL << 32) / 1000UL)),
        FilterCoefficientQ16: 0,
        FilterDampingQ16: 65536,
        FilterMode: VoiceFilterMode.Bypass,
        Oscillator: SynthOscillator.Pulse,
        PitchMillihertz: 1_320_000,
        Polynomial: 0,
        ReleaseFrames: 2400,
        SustainQ16: ((300 * 65536) / 1000),
        SweepMillihertzPerFrame: -40,
        VibratoDepthMillihertz: 30_000,
        VibratoRateMillihertz: 6_000
    );
    private static VoicePatch BedNoise() => new(
        AttackFrames: 2400,
        DecayFrames: 0,
        DurationFrames: 0,
        DutyDcOffsetQ16: 0,
        DutyThresholdQ32: 0,
        FilterCoefficientQ16: 0,
        FilterDampingQ16: 65536,
        FilterMode: VoiceFilterMode.Bypass,
        Oscillator: SynthOscillator.Noise,
        PitchMillihertz: 1_000,
        Polynomial: 40,
        ReleaseFrames: 0,
        SustainQ16: 65536,
        SweepMillihertzPerFrame: 0,
        VibratoDepthMillihertz: 0,
        VibratoRateMillihertz: 0
    );
    private static VoicePatch Hum() => new(
        AttackFrames: 2400,
        DecayFrames: 0,
        DurationFrames: 0,
        DutyDcOffsetQ16: 0,
        DutyThresholdQ32: 0,
        FilterCoefficientQ16: 0,
        FilterDampingQ16: 65536,
        FilterMode: VoiceFilterMode.Bypass,
        Oscillator: SynthOscillator.Sine,
        PitchMillihertz: 220_000,
        Polynomial: 0,
        ReleaseFrames: 0,
        SustainQ16: ((800 * 65536) / 1000),
        SweepMillihertzPerFrame: 0,
        VibratoDepthMillihertz: 0,
        VibratoRateMillihertz: 0
    );
    private static short[] Render(VoiceSynth synth, int totalFrames) {
        var output = new short[totalFrames];

        for (var offset = 0; (offset < totalFrames); offset += AudioMixer.MaxBlockFrames) {
            var frames = Math.Min(val1: AudioMixer.MaxBlockFrames, val2: (totalFrames - offset));

            synth.Render(
                destination: output.AsSpan(length: frames, start: offset),
                frames: frames
            );
        }

        return output;
    }
}
