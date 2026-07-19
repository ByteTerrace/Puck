using Puck.Abstractions.Machines;
using Puck.Maths;

namespace Puck.World.Audio;

/// <summary>
/// The seam every non-synth signal reaches the mixer through: pull up to <c>frames</c> stereo frames
/// for the current block. AP1's offline proof binds a synchronous headless core to it; AP2 binds tune
/// acquire/release hosting; AP3 binds the live machine worker — <see cref="WorldAudioMixer.MixBlock"/> never
/// reshapes. A shortfall is honest underrun: the mixer treats the missing tail as silence.
/// </summary>
public interface IAudioBlockSource {
    /// <summary>Fills the front of <paramref name="interleavedStereo"/> with up to <paramref name="frames"/>
    /// interleaved left/right s16 frames.</summary>
    /// <param name="interleavedStereo">The destination (at least <c>2·frames</c> samples).</param>
    /// <param name="frames">The frames requested.</param>
    /// <returns>The frames delivered, 0..<paramref name="frames"/>.</returns>
    int Pull(Span<short> interleavedStereo, int frames);
}

/// <summary>Adapts a live <see cref="IAudioMachine"/> ring to <see cref="IAudioBlockSource"/>: one destructive
/// drain per block (plan A7 — sharing rows tap the mixer's scratch, never re-drain). The ring's occupancy IS the
/// watermark — a drain that comes up short is an underrun and the mixer renders the shortfall silent.</summary>
/// <param name="machine">The machine to drain.</param>
public sealed class MachineBlockSource(IAudioMachine machine) : IAudioBlockSource {
    private readonly IAudioMachine m_machine = machine;

    /// <inheritdoc/>
    public int Pull(Span<short> interleavedStereo, int frames) =>
        (m_machine.ReadSamples(destination: interleavedStereo[..(frames * 2)]) / 2);
}

/// <summary>
/// The world audio mixer core (plan A1/A12): <see cref="MixBlock"/> is a synchronous pure function owning no
/// thread — the future device pump and the offline hash proof are two drivers of the same code. Fixed-point end to
/// end: s16 samples × Q16 composite gains → int32 accumulate → the deterministic polynomial soft-clip → s16.
/// <para>Per block: each emitter's TARGET coefficients derive from the snapshot (finite-support squared-smoothstep
/// attenuation — computed on SQUARED distances, no square root, and the zero of its support IS the cull;
/// equal-power pan from listener-relative azimuth via one <see cref="FixedQ4816.SinCos"/> per point emitter; beds
/// center-pan with a presence envelope whose slew <see cref="WorldAudioEmitter.FadeFrames"/> bounds), then the
/// LIVE coefficients ramp linearly from the previous block's across every frame — the zipper-noise killer.
/// Ramp state is keyed by emitter id; a new id ramps in from silence, a departed id drops its state. Each distinct
/// source is pulled ONCE into per-source scratch and every feed taps it (left | right | mix).</para>
/// <para>The soft-clip is the smooth-knee cubic <c>y = H + G·(1 − (1 − t)³)</c>: bit-transparent up to
/// <c>H = 24575</c> (0.75 FS), then the knee <c>t = (|s| − H)/W</c> over width <c>W = 3G = 24576</c> saturates
/// into the ceiling <c>H + G = 32767</c> at <c>|s| = 49151</c> (1.5 FS) with matched value and slope at both ends
/// (C¹, monotone), hard-limited beyond. Integer form: <c>y = 32767 − ⌊d³ / (27·2²⁶)⌋</c> with
/// <c>d = 49151 − |s|</c>. Never a libm function — the polynomial is the PCM hash's determinism contract.</para>
/// Zero steady-state allocation: every scratch and table is preallocated at construction.
/// </summary>
public sealed class WorldAudioMixer {
    /// <summary>The mixer rate (plan A1): device-native, exactly <see cref="FramesPerSimStep"/> frames per 240 Hz
    /// sim step, 21/20 engine ticks per frame.</summary>
    public const int SampleRate = 48_000;
    /// <summary>Audio frames per 240 Hz sim step (stepTicks 210 of the 50400/s engine clock): 48000/240 = 200 —
    /// the offline proof's block size. A contract invariant, not a tunable.</summary>
    public const int FramesPerSimStep = 200;
    /// <summary>The largest block <see cref="MixBlock"/> renders — sized to the device pump's 256-frame quantum
    /// (plan A2) with the proof's 200-frame sim block inside it.</summary>
    public const int MaxBlockFrames = 256;
    /// <summary>The registered-source capacity (each slot preallocates one stereo scratch).</summary>
    public const int MaxSources = 16;
    /// <summary>The registered-patch capacity.</summary>
    public const int MaxPatches = 32;

