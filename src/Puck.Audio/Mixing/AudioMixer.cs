using Puck.Abstractions.Machines;
using Puck.Hosting;
using Puck.Maths;

namespace Puck.Audio.Mixing;

/// <summary>
/// The seam every non-synth signal reaches the mixer through: pull up to <c>frames</c> stereo frames
/// for the current block. The offline proof binds a synchronous headless core to it; the tune host binds
/// acquire/release hosting; the live device pump binds the live machine worker — <see cref="AudioMixer.MixBlock"/> never
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
/// drain per block — sharing rows tap the mixer's scratch, never re-drain. The ring's occupancy is the
/// watermark — a drain that comes up short is an underrun and the mixer renders the shortfall silent.</summary>
/// <param name="machine">The machine to drain.</param>
public sealed class MachineBlockSource(IAudioMachine machine) : IAudioBlockSource {
    private readonly IAudioMachine m_machine = machine;

    /// <inheritdoc/>
    public int Pull(Span<short> interleavedStereo, int frames) =>
        (m_machine.ReadSamples(destination: interleavedStereo[..(frames * 2)]) / 2);
}
/// <summary>
/// The world audio mixer core: <see cref="MixBlock"/> is a synchronous pure function owning no
/// thread — the future device pump and the offline hash proof are two drivers of the same code. Fixed-point end to
/// end: s16 samples × Q16 composite gains → int32 accumulate → the deterministic polynomial soft-clip → s16.
/// <para>Per block: each emitter's target coefficients derive from the snapshot (finite-support authored
/// attenuation, and the zero of its support is the cull;
/// equal-power pan from listener-relative azimuth via one <see cref="FixedQ4816.SinCos"/> per point emitter; beds
/// center-pan with a presence envelope whose slew <see cref="AudioEmitter.FadeFrames"/> bounds), then the
/// live coefficients ramp linearly from the previous block's across every frame — the zipper-noise killer.
/// Ramp state is keyed by emitter id; a new id ramps in from silence, a departed id drops its state. Each distinct
/// source is pulled once into per-source scratch and every feed taps it (left | right | mix).</para>
/// <para>The soft-clip is the smooth-knee cubic <c>y = H + G·(1 − (1 − t)³)</c>: bit-transparent up to
/// <c>H = 24575</c> (0.75 FS), then the knee <c>t = (|s| − H)/W</c> over width <c>W = 3G = 24576</c> saturates
/// into the ceiling <c>H + G = 32767</c> at <c>|s| = 49151</c> (1.5 FS) with matched value and slope at both ends
/// (C¹, monotone), hard-limited beyond. Integer form: <c>y = 32767 − ⌊d³ / (27·2²⁶)⌋</c> with
/// <c>d = 49151 − |s|</c>. Never a libm function — the polynomial is the PCM hash's determinism contract.</para>
/// Zero steady-state allocation: every scratch and table is preallocated at construction.
/// </summary>
public sealed class AudioMixer {
    private const int CenterPanQ16 = 46341;
    private const long ClipKneeDivisor = (27L << 26);
    // Soft-clip constants: knee start H, knee width W = 3G, ceiling H + G = 32767; divisor 27·2^26 = G/W³ inverted.
    private const int ClipKneeStart = 24575;
    private const int ClipLimit = 49151;
    // Below this local-plane raw magnitude (~0.01 units) azimuth is meaningless; pan snaps to center.
    private const long PanEpsilonRaw = 655L;
    // Pan constants: π/4 in Q16 raw (the equal-power quarter arc), and cos(π/4) for the bed's center pan.
    private const long QuarterPiRawQ16 = 51472L;

