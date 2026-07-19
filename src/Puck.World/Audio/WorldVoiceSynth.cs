using Puck.Authoring;
using Puck.Maths;

namespace Puck.World.Audio;

/// <summary>The per-voice state-variable filter response. <see cref="Bypass"/> is the neutral default every
/// current <c>puck.synth.v1</c> patch converts to — patch-side filter fields are AP2's liftable extension; the
/// runtime block already carries them so the DSP lands proven.</summary>
public enum WorldVoiceFilterMode {
    /// <summary>No filtering — the voice output is the oscillator/envelope product unchanged.</summary>
    Bypass,
    /// <summary>The SVF low-pass tap.</summary>
    LowPass,
    /// <summary>The SVF band-pass tap.</summary>
    BandPass,
    /// <summary>The SVF high-pass tap.</summary>
    HighPass,
}

/// <summary>
/// The flat runtime parameter block one trigger voices from — a post-<see cref="SynthPatchCanonicalizer.Normalize"/>
/// <see cref="SynthPatchDocument"/> converted ONCE at registration (never per sample): duty is pre-scaled to a Q32
/// phase threshold with its DC term precomputed, and all frame/millihertz fields ride verbatim (they are already
/// runtime units by the document's design). Filter parameters have no document fields yet — <see cref="FromDocument"/>
/// sets <see cref="WorldVoiceFilterMode.Bypass"/>; AP2 lifts them into <c>puck.synth.v1</c> when wanted.
/// </summary>
/// <param name="Oscillator">The oscillator kind.</param>
/// <param name="DutyThresholdQ32">The pulse duty as a Q32 phase threshold (phase below = high half).</param>
/// <param name="DutyDcOffsetQ16">The pulse wave's DC term, Q16, subtracted so off-center duties stay zero-mean.</param>
/// <param name="Polynomial">The noise character byte (0 = white passthrough; 1..127 darken, 128..255 brighten).</param>
/// <param name="AttackFrames">Envelope attack, frames.</param>
/// <param name="DecayFrames">Envelope decay, frames.</param>
/// <param name="SustainQ16">Envelope sustain level, Q16.</param>
/// <param name="ReleaseFrames">Envelope release, frames.</param>
/// <param name="PitchMillihertz">Base pitch, millihertz.</param>
/// <param name="SweepMillihertzPerFrame">Linear pitch sweep, millihertz per frame (0 = none).</param>
/// <param name="VibratoDepthMillihertz">Vibrato peak deviation, millihertz (0 = none).</param>
/// <param name="VibratoRateMillihertz">Vibrato rate, millihertz.</param>
/// <param name="DurationFrames">Total voice length in frames; 0 = loop until stolen or released.</param>
/// <param name="FilterMode">The SVF response tap.</param>
/// <param name="FilterCoefficientQ16">The SVF frequency coefficient <c>f ≈ 2·sin(π·fc/fs)</c>, Q16; stable below
/// ~fs/6.</param>
/// <param name="FilterDampingQ16">The SVF damping <c>q = 1/Q</c>, Q16 (65536 = critically plain; smaller rings).</param>
public readonly record struct WorldVoicePatch(
    SynthOscillator Oscillator,
    uint DutyThresholdQ32,
    int DutyDcOffsetQ16,
    int Polynomial,
    int AttackFrames,
    int DecayFrames,
    int SustainQ16,
    int ReleaseFrames,
    int PitchMillihertz,
    int SweepMillihertzPerFrame,
    int VibratoDepthMillihertz,
    int VibratoRateMillihertz,
    int DurationFrames,
    WorldVoiceFilterMode FilterMode,
    int FilterCoefficientQ16,
    int FilterDampingQ16
) {
    /// <summary>Converts a normalized document (see <see cref="SynthPatchCanonicalizer.Normalize"/> — every
    /// optional member defaulted, cross-oscillator fields cleared) into the runtime block.</summary>
    /// <param name="document">The normalized document.</param>
    /// <returns>The runtime parameter block.</returns>
    public static WorldVoicePatch FromDocument(SynthPatchDocument document) {
        ArgumentNullException.ThrowIfNull(document);

        var dutyThousandths = (document.DutyThousandths ?? 500);
        var sustainThousandths = (document.SustainThousandths ?? 1000);

        return new(
            Oscillator: (document.Oscillator ?? SynthOscillator.Pulse),
            DutyThresholdQ32: ((uint)((((ulong)dutyThousandths) << 32) / 1000UL)),
            DutyDcOffsetQ16: ((int)((((2L * dutyThousandths) - 1000L) * 65536L) / 1000L)),
            Polynomial: (document.Polynomial ?? 0),
            AttackFrames: (document.AttackFrames ?? 0),
            DecayFrames: (document.DecayFrames ?? 0),
            SustainQ16: ((int)((sustainThousandths * 65536L) / 1000L)),
            ReleaseFrames: (document.ReleaseFrames ?? 0),
            PitchMillihertz: document.PitchMillihertz,
            SweepMillihertzPerFrame: (document.SweepMillihertzPerFrame ?? 0),
            VibratoDepthMillihertz: (document.VibratoDepthMillihertz ?? 0),
            VibratoRateMillihertz: (document.VibratoRateMillihertz ?? 0),
            DurationFrames: (document.DurationFrames ?? 0),
            FilterMode: WorldVoiceFilterMode.Bypass,
            FilterCoefficientQ16: 0,
            FilterDampingQ16: 65536
        );
    }
}