    // Soft-clip constants: knee start H, knee width W = 3G, ceiling H + G = 32767; divisor 27·2^26 = G/W³ inverted.
    private const int ClipKneeStart = 24575;
    private const int ClipLimit = 49151;
    private const long ClipKneeDivisor = (27L << 26);

    // Pan constants: π/4 in Q16 raw (the equal-power quarter arc), and cos(π/4) for the bed's center pan.
    private const long QuarterPiRawQ16 = 51472L;
    private const int CenterPanQ16 = 46341;
    // Below this local-plane raw magnitude (~0.01 units) azimuth is meaningless; pan snaps to center.
    private const long PanEpsilonRaw = 655L;

    private readonly WorldVoiceSynth m_synth = new();
    private readonly int[] m_accumulateLeft = new int[MaxBlockFrames];
    private readonly int[] m_accumulateRight = new int[MaxBlockFrames];
    private readonly int[] m_synthScratch = new int[MaxBlockFrames];

    private readonly WorldAudioSourceKey[] m_sourceKeys = new WorldAudioSourceKey[MaxSources];
    private readonly IAudioBlockSource?[] m_sources = new IAudioBlockSource?[MaxSources];
    private readonly short[][] m_sourceScratch = new short[MaxSources][];
    private readonly int[] m_sourcePulledFrames = new int[MaxSources];
    private readonly bool[] m_sourcePulled = new bool[MaxSources];
    private int m_sourceCount;

    private readonly string[] m_patchIds = new string[MaxPatches];
    private readonly WorldVoicePatch[] m_patches = new WorldVoicePatch[MaxPatches];
    private int m_patchCount;

    // Coefficient-ramp rows, keyed by emitter id, rebuilt against each block's snapshot.
    private readonly int[] m_rowIds = new int[WorldAudioSnapshot.DefaultMaxEmitters];
    private readonly int[] m_rowPreviousLeft = new int[WorldAudioSnapshot.DefaultMaxEmitters];
    private readonly int[] m_rowPreviousRight = new int[WorldAudioSnapshot.DefaultMaxEmitters];
    private readonly bool[] m_rowSeen = new bool[WorldAudioSnapshot.DefaultMaxEmitters];
    private int m_rowCount;

    private ulong m_lastTriggerSequence;

    /// <summary>Initializes the mixer with every scratch preallocated.</summary>
    public WorldAudioMixer() {
        for (var i = 0; (i < MaxSources); i++) {
            m_sourceScratch[i] = new short[MaxBlockFrames * 2];
        }
    }

    /// <summary>Gets the synth (proof introspection; triggers route through snapshots).</summary>
    public WorldVoiceSynth Synth => m_synth;
    /// <summary>Gets the master gain, Q16. A code default until AP2's <c>WorldAudioDefaults</c> section lands
    /// (liftable: <c>MasterGain</c>).</summary>
    public int MasterGainQ16 { get; set; } = 65536;
    /// <summary>Gets the count of triggers refused because their patch id was unregistered — honest loss, echoed
    /// by AP2's <c>speaker.state</c>.</summary>
    public int DroppedTriggerCount { get; private set; }

