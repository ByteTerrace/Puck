#:project ../Puck.World.csproj
#:project ../../Puck.Forge/Puck.Forge.csproj
#:property PublishAot=false
#:property JsonSerializerIsReflectionEnabledByDefault=true
#:property EnforceCodeStyleInBuild=false
#:property AnalysisLevel=none
// audio-mix — the AP1 offline PCM hash proof, one .NET 10 file-based app (no device, no window, no thread):
//
//   dotnet run src/Puck.World/scripts/audio-mix.cs
//
// Drives WorldAudioMixer.MixBlock tick-synchronously (the same pure core the AP3 device pump will drive): N sim
// steps at 240 Hz, exactly 200 frames per step, against a scripted pose table — a listener orbit, a stereo pair
// sharing ONE tune source (the checked-in docs/examples/tunes fixture through a synchronous headless Humble core —
// the TuneRom.Verify pattern, never QueuedMachineWorker), a moving emitter that enters/leaves its cull radius, an
// ambient bed the orbit crosses, and seeded synth triggers. SHA-256 hashes the concatenated s16 PCM and asserts a
// second full fresh run reproduces it bit for bit. The hash is SELF-REFERENTIAL per the determinism doctrine: a
// deliberate mix-law change re-goldens it (re-run, take the new value) — it pins no historical bytes.
//
// Structural batteries then prove the spatialization is REAL, not just stable:
//   (b) stereo geometry — a hard-right emitter lands its energy in R, mirrored for hard-left;
//   (c) the cull contract — an out-of-radius emitter bit-compares equal to an absent one and its source is never
//       pulled; the same emitter in radius is loud;
//   (d) the single-pull contract — two emitters sharing one source cost one Pull per block;
//   (e) the soft clip — a deliberately hot block pins at 32767 without wrap; a knee-region block compresses to the
//       documented cubic's exact value; boundary continuity holds;
//   (f) the coefficient ramp — a gain step produces a monotone ramp with no per-sample jump above the ramp bound;
//   (g) the synth — seeded reproducibility (bit-exact), envelope completion frees the voice, 40 triggers pin at 32
//       voices (steal-quietest), the SVF low-pass measurably darkens noise, triggers are once-only under
//       snapshot hold, and the bed fade bounds presence slew;
//   (h) THE WORLD-DOCUMENT PIPELINE (AP2) — a proof-authored FIXTURE WORLD (every speaker $type, all four source
//       kinds, a scene-row + placement emission facet, a sound-bearing creation placed) serialized, reloaded through
//       the strict loader/validator, derived by WorldAudioDirector (stable ids, arrival triggers, tune
//       acquire/release hosting), published per step against a scripted listener orbit, and mixed — the
//       document→derivation→MixBlock→hash pipeline end to end, its own golden PCM hash reproduced across two full
//       fresh runs.
using System.Numerics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Puck.Authoring;
using Puck.Forge.Tune;
using Puck.HumbleGamingBrick;
using Puck.HumbleGamingBrick.Interfaces;
using Puck.Maths;
using Puck.SdfVm;
using Puck.World;
using Puck.World.Audio;
using Puck.World.Client;

const int Frames = WorldAudioMixer.FramesPerSimStep; // 200
const int Steps = 480;                               // 2 s of timeline
const int UnityQ16 = 65536;

var failures = 0;

void Check(string name, bool ok, string detail) {
    Console.WriteLine($"[proof]   {(ok ? "PASS" : "FAIL")} {name}: {detail}");

    if (!ok) {
        failures++;
    }
}

var repoRoot = Directory.Exists(Path.Combine("docs", "examples", "tunes"))
    ? "."
    : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "..", ".."));
var tunesRoot = Path.Combine(repoRoot, "docs", "examples", "tunes");
var tuneDocument = AudioDocumentStore.Load(Path.Combine(tunesRoot, "tune.audio.json"), tunesRoot);

if (tuneDocument is null) {
    Console.Error.WriteLine($"[proof] FAIL: tune fixture not found under '{tunesRoot}'");

    return 1;
}

var tuneRom = TuneRom.Build(tuneDocument);

// ---- disposable probe patches (authored here, never Demo content) ------------------------------------------------
static WorldVoicePatch Patch(SynthPatchDocument document) =>
    WorldVoicePatch.FromDocument(SynthPatchCanonicalizer.Canonicalize(document).Document);

var chirp = Patch(new SynthPatchDocument(
    Schema: SynthPatchDocument.CurrentSchema, Name: "chirp", Oscillator: SynthOscillator.Pulse,
    DutyThousandths: 250, Polynomial: null, AttackFrames: 480, DecayFrames: 4800, SustainThousandths: 300,
    ReleaseFrames: 2400, PitchMillihertz: 1_320_000, SweepMillihertzPerFrame: -40,
    VibratoDepthMillihertz: 30_000, VibratoRateMillihertz: 6_000, DurationFrames: 9_600));
var bedNoise = Patch(new SynthPatchDocument(
    Schema: SynthPatchDocument.CurrentSchema, Name: "bed", Oscillator: SynthOscillator.Noise,
    DutyThousandths: null, Polynomial: 40, AttackFrames: 2400, DecayFrames: 0, SustainThousandths: 1000,
    ReleaseFrames: 0, PitchMillihertz: 1_000));
