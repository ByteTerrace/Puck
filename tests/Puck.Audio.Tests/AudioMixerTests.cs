using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Puck.Audio.Mixing;
using Puck.Maths;

namespace Puck.Audio.Tests;

/// <summary>
/// The mixer core's byte-identity re-proof, re-homed from the quarantined
/// <c>experimental/scripts/world/audio-mix.cs</c> offline harness (never run — <c>Puck.Audio.Mixing</c>'s own
/// closure has no document/emulator dependency, so every scenario here uses a synthetic deterministic source
/// instead of a real tune cart). Every hash is SELF-REFERENTIAL: it pins that two fresh runs against the same
/// scripted input agree bit for bit, never a historical value — a deliberate mix-law correction is expected to
/// change it.
/// </summary>
public sealed class WorldAudioMixerTests {
    private static readonly int Frames = AudioMixer.FramesPerSimStep; // 200 at the 240 Hz default.

    private const int UnityQ16 = 65536;

    private static FixedQ4816 Q(long units) => FixedQ4816.FromRawBits(value: (units * 65536L));
    private static FixedQ4816 QRaw(long raw) => FixedQ4816.FromRawBits(value: raw);

    [Fact]
    public void ScriptedTimelineReproducesItsPcmHashAcrossTwoFreshRuns() {
        static string RunTimeline() {
            const int Steps = 240; // 1 s of timeline.

            var mixer = new AudioMixer();

            mixer.RegisterPatch(id: "chirp", patch: Chirp());
            mixer.RegisterPatch(id: "bed", patch: BedNoise());
            mixer.SetSource(key: AudioSourceKey.Tune(id: "shared"), source: new SeededPatternSource(seed: 0xC0FFEEUL));

            var snapshot = new AudioSnapshot();
            var block = new short[(Frames * 2)];
            var sequence = 0UL;

            using var sha = IncrementalHash.CreateHash(hashAlgorithm: HashAlgorithmName.SHA256);

            for (var step = 0; (step < Steps); step++) {
                BuildScriptedSnapshot(
                    sequence: ref sequence,
                    snapshot: snapshot,
                    step: step,
                    steps: Steps
                );
                mixer.MixBlock(
                    snapshot: snapshot,
                    stereoInterleaved: block
                );
                sha.AppendData(data: MemoryMarshal.AsBytes(span: block.AsSpan()));
            }

            return Convert.ToHexString(inArray: sha.GetHashAndReset());
        }

        var first = RunTimeline();
        var second = RunTimeline();

        Assert.Equal(actual: second, expected: first);
    }
    [Fact]
    public void HardRightEmitterLandsItsEnergyInTheRightChannel() {
        var (left, right) = ChannelEnergy(emitterX: 3);

        Assert.True(condition: (right > (10 * Math.Max(val1: left, val2: 1))));
    }
    [Fact]
    public void HardLeftEmitterLandsItsEnergyInTheLeftChannel() {
        var (left, right) = ChannelEnergy(emitterX: -3);

        Assert.True(condition: (left > (10 * Math.Max(val1: right, val2: 1))));
    }
    [Fact]
    public void LeftAndRightPlacementsAreExactMirrors() {
        var (rightCaseL, rightCaseR) = ChannelEnergy(emitterX: 3);
        var (leftCaseL, leftCaseR) = ChannelEnergy(emitterX: -3);

        Assert.Equal(actual: leftCaseL, expected: rightCaseR);
        Assert.Equal(actual: leftCaseR, expected: rightCaseL);
    }
    [Fact]
    public void CulledEmitterIsBitIdenticalToAnAbsentOneAndItsSourceIsNeverPulled() {
        var culledCounter = new CountingSource(value: 16384);
        var culled = RenderCullScenario(
            distance: 10,
            source: culledCounter,
            withEmitter: true
        );
        var absent = RenderCullScenario(
            withEmitter: false,
            distance: 0,
            source: new CountingSource(value: 16384)
        );

        Assert.True(condition: culled.AsSpan().SequenceEqual(other: absent));
        Assert.Equal(expected: 0, actual: culledCounter.Pulls);
    }
    [Fact]
    public void InRadiusEmitterIsAudibleAndPullsEveryBlock() {
        var counter = new CountingSource(value: 16384);
        var audible = RenderCullScenario(
            distance: 2,
            source: counter,
            withEmitter: true
        );

        Assert.NotEqual(expected: 0, actual: audible[(((2 * Frames) * 2) + 101)]);
        Assert.Equal(expected: 3, actual: counter.Pulls);
    }
    [Fact]
    public void TwoEmittersSharingOneSourceCostOnePullPerBlock() {
        var counter = new CountingSource(value: 8192);
        var mixer = new AudioMixer();

        mixer.SetSource(
            key: AudioSourceKey.Tune(id: "shared"),
            source: counter
        );

        var snapshot = new AudioSnapshot();
        var block = new short[(Frames * 2)];

        for (var i = 0; (i < 5); i++) {
            snapshot.Reset(listener: new AudioListener(Position: default, Yaw: FixedComplex.MultiplicativeIdentity));

            for (var id = 1; (id <= 2); id++) {
                snapshot.TryAddEmitter(emitter: new AudioEmitter(
                    Id: id,
                    Kind: AudioEmitterKind.Point,
                    Position: new FixedVector3(X: Q(units: ((id == 1) ? -2 : 2)), Y: FixedQ4816.Zero, Z: FixedQ4816.Zero),
                    MinRadius: Q(units: 4),
                    MaxRadius: Q(units: 8),
                    Curve: AudioAttenuationCurve.Smoothstep,
                    FadeFrames: 0,
                    GainQ16: UnityQ16,
                    Channel: ((id == 1) ? AudioChannel.Left : AudioChannel.Right),
                    Source: AudioSourceKey.Tune(id: "shared")
                ));
            }

            mixer.MixBlock(
                snapshot: snapshot,
                stereoInterleaved: block
            );
        }

        Assert.Equal(expected: 5, actual: counter.Pulls);
    }
    [Fact]
    public void HotBlockPinsAtTheCeilingWithoutWrapping() {
        var hot = RenderHotScenario(emitterCount: 2);
        var hotMax = 0;
        var hotMin = int.MaxValue;

        foreach (var sample in hot) {
            hotMax = Math.Max(val1: hotMax, val2: sample);
            hotMin = Math.Min(val1: hotMin, val2: sample);
        }

        Assert.Equal(actual: hotMax, expected: 32767);
        Assert.True(condition: (hotMin >= 0));
    }
    [Fact]
    public void KneeRegionCompressesToTheDocumentedCubic() {
        var knee = RenderHotScenario(emitterCount: 1);
        var expected = ((int)AudioMixer.SoftClip(sample: 46340));

        Assert.Equal(expected: expected, actual: knee[100]);
        Assert.InRange(actual: expected, high: 46339, low: 24576);
    }
    [Fact]
    public void SoftClipBoundariesAreExactAndSymmetric() {
        Assert.Equal(expected: 24575, actual: AudioMixer.SoftClip(sample: 24575));
        Assert.Equal(expected: 24576, actual: AudioMixer.SoftClip(sample: 24576));
        Assert.Equal(expected: 32767, actual: AudioMixer.SoftClip(sample: 49151));
        Assert.Equal(expected: -32767, actual: AudioMixer.SoftClip(sample: -49151));
    }
    [Fact]
    public void GainStepRampsMonotonicallyAndBoundedPerSample() {
        var mixer = new AudioMixer();

        mixer.SetSource(
            key: AudioSourceKey.Tune(id: "const"),
            source: new ConstSource(value: 16384)
        );

        var snapshot = new AudioSnapshot();
        var blocks = new short[4][];

        for (var i = 0; (i < 4); i++) {
            snapshot.Reset(listener: new AudioListener(Position: default, Yaw: FixedComplex.MultiplicativeIdentity));
            snapshot.TryAddEmitter(emitter: new AudioEmitter(
                Id: 1,
                Kind: AudioEmitterKind.Point,
                Position: default,
                MinRadius: Q(units: 1),
                MaxRadius: Q(units: 4),
                Curve: AudioAttenuationCurve.Smoothstep,
                FadeFrames: 0,
                GainQ16: ((i < 2) ? (UnityQ16 / 4) : UnityQ16),
                Channel: AudioChannel.Mix,
                Source: AudioSourceKey.Tune(id: "const")
            ));
            blocks[i] = new short[(Frames * 2)];
            mixer.MixBlock(
                snapshot: snapshot,
                stereoInterleaved: blocks[i]
            );
        }

        var monotone = true;
        var maxStep = 0;

        for (var n = 1; (n < Frames); n++) {
            var delta = (blocks[2][(2 * n)] - blocks[2][(2 * (n - 1))]);

            monotone &= (delta >= 0);
            maxStep = Math.Max(val1: maxStep, val2: Math.Abs(value: delta));
        }

        Assert.True(condition: monotone);
        // Ramp bound: coefficient travel (46341 - 11585) over 200 frames on a 16384 source ~= 43.4/sample.
        Assert.InRange(actual: maxStep, high: 45, low: 40);
        Assert.InRange(
            actual: blocks[2][(2 * (Frames - 1))],
            low: (11585 - 2),
            high: (11585 + 2)
        );
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
    private static void BuildScriptedSnapshot(AudioSnapshot snapshot, int step, int steps, ref ulong sequence) {
        // Listener: one full orbit of radius 2 over the timeline, yaw sweeping with it.
        var angle = QRaw(raw: ((step * 411775L) / steps));

        var (sin, cos) = FixedQ4816.SinCos(angle: angle);

        snapshot.Reset(listener: new AudioListener(
            Position: new FixedVector3(X: QRaw(raw: (cos.Value * 2)), Y: FixedQ4816.Zero, Z: QRaw(raw: (sin.Value * 2))),
            Yaw: FixedComplex.FromAngle(angle: angle)
        ));

        // The stereo pair: two rows, one shared source, separated by geometry.
        snapshot.TryAddEmitter(emitter: new AudioEmitter(
            Id: 1, Kind: AudioEmitterKind.Point, Position: new FixedVector3(X: QRaw(raw: -98304), Y: FixedQ4816.Zero, Z: FixedQ4816.Zero),
            MinRadius: QRaw(raw: 32768), MaxRadius: Q(units: 8), Curve: AudioAttenuationCurve.Smoothstep, FadeFrames: 0, GainQ16: UnityQ16,
            Channel: AudioChannel.Left, Source: AudioSourceKey.Tune(id: "shared")
        ));
        snapshot.TryAddEmitter(emitter: new AudioEmitter(
            Id: 2, Kind: AudioEmitterKind.Point, Position: new FixedVector3(X: QRaw(raw: 98304), Y: FixedQ4816.Zero, Z: FixedQ4816.Zero),
            MinRadius: QRaw(raw: 32768), MaxRadius: Q(units: 8), Curve: AudioAttenuationCurve.Smoothstep, FadeFrames: 0, GainQ16: UnityQ16,
            Channel: AudioChannel.Right, Source: AudioSourceKey.Tune(id: "shared")
        ));

        // The mover: crosses the scene left to right, entering and leaving its finite support.
        snapshot.TryAddEmitter(emitter: new AudioEmitter(
            Id: 3, Kind: AudioEmitterKind.Point, Position: new FixedVector3(X: QRaw(raw: (-655360L + ((step * 40960L) / 15))), Y: FixedQ4816.Zero, Z: FixedQ4816.Zero),
            MinRadius: QRaw(raw: 32768), MaxRadius: Q(units: 3), Curve: AudioAttenuationCurve.Smoothstep, FadeFrames: 0, GainQ16: (UnityQ16 / 2),
            Channel: AudioChannel.Mix, Source: AudioSourceKey.Tune(id: "shared")
        ));

        // The bed: a noise region south of the orbit; the orbit crosses its outer edge.
        snapshot.TryAddEmitter(emitter: new AudioEmitter(
            Id: 4, Kind: AudioEmitterKind.Bed, Position: new FixedVector3(X: FixedQ4816.Zero, Y: FixedQ4816.Zero, Z: Q(units: -6)),
            MinRadius: Q(units: 2), MaxRadius: Q(units: 5), Curve: AudioAttenuationCurve.Smoothstep, FadeFrames: 4800, GainQ16: 45875,
            Channel: AudioChannel.Mix, Source: AudioSourceKey.Synth(patchId: "bed")
        ));

        // The creature: seeded chirps ahead of the orbit center.
        snapshot.TryAddEmitter(emitter: new AudioEmitter(
            Id: 5, Kind: AudioEmitterKind.Point, Position: new FixedVector3(X: FixedQ4816.Zero, Y: FixedQ4816.Zero, Z: Q(units: 3)),
            MinRadius: QRaw(raw: 32768), MaxRadius: Q(units: 10), Curve: AudioAttenuationCurve.Smoothstep, FadeFrames: 0, GainQ16: UnityQ16,
            Channel: AudioChannel.Mix, Source: AudioSourceKey.Synth(patchId: "chirp")
        ));

        if (step == 0) {
            snapshot.TryAddTrigger(trigger: new SynthTrigger(Sequence: ++sequence, PatchId: "bed", Seed: 42UL, GainQ16: UnityQ16, EmitterId: 4));
        }

        if ((step > 0) && ((step % 48) == 0)) {
            snapshot.TryAddTrigger(trigger: new SynthTrigger(Sequence: ++sequence, PatchId: "chirp", Seed: (0xC0FFEEUL + ((ulong)step)), GainQ16: UnityQ16, EmitterId: 5));
        }
    }
    private static (long Left, long Right) ChannelEnergy(long emitterX) {
        var mixer = new AudioMixer();

        mixer.SetSource(
            key: AudioSourceKey.Tune(id: "const"),
            source: new ConstSource(value: 16384)
        );

        var snapshot = new AudioSnapshot();
        var block = new short[(Frames * 2)];

        for (var i = 0; (i < 3); i++) {
            snapshot.Reset(listener: new AudioListener(Position: default, Yaw: FixedComplex.MultiplicativeIdentity));
            snapshot.TryAddEmitter(emitter: new AudioEmitter(
                Id: 1, Kind: AudioEmitterKind.Point, Position: new FixedVector3(X: Q(units: emitterX), Y: FixedQ4816.Zero, Z: FixedQ4816.Zero),
                MinRadius: Q(units: 4), MaxRadius: Q(units: 8), Curve: AudioAttenuationCurve.Smoothstep, FadeFrames: 0, GainQ16: UnityQ16,
                Channel: AudioChannel.Mix, Source: AudioSourceKey.Tune(id: "const")
            ));
            mixer.MixBlock(
                snapshot: snapshot,
                stereoInterleaved: block
            );
        }

        long left = 0, right = 0;

        for (var n = 0; (n < Frames); n++) {
            left += (((long)block[(2 * n)]) * block[(2 * n)]);
            right += (((long)block[((2 * n) + 1)]) * block[((2 * n) + 1)]);
        }

        return (left, right);
    }
    private static short[] RenderCullScenario(bool withEmitter, long distance, IAudioBlockSource source) {
        var mixer = new AudioMixer();

        mixer.SetSource(
            key: AudioSourceKey.Tune(id: "const"),
            source: source
        );

        var snapshot = new AudioSnapshot();
        var output = new short[((3 * Frames) * 2)];

        for (var i = 0; (i < 3); i++) {
            snapshot.Reset(listener: new AudioListener(Position: default, Yaw: FixedComplex.MultiplicativeIdentity));

            if (withEmitter) {
                snapshot.TryAddEmitter(emitter: new AudioEmitter(
                    Id: 7, Kind: AudioEmitterKind.Point, Position: new FixedVector3(X: Q(units: distance), Y: FixedQ4816.Zero, Z: FixedQ4816.Zero),
                    MinRadius: Q(units: 1), MaxRadius: Q(units: 4), Curve: AudioAttenuationCurve.Smoothstep, FadeFrames: 0, GainQ16: UnityQ16,
                    Channel: AudioChannel.Mix, Source: AudioSourceKey.Tune(id: "const")
                ));
            }

            mixer.MixBlock(
                snapshot: snapshot,
                stereoInterleaved: output.AsSpan(length: (Frames * 2), start: ((i * Frames) * 2))
            );
        }

        return output;
    }
    private static short[] RenderHotScenario(int emitterCount) {
        var mixer = new AudioMixer();

        mixer.SetSource(
            key: AudioSourceKey.Tune(id: "const"),
            source: new ConstSource(value: 16384)
        );

        var snapshot = new AudioSnapshot();
        var block = new short[(Frames * 2)];

        for (var i = 0; (i < 2); i++) {
            snapshot.Reset(listener: new AudioListener(Position: default, Yaw: FixedComplex.MultiplicativeIdentity));

            for (var id = 1; (id <= emitterCount); id++) {
                snapshot.TryAddEmitter(emitter: new AudioEmitter(
                    Id: id, Kind: AudioEmitterKind.Point, Position: default,
                    MinRadius: Q(units: 1), MaxRadius: Q(units: 4), Curve: AudioAttenuationCurve.Smoothstep, FadeFrames: 0, GainQ16: (4 * UnityQ16),
                    Channel: AudioChannel.Mix, Source: AudioSourceKey.Tune(id: "const")
                ));
            }

            mixer.MixBlock(
                snapshot: snapshot,
                stereoInterleaved: block
            ); // Block 2 is steady state (prev == target).
        }

        return block;
    }
}