    /// <summary>Gets the running peak |output sample| since construction — the <c>audio.state</c> meter. Monotone
    /// by design: a nonzero value is durable proof the mix has produced signal (the live smoke's assertion), and a
    /// zero proves every block so far was silent.</summary>
    public int OutputPeak { get; private set; }

    /// <summary>Gets the count of source identities currently bound to a live block source.</summary>
    public int BoundSourceCount {
        get {
            var count = 0;

            for (var i = 0; (i < m_sourceCount); i++) {
                if (m_sources[i] is not null) {
                    count++;
                }
            }

            return count;
        }
    }

    /// <summary>Registers (or replaces) a synth patch under an id.</summary>
    /// <param name="id">The patch id trigger events reference.</param>
    /// <param name="patch">The runtime parameter block.</param>
    /// <exception cref="InvalidOperationException">The patch table is full.</exception>
    public void RegisterPatch(string id, in WorldVoicePatch patch) {
        ArgumentException.ThrowIfNullOrEmpty(argument: id);

        for (var i = 0; (i < m_patchCount); i++) {
            if (string.Equals(a: m_patchIds[i], b: id, comparisonType: StringComparison.Ordinal)) {
                m_patches[i] = patch;

                return;
            }
        }

        if (m_patchCount >= MaxPatches) {
            throw new InvalidOperationException(message: $"patch table full ({MaxPatches}); cannot register '{id}'.");
        }

        m_patchIds[m_patchCount] = id;
        m_patches[m_patchCount] = patch;
        m_patchCount++;
    }

    /// <summary>Binds (or rebinds) a block source to a source identity.</summary>
    /// <param name="key">The source identity emitters reference.</param>
    /// <param name="source">The pull seam.</param>
    /// <exception cref="InvalidOperationException">The source table is full.</exception>
    public void SetSource(in WorldAudioSourceKey key, IAudioBlockSource source) {
        ArgumentNullException.ThrowIfNull(argument: source);

        var slot = FindSource(key: in key);

        if (slot < 0) {
            if (m_sourceCount >= MaxSources) {
                throw new InvalidOperationException(message: $"source table full ({MaxSources}); cannot bind {key.Kind} '{key.Id ?? key.Slot.ToString()}'.");
            }

            slot = m_sourceCount++;
            m_sourceKeys[slot] = key;
        }

        m_sources[slot] = source;
    }

    /// <summary>Unbinds a source identity; emitters referencing it render silence until rebound.</summary>
    /// <param name="key">The source identity to unbind.</param>
    public void RemoveSource(in WorldAudioSourceKey key) {
        var slot = FindSource(key: in key);

        if (slot >= 0) {
            m_sources[slot] = null;
        }
    }

    /// <summary>Mixes one block from the given snapshot into interleaved stereo s16 — synchronous, pure, owning
    /// no thread. The span length fixes the block size (<c>2·frames</c> samples, frames ≤
    /// <see cref="MaxBlockFrames"/>).</summary>
    /// <param name="snapshot">The current published snapshot (held, not interpolated — plan A3).</param>
    /// <param name="stereoInterleaved">The output block; fully overwritten.</param>
    public void MixBlock(WorldAudioSnapshot snapshot, Span<short> stereoInterleaved) {
        ArgumentNullException.ThrowIfNull(argument: snapshot);

        var frames = (stereoInterleaved.Length / 2);

        ArgumentOutOfRangeException.ThrowIfGreaterThan(value: frames, other: MaxBlockFrames);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value: frames);
        ConsumeTriggers(snapshot: snapshot);
        m_synth.ReleaseUnbound(emitters: snapshot.Emitters);

        var emitters = snapshot.Emitters;

        ArgumentOutOfRangeException.ThrowIfGreaterThan(value: emitters.Length, other: WorldAudioSnapshot.DefaultMaxEmitters);

        // Drop the ramp rows of departed emitters FIRST, so the row table can never outgrow the emitter capacity.
        Array.Clear(array: m_rowSeen);

