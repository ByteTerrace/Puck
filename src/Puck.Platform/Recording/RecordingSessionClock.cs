using System.Diagnostics;

namespace Puck.Platform.Recording;

/// <summary>
/// The recording session's shared wall clock: a single QPC epoch that video frames and every audio source stamp
/// against, so their nanosecond timestamps share one timeline. The epoch is captured at construction and can be
/// re-anchored at the true capture start (the frozen <c>IAudioCaptureSourceFactory</c> has no per-session hook, so the
/// factory is handed this clock and the recording session re-anchors it when it begins — see the handoff note).
/// </summary>
/// <remarks>Backed by <see cref="Stopwatch.GetTimestamp"/> (QPC on Windows), normalized to 100-nanosecond units so a
/// WASAPI device's QPC position (already 100-ns units) maps in without a second frequency conversion.</remarks>
public sealed class RecordingSessionClock {
    private long m_epochHectonanoseconds;

    /// <summary>Creates a clock whose epoch is now.</summary>
    public RecordingSessionClock() => m_epochHectonanoseconds = NowHectonanoseconds();

    /// <summary>Re-anchors the epoch to the current instant — the recording session calls this at capture start.</summary>
    public void ResetEpochToNow() => Volatile.Write(location: ref m_epochHectonanoseconds, value: NowHectonanoseconds());

    /// <summary>The nanoseconds elapsed on this clock since its epoch.</summary>
    public long NowNanoseconds() => NanosecondsFromHectonanoseconds(hectonanoseconds: NowHectonanoseconds());

    /// <summary>Maps a QPC position in 100-nanosecond units (as WASAPI reports) to session nanoseconds.</summary>
    /// <param name="hectonanoseconds">A timestamp in 100-ns units on the same QPC domain as the epoch.</param>
    public long NanosecondsFromHectonanoseconds(long hectonanoseconds) {
        var delta = (hectonanoseconds - Volatile.Read(location: ref m_epochHectonanoseconds));

        return ((delta < 0) ? 0 : (delta * 100));
    }

    /// <summary>The current QPC timestamp normalized to 100-nanosecond units.</summary>
    public static long NowHectonanoseconds() => (long)((((Int128)Stopwatch.GetTimestamp()) * 10_000_000) / Stopwatch.Frequency);
}