var hum = Patch(new SynthPatchDocument(
    Schema: SynthPatchDocument.CurrentSchema, Name: "hum", Oscillator: SynthOscillator.Sine,
    DutyThousandths: null, Polynomial: null, AttackFrames: 2400, DecayFrames: 0, SustainThousandths: 800,
    ReleaseFrames: 0, PitchMillihertz: 220_000));

static FixedQ4816 Q(long units) => FixedQ4816.FromRawBits(units * 65536L);
static FixedQ4816 QRaw(long raw) => FixedQ4816.FromRawBits(raw);

// ---- the scripted pose table -------------------------------------------------------------------------------------
static void BuildSnapshot(WorldAudioSnapshot snapshot, int step, ref ulong sequence) {
    // Listener: one full orbit of radius 2 over the timeline, yaw sweeping with it.
    var angle = QRaw((step * 411775L) / Steps);
    var (sin, cos) = FixedQ4816.SinCos(angle);

    snapshot.Reset(new WorldAudioListener(
        Position: new FixedVector3(X: QRaw(cos.Value * 2), Y: FixedQ4816.Zero, Z: QRaw(sin.Value * 2)),
        Yaw: FixedComplex.FromAngle(angle)));

    // The stereo pair: two rows, one tune source, separation by geometry (plan A7).
    snapshot.TryAddEmitter(new WorldAudioEmitter(
        Id: 1, Kind: WorldAudioEmitterKind.Point, Position: new FixedVector3(X: QRaw(-98304), Y: FixedQ4816.Zero, Z: FixedQ4816.Zero),
        MinRadius: QRaw(32768), MaxRadius: Q(8), FadeFrames: 0, GainQ16: UnityQ16,
        Channel: WorldAudioChannel.Left, Source: WorldAudioSourceKey.Tune("sunny")));
    snapshot.TryAddEmitter(new WorldAudioEmitter(
        Id: 2, Kind: WorldAudioEmitterKind.Point, Position: new FixedVector3(X: QRaw(98304), Y: FixedQ4816.Zero, Z: FixedQ4816.Zero),
        MinRadius: QRaw(32768), MaxRadius: Q(8), FadeFrames: 0, GainQ16: UnityQ16,
        Channel: WorldAudioChannel.Right, Source: WorldAudioSourceKey.Tune("sunny")));

    // The mover: crosses the scene left to right, entering and leaving its finite support.
    snapshot.TryAddEmitter(new WorldAudioEmitter(
        Id: 3, Kind: WorldAudioEmitterKind.Point, Position: new FixedVector3(X: QRaw(-655360L + ((step * 40960L) / 15)), Y: FixedQ4816.Zero, Z: FixedQ4816.Zero),
        MinRadius: QRaw(32768), MaxRadius: Q(3), FadeFrames: 0, GainQ16: (UnityQ16 / 2),
        Channel: WorldAudioChannel.Mix, Source: WorldAudioSourceKey.Tune("sunny")));

    // The bed: a noise region south of the orbit; the orbit crosses its outer edge.
    snapshot.TryAddEmitter(new WorldAudioEmitter(
        Id: 4, Kind: WorldAudioEmitterKind.Bed, Position: new FixedVector3(X: FixedQ4816.Zero, Y: FixedQ4816.Zero, Z: Q(-6)),
        MinRadius: Q(2), MaxRadius: Q(5), FadeFrames: 4800, GainQ16: 45875,
        Channel: WorldAudioChannel.Mix, Source: WorldAudioSourceKey.Synth("bed")));

    // The creature: seeded chirps ahead of the orbit center.
    snapshot.TryAddEmitter(new WorldAudioEmitter(
        Id: 5, Kind: WorldAudioEmitterKind.Point, Position: new FixedVector3(X: FixedQ4816.Zero, Y: FixedQ4816.Zero, Z: Q(3)),
        MinRadius: QRaw(32768), MaxRadius: Q(10), FadeFrames: 0, GainQ16: UnityQ16,
        Channel: WorldAudioChannel.Mix, Source: WorldAudioSourceKey.Synth("chirp")));

    if (step == 0) {
        snapshot.TryAddTrigger(new WorldSynthTrigger(Sequence: ++sequence, PatchId: "bed", Seed: 42UL, GainQ16: UnityQ16, EmitterId: 4));
    }

    if ((step > 0) && ((step % 48) == 0)) {
        snapshot.TryAddTrigger(new WorldSynthTrigger(Sequence: ++sequence, PatchId: "chirp", Seed: (0xC0FFEEUL + (ulong)step), GainQ16: UnityQ16, EmitterId: 5));
    }
}