        for (var e = 0; (e < emitters.Length); e++) {
            var existing = FindRow(id: emitters[e].Id);

            if (existing >= 0) {
                m_rowSeen[existing] = true;
            }
        }

        EvictStaleRows();

        // Pass 1: derive every emitter's target coefficients and refresh the ramp rows (marks pull demand).
        Span<int> targetLeft = stackalloc int[WorldAudioSnapshot.DefaultMaxEmitters];
        Span<int> targetRight = stackalloc int[WorldAudioSnapshot.DefaultMaxEmitters];
        Span<int> rowOf = stackalloc int[WorldAudioSnapshot.DefaultMaxEmitters];

        Array.Clear(array: m_sourcePulled);

        for (var e = 0; (e < emitters.Length); e++) {
            ref readonly var emitter = ref emitters[e];

            ComputeTargets(listener: snapshot.Listener, emitter: in emitter, frames: frames, left: out var left, right: out var right);
            targetLeft[e] = left;
            targetRight[e] = right;
            rowOf[e] = TouchRow(id: emitter.Id);

            // Pull demand: an external source feeds this block iff some emitter tapping it is audible now or was
            // audible last block (the ramp-out still needs samples).
            var row = rowOf[e];
            var audible = (((left | right) != 0) || ((m_rowPreviousLeft[row] | m_rowPreviousRight[row]) != 0));

            if (audible && (emitter.Source.Kind is WorldAudioSourceKind.Machine or WorldAudioSourceKind.Tune)) {
                PullSource(key: emitter.Source, frames: frames);
            }
        }

        var accumulateLeft = m_accumulateLeft.AsSpan(start: 0, length: frames);
        var accumulateRight = m_accumulateRight.AsSpan(start: 0, length: frames);

        accumulateLeft.Clear();
        accumulateRight.Clear();

        // Pass 2: accumulate each emitter with per-frame ramped coefficients.
        for (var e = 0; (e < emitters.Length); e++) {
            AccumulateEmitter(
                emitter: in emitters[e],
                frames: frames,
                left: accumulateLeft,
                previousLeft: m_rowPreviousLeft[rowOf[e]],
                previousRight: m_rowPreviousRight[rowOf[e]],
                right: accumulateRight,
                targetLeft: targetLeft[e],
                targetRight: targetRight[e]
            );
            m_rowPreviousLeft[rowOf[e]] = targetLeft[e];
            m_rowPreviousRight[rowOf[e]] = targetRight[e];
        }

        // Output: the deterministic soft-clip, then interleave (feeding the running peak meter on the way out).
        var peak = OutputPeak;

        for (var n = 0; (n < frames); n++) {
            var left = SoftClip(sample: accumulateLeft[n]);
            var right = SoftClip(sample: accumulateRight[n]);

            stereoInterleaved[(2 * n)] = left;
            stereoInterleaved[((2 * n) + 1)] = right;
            peak = Math.Max(val1: peak, val2: Math.Max(val1: Math.Abs(value: (int)left), val2: Math.Abs(value: (int)right)));
        }