/// <summary>
/// The world's deterministic voice synth (plan A9): 32 voices in a fixed struct array, zero steady-state
/// allocation, fixed-point end to end. Sine is a <see cref="FixedComplex"/> rotor (one complex multiply per
/// sample, renormalized at control ticks); pulse/saw/triangle are Q32 phase accumulators; noise is a
/// <see cref="Pcg32XshRr"/> stream created from the trigger seed plus a one-pole tilt keyed by the patch's
/// polynomial byte. The envelope runs in sample units; pitch (base + linear sweep + triangle-LFO vibrato, the
/// hardware-kin choice) is re-evaluated every <see cref="ControlIntervalFrames"/> samples. Each voice ends in one
/// Chamberlin state-variable filter — the arc's one new DSP element: <c>low += f·band; high = x − low − q·band;
/// band += f·high</c> per sample, tap selected by mode, integer Q16 throughout.
/// The same pure <see cref="Render"/> executes on the audio thread and the offline proof.
/// Voice allocation: a free voice first; otherwise steal the QUIETEST (lowest current envelope level), ties
/// broken oldest-first — the policy that never robs a fresh attack to keep a dying tail.
/// </summary>
public sealed class WorldVoiceSynth {
    /// <summary>The fixed voice count.</summary>
    public const int VoiceCount = 32;
    /// <summary>The control-rate interval in samples: pitch/rotor updates and rotor renormalization run once per
    /// interval — vibrato and sweeps move at millihertz scales, so 64 samples (1.33 ms) is inaudibly coarse and
    /// keeps the per-sample path multiply-only.</summary>
    public const int ControlIntervalFrames = 64;
    /// <summary>The mixer rate every frame unit in <c>puck.synth.v1</c> is denominated in (plan A1).</summary>
    public const int SampleRate = 48_000;

    // 2π and the phase→radians bridge in Q16: rotor step angle = phaseIncrementQ32 · 2π >> 32.
    private const long TwoPiRawQ16 = 411775L;
    private const long PeakQ32 = (65536L << 16);

    private readonly Voice[] m_voices = new Voice[VoiceCount];
    private readonly int[] m_monoAccumulator = new int[WorldAudioMixer.MaxBlockFrames];
    private ulong m_nextTriggerOrdinal;

    private enum EnvelopePhase {
        Attack,
        Decay,
        Sustain,
        Release,
    }

    private struct Voice {
        public bool Active;
        public int EmitterId;
        public WorldVoicePatch Patch;
        public int GainQ16;
        public ulong TriggerOrdinal;
        public long TotalFrames;
        public EnvelopePhase Envelope;
        public long EnvelopeLevelQ32;
        public long EnvelopeStepQ32;
        public uint Phase;
        public uint PhaseIncrement;
        public FixedComplex Rotor;
        public FixedComplex RotorStep;
        public uint VibratoPhase;
        public uint VibratoIncrement;
        public Pcg32XshRr Noise;
        public int NoiseTiltState;
        public int FilterLow;
        public int FilterBand;
        public int ControlCountdown;
    }

    /// <summary>Gets the number of currently sounding voices.</summary>
    public int ActiveVoiceCount {
        get {
            var count = 0;

            for (var i = 0; (i < VoiceCount); i++) {
                if (m_voices[i].Active) {
                    count++;
                }
            }

            return count;
        }
    }

