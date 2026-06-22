using Puck.Commands;

namespace Puck.Demo.Replay;

/// <summary>
/// Accumulates a session's per-tick <see cref="CommandSnapshot"/> stream, plus the seed, into a
/// <see cref="SnapshotRecording"/>. Pair it with <see cref="RecordingSnapshotSource"/> to capture a live run
/// transparently, or feed it snapshots directly.
/// </summary>
public sealed class InputRecorder {
    private readonly List<CommandSnapshot> m_snapshots = [];
    private readonly uint m_seed;

    /// <summary>Begins a recording for the given simulation seed.</summary>
    /// <param name="seed">The seed the recorded run is created with.</param>
    public InputRecorder(uint seed) {
        m_seed = seed;
    }

    /// <summary>Appends one tick's snapshot to the recording, in produced order.</summary>
    /// <param name="snapshot">The tick's snapshot.</param>
    public void Record(in CommandSnapshot snapshot) {
        m_snapshots.Add(item: snapshot);
    }

    /// <summary>Builds the immutable recording captured so far.</summary>
    /// <returns>The recording.</returns>
    public SnapshotRecording ToRecording() {
        return new SnapshotRecording { Seed = m_seed, Snapshots = [.. m_snapshots], };
    }
}

/// <summary>
/// An <see cref="ISnapshotSource"/> decorator that records every snapshot it forwards. Installing it in front of the
/// live router captures a session for replay with no change to the consumer — record↔replay is a one-line source swap
/// (this for recording, <see cref="ReplaySnapshotSource"/> for playback).
/// </summary>
public sealed class RecordingSnapshotSource : ISnapshotSource {
    private readonly ISnapshotSource m_inner;
    private readonly InputRecorder m_recorder;

    /// <summary>Wraps an inner source, recording each snapshot into <paramref name="recorder"/>.</summary>
    /// <param name="inner">The source whose snapshots are forwarded and recorded.</param>
    /// <param name="recorder">The recorder the snapshots are appended to.</param>
    /// <exception cref="ArgumentNullException">Any argument is <see langword="null"/>.</exception>
    public RecordingSnapshotSource(ISnapshotSource inner, InputRecorder recorder) {
        ArgumentNullException.ThrowIfNull(argument: inner);
        ArgumentNullException.ThrowIfNull(argument: recorder);

        m_inner = inner;
        m_recorder = recorder;
    }

    /// <inheritdoc/>
    public CommandSnapshot SnapshotForTick(ulong tick, ulong windowEndTick) {
        var snapshot = m_inner.SnapshotForTick(tick: tick, windowEndTick: windowEndTick);

        m_recorder.Record(snapshot: in snapshot);

        return snapshot;
    }
}
