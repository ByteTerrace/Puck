using System.Runtime.CompilerServices;
using Puck.Maths;

namespace Puck.Platform.Probes;

/// <summary>The declared channel ceiling for one <see cref="ProbeReading"/> — every probe kind's channel
/// count is bounded by this so the reading stays a fixed-size, blittable record.</summary>
public static class ProbeReadingLimits {
    /// <summary>The maximum number of named channels one reading carries.</summary>
    public const int MaxChannels = 8;
}
/// <summary>Zero-alloc fixed-point storage for one <see cref="ProbeReading"/>'s channels —
/// <see cref="ProbeReadingLimits.MaxChannels"/> <see cref="FixedQ4816"/> slots inline in the value itself (an
/// <c>InlineArray</c>, never a heap array), so constructing and copying a reading allocates nothing.</summary>
[InlineArray(ProbeReadingLimits.MaxChannels)]
public struct ProbeChannelValues {
    private FixedQ4816 m_element0;
}
/// <summary>
/// One probe publication — the neutral currency between an probe and every binding that consumes it. Blittable
/// and fixed size so it sits unmodified in a <see cref="ProbeReadingRing"/> slot (managed today; the same layout
/// an out-of-process shared-memory ring will use for a model probe): named fixed-point channels, a confidence,
/// and the two <see cref="System.Diagnostics.Stopwatch"/>-domain timestamps that bound its latency — when the
/// analyzed frame was captured, and when this reading finished computing from it. Never carries pixels.
/// </summary>
public readonly struct ProbeReading {
    /// <summary>Constructs a reading.</summary>
    /// <param name="sequence">A monotonically increasing publication ordinal, assigned by the probe.</param>
    /// <param name="captureTimestamp">The <see cref="System.Diagnostics.Stopwatch"/> timestamp of the analyzed
    /// frame — the same clock domain as <c>ICameraStream.LastFrameTimestamp</c>.</param>
    /// <param name="completionTimestamp">The <see cref="System.Diagnostics.Stopwatch"/> timestamp this reading
    /// finished computing.</param>
    /// <param name="confidence">The unipolar confidence, nominally in <c>[0, 1]</c>.</param>
    /// <param name="channelCount">The number of valid leading slots in <paramref name="channels"/>.</param>
    /// <param name="channels">The channel values; only the first <paramref name="channelCount"/> slots are valid.</param>
    /// <param name="maskSlot">The mask texture slot, or <c>-1</c> when this reading carries no mask.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="channelCount"/> is negative or exceeds
    /// <see cref="ProbeReadingLimits.MaxChannels"/>, or <paramref name="maskSlot"/> is less than <c>-1</c>.</exception>
    public ProbeReading(long sequence, long captureTimestamp, long completionTimestamp, FixedQ4816 confidence, int channelCount, in ProbeChannelValues channels, int maskSlot = -1) {
        ArgumentOutOfRangeException.ThrowIfNegative(channelCount);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(channelCount, ProbeReadingLimits.MaxChannels);
        ArgumentOutOfRangeException.ThrowIfLessThan(maskSlot, -1);

        Sequence = sequence;
        CaptureTimestamp = captureTimestamp;
        CompletionTimestamp = completionTimestamp;
        Confidence = confidence;
        ChannelCount = channelCount;
        Channels = channels;
        MaskSlot = maskSlot;
    }

    /// <summary>Gets the publication ordinal.</summary>
    public long Sequence { get; }
    /// <summary>Gets the analyzed frame's capture timestamp, in the <see cref="System.Diagnostics.Stopwatch"/> domain.</summary>
    public long CaptureTimestamp { get; }
    /// <summary>Gets this reading's completion timestamp, in the <see cref="System.Diagnostics.Stopwatch"/> domain.</summary>
    public long CompletionTimestamp { get; }
    /// <summary>Gets the unipolar confidence.</summary>
    public FixedQ4816 Confidence { get; }
    /// <summary>Gets the number of valid channels.</summary>
    public int ChannelCount { get; }
    /// <summary>Gets the channel values; only the first <see cref="ChannelCount"/> slots are valid.</summary>
    public ProbeChannelValues Channels { get; }
    /// <summary>Gets the mask texture slot, or <c>-1</c> when this reading carries no mask.</summary>
    public int MaskSlot { get; }

    /// <summary>Reads one channel's value by ordinal.</summary>
    /// <param name="channel">The channel ordinal (<c>0..</c><see cref="ChannelCount"/><c>-1</c>).</param>
    public FixedQ4816 this[int channel] => Channels[channel];
}
