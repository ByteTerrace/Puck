using System.Text;
using Puck.Maths;

namespace Puck.Audio.Mixing;

/// <summary>One character's sample-frame interval. A rest has no synth trigger.</summary>
public readonly record struct SpeechSyllable(int Character, long StartFrame, int Frames, bool Rest);
/// <summary>Animalese-style text voiced by <see cref="VoiceSynth"/> or six authored PCM syllables. The audio pull advances an integer frame
/// clock; neither the schedule nor the noise seeds depend on presentation cadence or utterance history.</summary>
public sealed class TextSpeechSource : IAudioBlockSource {
    private readonly VoicePatch m_patch;
    private readonly int m_pitch;
    private readonly int m_formant;
    private readonly ulong m_seed;
    private readonly short[][]? m_samples;
    private readonly long m_sampleStep;

    private short[]? m_sample;
    private int m_sampleFrame;
    private int m_sampleDuration;

    private readonly VoiceSynth m_synth = new();
    private readonly short[] m_mono = new short[AudioMixer.MaxBlockFrames];

    private int m_next;
    private long m_frame;

    /// <summary>Builds the complete text-derived schedule. Cadence uses the existing 240 Hz audio tick.</summary>
    public TextSpeechSource(string text, VoicePatch patch, int cadenceTicks, int pitchThousandths = 1000,
        int formantThousandths = 1000, int paceThousandths = 1000, short[][]? samples = null,
        int sampleRateThousandths = 1500) {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentOutOfRangeException.ThrowIfLessThan(cadenceTicks, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(cadenceTicks, 240);
        CheckScale(value: pitchThousandths);
        CheckScale(value: formantThousandths);
        CheckScale(value: paceThousandths);
        CheckScale(value: sampleRateThousandths);
        if ((samples is not null) && ((samples.Length != 6) || samples.Any(predicate: sample => ((sample is null) || (sample.Length == 0))))) {
            throw new ArgumentException(message: "A sample bank needs six nonempty syllables (A, E, I, O, U, consonant).", paramName: nameof(samples));
        }
        m_samples = samples;
        m_sampleStep = (((long)pitchThousandths) * sampleRateThousandths);
        m_patch = patch;
        m_pitch = pitchThousandths;
        m_formant = formantThousandths;
        m_seed = Fnv1aHash.Compute(values: text);
        var cadence = (((cadenceTicks * AudioMixer.FramesPerSimStep) * 1000) / paceThousandths);
        var schedule = new List<SpeechSyllable>();
        var frame = 0L;

        foreach (var rune in text.EnumerateRunes()) {
            var rest = !Rune.IsLetterOrDigit(value: rune);
            var slots = rune.Value switch { '.' or '!' or '?' or '\n' => 3, ',' or ';' or ':' => 2, _ => 1 };

            schedule.Add(item: new SpeechSyllable(rune.Value, frame, (cadence * slots), rest));
            frame += (cadence * slots);
        }
        Schedule = schedule.AsReadOnly();
        TotalFrames = frame;
    }

    /// <summary>Gets the immutable character/rest schedule.</summary>
    public IReadOnlyList<SpeechSyllable> Schedule { get; }
    /// <summary>Gets the exact utterance length including its rests.</summary>
    public long TotalFrames { get; }
    /// <summary>Gets whether every scheduled frame has been pulled.</summary>
    public bool Finished => (m_frame >= TotalFrames);

    private static void CheckScale(int value) {
        ArgumentOutOfRangeException.ThrowIfLessThan(value, 250);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(value, 4000);
    }
    private void Start(SpeechSyllable syllable, int ordinal) {
        m_sample = null;
        if (syllable.Rest) {
            return;
        }
        var letter = Rune.ToLowerInvariant(value: new Rune(value: syllable.Character)).Value;

        if (m_samples is not null) {
            var index = letter switch { 'a' => 0, 'e' => 1, 'i' or 'y' => 2, 'o' => 3, 'u' => 4, _ => 5 };

            m_sample = m_samples[index];
            m_sampleFrame = 0;
            m_sampleDuration = ((int)Math.Min(val1: ((syllable.Frames * 3L) / 4),
                val2: ((((m_sample.Length * 1_000_000L) + m_sampleStep) - 1) / m_sampleStep)));
            return;
        }

        var (first, second) = letter switch {
            'a' => (700, 1220),
            'e' => (500, 1900),
            'i' or 'y' => (300, 2300),
            'o' => (500, 900),
            'u' => (350, 750),
            _ => (500, 1500),
        };
        var duration = ((syllable.Frames * 3) / 4);
        var release = Math.Min(val1: m_patch.ReleaseFrames, val2: (duration / 4));
        var patch = m_patch with {
            PitchMillihertz = ((int)Math.Clamp(((((long)m_patch.PitchMillihertz) * m_pitch) / 1000), 1, 24_000_000)),
            AttackFrames = Math.Min(val1: m_patch.AttackFrames, val2: (duration / 4)),
            DecayFrames = Math.Min(val1: m_patch.DecayFrames, val2: (duration / 4)),
            DurationFrames = (duration - release),
            ReleaseFrames = release,
            FilterMode = VoiceFilterMode.BandPass,
            FilterDampingQ16 = 12000,
        };
        // f ~= 2*pi*fc/fs. Limit the formant below fs/6, the existing Chamberlin filter's stable band.
        var firstPatch = patch with { FilterCoefficientQ16 = Coefficient(frequency: first) };
        var secondPatch = patch with { FilterCoefficientQ16 = Coefficient(frequency: second) };
        var seed = m_seed ^ ((ulong)ordinal);

        m_synth.Trigger(firstPatch, seed, 20000);
        m_synth.Trigger(secondPatch, seed, 12000);
        if (letter is not ('a' or 'e' or 'i' or 'o' or 'u' or 'y')) {
            var onset = patch with {
                Oscillator = SynthOscillator.Noise,
                FilterMode = VoiceFilterMode.HighPass,
                DurationFrames = Math.Min(val1: 384, val2: (duration / 4)),
                AttackFrames = 0,
                DecayFrames = 0,
                ReleaseFrames = Math.Min(val1: 96, val2: (duration / 8)),
                FilterCoefficientQ16 = Coefficient(frequency: 1500),
            };

            m_synth.Trigger(onset, seed, 8000);
        }
    }
    private int Coefficient(int frequency) => ((int)((411775L * Math.Min(val1: 7000, val2: ((frequency * m_formant) / 1000))) / AudioMixer.SampleRate));

    /// <inheritdoc/>
    public int Pull(Span<short> interleavedStereo, int frames) {
        ArgumentOutOfRangeException.ThrowIfNegative(frames);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(frames, (interleavedStereo.Length / 2));
        interleavedStereo[..(frames * 2)].Clear();
        var written = 0;

        while ((written < frames) && !Finished) {
            if ((m_next < Schedule.Count) && (Schedule[m_next].StartFrame == m_frame)) {
                Start(Schedule[m_next], m_next);
                m_next++;
            }
            var boundary = ((m_next < Schedule.Count) ? Schedule[m_next].StartFrame : TotalFrames);
            var count = ((int)Math.Min(val1: Math.Min(val1: (frames - written), val2: AudioMixer.MaxBlockFrames), val2: (boundary - m_frame)));

            if (m_samples is null) {
                m_synth.Render(destination: m_mono, frames: count);
            } else {
                RenderSample(output: m_mono.AsSpan(length: count, start: 0));
            }
            for (var index = 0; (index < count); index++) {
                interleavedStereo[((written + index) * 2)] = m_mono[index];
                interleavedStereo[(((written + index) * 2) + 1)] = m_mono[index];
            }
            m_frame += count;
            written += count;
        }
        return written;
    }

    private void RenderSample(Span<short> output) {
        output.Clear();
        if (m_sample is null) { return; }
        for (var index = 0; ((index < output.Length) && (m_sampleFrame < m_sampleDuration)); index++, m_sampleFrame++) {
            var position = (m_sampleFrame * m_sampleStep);
            var frame = ((int)(position / 1_000_000));
            var fraction = (position % 1_000_000);
            var first = m_sample[frame];
            var second = m_sample[Math.Min(val1: (frame + 1), val2: (m_sample.Length - 1))];
            var value = (((first * (1_000_000 - fraction)) + (second * fraction)) / 1_000_000);
            // A short release prevents a clipped authored sample from clicking at the character boundary.
            output[index] = ((short)((value * Math.Min(val1: 48, val2: (m_sampleDuration - m_sampleFrame))) / 48));
        }
    }
}