string RunTimeline(WorldVoicePatch chirpPatch, WorldVoicePatch bedPatch, byte[] rom) {
    using var tune = new TuneSource(rom);
    var mixer = new WorldAudioMixer();

    mixer.RegisterPatch("chirp", chirpPatch);
    mixer.RegisterPatch("bed", bedPatch);
    mixer.SetSource(WorldAudioSourceKey.Tune("sunny"), tune);

    var snapshot = new WorldAudioSnapshot();
    var block = new short[Frames * 2];
    var sequence = 0UL;
    using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

    for (var step = 0; step < Steps; step++) {
        BuildSnapshot(snapshot, step, ref sequence);
        mixer.MixBlock(snapshot, block);
        sha.AppendData(MemoryMarshal.AsBytes(block.AsSpan()));
    }

    return Convert.ToHexString(sha.GetHashAndReset());
}

Console.WriteLine("[proof] === audio-mix (a): the golden PCM hash, reproduced across two full fresh runs ===");

var hashFirst = RunTimeline(chirp, bedNoise, tuneRom);
var hashSecond = RunTimeline(chirp, bedNoise, tuneRom);

Console.WriteLine("[proof]");
Console.WriteLine($"[proof]   ================= GOLDEN PCM HASH (self-referential; a mix-law change re-goldens) =================");
Console.WriteLine($"[proof]   {hashFirst}");
Console.WriteLine($"[proof]   ====================================================================================================");
Console.WriteLine("[proof]");
Check("golden-hash-reproduced", hashFirst == hashSecond, $"run2 {(hashFirst == hashSecond ? "==" : "!=")} run1");

// ---- (b) stereo geometry -----------------------------------------------------------------------------------------
Console.WriteLine("[proof] === audio-mix (b): pan geometry lands energy on the geometric side ===");

(long L, long R) Energies(long emitterX) {
    var mixer = new WorldAudioMixer();

    mixer.SetSource(WorldAudioSourceKey.Tune("const"), new ConstSource(16384));

    var snapshot = new WorldAudioSnapshot();
    var block = new short[Frames * 2];

    for (var i = 0; i < 3; i++) {
        snapshot.Reset(new WorldAudioListener(Position: default, Yaw: FixedComplex.MultiplicativeIdentity));
        snapshot.TryAddEmitter(new WorldAudioEmitter(
            Id: 1, Kind: WorldAudioEmitterKind.Point, Position: new FixedVector3(X: Q(emitterX), Y: FixedQ4816.Zero, Z: FixedQ4816.Zero),
            MinRadius: Q(4), MaxRadius: Q(8), FadeFrames: 0, GainQ16: UnityQ16,
            Channel: WorldAudioChannel.Mix, Source: WorldAudioSourceKey.Tune("const")));
        mixer.MixBlock(snapshot, block);
    }

    long left = 0, right = 0;

    for (var n = 0; n < Frames; n++) {
        left += ((long)block[2 * n]) * block[2 * n];
        right += ((long)block[(2 * n) + 1]) * block[(2 * n) + 1];
    }

    return (left, right);
}

var (rightCaseL, rightCaseR) = Energies(3);
var (leftCaseL, leftCaseR) = Energies(-3);

Check("hard-right-emitter-lands-right", (rightCaseR > (10 * Math.Max(rightCaseL, 1))), $"L={rightCaseL} R={rightCaseR}");
Check("hard-left-emitter-lands-left", (leftCaseL > (10 * Math.Max(leftCaseR, 1))), $"L={leftCaseL} R={leftCaseR}");
Check("mirror-symmetry", (rightCaseR == leftCaseL) && (rightCaseL == leftCaseR), $"R-case ({rightCaseL},{rightCaseR}) mirrors L-case ({leftCaseL},{leftCaseR})");

// ---- (c) the cull contract ---------------------------------------------------------------------------------------
Console.WriteLine("[proof] === audio-mix (c): finite support IS the cull — bit-identical to absence, zero pulls ===");

short[] RenderCull(bool withEmitter, long distance, CountingSource source) {
    var mixer = new WorldAudioMixer();

    mixer.SetSource(WorldAudioSourceKey.Tune("const"), source);

    var snapshot = new WorldAudioSnapshot();
    var output = new short[3 * Frames * 2];

    for (var i = 0; i < 3; i++) {
        snapshot.Reset(new WorldAudioListener(Position: default, Yaw: FixedComplex.MultiplicativeIdentity));

        if (withEmitter) {
            snapshot.TryAddEmitter(new WorldAudioEmitter(
                Id: 7, Kind: WorldAudioEmitterKind.Point, Position: new FixedVector3(X: Q(distance), Y: FixedQ4816.Zero, Z: FixedQ4816.Zero),
                MinRadius: Q(1), MaxRadius: Q(4), FadeFrames: 0, GainQ16: UnityQ16,
                Channel: WorldAudioChannel.Mix, Source: WorldAudioSourceKey.Tune("const")));
        }

        mixer.MixBlock(snapshot, output.AsSpan(i * Frames * 2, Frames * 2));
    }

    return output;
}

var culledCounter = new CountingSource(16384);
var culled = RenderCull(true, 10, culledCounter);
var absent = RenderCull(false, 0, new CountingSource(16384));
var audibleCounter = new CountingSource(16384);
var audible = RenderCull(true, 2, audibleCounter);