        OutputPeak = peak;
    }

    /// <summary>The soft-clip transfer curve, exposed for the proof's structural assertions.</summary>
    /// <param name="sample">The accumulated int32 sample.</param>
    /// <returns>The saturated s16 sample.</returns>
    public static short SoftClip(int sample) {
        var magnitude = Math.Abs(value: ((long)sample));

        if (magnitude <= ClipKneeStart) {
            return ((short)sample);
        }

        int shaped;

        if (magnitude >= ClipLimit) {
            shaped = 32767;
        } else {
            var d = (ClipLimit - magnitude);

            shaped = ((int)(32767L - ((d * d * d) / ClipKneeDivisor)));
        }

        return ((short)((sample < 0) ? -shaped : shaped));
    }

    private static void SmoothstepAttenuation(long d2Q16, long min2Q16, long max2Q16, out int attenuationQ16) {
        if (d2Q16 >= max2Q16) {
            attenuationQ16 = 0;

            return;
        }

        if (d2Q16 <= min2Q16) {
            attenuationQ16 = 65536;

            return;
        }

        // Squared-smoothstep (plan A1): smoothstep over the SQUARED-distance ratio — finite support, no sqrt.
        var t = (((max2Q16 - d2Q16) << 16) / (max2Q16 - min2Q16));

        attenuationQ16 = ((int)((((t * t) >> 16) * ((3L << 16) - (2L * t))) >> 16));
    }

    private void ComputeTargets(in WorldAudioListener listener, in WorldAudioEmitter emitter, int frames, out int left, out int right) {
        var dxRaw = (emitter.Position.X.Value - listener.Position.X.Value);
        var dyRaw = (emitter.Position.Y.Value - listener.Position.Y.Value);
        var dzRaw = (emitter.Position.Z.Value - listener.Position.Z.Value);

        // Distance is 3D; azimuth ignores elevation (plan A1). Squares stay exact in Int128, saturated to long.
        var d2Wide = ((((Int128)dxRaw * dxRaw) + ((Int128)dyRaw * dyRaw) + ((Int128)dzRaw * dzRaw)) >> 16);
        var d2Q16 = ((d2Wide > long.MaxValue) ? long.MaxValue : ((long)d2Wide));
        var min2Q16 = ((emitter.MinRadius.Value * emitter.MinRadius.Value) >> 16);
        var max2Q16 = ((emitter.MaxRadius.Value * emitter.MaxRadius.Value) >> 16);

        SmoothstepAttenuation(d2Q16: d2Q16, min2Q16: min2Q16, max2Q16: max2Q16, attenuationQ16: out var attenuationQ16);

        if (attenuationQ16 == 0) {
            left = 0;
            right = 0;

            return;
        }

        var gain = ((int)((((((long)emitter.GainQ16) * attenuationQ16) >> 16) * MasterGainQ16) >> 16));

        if (emitter.Kind == WorldAudioEmitterKind.Bed) {
            // Beds are presence, not position: center pan; FadeFrames bounds the slew below.
            left = ((int)((((long)gain) * CenterPanQ16) >> 16));
            right = left;
        } else {
            ComputePan(listener: in listener, dxRaw: dxRaw, dzRaw: dzRaw, out var panLeftQ16, out var panRightQ16);
            left = ((int)((((long)gain) * panLeftQ16) >> 16));
            right = ((int)((((long)gain) * panRightQ16) >> 16));
        }

        if ((emitter.Kind == WorldAudioEmitterKind.Bed) && (emitter.FadeFrames > 0)) {
            // Presence slew bound: coefficients may move at most full-scale-per-FadeFrames each block.
            var row = FindRow(id: emitter.Id);
            var maxStep = ((int)Math.Max(val1: 1L, val2: ((65536L * frames) / emitter.FadeFrames)));

            if (row >= 0) {
                left = Math.Clamp(value: left, min: (m_rowPreviousLeft[row] - maxStep), max: (m_rowPreviousLeft[row] + maxStep));
                right = Math.Clamp(value: right, min: (m_rowPreviousRight[row] - maxStep), max: (m_rowPreviousRight[row] + maxStep));
            } else {
                left = Math.Clamp(value: left, min: -maxStep, max: maxStep);
                right = Math.Clamp(value: right, min: -maxStep, max: maxStep);
            }
        }
    }

    private static void ComputePan(in WorldAudioListener listener, long dxRaw, long dzRaw, out int panLeftQ16, out int panRightQ16) {
        if ((Math.Abs(value: dxRaw) | Math.Abs(value: dzRaw)) < PanEpsilonRaw) {
            // On top of the listener: azimuth is undefined; hold center.
            panLeftQ16 = CenterPanQ16;
            panRightQ16 = CenterPanQ16;

            return;
        }

        // Local direction = inverse yaw applied to the world-plane delta; its normalized X is the pan position
        // p ∈ [-1, 1] (right positive) with rear directions folding to the same side — no extra trig.
        var local = listener.Yaw.Conjugate().Rotate(vector: new FixedVector2(
            X: FixedQ4816.FromRawBits(value: dxRaw),
            Y: FixedQ4816.FromRawBits(value: dzRaw)
        ));
        var direction = new FixedComplex(Real: local.X, Imaginary: local.Y).Normalize();
        var p = Math.Clamp(value: direction.Real.Value, min: -65536L, max: 65536L);

        // Equal-power: φ = (p + 1)·π/4 ∈ [0, π/2]; gL = cos φ, gR = sin φ — ONE SinCos per emitter (plan A1).
        var phi = (((p + 65536L) * QuarterPiRawQ16) >> 16);
        var (sin, cos) = FixedQ4816.SinCos(angle: FixedQ4816.FromRawBits(value: phi));

        panLeftQ16 = ((int)Math.Clamp(value: cos.Value, min: 0L, max: 65536L));
        panRightQ16 = ((int)Math.Clamp(value: sin.Value, min: 0L, max: 65536L));
    }

    private void AccumulateEmitter(
        in WorldAudioEmitter emitter,
        int frames,
        Span<int> left,
        int previousLeft,
        int previousRight,
        Span<int> right,
        int targetLeft,
        int targetRight
    ) {
        var isSynth = (emitter.Source.Kind == WorldAudioSourceKind.Synth);

        // Synth voices advance even while inaudible — time flows for a culled creature; external sources simply
        // are not tapped, so a fully-silent emitter is BIT-IDENTICAL to an absent one (the cull contract).
        var silent = (((previousLeft | previousRight) | (targetLeft | targetRight)) == 0);

        if (isSynth) {
            var scratch = m_synthScratch.AsSpan(start: 0, length: frames);

            scratch.Clear();
            m_synth.RenderBound(emitterId: emitter.Id, accumulator: scratch);

            if (silent) {
                return;
            }

            AccumulateMono(frames: frames, left: left, previousLeft: previousLeft, previousRight: previousRight, right: right, source: scratch, targetLeft: targetLeft, targetRight: targetRight);

            return;
        }

        if (silent || (emitter.Source.Kind == WorldAudioSourceKind.None)) {
            return;
        }

        var slot = FindSource(key: emitter.Source);

        if ((slot < 0) || (m_sources[slot] is null)) {
            // Unbound source: honest silence (AP2's speaker.state echoes the fault).
            return;
        }

        AccumulateStereoTap(
            channel: emitter.Channel,
            frames: frames,
            left: left,
            previousLeft: previousLeft,
            previousRight: previousRight,
            pulledFrames: m_sourcePulledFrames[slot],
            right: right,
            source: m_sourceScratch[slot],
            targetLeft: targetLeft,
            targetRight: targetRight
        );
    }

    private static void AccumulateMono(int frames, Span<int> left, int previousLeft, int previousRight, Span<int> right, ReadOnlySpan<int> source, int targetLeft, int targetRight) {
        // Linear coefficient ramp in Q32: prev → target across the block, one add per frame.
        var currentLeft = (((long)previousLeft) << 16);
        var currentRight = (((long)previousRight) << 16);
        var stepLeft = (((((long)targetLeft) - previousLeft) << 16) / frames);
        var stepRight = (((((long)targetRight) - previousRight) << 16) / frames);

        for (var n = 0; (n < frames); n++) {
            currentLeft += stepLeft;
            currentRight += stepRight;

            var sample = ((long)source[n]);

            left[n] += ((int)((sample * (currentLeft >> 16)) >> 16));
            right[n] += ((int)((sample * (currentRight >> 16)) >> 16));
        }
    }

    private static void AccumulateStereoTap(
        WorldAudioChannel channel,
        int frames,
        Span<int> left,
        int previousLeft,
        int previousRight,
        int pulledFrames,
        Span<int> right,
        ReadOnlySpan<short> source,
        int targetLeft,
        int targetRight
    ) {
        var currentLeft = (((long)previousLeft) << 16);
        var currentRight = (((long)previousRight) << 16);
        var stepLeft = (((((long)targetLeft) - previousLeft) << 16) / frames);
        var stepRight = (((((long)targetRight) - previousRight) << 16) / frames);

        for (var n = 0; (n < frames); n++) {
            currentLeft += stepLeft;
            currentRight += stepRight;

            if (n >= pulledFrames) {
                continue; // Underrun tail: silence, but the ramp still advances (no step on refill).
            }

            long sample = (channel switch {
                WorldAudioChannel.Left => source[(2 * n)],
                WorldAudioChannel.Right => source[((2 * n) + 1)],
                _ => ((source[(2 * n)] + source[((2 * n) + 1)]) / 2),
            });

            left[n] += ((int)((sample * (currentLeft >> 16)) >> 16));
            right[n] += ((int)((sample * (currentRight >> 16)) >> 16));
        }
    }

    private void ConsumeTriggers(WorldAudioSnapshot snapshot) {
        var triggers = snapshot.Triggers;

        for (var i = 0; (i < triggers.Length); i++) {
            ref readonly var trigger = ref triggers[i];

            if (trigger.Sequence <= m_lastTriggerSequence) {
                continue; // Already fired under a previous hold of this (or an earlier) snapshot.
            }

            m_lastTriggerSequence = trigger.Sequence;

            var patch = FindPatch(id: trigger.PatchId);

            if (patch < 0) {
                DroppedTriggerCount++;

                continue;
            }

            _ = m_synth.Trigger(patch: in m_patches[patch], seed: trigger.Seed, gainQ16: trigger.GainQ16, emitterId: trigger.EmitterId);
        }
    }

    private void PullSource(in WorldAudioSourceKey key, int frames) {
        var slot = FindSource(key: in key);

        if ((slot < 0) || m_sourcePulled[slot]) {
            return; // Unbound (silence) or already pulled this block (the single-pull contract).
        }

        m_sourcePulled[slot] = true;
        m_sourcePulledFrames[slot] = ((m_sources[slot] is { } source)
            ? source.Pull(interleavedStereo: m_sourceScratch[slot].AsSpan(), frames: frames)
            : 0);
    }

    private int FindSource(in WorldAudioSourceKey key) {
        for (var i = 0; (i < m_sourceCount); i++) {
            if (m_sourceKeys[i] == key) {
                return i;
            }
        }

        return -1;
    }

    private int FindPatch(string id) {
        for (var i = 0; (i < m_patchCount); i++) {
            if (string.Equals(a: m_patchIds[i], b: id, comparisonType: StringComparison.Ordinal)) {
                return i;
            }
        }

        return -1;
    }

    private int FindRow(int id) {
        for (var i = 0; (i < m_rowCount); i++) {
            if (m_rowIds[i] == id) {
                return i;
            }
        }

        return -1;
    }

    // Finds or creates the ramp row for an emitter id (a new row enters from silence) and marks it live.
    private int TouchRow(int id) {
        var row = FindRow(id: id);

        if (row < 0) {
            row = m_rowCount++;
            m_rowIds[row] = id;
            m_rowPreviousLeft[row] = 0;
            m_rowPreviousRight[row] = 0;
        }

        m_rowSeen[row] = true;

        return row;
    }

    // Drops rows whose emitter left the table (compact in place; ids re-entering later ramp from silence).
    private void EvictStaleRows() {
        var write = 0;

        for (var read = 0; (read < m_rowCount); read++) {
            if (!m_rowSeen[read]) {
                continue;
            }

            if (write != read) {
                m_rowIds[write] = m_rowIds[read];
                m_rowPreviousLeft[write] = m_rowPreviousLeft[read];
                m_rowPreviousRight[write] = m_rowPreviousRight[read];
                m_rowSeen[write] = true;
            }

            write++;
        }

        m_rowCount = write;
    }
}