    /// <summary>Starts a voice. Allocation prefers a free slot, then steals the quietest (oldest on ties).</summary>
    /// <param name="patch">The runtime parameter block.</param>
    /// <param name="seed">The trigger seed — noise voices reproduce bit for bit from it.</param>
    /// <param name="gainQ16">The voice gain, Q16.</param>
    /// <param name="emitterId">The emitter the voice is bound to, or -1 for an unbound (proof-driven) voice.</param>
    /// <returns>The voice slot used.</returns>
    public int Trigger(in WorldVoicePatch patch, ulong seed, int gainQ16, int emitterId = -1) {
        var slot = AllocateVoice();
        ref var voice = ref m_voices[slot];

        voice = default;
        voice.Active = true;
        voice.EmitterId = emitterId;
        voice.Patch = patch;
        voice.GainQ16 = gainQ16;
        voice.TriggerOrdinal = m_nextTriggerOrdinal++;
        voice.Rotor = FixedComplex.MultiplicativeIdentity;
        // Start the triangle LFO at its zero crossing (quarter turn) so vibrato onsets at the base pitch.
        voice.VibratoPhase = (1U << 30);
        voice.VibratoIncrement = ((patch.VibratoDepthMillihertz > 0)
            ? ((uint)((((ulong)patch.VibratoRateMillihertz) << 32) / (1000UL * SampleRate)))
            : 0U);
        voice.Noise = Pcg32XshRr.Create(state: seed, stream: 1UL);

        if (patch.AttackFrames > 0) {
            voice.Envelope = EnvelopePhase.Attack;
            voice.EnvelopeLevelQ32 = 0L;
            voice.EnvelopeStepQ32 = CeilingStep(travelQ32: PeakQ32, frames: patch.AttackFrames);
        } else {
            EnterPostAttack(voice: ref voice);
        }

        UpdateControlRate(voice: ref voice);

        return slot;
    }

    /// <summary>Renders every active voice, summed and saturated, into a MONO span — the pure surface both the
    /// audio thread and the offline proof drive. Advances all voice state by <paramref name="frames"/>.</summary>
    /// <param name="destination">The mono s16 destination (at least <paramref name="frames"/> long).</param>
    /// <param name="frames">The frame count (at most <see cref="WorldAudioMixer.MaxBlockFrames"/>).</param>
    public void Render(Span<short> destination, int frames) {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(value: frames, other: WorldAudioMixer.MaxBlockFrames);

        var accumulator = m_monoAccumulator.AsSpan(start: 0, length: frames);

        accumulator.Clear();

        for (var i = 0; (i < VoiceCount); i++) {
            if (m_voices[i].Active) {
                RenderVoice(voice: ref m_voices[i], accumulator: accumulator);
            }
        }

        for (var n = 0; (n < frames); n++) {
            destination[n] = ((short)Math.Clamp(value: accumulator[n], min: -32767, max: 32767));
        }
    }

    /// <summary>Renders the voices bound to one emitter additively into an int accumulator (s16 domain, unsaturated)
    /// — the mixer's per-emitter tap. Advances only those voices.</summary>
    /// <param name="emitterId">The emitter binding to render.</param>
    /// <param name="accumulator">The pre-cleared mono accumulator.</param>
    internal void RenderBound(int emitterId, Span<int> accumulator) {
        for (var i = 0; (i < VoiceCount); i++) {
            if (m_voices[i].Active && (m_voices[i].EmitterId == emitterId)) {
                RenderVoice(voice: ref m_voices[i], accumulator: accumulator);
            }
        }
    }

    /// <summary>Frees every emitter-bound voice whose emitter is absent from <paramref name="emitters"/> — a row
    /// that left the table takes its voices with it (they would otherwise sound from nowhere forever).</summary>
    /// <param name="emitters">The current snapshot's emitter table.</param>
    internal void ReleaseUnbound(ReadOnlySpan<WorldAudioEmitter> emitters) {
        for (var i = 0; (i < VoiceCount); i++) {
            ref var voice = ref m_voices[i];

            if (!voice.Active || (voice.EmitterId < 0)) {
                continue;
            }

            var bound = false;

            for (var e = 0; (e < emitters.Length); e++) {
                if (emitters[e].Id == voice.EmitterId) {
                    bound = true;

                    break;
                }
            }

            if (!bound) {
                voice.Active = false;
            }
        }
    }