Check("culled-bit-equals-absent", culled.AsSpan().SequenceEqual(absent), "3 blocks bit-compare");
Check("culled-source-never-pulled", (culledCounter.Pulls == 0), $"pulls={culledCounter.Pulls}");
Check("in-radius-is-audible", (audible[(2 * Frames * 2) + 101] != 0) && (audibleCounter.Pulls == 3), $"steady right sample={audible[(2 * Frames * 2) + 101]}, pulls={audibleCounter.Pulls}");

// ---- (d) the single-pull contract --------------------------------------------------------------------------------
Console.WriteLine("[proof] === audio-mix (d): two feeds, one source, one pull per block ===");

{
    var counter = new CountingSource(8192);
    var mixer = new WorldAudioMixer();

    mixer.SetSource(WorldAudioSourceKey.Tune("shared"), counter);

    var snapshot = new WorldAudioSnapshot();
    var block = new short[Frames * 2];

    for (var i = 0; i < 5; i++) {
        snapshot.Reset(new WorldAudioListener(Position: default, Yaw: FixedComplex.MultiplicativeIdentity));

        for (var id = 1; id <= 2; id++) {
            snapshot.TryAddEmitter(new WorldAudioEmitter(
                Id: id, Kind: WorldAudioEmitterKind.Point, Position: new FixedVector3(X: Q((id == 1) ? -2 : 2), Y: FixedQ4816.Zero, Z: FixedQ4816.Zero),
                MinRadius: Q(4), MaxRadius: Q(8), FadeFrames: 0, GainQ16: UnityQ16,
                Channel: ((id == 1) ? WorldAudioChannel.Left : WorldAudioChannel.Right), Source: WorldAudioSourceKey.Tune("shared")));
        }

        mixer.MixBlock(snapshot, block);
    }

    Check("shared-source-pulled-once-per-block", (counter.Pulls == 5), $"5 blocks, pulls={counter.Pulls}");
}

// ---- (e) the soft clip -------------------------------------------------------------------------------------------
Console.WriteLine("[proof] === audio-mix (e): the cubic knee engages without wrap; boundaries are exact ===");

short[] RenderHot(int emitterCount) {
    var mixer = new WorldAudioMixer();

    mixer.SetSource(WorldAudioSourceKey.Tune("const"), new ConstSource(16384));

    var snapshot = new WorldAudioSnapshot();
    var block = new short[Frames * 2];

    for (var i = 0; i < 2; i++) {
        snapshot.Reset(new WorldAudioListener(Position: default, Yaw: FixedComplex.MultiplicativeIdentity));

        for (var id = 1; id <= emitterCount; id++) {
            snapshot.TryAddEmitter(new WorldAudioEmitter(
                Id: id, Kind: WorldAudioEmitterKind.Point, Position: default,
                MinRadius: Q(1), MaxRadius: Q(4), FadeFrames: 0, GainQ16: (4 * UnityQ16),
                Channel: WorldAudioChannel.Mix, Source: WorldAudioSourceKey.Tune("const")));
        }

        mixer.MixBlock(snapshot, block); // block 2 is steady state (prev == target)
    }

    return block;
}

var hot = RenderHot(2);
var knee = RenderHot(1);
var hotMax = 0;
var hotMin = int.MaxValue;

foreach (var s in hot) {
    hotMax = Math.Max(hotMax, s);
    hotMin = Math.Min(hotMin, s);
}

// One emitter at listener center: coefficient (4·unity·centerPan) makes each accumulated sample exactly 46340.
var kneeExpected = (int)WorldAudioMixer.SoftClip(46340);

Check("hot-block-pins-without-wrap", (hotMax == 32767) && (hotMin >= 0), $"max={hotMax} min={hotMin} (input 92680 FS-positive; wrap would go negative)");
Check("knee-compresses-exactly", (knee[100] == kneeExpected) && (kneeExpected > 24575) && (kneeExpected < 46340), $"46340 -> {knee[100]} (expected {kneeExpected})");
Check("knee-boundaries-exact", (WorldAudioMixer.SoftClip(24575) == 24575) && (WorldAudioMixer.SoftClip(24576) == 24576) && (WorldAudioMixer.SoftClip(49151) == 32767) && (WorldAudioMixer.SoftClip(-49151) == -32767), "transparent at H, continuous at H+1, pinned at the limit, symmetric");

// ---- (f) the coefficient ramp ------------------------------------------------------------------------------------
Console.WriteLine("[proof] === audio-mix (f): a gain step ramps across the block, bounded per sample ===");

