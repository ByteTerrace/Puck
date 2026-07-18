namespace Puck.Recording.Audio;

/// <summary>
/// A streaming linear-interpolation resampler that maps an interleaved capture block of arbitrary sample rate and
/// channel count to interleaved 48 kHz stereo (the Opus lane's working format). It is a simple v1 resampler:
/// per-channel linear interpolation with phase carried across blocks, mono up-mixed by duplication and more than
/// two channels down-mixed to the first two. It is not a polyphase/anti-aliased resampler; document the choice.
/// </summary>
internal sealed class LinearResampler {
    private const int OutputSampleRate = 48000;

    private readonly double m_step;
    private double m_fraction;
    private bool m_hasPrevious;
    private float m_previousLeft;
    private float m_previousRight;

    /// <summary>Initializes a new instance of the <see cref="LinearResampler"/> class.</summary>
    /// <param name="inputSampleRate">The source sample rate in hertz.</param>
    /// <param name="inputChannels">The source interleaved channel count.</param>
    public LinearResampler(int inputSampleRate, int inputChannels) {
        InputChannels = Math.Max(val1: 1, val2: inputChannels);
        m_step = ((double)Math.Max(val1: 1, val2: inputSampleRate) / OutputSampleRate);
    }

    /// <summary>Gets the source interleaved channel count.</summary>
    public int InputChannels { get; }

    /// <summary>Resamples an interleaved source block into interleaved 48 kHz stereo, appending to a ring.</summary>
    /// <param name="input">The interleaved source samples.</param>
    /// <param name="output">The destination stereo ring.</param>
    public void Resample(ReadOnlySpan<float> input, FloatRing output) {
        var frameCount = (input.Length / InputChannels);
        Span<float> emitted = stackalloc float[2];

        for (var frame = 0; (frame < frameCount); frame++) {
            var baseIndex = (frame * InputChannels);
            var left = input[baseIndex];
            var right = ((InputChannels >= 2)
                ? input[(baseIndex + 1)]
                : left);

            if (!m_hasPrevious) {
                m_hasPrevious = true;
                m_previousLeft = left;
                m_previousRight = right;

                continue;
            }

            while (m_fraction < 1.0) {
                var t = (float)m_fraction;

                emitted[0] = (m_previousLeft + ((left - m_previousLeft) * t));
                emitted[1] = (m_previousRight + ((right - m_previousRight) * t));
                output.Enqueue(samples: emitted);

                m_fraction += m_step;
            }

            m_fraction -= 1.0;
            m_previousLeft = left;
            m_previousRight = right;
        }
    }
}