    /// <summary>The largest block <see cref="MixBlock"/> renders — the device pump's own real-time quantum ceiling,
    /// unrelated to any simulation rate. A sim step's frame total is not bounded by this: at 90 Hz a step is ~533⅓
    /// frames (spans two blocks), at 45 Hz ~1067 (spans five), at 30 Hz 1600 (spans seven) — a caller driving audio
    /// in lockstep with the simulation renders one step across as many <see cref="MixBlock"/> calls as its frame
    /// total needs, never assuming a step is one block or a block is one step.</summary>
    public const int MaxBlockFrames = 256;
    /// <summary>The registered-patch capacity.</summary>
    public const int MaxPatches = 32;
    /// <summary>The registered-source capacity (each slot preallocates one stereo scratch).</summary>
    public const int MaxSources = 16;
    /// <summary>The mixer rate: device-native, 48000 Hz. Single-sourced from <see cref="MachineAudioRate.SampleRate"/> —
    /// the rate every booted machine synthesizes at — so a machine host and this mixer, on opposite sides of the
    /// presentation firewall, can never disagree on it. Not assumed to be a multiple of the
    /// world's simulation rate: 90 Hz and 45 Hz (both required Steam Deck OLED refresh rates) divide the 50400
    /// engine-tick base cleanly but not 48000. <see cref="FramesPerStep"/> and <see cref="AdvanceStepFrames"/> are
    /// the two ways of asking how a sim step maps to audio frames without assuming that division is exact.</summary>
    public const int SampleRate = MachineAudioRate.SampleRate;

    /// <summary>The frames in one sim step at the engine's default simulation rate (240 Hz — exact here, since
    /// 48000/240 divides evenly). A host that authors its own simulation rate should prefer
    /// <see cref="FramesPerStep"/> or <see cref="AdvanceStepFrames"/>, parameterized by that rate, over this fixed
    /// value.</summary>
    public static readonly int FramesPerSimStep = FramesPerStep(simulationRateHz: 240U);

    private ulong m_lastTriggerSequence;
    private int m_patchCount;
    private int m_rowCount;
    private int m_sourceCount;

    /// <summary>Returns the attenuation, Q16, a point emitter's curve applies at exactly half its finite-support
    /// radius — the fixed reference distance <c>world.status</c>'s half-radius-gain echo reports.</summary>
    /// <param name="curve">The attenuation law.</param>
    /// <returns>The attenuation at half radius, Q16.</returns>
    public static int HalfRadiusAttenuationQ16(AudioAttenuationCurve curve) {
        const long HalfDistanceSquaredQ16 = 16384;
        const long UnitRadiusQ16 = 65536;

        if (curve == AudioAttenuationCurve.Linear) {
            LinearAttenuation(
                attenuationQ16: out var linear,
                d2Q16: HalfDistanceSquaredQ16,
                maxRaw: UnitRadiusQ16,
                minRaw: 0
            );

            return linear;
        }

        SmoothstepAttenuation(
            attenuationQ16: out var smoothstep,
            d2Q16: HalfDistanceSquaredQ16,
            max2Q16: UnitRadiusQ16,
            min2Q16: 0
        );

        return smoothstep;
    }

    private void AccumulateEmitter(
        in AudioEmitter emitter,
        int frames,
        Span<int> left,
        int previousLeft,
        int previousRight,
        Span<int> right,
        int targetLeft,
        int targetRight
    ) {
        var isSynth = (emitter.Source.Kind == AudioSourceKind.Synth);

        // Synth voices advance even while inaudible — time flows for a culled creature; external sources simply
        // are not tapped, so a fully-silent emitter is BIT-IDENTICAL to an absent one (the cull contract).
        var silent = ((previousLeft | previousRight | targetLeft | targetRight) == 0);

        if (isSynth) {
            var scratch = m_synthScratch.AsSpan(
                length: frames,
                start: 0
            );

            scratch.Clear();
            m_synth.RenderBound(
                emitterId: emitter.Id,
                accumulator: scratch
            );

            if (silent) {
                return;
            }

            AccumulateMono(
                frames: frames,
                left: left,
                previousLeft: previousLeft,
                previousRight: previousRight,
                right: right,
                source: scratch,
                targetLeft: targetLeft,
                targetRight: targetRight
            );

            return;
        }

        if (
            silent ||
            (emitter.Source.Kind == AudioSourceKind.None)
        ) {
            return;
        }

        var slot = FindSource(key: emitter.Source);

        if (
            (slot < 0) ||
            (m_sources[slot] is null)
        ) {
            // Unbound source: honest silence (speaker.state echoes the fault).
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
        AudioChannel channel,
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
                AudioChannel.Left => source[(2 * n)],
                AudioChannel.Right => source[((2 * n) + 1)],
                _ => ((source[(2 * n)] + source[((2 * n) + 1)]) / 2),
            });