{
    var mixer = new WorldAudioMixer();

    mixer.SetSource(WorldAudioSourceKey.Tune("const"), new ConstSource(16384));

    var snapshot = new WorldAudioSnapshot();
    var blocks = new short[4][];

    for (var i = 0; i < 4; i++) {
        snapshot.Reset(new WorldAudioListener(Position: default, Yaw: FixedComplex.MultiplicativeIdentity));
        snapshot.TryAddEmitter(new WorldAudioEmitter(
            Id: 1, Kind: WorldAudioEmitterKind.Point, Position: default,
            MinRadius: Q(1), MaxRadius: Q(4), FadeFrames: 0, GainQ16: ((i < 2) ? (UnityQ16 / 4) : UnityQ16),
            Channel: WorldAudioChannel.Mix, Source: WorldAudioSourceKey.Tune("const")));
        blocks[i] = new short[Frames * 2];
        mixer.MixBlock(snapshot, blocks[i]);
    }

    var steadyLow = blocks[1][2 * 100];
    var steadyHigh = blocks[3][2 * 100];
    var monotone = true;
    var maxStep = 0;

    for (var n = 1; n < Frames; n++) {
        var delta = blocks[2][2 * n] - blocks[2][2 * (n - 1)];

        monotone &= (delta >= 0);
        maxStep = Math.Max(maxStep, Math.Abs(delta));
    }

    // Ramp bound: coefficient travel (46341 - 11585) over 200 frames on a 16384 source ≈ 43.4/sample.
    Check("steady-plateaus", (steadyLow == 2896) && (steadyHigh == 11585), $"low={steadyLow} (expect 2896) high={steadyHigh} (expect 11585)");
    Check("ramp-monotone-and-bounded", monotone && (maxStep <= 45) && (maxStep >= 40), $"maxStep={maxStep} (bound 45)");
    Check("ramp-lands-on-target", (Math.Abs(blocks[2][2 * (Frames - 1)] - 11585) <= 2), $"last ramp sample={blocks[2][2 * (Frames - 1)]}");
}

// ---- (g) the synth -----------------------------------------------------------------------------------------------
Console.WriteLine("[proof] === audio-mix (g): seeded voices, steal policy, SVF, once-only triggers, bed fade ===");

short[] RenderSynth(WorldVoiceSynth synth, int totalFrames) {
    var output = new short[totalFrames];

    for (var offset = 0; offset < totalFrames; offset += Frames) {
        synth.Render(output.AsSpan(offset, Math.Min(Frames, totalFrames - offset)), Math.Min(Frames, totalFrames - offset));
    }

    return output;
}

{
    var first = new WorldVoiceSynth();
    var second = new WorldVoiceSynth();

    first.Trigger(chirp, 0xBEEF, UnityQ16);
    second.Trigger(chirp, 0xBEEF, UnityQ16);

    var renderFirst = RenderSynth(first, 12_000);
    var renderSecond = RenderSynth(second, 12_000);
    long energy = 0;

    foreach (var s in renderFirst) {
        energy += Math.Abs((int)s);
    }

    Check("seeded-voice-reproduces-bitwise", renderFirst.AsSpan().SequenceEqual(renderSecond), "two fresh synths, same seed, 12000 frames");
    Check("voice-sounds-and-completes", (energy > 0) && (first.ActiveVoiceCount == 0), $"energy={energy}, active after duration+release={first.ActiveVoiceCount}");

    var noisy = new WorldVoiceSynth();
    var noisySecond = new WorldVoiceSynth();

    noisy.Trigger(bedNoise, 7UL, UnityQ16);
    noisySecond.Trigger(bedNoise, 7UL, UnityQ16);
    Check("seeded-noise-reproduces-bitwise", RenderSynth(noisy, 4800).AsSpan().SequenceEqual(RenderSynth(noisySecond, 4800)), "the Pcg32 stream is the seed's function");

    var crowded = new WorldVoiceSynth();

    for (var i = 0; i < 40; i++) {
        crowded.Trigger(hum, (ulong)i, UnityQ16);
    }

    Check("steal-quietest-pins-at-32", (crowded.ActiveVoiceCount == WorldVoiceSynth.VoiceCount), $"40 triggers -> {crowded.ActiveVoiceCount} voices");

    // The SVF: identical seeded noise, one voice filtered low-pass — first-difference energy must drop.
    var open = new WorldVoiceSynth();
    var dark = new WorldVoiceSynth();
    var white = bedNoise with { Polynomial = 0 };

    open.Trigger(white, 99UL, UnityQ16);
    dark.Trigger(white with { FilterMode = WorldVoiceFilterMode.LowPass, FilterCoefficientQ16 = 8573, FilterDampingQ16 = 65536 }, 99UL, UnityQ16);

    var openRender = RenderSynth(open, 9600);
    var darkRender = RenderSynth(dark, 9600);
    long openRoughness = 0, darkRoughness = 0;

    for (var n = 4801; n < 9600; n++) { // settled tail, past the attack
        openRoughness += Math.Abs(openRender[n] - openRender[n - 1]);
        darkRoughness += Math.Abs(darkRender[n] - darkRender[n - 1]);
    }

    Check("svf-lowpass-darkens-noise", (darkRoughness * 4) < openRoughness, $"first-difference energy open={openRoughness} lowpass={darkRoughness}");
}