    private static void EnterPostAttack(ref Voice voice) {
        if (voice.Patch.DecayFrames > 0) {
            voice.Envelope = EnvelopePhase.Decay;
            voice.EnvelopeLevelQ32 = PeakQ32;
            voice.EnvelopeStepQ32 = CeilingStep(travelQ32: (PeakQ32 - (((long)voice.Patch.SustainQ16) << 16)), frames: voice.Patch.DecayFrames);
        } else {
            voice.Envelope = EnvelopePhase.Sustain;
            voice.EnvelopeLevelQ32 = (((long)voice.Patch.SustainQ16) << 16);
        }
    }

    // Ceiling division keeps every envelope phase within its DECLARED frame count — a truncated step would leave a
    // sub-step residue that overstays the phase by several samples.
    private static long CeilingStep(long travelQ32, int frames) =>
        (((travelQ32 + frames) - 1L) / frames);

    private static void EnterRelease(ref Voice voice) {
        if ((voice.Patch.ReleaseFrames > 0) && (voice.EnvelopeLevelQ32 > 0L)) {
            voice.Envelope = EnvelopePhase.Release;
            voice.EnvelopeStepQ32 = CeilingStep(travelQ32: voice.EnvelopeLevelQ32, frames: voice.Patch.ReleaseFrames);
        } else {
            voice.Active = false;
        }
    }

    // Advances the envelope by one sample; returns the level Q16 (0 with Active cleared once done).
    private static int StepEnvelope(ref Voice voice) {
        if ((voice.Patch.DurationFrames > 0) && (voice.TotalFrames >= voice.Patch.DurationFrames) && (voice.Envelope != EnvelopePhase.Release)) {
            EnterRelease(voice: ref voice);

            if (!voice.Active) {
                return 0;
            }
        }

        switch (voice.Envelope) {
            case EnvelopePhase.Attack:
                voice.EnvelopeLevelQ32 += voice.EnvelopeStepQ32;

                if (voice.EnvelopeLevelQ32 >= PeakQ32) {
                    EnterPostAttack(voice: ref voice);
                }

                break;
            case EnvelopePhase.Decay:
                voice.EnvelopeLevelQ32 -= voice.EnvelopeStepQ32;

                if (voice.EnvelopeLevelQ32 <= (((long)voice.Patch.SustainQ16) << 16)) {
                    voice.Envelope = EnvelopePhase.Sustain;
                    voice.EnvelopeLevelQ32 = (((long)voice.Patch.SustainQ16) << 16);
                }

                break;
            case EnvelopePhase.Release:
                voice.EnvelopeLevelQ32 -= voice.EnvelopeStepQ32;

                if (voice.EnvelopeLevelQ32 <= 0L) {
                    voice.EnvelopeLevelQ32 = 0L;
                    voice.Active = false;

                    return 0;
                }

                break;
            case EnvelopePhase.Sustain:
            default:
                break;
        }

        return ((int)(voice.EnvelopeLevelQ32 >> 16));
    }

    // Re-evaluates pitch (base + sweep + triangle vibrato) and rebuilds the phase increment + sine rotor step;
    // renormalizes the rotor against multiply drift. Runs once per control interval.
    private static void UpdateControlRate(ref Voice voice) {
        var pitch = ((long)voice.Patch.PitchMillihertz);

        if (voice.Patch.SweepMillihertzPerFrame != 0) {
            pitch += (voice.Patch.SweepMillihertzPerFrame * voice.TotalFrames);
        }

        if (voice.Patch.VibratoDepthMillihertz > 0) {
            // Triangle LFO from the Q32 phase: saw in [-65536, 65536), folded to the zero-mean triangle.
            var saw = (((long)(voice.VibratoPhase >> 15)) - 65536L);
            var triangle = (65536L - (2L * Math.Abs(value: saw)));

            pitch += ((voice.Patch.VibratoDepthMillihertz * triangle) >> 16);
        }

        pitch = Math.Clamp(value: pitch, min: 1L, max: SynthPatchDocument.MaxPitchMillihertz);
        voice.PhaseIncrement = ((uint)((((ulong)pitch) << 32) / (1000UL * SampleRate)));

        if (voice.Patch.Oscillator == SynthOscillator.Sine) {
            var stepAngleRaw = ((long)((((ulong)voice.PhaseIncrement) * ((ulong)TwoPiRawQ16)) >> 32));

            voice.RotorStep = FixedComplex.FromAngle(angle: FixedQ4816.FromRawBits(value: stepAngleRaw));
            voice.Rotor = voice.Rotor.Normalize();
        }

        voice.ControlCountdown = ControlIntervalFrames;
    }