            left[n] += ((int)((sample * (currentLeft >> 16)) >> 16));
            right[n] += ((int)((sample * (currentRight >> 16)) >> 16));
        }
    }
    private static void ComputePan(in AudioListener listener, long dxRaw, long dzRaw, out int panLeftQ16, out int panRightQ16) {
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
        var direction = new FixedComplex(
            Real: local.X,
            Imaginary: local.Y
        ).Normalize();
        var p = Math.Clamp(
            value: direction.Real.Value,
            min: -65536L,
            max: 65536L
        );

        // Equal-power: φ = (p + 1)·π/4 ∈ [0, π/2]; gL = cos φ, gR = sin φ — ONE SinCos per emitter.
        var phi = (((p + 65536L) * QuarterPiRawQ16) >> 16);

        var (sin, cos) = FixedQ4816.SinCos(angle: FixedQ4816.FromRawBits(value: phi));

        panLeftQ16 = ((int)Math.Clamp(
            value: cos.Value,
            min: 0L,
            max: 65536L
        ));
        panRightQ16 = ((int)Math.Clamp(
            value: sin.Value,
            min: 0L,
            max: 65536L
        ));
    }
    private void ComputeTargets(in AudioListener listener, in AudioEmitter emitter, int frames, out int left, out int right) {
        var dxRaw = (emitter.Position.X.Value - listener.Position.X.Value);
        var dyRaw = (emitter.Position.Y.Value - listener.Position.Y.Value);
        var dzRaw = (emitter.Position.Z.Value - listener.Position.Z.Value);

        // Distance is 3D; azimuth ignores elevation. Every square stays exact in Int128, saturated to long — a raw Q16
        // long product overflows above a ~46 340-unit radius, so the radius squares saturate the same
        // way the distance square does rather than wrapping into a spurious attenuation.
        var d2Wide = ((((((Int128)dxRaw) * dxRaw) + (((Int128)dyRaw) * dyRaw)) + (((Int128)dzRaw) * dzRaw)) >> 16);
        var d2Q16 = ((d2Wide > long.MaxValue)
            ? long.MaxValue
            : ((long)d2Wide)
        );
        var min2Q16 = SaturatingSquareQ16(valueRaw: emitter.MinRadius.Value);
        var max2Q16 = SaturatingSquareQ16(valueRaw: emitter.MaxRadius.Value);

        int attenuationQ16;

        if (emitter.Curve == AudioAttenuationCurve.Linear) {
            LinearAttenuation(
                d2Q16: d2Q16,
                minRaw: emitter.MinRadius.Value,
                maxRaw: emitter.MaxRadius.Value,
                attenuationQ16: out attenuationQ16
            );
        } else {
            SmoothstepAttenuation(
                attenuationQ16: out attenuationQ16,
                d2Q16: d2Q16,
                max2Q16: max2Q16,
                min2Q16: min2Q16
            );
        }

        if (attenuationQ16 == 0) {
            left = 0;
            right = 0;

            return;
        }

        var gain = ((int)((((((long)emitter.GainQ16) * attenuationQ16) >> 16) * MasterGainQ16) >> 16));

        if (emitter.Kind == AudioEmitterKind.Bed) {
            // Beds are presence, not position: center pan; FadeFrames bounds the slew below.
            left = ((int)((((long)gain) * CenterPanQ16) >> 16));
            right = left;
        } else {
            ComputePan(
                listener: in listener,
                dxRaw: dxRaw,
                dzRaw: dzRaw,
                out var panLeftQ16,
                out var panRightQ16
            );
            left = ((int)((((long)gain) * panLeftQ16) >> 16));
            right = ((int)((((long)gain) * panRightQ16) >> 16));
        }

        if (
            (emitter.Kind == AudioEmitterKind.Bed) &&
            (emitter.FadeFrames > 0)
        ) {
            // Presence slew bound: coefficients may move at most full-scale-per-FadeFrames each block.
            var row = FindRow(id: emitter.Id);
            var maxStep = ((int)Math.Max(
                val1: 1L,
                val2: ((65536L * frames) / emitter.FadeFrames)
            ));

            if (row >= 0) {
                left = Math.Clamp(
                    value: left,
                    min: (m_rowPreviousLeft[row] - maxStep),
                    max: (m_rowPreviousLeft[row] + maxStep)
                );
                right = Math.Clamp(
                    value: right,
                    min: (m_rowPreviousRight[row] - maxStep),
                    max: (m_rowPreviousRight[row] + maxStep)
                );
            } else {
                left = Math.Clamp(
                    max: maxStep,
                    min: -maxStep,
                    value: left
                );
                right = Math.Clamp(
                    max: maxStep,
                    min: -maxStep,
                    value: right
                );
            }
        }
    }
    private void ConsumeTriggers(AudioSnapshot snapshot) {
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

            _ = m_synth.Trigger(
                patch: in m_patches[patch],
                seed: trigger.Seed,
                gainQ16: trigger.GainQ16,
                emitterId: trigger.EmitterId
            );
        }
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
    private int FindPatch(string id) {
        for (var i = 0; (i < m_patchCount); i++) {
            if (string.Equals(
                a: m_patchIds[i],
                b: id,
                comparisonType: StringComparison.Ordinal
            )) {
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
    private int FindSource(in AudioSourceKey key) {
        for (var i = 0; (i < m_sourceCount); i++) {
            if (m_sourceKeys[i] == key) {
                return i;
            }
        }

        return -1;
    }
    private static void LinearAttenuation(long d2Q16, long minRaw, long maxRaw, out int attenuationQ16) {
        var distanceRaw = FixedQ4816.Sqrt(value: FixedQ4816.FromRawBits(value: d2Q16)).Value;

        if (distanceRaw >= maxRaw) {
            attenuationQ16 = 0;

            return;
        }

        if (distanceRaw <= minRaw) {
            attenuationQ16 = 65536;

            return;
        }

        attenuationQ16 = ((int)((((Int128)(maxRaw - distanceRaw)) << 16) / (maxRaw - minRaw)));
    }
    private void PullSource(in AudioSourceKey key, int frames) {
        var slot = FindSource(key: in key);

        if (
            (slot < 0) ||
            m_sourcePulled[slot]
        ) {
            return; // Unbound (silence) or already pulled this block (the single-pull contract).
        }

        m_sourcePulled[slot] = true;
        m_sourcePulledFrames[slot] = ((m_sources[slot] is { } source)
            ? source.Pull(
                interleavedStereo: m_sourceScratch[slot].AsSpan(),
                frames: frames
            )
            : 0
        );
    }
    // One Q16 raw value squared back into a Q16 quantity ((r·2^16)^2 >> 16 = r^2·2^16), computed in Int128 and
    // saturated to long — the overflow-safe form of `raw * raw >> 16`.
    private static long SaturatingSquareQ16(long valueRaw) {
        var wide = ((((Int128)valueRaw) * valueRaw) >> 16);

        return ((wide > long.MaxValue)
            ? long.MaxValue
            : ((long)wide)
        );
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

        // Squared-smoothstep: smoothstep over the SQUARED-distance ratio — finite support, no sqrt.
        var t = (((max2Q16 - d2Q16) << 16) / (max2Q16 - min2Q16));

        attenuationQ16 = ((int)((((t * t) >> 16) * ((3L << 16) - (2L * t))) >> 16));
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

    /// <summary>Advances <paramref name="accumulator"/> by one sim step of <paramref name="stepTicks"/> engine ticks
    /// and returns the exact whole number of audio frames that step renders. At 90 Hz (560 ticks/step, 533⅓
    /// frames/step) the sequence is 533, 533, 534, 533, 533, 534, … — never a fixed 533 or 534 every step, and never
    /// drifting off the true mean, because the part of the division too small to represent one call is carried
    /// exactly into the next (<see cref="FixedRateAccumulator"/>'s own contract). At 240 Hz (210 ticks/step) the
    /// division is exact — 200 every step, no remainder ever accrues — so a caller migrating from the retired fixed
    /// <c>FramesPerSimStep = 200</c> literal to this method at today's rate sees byte-identical output.</summary>
    /// <param name="accumulator">The accumulator from <see cref="CreateStepAccumulator"/> (or restored via
    /// <see cref="FixedRateAccumulator.FromRemainder"/>), advanced in place. Authoritative state: hold it in a
    /// mutable field, array slot, or <see langword="ref"/> local — never a <see langword="readonly"/> field or
    /// collection indexer (see <see cref="FixedRateAccumulator"/>'s own remarks) — and persist its
    /// <see cref="FixedRateAccumulator.Remainder"/> anywhere the caller's own state must reproduce this exactly.</param>
    /// <param name="stepTicks">The engine ticks the step covers (<c>Puck.Hosting.EngineTicks.PerRate</c> at the
    /// world's simulation rate).</param>
    /// <returns>The non-negative frame count this step renders.</returns>
    public static int AdvanceStepFrames(ref FixedRateAccumulator accumulator, ulong stepTicks) {
        // FromRawBits/.Value reinterpret the accumulator's raw storage as a plain frame COUNT rather than a scaled
        // Q16 real number — the exact-integer-division-with-carried-remainder machine underneath is identical either
        // way; only the unit the raw bits name changes, and both inputs here are already exact integers, so nothing
        // about that reinterpretation loses precision.
        var delta = accumulator.Integrate(
            ratePerSecond: FixedQ4816.FromRawBits(value: SampleRate),
            elapsedTicks: stepTicks
        );

        return checked((int)delta.Value);
    }
    /// <summary>Binds a fresh exact-remainder accumulator for advancing audio frames in lockstep with sim steps —
    /// the same <see cref="FixedRateAccumulator"/> technique the simulation's motion integration already uses,
    /// reused here rather than a second hand-rolled remainder (see <see cref="AdvanceStepFrames"/>).
    /// Works at any simulation rate that divides <see cref="Puck.Hosting.EngineTicks.PerSecond"/>, including ones
    /// like 90 and 45 Hz that do not divide <see cref="SampleRate"/>.</summary>
    /// <returns>A fresh accumulator bound to the engine tick base, ready for <see cref="AdvanceStepFrames"/>.</returns>
    public static FixedRateAccumulator CreateStepAccumulator() =>
        new(ticksPerSecond: ((long)EngineTicks.PerSecond));
    /// <summary>Computes the ceiling of one sim step's audio-frame count at <paramref name="simulationRateHz"/> —
    /// safe as a one-off slack margin (a cue-life padding, for instance) precisely because it never understates. No single per-step frame count is exact for every step when
    /// <see cref="SampleRate"/> is not a multiple of <paramref name="simulationRateHz"/> (90 Hz: 533⅓ frames/step);
    /// a caller rendering many consecutive steps without long-run drift uses <see cref="AdvanceStepFrames"/> instead.</summary>
    /// <param name="simulationRateHz">The simulation rate, in hertz (any positive value; need not divide
    /// <see cref="SampleRate"/>).</param>
    /// <returns>⌈<see cref="SampleRate"/> / <paramref name="simulationRateHz"/>⌉ — never smaller than any individual
    /// step actually renders.</returns>
    public static int FramesPerStep(uint simulationRateHz) {
        ArgumentOutOfRangeException.ThrowIfZero(value: simulationRateHz);

        return checked((int)(((SampleRate + simulationRateHz) - 1) / simulationRateHz));
    }
    /// <summary>Mixes one block from the given snapshot into interleaved stereo s16 — synchronous, pure, owning
    /// no thread. The span length fixes the block size (<c>2·frames</c> samples, frames ≤
    /// <see cref="MaxBlockFrames"/>).</summary>
    /// <param name="snapshot">The current published snapshot (held, not interpolated).</param>
    /// <param name="stereoInterleaved">The output block; fully overwritten.</param>
    public void MixBlock(AudioSnapshot snapshot, Span<short> stereoInterleaved) {
        ArgumentNullException.ThrowIfNull(argument: snapshot);

        var frames = (stereoInterleaved.Length / 2);

        ArgumentOutOfRangeException.ThrowIfGreaterThan(
            value: frames,
            other: MaxBlockFrames
        );
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value: frames);
        ConsumeTriggers(snapshot: snapshot);
        m_synth.ReleaseUnbound(emitters: snapshot.Emitters);

        var emitters = snapshot.Emitters;

        ArgumentOutOfRangeException.ThrowIfGreaterThan(
            value: emitters.Length,
            other: AudioSnapshot.DefaultMaxEmitters
        );

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
        Span<int> targetLeft = stackalloc int[AudioSnapshot.DefaultMaxEmitters];
        Span<int> targetRight = stackalloc int[AudioSnapshot.DefaultMaxEmitters];
        Span<int> rowOf = stackalloc int[AudioSnapshot.DefaultMaxEmitters];

        Array.Clear(array: m_sourcePulled);

        for (var e = 0; (e < emitters.Length); e++) {
            ref readonly var emitter = ref emitters[e];

            ComputeTargets(
                listener: snapshot.Listener,
                emitter: in emitter,
                frames: frames,
                left: out var left,
                right: out var right
            );
            targetLeft[e] = left;
            targetRight[e] = right;
            rowOf[e] = TouchRow(id: emitter.Id);

            // Pull demand: an external source feeds this block iff some emitter tapping it is audible now or was
            // audible last block (the ramp-out still needs samples).
            var row = rowOf[e];
            var audible = (((left | right) != 0) || ((m_rowPreviousLeft[row] | m_rowPreviousRight[row]) != 0));

            if (
                audible &&
                (emitter.Source.Kind is AudioSourceKind.Machine or AudioSourceKind.Tune)
            ) {
                PullSource(
                    key: emitter.Source,
                    frames: frames
                );
            }
        }

        var accumulateLeft = m_accumulateLeft.AsSpan(
            length: frames,
            start: 0
        );
        var accumulateRight = m_accumulateRight.AsSpan(
            length: frames,
            start: 0
        );

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
            peak = Math.Max(
                val1: peak,
                val2: Math.Max(
                    val1: Math.Abs(value: ((int)left)),
                    val2: Math.Abs(value: ((int)right))
                )
            );
        }

        OutputPeak = peak;
    }
    /// <summary>Registers (or replaces) a synth patch under an id. A full table is CONTAINED loss: the
    /// overflow row is dropped and <see cref="DroppedRegistrationCount"/> increments — never a throw, so a derived plan
    /// that outgrows the registry renders its overflow silent instead of crashing the reconcile.</summary>
    /// <param name="id">The patch id trigger events reference.</param>
    /// <param name="patch">The runtime parameter block.</param>
    public void RegisterPatch(string id, in VoicePatch patch) {
        ArgumentException.ThrowIfNullOrEmpty(argument: id);

        for (var i = 0; (i < m_patchCount); i++) {
            if (string.Equals(
                a: m_patchIds[i],
                b: id,
                comparisonType: StringComparison.Ordinal
            )) {
                m_patches[i] = patch;

                return;
            }
        }

        if (m_patchCount >= MaxPatches) {
            DroppedRegistrationCount++;
            Console.Error.WriteLine(value: $"[world.audio: patch table full ({MaxPatches}); '{id}' dropped — its voice renders silent]");

            return;
        }

        m_patchIds[m_patchCount] = id;
        m_patches[m_patchCount] = patch;
        m_patchCount++;
    }
    /// <summary>Unbinds a source identity; emitters referencing it render silence until rebound.</summary>
    /// <param name="key">The source identity to unbind.</param>
    public void RemoveSource(in AudioSourceKey key) {
        var slot = FindSource(key: in key);

        if (slot >= 0) {
            m_sources[slot] = null;
        }
    }
    /// <summary>Reclaims patch slots whose id left the live derived plan — the compose-boundary reclaim
    /// that keeps the bounded table from filling with the carcasses of churned sound emitters across reconciles. Compacts
    /// the table in place, preserving the surviving rows.</summary>
    /// <param name="live">The patch ids the current derived plan registers; every other slot is retired.</param>
    public void RetirePatches(IReadOnlySet<string> live) {
        ArgumentNullException.ThrowIfNull(argument: live);

        var write = 0;

        for (var read = 0; (read < m_patchCount); read++) {
            if (!live.Contains(item: m_patchIds[read])) {
                continue;
            }

            if (write != read) {
                m_patchIds[write] = m_patchIds[read];
                m_patches[write] = m_patches[read];
            }

            write++;
        }

        m_patchCount = write;
    }
    /// <summary>Binds (or rebinds) a block source to a source identity. A full table is CONTAINED loss:
    /// the bind is dropped and <see cref="DroppedRegistrationCount"/> increments — never a throw, so an overfull source
    /// registry renders the excess emitters silent instead of crashing the reconcile.</summary>
    /// <param name="key">The source identity emitters reference.</param>
    /// <param name="source">The pull seam.</param>
    public void SetSource(in AudioSourceKey key, IAudioBlockSource source) {
        ArgumentNullException.ThrowIfNull(argument: source);

        var slot = FindSource(key: in key);

        if (slot < 0) {
            if (m_sourceCount >= MaxSources) {
                DroppedRegistrationCount++;
                Console.Error.WriteLine(value: $"[world.audio: source table full ({MaxSources}); {key.Kind} '{(key.Id ?? key.Slot.ToString())}' dropped — its emitters render silent]");

                return;
            }

            slot = m_sourceCount++;
            m_sourceKeys[slot] = key;
        }

        m_sources[slot] = source;
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

            shaped = ((int)(32767L - (((d * d) * d) / ClipKneeDivisor)));
        }

        return ((short)((sample < 0)
            ? -shaped
            : shaped));
    }

    private readonly VoiceSynth m_synth = new();
    private readonly int[] m_accumulateLeft = new int[MaxBlockFrames];
    private readonly int[] m_accumulateRight = new int[MaxBlockFrames];
    private readonly int[] m_synthScratch = new int[MaxBlockFrames];
    private readonly AudioSourceKey[] m_sourceKeys = new AudioSourceKey[MaxSources];
    private readonly IAudioBlockSource?[] m_sources = new IAudioBlockSource?[MaxSources];
    private readonly short[][] m_sourceScratch = new short[MaxSources][];
    private readonly int[] m_sourcePulledFrames = new int[MaxSources];
    private readonly bool[] m_sourcePulled = new bool[MaxSources];
    private readonly string[] m_patchIds = new string[MaxPatches];
    private readonly VoicePatch[] m_patches = new VoicePatch[MaxPatches];
    // Coefficient-ramp rows, keyed by emitter id, rebuilt against each block's snapshot.
    private readonly int[] m_rowIds = new int[AudioSnapshot.DefaultMaxEmitters];
    private readonly int[] m_rowPreviousLeft = new int[AudioSnapshot.DefaultMaxEmitters];
    private readonly int[] m_rowPreviousRight = new int[AudioSnapshot.DefaultMaxEmitters];
    private readonly bool[] m_rowSeen = new bool[AudioSnapshot.DefaultMaxEmitters];

    /// <summary>Gets or sets the master gain, Q16. Defaults to unity; the host drives it from its authored master
    /// gain and its live volume lever.</summary>
    public int MasterGainQ16 { get; set; } = 65536;

    /// <summary>Initializes the mixer with every scratch preallocated.</summary>
    public AudioMixer() {
        for (var i = 0; (i < MaxSources); i++) {
            m_sourceScratch[i] = new short[(MaxBlockFrames * 2)];
        }
    }

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
    /// <summary>Gets the count of patch/source registrations refused because the table was full — honest, CONTAINED
    /// loss (never a throw): a derived audio plan that overfills the registry drops the overflow rows and renders them
    /// silent rather than crashing the reconcile. The compose boundary warns before this fires; the count is the
    /// durable proof it happened.</summary>
    public int DroppedRegistrationCount { get; private set; }
    /// <summary>Gets the count of triggers refused because their patch id was unregistered — honest loss, echoed
    /// by <c>speaker.state</c>.</summary>
    public int DroppedTriggerCount { get; private set; }
    /// <summary>Gets the running peak |output sample| since construction — the <c>audio.state</c> meter. Monotone
    /// by design: a nonzero value is durable proof the mix has produced signal (the live smoke's assertion), and a
    /// zero proves every block so far was silent.</summary>
    public int OutputPeak { get; private set; }
    /// <summary>Gets the synth (proof introspection; triggers route through snapshots).</summary>
    public VoiceSynth Synth => m_synth;
}