{
    // Once-only triggers under snapshot hold: the same snapshot mixed twice fires its trigger once.
    var mixer = new WorldAudioMixer();

    mixer.RegisterPatch("hum", hum);

    var snapshot = new WorldAudioSnapshot();
    var block = new short[Frames * 2];

    snapshot.Reset(new WorldAudioListener(Position: default, Yaw: FixedComplex.MultiplicativeIdentity));
    snapshot.TryAddEmitter(new WorldAudioEmitter(
        Id: 1, Kind: WorldAudioEmitterKind.Point, Position: new FixedVector3(X: Q(1), Y: FixedQ4816.Zero, Z: FixedQ4816.Zero),
        MinRadius: Q(2), MaxRadius: Q(4), FadeFrames: 0, GainQ16: UnityQ16,
        Channel: WorldAudioChannel.Mix, Source: WorldAudioSourceKey.Synth("hum")));
    snapshot.TryAddTrigger(new WorldSynthTrigger(Sequence: 1, PatchId: "hum", Seed: 1UL, GainQ16: UnityQ16, EmitterId: 1));
    mixer.MixBlock(snapshot, block);
    mixer.MixBlock(snapshot, block);
    Check("triggers-fire-once-under-hold", (mixer.Synth.ActiveVoiceCount == 1), $"same snapshot mixed twice -> {mixer.Synth.ActiveVoiceCount} voice(s)");
}

{
    // Bed fade: FadeFrames bounds presence slew — the first block of a full-presence bed stays quiet.
    short[] BedBlock(int fadeFrames) {
        var mixer = new WorldAudioMixer();

        mixer.SetSource(WorldAudioSourceKey.Tune("const"), new ConstSource(16384));

        var snapshot = new WorldAudioSnapshot();
        var block = new short[Frames * 2];

        snapshot.Reset(new WorldAudioListener(Position: default, Yaw: FixedComplex.MultiplicativeIdentity));
        snapshot.TryAddEmitter(new WorldAudioEmitter(
            Id: 1, Kind: WorldAudioEmitterKind.Bed, Position: default,
            MinRadius: Q(2), MaxRadius: Q(5), FadeFrames: fadeFrames, GainQ16: UnityQ16,
            Channel: WorldAudioChannel.Mix, Source: WorldAudioSourceKey.Tune("const")));
        mixer.MixBlock(snapshot, block);

        return block;
    }

    var faded = BedBlock(4800);
    var instant = BedBlock(0);
    int fadedPeak = 0, instantPeak = 0;

    for (var n = 0; n < Frames; n++) {
        fadedPeak = Math.Max(fadedPeak, Math.Abs((int)faded[2 * n]));
        instantPeak = Math.Max(instantPeak, Math.Abs((int)instant[2 * n]));
    }

    // Slew bound: 65536·200/4800 = 2730 coefficient/block -> ≤ ~683 on a 16384 source; unbounded ramps to ~11585.
    Check("bed-fade-bounds-presence-slew", (fadedPeak <= 700) && (instantPeak >= 11_000), $"faded peak={fadedPeak} (bound ~683), instant peak={instantPeak}");
}

// ---- (h) the world-document pipeline (AP2) -----------------------------------------------------------------------
Console.WriteLine("[proof] === audio-mix (h): the fixture WORLD DOCUMENT drives derivation -> MixBlock -> hash ===");

