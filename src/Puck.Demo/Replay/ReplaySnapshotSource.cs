using Puck.Commands;

namespace Puck.Demo.Replay;

/// <summary>
/// An <see cref="ISnapshotSource"/> that replays a <see cref="SnapshotRecording"/>'s per-tick snapshots verbatim,
/// ignoring the capture window. Swapping the live router for this — the consumer unchanged — turns a record run into
/// a bit-identical replay. Snapshots are indexed by tick from the run's first tick (tick 0); a tick past the end
/// yields an empty snapshot.
/// </summary>
public sealed class ReplaySnapshotSource : ISnapshotSource {
    private readonly SnapshotRecording m_recording;

    /// <summary>Initializes the source from a recording.</summary>
    /// <param name="recording">The recording to replay.</param>
    /// <exception cref="ArgumentNullException"><paramref name="recording"/> is <see langword="null"/>.</exception>
    public ReplaySnapshotSource(SnapshotRecording recording) {
        ArgumentNullException.ThrowIfNull(argument: recording);

        m_recording = recording;
    }

    /// <summary>The recording's seed (the world must be created with it for an exact reproduction).</summary>
    public uint Seed => m_recording.Seed;
    /// <summary>The number of recorded ticks.</summary>
    public int TickCount => m_recording.Snapshots.Length;

    /// <inheritdoc/>
    public CommandSnapshot SnapshotForTick(ulong tick, ulong windowEndTick) {
        return ((tick < (ulong)m_recording.Snapshots.Length)
            ? m_recording.Snapshots[(int)tick]
            : CommandSnapshot.Empty(tick: tick));
    }
}
