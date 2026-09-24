namespace Puck.GamingBricks;

/// <summary>
/// The bounded ring of interleaved stereo frames every GamingBrick audio stage buffers its output in: the SM83 output
/// stage, the GBA APU, and the queued worker's host-readable copy. It counts whole frames, so a read always hands back
/// complete left/right pairs and the reader can never stop halfway through a frame and swap the channels.
/// <para>
/// <b>Overflow drops the oldest frame.</b> When the ring is full, <see cref="Push(short, short)"/> discards the oldest
/// frame before it appends, so the ring always holds the newest <see cref="CapacityFrames"/> frames. A host that stalls
/// loses the past and never the present, and the writer lapping the reader can never make a full ring read as empty.
/// </para>
/// <para>
/// The ring is host-facing output plumbing and never emulated state: nothing here is written to a snapshot. It is a
/// mutable struct, so hold it in a non-readonly field and call it in place. It takes no lock; a caller that reads and
/// writes it from different threads provides its own.
/// </para>
/// </summary>
public struct StereoSampleRing {
    private int m_capacityFrames;
    private int m_frameCount;
    private int m_readFrame;
    private short[] m_samples;
    private int m_writeFrame;

    /// <summary>Gets the number of frames the ring holds before an append drops the oldest one, which is zero while the
    /// ring is unconfigured or disabled.</summary>
    public readonly int CapacityFrames =>
        m_capacityFrames;
    /// <summary>Gets the number of buffered frames waiting to be read.</summary>
    public readonly int FrameCount =>
        m_frameCount;
    /// <summary>Gets the number of buffered samples waiting to be read: two per frame, so always even.</summary>
    public readonly int SampleCount =>
        (m_frameCount * 2);

    /// <summary>Empties the ring without changing its capacity, starting a fresh stream.</summary>
    public void Clear() {
        m_frameCount = 0;
        m_readFrame = 0;
        m_writeFrame = 0;
    }
    /// <summary>Sizes the ring to hold <paramref name="capacityFrames"/> stereo frames and discards anything buffered.
    /// A capacity of zero disables the ring: appends are dropped and reads return nothing.</summary>
    /// <param name="capacityFrames">The number of frames the ring holds, usually one emulated second at the output
    /// rate.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="capacityFrames"/> is negative.</exception>
    public void Configure(int capacityFrames) {
        ArgumentOutOfRangeException.ThrowIfNegative(value: capacityFrames);

        if (m_capacityFrames != capacityFrames) {
            m_samples = ((capacityFrames > 0)
                ? new short[(capacityFrames * 2)]
                : []
            );
            m_capacityFrames = capacityFrames;
        }

        Clear();
    }
    /// <summary>Appends one stereo frame. When the ring is full the oldest frame is dropped first; while the ring is
    /// disabled the frame is dropped.</summary>
    /// <param name="left">The left sample.</param>
    /// <param name="right">The right sample.</param>
    public void Push(short left, short right) {
        if (m_capacityFrames == 0) {
            return;
        }

        if (m_frameCount == m_capacityFrames) {
            m_readFrame = Advance(frame: m_readFrame);
            --m_frameCount;
        }

        var index = (m_writeFrame * 2);

        m_samples[index] = left;
        m_samples[(index + 1)] = right;
        m_writeFrame = Advance(frame: m_writeFrame);
        ++m_frameCount;
    }
    /// <summary>Appends interleaved left/right samples frame by frame, each append dropping the oldest frame when the
    /// ring is full.</summary>
    /// <param name="interleaved">The samples to append, left then right for each frame.</param>
    /// <exception cref="ArgumentException"><paramref name="interleaved"/> has an odd length, so it does not hold whole
    /// frames.</exception>
    public void Push(ReadOnlySpan<short> interleaved) {
        if ((interleaved.Length & 1) != 0) {
            throw new ArgumentException(
                message: "interleaved stereo samples come in left/right pairs; the span has an odd length.",
                paramName: nameof(interleaved)
            );
        }

        for (var index = 0; (index < interleaved.Length); index += 2) {
            Push(
                left: interleaved[index],
                right: interleaved[(index + 1)]
            );
        }
    }
    /// <summary>Reads buffered frames, oldest first, into <paramref name="destination"/> as interleaved left/right
    /// samples. Only whole frames are copied: an odd trailing slot in <paramref name="destination"/> stays
    /// untouched.</summary>
    /// <param name="destination">The span to fill.</param>
    /// <returns>The number of samples written, which is always even.</returns>
    public int Read(Span<short> destination) {
        var frames = Math.Min(
            val1: (destination.Length / 2),
            val2: m_frameCount
        );

        for (var frame = 0; (frame < frames); ++frame) {
            var index = (m_readFrame * 2);

            destination[(frame * 2)] = m_samples[index];
            destination[((frame * 2) + 1)] = m_samples[(index + 1)];
            m_readFrame = Advance(frame: m_readFrame);
        }

        m_frameCount -= frames;

        return (frames * 2);
    }

    private readonly int Advance(int frame) =>
        (((frame + 1) == m_capacityFrames)
            ? 0
            : (frame + 1)
        );
}