{
    // The proof-authored fixture world (disposable content only): every speaker $type, all four source kinds, a
    // scene-row emission facet, a placement emission facet, and a sound-bearing creation placed (its anchored
    // speaker rides the placement's stamped shape transform).
    var chirpPatch = SynthPatchCanonicalizer.Canonicalize(new SynthPatchDocument(
        Schema: SynthPatchDocument.CurrentSchema, Name: "chirp", Oscillator: SynthOscillator.Pulse,
        DutyThousandths: 250, Polynomial: null, AttackFrames: 480, DecayFrames: 4800, SustainThousandths: 300,
        ReleaseFrames: 2400, PitchMillihertz: 1_320_000, DurationFrames: 9_600));
    var droneBed = SynthPatchCanonicalizer.Canonicalize(new SynthPatchDocument(
        Schema: SynthPatchDocument.CurrentSchema, Name: "drone", Oscillator: SynthOscillator.Noise,
        DutyThousandths: null, Polynomial: 40, AttackFrames: 2400, DecayFrames: 0, SustainThousandths: 1000,
        ReleaseFrames: 0, PitchMillihertz: 1_000));
    var humInline = new SynthPatchDocument(
        Schema: SynthPatchDocument.CurrentSchema, Name: "hum", Oscillator: SynthOscillator.Sine,
        DutyThousandths: null, Polynomial: null, AttackFrames: 2400, DecayFrames: 0, SustainThousandths: 800,
        ReleaseFrames: 0, PitchMillihertz: 220_000);
    var tuneCanonical = AudioCanonicalizer.Canonicalize(tuneDocument);
    var statue = CreationCanonicalizer.Canonicalize(new CreationDocument(
        Schema: CreationDocument.CurrentSchema, Name: "statue", Intent: null, BakeStyle: null, Palette: null,
        Shapes: [new ShapeDocument(Id: 1, Name: null, Type: AvatarPrimitive.Sphere, Position: new Vector3(0f, 0.6f, 0f), Rotation: Quaternion.Identity, Scale: Vector3.One, Material: null, Blend: null, Smooth: null, Group: null)],
        Frames: null,
        Behavior: new CreationBehaviorDocument(Locomotion: "hover", Faces: null, Sounds: [
            new CreationSoundDocument(Name: "hum", ShapeId: 1, Patch: humInline, Level: 1f, Radius: 6f),
        ])));

    var sceneRows = new List<Puck.World.WorldSceneRow>(WorldDefinition.Default.Scene.Rows);

    sceneRows[0] = (((WorldSceneRow.Boulder)sceneRows[0]) with { Emission = new WorldEmission(PatchId: "chirp", Level: 1f, Radius: 8f) });

    var fixture = WorldDefinition.Default with {
        Scene = (WorldDefinition.Default.Scene with { Rows = sceneRows }),
        Patches = [
            new WorldPatch(Id: "chirp", Document: chirpPatch.Document, Hash: chirpPatch.Hash),
            new WorldPatch(Id: "drone", Document: droneBed.Document, Hash: droneBed.Hash),
        ],
        Tunes = [new WorldTune(Id: "sunny", Document: tuneCanonical.Document, Hash: tuneCanonical.Hash)],
        Creations = [new WorldCreation(Id: "statue", Document: statue.Document, Hash: statue.Hash)],
        Placements = [new WorldPlacement(Id: "statue-1", CreationId: "statue", Position: new Vector3(0f, 0f, 3f), YawDegrees: 0f, Scale: 1f, Emission: new WorldEmission(PatchId: "drone", Level: 0.5f, Radius: 6f))],
        Speakers = [
            new WorldSpeaker.Fixed(Name: "stereo-left", Position: new Vector3(-1.5f, 0f, 0f), Feed: new WorldSpeakerFeed(Source: new WorldSpeakerSource.Tune(TuneId: "sunny"), Channel: "left", Gain: 1f)),
            new WorldSpeaker.Fixed(Name: "stereo-right", Position: new Vector3(1.5f, 0f, 0f), Feed: new WorldSpeakerFeed(Source: new WorldSpeakerSource.Tune(TuneId: "sunny"), Channel: "right", Gain: 1f)),
            new WorldSpeaker.Anchored(Name: "statue-voice", Anchor: new WorldAnchor.Placement(PlacementId: "statue-1", ShapeId: 1), Offset: new Vector3(0f, 0.25f, 0f), Feed: new WorldSpeakerFeed(Source: new WorldSpeakerSource.Tune(TuneId: "sunny"), Channel: "mix", Gain: 0.5f)),
            new WorldSpeaker.Bed(Name: "wind", Center: new Vector3(0f, 0f, -6f), Radius: 5f, InnerRadius: 2f, FadeSeconds: 0.1f, Feed: new WorldSpeakerFeed(Source: new WorldSpeakerSource.Synth(PatchId: "drone"), Channel: "mix", Gain: 0.7f)),
            new WorldSpeaker.Fixed(Name: "cabinet", Position: new Vector3(-3f, 1.2f, -3f), Feed: new WorldSpeakerFeed(Source: new WorldSpeakerSource.Machine(ScreenIndex: 0), Channel: "mix", Gain: 1f)),
            new WorldSpeaker.Fixed(Name: "mute", Position: new Vector3(0f, 0f, 8f), Feed: new WorldSpeakerFeed(Source: new WorldSpeakerSource.None(), Channel: "mix", Gain: 1f)),
        ],
        Audio = (WorldAudioDefaults.Default with { MasterGain = 0.8f }),
    };

    // The full document pipeline: canonical serialize -> the strict loader/validator (hash pins re-verified) -> the
    // validated definition the derivation consumes.
    var fixturePath = Path.Combine(Path.GetTempPath(), $"puck-audio-fixture-{Environment.ProcessId}.world.json");

    _ = WorldDefinitionSerialization.Save(fixture, fixturePath);

    if (!WorldDefinitionLoader.TryLoadFile(fixturePath, out var loadedFixture, out var loadReason)) {
        Check("fixture-world-loads", false, loadReason);
    } else {
        Check("fixture-world-loads", true, $"{fixturePath} validated");

        string RunWorldTimeline() {
            _ = WorldDefinitionLoader.TryLoadFile(fixturePath, out var definition, out _);

            var director = new WorldAudioDirector(client: null, animator: null);

            director.ReconcileSpeakers(definition);

            var mixer = new WorldAudioMixer();

            director.AttachMixer(mixer);

            var block = new short[Frames * 2];
            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var seats = new WorldSeatCameraPose[1];

            try {
                for (var step = 0; step < Steps; step++) {
                    // The scripted listener orbit (radius 2, one revolution) through fixed-point SinCos, so the
                    // float eye is an exact function of the step on every machine.
                    var (sin, cos) = FixedQ4816.SinCos(QRaw((step * 411775L) / Steps));
                    var eye = new Vector3((cos.Value * 2f) / 65536f, 0f, (sin.Value * 2f) / 65536f);

                    seats[0] = new WorldSeatCameraPose(Joined: true, Eye: eye, Forward: -eye);

                    var snapshot = director.Publish(transforms: ReadOnlySpan<DynamicTransform>.Empty, seats: seats);

                    mixer.MixBlock(snapshot, block);
                    sha.AppendData(MemoryMarshal.AsBytes(block.AsSpan()));

                    if (step == 0) {
                        Check("fixture-derives-nine-emitters", (snapshot.Emitters.Length == 9), $"emitters={snapshot.Emitters.Length} (6 speakers + scene facet + placement facet + creation sound)");
                        Check("fixture-master-gain-flows", (mixer.MasterGainQ16 == 52429), $"MasterGainQ16={mixer.MasterGainQ16} (0.8 -> 52429)");
                    }

                    if (step == 1) {
                        Check("fixture-arrival-voices-sound", (mixer.Synth.ActiveVoiceCount >= 3), $"active voices={mixer.Synth.ActiveVoiceCount} (chirp one-shot + drone loops x2 + hum loop)");
                    }
                }
            } finally {
                director.DetachMixer();
            }

            return Convert.ToHexString(sha.GetHashAndReset());
        }

        var worldHashFirst = RunWorldTimeline();
        var worldHashSecond = RunWorldTimeline();

        Console.WriteLine("[proof]");
        Console.WriteLine($"[proof]   ============ GOLDEN WORLD-DOCUMENT PCM HASH (self-referential; re-goldens on a law change) ============");
        Console.WriteLine($"[proof]   {worldHashFirst}");
        Console.WriteLine($"[proof]   ====================================================================================================");
        Console.WriteLine("[proof]");
        Check("world-hash-reproduced", worldHashFirst == worldHashSecond, $"run2 {(worldHashFirst == worldHashSecond ? "==" : "!=")} run1");

        {
            // The derivation listing is a pure function of the document — assert its stable facts.
            var director = new WorldAudioDirector(client: null, animator: null);

            director.ReconcileSpeakers(loadedFixture);

            var listing = director.DescribeEmitters();

            Console.WriteLine($"[proof]   {listing}");
            Check("fixture-derivation-listing", (listing.Contains("speaker:stereo-left point tune:sunny left")
                && listing.Contains("speaker:wind bed synth:drone mix")
                && listing.Contains("speaker:cabinet point machine:0 mix")
                && listing.Contains("speaker:mute point none mix")
                && listing.Contains("scene:boulder-1 point synth:chirp mix")
                && listing.Contains("placement:statue-1 point synth:drone mix")
                && listing.Contains("sound:statue-1:hum point synth:sound:statue-1:hum mix")), "all nine derivation keys present with their source tokens");
        }

        File.Delete(fixturePath);
    }
}