    private static int OscillatorSample(ref Voice voice) {
        switch (voice.Patch.Oscillator) {
            case SynthOscillator.Sine:
                voice.Rotor *= voice.RotorStep;

                return ((int)voice.Rotor.Imaginary.Value);
            case SynthOscillator.Saw: {
                voice.Phase += voice.PhaseIncrement;

                return ((int)(((long)(voice.Phase >> 15)) - 65536L));
            }
            case SynthOscillator.Triangle: {
                voice.Phase += voice.PhaseIncrement;

                var saw = (((long)(voice.Phase >> 15)) - 65536L);

                return ((int)(65536L - (2L * Math.Abs(value: saw))));
            }
            case SynthOscillator.Noise: {
                var draw = voice.Noise.NextUInt32();
                var white = ((((int)(draw >> 16)) - 32768) * 2);
                var polynomial = voice.Patch.Polynomial;

                if (polynomial == 0) {
                    return white;
                }

                // One-pole tilt: k darkens as the low seven bits rise; the top bit flips to the high-pass
                // complement (bright family). A minimum-viable character map — liftable when patches want more.
                var k = ((128 - (polynomial & 127)) << 9);

                voice.NoiseTiltState += ((int)((((long)k) * (white - voice.NoiseTiltState)) >> 16));

                return ((polynomial < 128) ? voice.NoiseTiltState : (white - voice.NoiseTiltState));
            }
            case SynthOscillator.Pulse:
            default: {
                voice.Phase += voice.PhaseIncrement;

                var raw = ((voice.Phase < voice.Patch.DutyThresholdQ32) ? 65536 : -65536);

                return (raw - voice.Patch.DutyDcOffsetQ16);
            }
        }
    }

    private static int ApplyFilter(ref Voice voice, int sample) {
        if (voice.Patch.FilterMode == WorldVoiceFilterMode.Bypass) {
            return sample;
        }

        // Chamberlin SVF, Q16: one integrator pair per voice, taps low/band/high.
        var f = ((long)voice.Patch.FilterCoefficientQ16);
        var q = ((long)voice.Patch.FilterDampingQ16);

        voice.FilterLow += ((int)((f * voice.FilterBand) >> 16));

        var high = ((sample - voice.FilterLow) - ((int)((q * voice.FilterBand) >> 16)));

        voice.FilterBand += ((int)((f * high) >> 16));

        return (voice.Patch.FilterMode switch {
            WorldVoiceFilterMode.LowPass => voice.FilterLow,
            WorldVoiceFilterMode.BandPass => voice.FilterBand,
            _ => high,
        });
    }

    private static void RenderVoice(ref Voice voice, Span<int> accumulator) {
        for (var n = 0; (n < accumulator.Length); n++) {
            if (voice.ControlCountdown <= 0) {
                UpdateControlRate(voice: ref voice);
            }

            var level = StepEnvelope(voice: ref voice);

            if (!voice.Active) {
                return;
            }

            var oscillator = OscillatorSample(voice: ref voice);
            var shaped = ApplyFilter(voice: ref voice, sample: ((int)((((long)oscillator) * level) >> 16)));
            var scaled = ((int)((((long)shaped) * voice.GainQ16) >> 16));

            accumulator[n] += ((int)((((long)scaled) * 32767L) >> 16));
            voice.VibratoPhase += voice.VibratoIncrement;
            voice.TotalFrames++;
            voice.ControlCountdown--;
        }
    }

    private int AllocateVoice() {
        var quietest = 0;
        var quietestLevel = long.MaxValue;
        var quietestOrdinal = ulong.MaxValue;

        for (var i = 0; (i < VoiceCount); i++) {
            ref var voice = ref m_voices[i];

            if (!voice.Active) {
                return i;
            }

            if ((voice.EnvelopeLevelQ32 < quietestLevel) ||
                ((voice.EnvelopeLevelQ32 == quietestLevel) && (voice.TriggerOrdinal < quietestOrdinal))) {
                quietest = i;
                quietestLevel = voice.EnvelopeLevelQ32;
                quietestOrdinal = voice.TriggerOrdinal;
            }
        }

        return quietest;
    }
}