Console.WriteLine($"[proof] audio-mix {((failures == 0) ? "PASS" : $"FAIL ({failures})")}");

return ((failures == 0) ? 0 : 1);

// ---- the proof's block sources -----------------------------------------------------------------------------------

// The synchronous headless tune host (the TuneRom.Verify pattern): one real Humble core run cycle-exactly inside
// Pull — never QueuedMachineWorker (its worker thread is the one nondeterministic scheduling element, plan A12).
// The exact-rational cycle accumulator (subtract-not-reset) keeps machine time locked to 48000 with zero drift.
sealed class TuneSource : IDisposable, Puck.World.Audio.IAudioBlockSource {
    private const long CyclesPerSecond = 4_194_304L;

    private readonly MachineInstance m_machine;
    private readonly IAudioSink m_sink;
    private long m_cycleAccumulator;

    public TuneSource(byte[] rom) {
        m_machine = MachineFactory.Create(
            configuration: new MachineConfiguration(model: ConsoleModel.Cgb, cartridgeRom: rom),
            compose: static services => services.AddHumbleGamingBrickComponents());
        m_sink = m_machine.GetRequiredService<IAudioSink>();
        m_sink.Configure(sampleRate: WorldAudioMixer.SampleRate);
        // Boot pre-roll: the jukebox reaches its play state within 8 frames (TuneVerify's boot bar); the buffered
        // boot audio simply becomes the stream's deterministic head.
        m_machine.Machine.Run(tCycles: (8UL * 70224UL));
    }

    public int Pull(Span<short> interleavedStereo, int frames) {
        m_cycleAccumulator += (frames * CyclesPerSecond);

        var run = (m_cycleAccumulator / WorldAudioMixer.SampleRate);

        m_machine.Machine.Run(tCycles: (ulong)run);
        m_cycleAccumulator -= (run * WorldAudioMixer.SampleRate);

        return (m_sink.ReadSamples(destination: interleavedStereo[..(frames * 2)]) / 2);
    }

    public void Dispose() => m_machine.Dispose();
}

// A constant-valued stereo source: the ramp/clip/pan scenarios read gain arithmetic directly off it.
sealed class ConstSource(short value) : Puck.World.Audio.IAudioBlockSource {
    public int Pull(Span<short> interleavedStereo, int frames) {
        interleavedStereo[..(frames * 2)].Fill(value);

        return frames;
    }
}

// A pull-counting constant source: the single-pull and never-pulled contracts read its counter.
sealed class CountingSource(short value) : Puck.World.Audio.IAudioBlockSource {
    public int Pulls { get; private set; }

    public int Pull(Span<short> interleavedStereo, int frames) {
        Pulls++;
        interleavedStereo[..(frames * 2)].Fill(value);

        return frames;
    }
}
