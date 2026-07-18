namespace Puck.Commands;

/// <summary>The outcome of a <see cref="DeterminismHarness"/> battery.</summary>
public enum DeterminismVerdict {
    /// <summary>The scripted run was deterministic and the recorded run replayed bit-for-bit.</summary>
    Verified,
    /// <summary>The same scripted input produced different per-tick state on a repeat run.</summary>
    NonDeterministic,
    /// <summary>The recorded run was deterministic, but a binary-round-tripped replay diverged from it.</summary>
    ReplayDiverged,
}

/// <summary>The harness outcome: the verdict, the first divergent tick (-1 when verified), and the live run's
/// per-tick state-hash trace (consumers report the final hash from it).</summary>
public readonly record struct DeterminismReport(DeterminismVerdict Verdict, int DivergenceTick, ulong[] LiveHashes);

/// <summary>A determinism subject: builds and ticks one FRESH simulation per call. Each Run* call must construct
/// completely new sim state; the returned array is the per-tick state-hash trace, one entry per tick.</summary>
/// <typeparam name="TRecording">The recording container (plain <see cref="SnapshotRecording"/>, or a wrapper
/// carrying consumer side channels such as roster events).</typeparam>
public interface IDeterminismSubject<TRecording> {
    /// <summary>One fresh scripted run. Apply <paramref name="decorate"/> ONCE to the run's snapshot source at the
    /// point the sim consumes it (the harness passes the recording decorator on the record run, identity on the
    /// repeat). <paramref name="record"/> is true only on the record run — capture side channels then.</summary>
    ulong[] RunScripted(bool record, Func<ISnapshotSource, ISnapshotSource> decorate);
    /// <summary>Packs the harness-captured input recording with any side channels captured during the record run.</summary>
    TRecording PackRecording(SnapshotRecording input);
    /// <summary>Writes the recording's binary form (the round-trip under test).</summary>
    void WriteRecording(Stream stream, TRecording recording);
    /// <summary>Reads the binary form back.</summary>
    TRecording ReadRecording(Stream stream);
    /// <summary>One fresh run driven by the round-tripped recording (typically via <see cref="ReplaySnapshotSource"/>).</summary>
    ulong[] RunReplay(TRecording recording);
}

/// <summary>
/// The shared record-and-replay determinism battery: scripted run (recorded) → identical repeat →
/// first-divergence → pack → binary round-trip → replay → first-divergence. Every consumer (a Post stage's neutral
/// sim, a Post stage's CLI-driven sim, the demo's overworld self-check) duplicated this sequence verbatim; only how
/// a fresh run is built/ticked, the recording container, and the replay source vary — which is exactly what
/// <see cref="IDeterminismSubject{TRecording}"/> abstracts.
/// </summary>
public static class DeterminismHarness {
    /// <summary>The canonical battery: scripted run (recorded) → identical repeat → first-divergence →
    /// pack → binary round-trip → replay → first-divergence.</summary>
    public static DeterminismReport Verify<TRecording>(uint seed, IDeterminismSubject<TRecording> subject) {
        ArgumentNullException.ThrowIfNull(subject);

        var recorder = new InputRecorder(seed: seed);
        var live = subject.RunScripted(record: true, decorate: source => new RecordingSnapshotSource(inner: source, recorder: recorder));
        var repeat = subject.RunScripted(record: false, decorate: static source => source);
        var sameDivergence = HashTrace.FirstDivergence(left: live, right: repeat);

        if (sameDivergence >= 0) {
            return new DeterminismReport(Verdict: DeterminismVerdict.NonDeterministic, DivergenceTick: sameDivergence, LiveHashes: live);
        }

        var recording = subject.PackRecording(input: recorder.ToRecording());

        using var stream = new MemoryStream();

        subject.WriteRecording(stream: stream, recording: recording);
        stream.Position = 0L;

        var roundTripped = subject.ReadRecording(stream: stream);
        var replayed = subject.RunReplay(recording: roundTripped);
        var replayDivergence = HashTrace.FirstDivergence(left: live, right: replayed);

        if (replayDivergence >= 0) {
            return new DeterminismReport(Verdict: DeterminismVerdict.ReplayDiverged, DivergenceTick: replayDivergence, LiveHashes: live);
        }

        return new DeterminismReport(Verdict: DeterminismVerdict.Verified, DivergenceTick: -1, LiveHashes: live);
    }

    /// <summary>The plain-<see cref="SnapshotRecording"/> convenience (no side channels): the two delegates are
    /// the whole subject; write/read use <see cref="SnapshotRecording.Write"/>/<see cref="SnapshotRecording.Read"/>
    /// against <paramref name="registry"/>.</summary>
    public static DeterminismReport Verify(
        uint seed,
        CommandRegistry registry,
        Func<Func<ISnapshotSource, ISnapshotSource>, ulong[]> runScripted,
        Func<SnapshotRecording, ulong[]> runReplay
    ) {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(runReplay);
        ArgumentNullException.ThrowIfNull(runScripted);

        return Verify(seed: seed, subject: new DelegateSubject(registry: registry, runReplay: runReplay, runScripted: runScripted));
    }

    // The delegate overload's adapter subject: the two delegates ARE the whole subject, and the plain
    // SnapshotRecording needs no packing (identity).
    private sealed class DelegateSubject : IDeterminismSubject<SnapshotRecording> {
        private readonly CommandRegistry m_registry;
        private readonly Func<SnapshotRecording, ulong[]> m_runReplay;
        private readonly Func<Func<ISnapshotSource, ISnapshotSource>, ulong[]> m_runScripted;

        public DelegateSubject(CommandRegistry registry, Func<SnapshotRecording, ulong[]> runReplay, Func<Func<ISnapshotSource, ISnapshotSource>, ulong[]> runScripted) {
            m_registry = registry;
            m_runReplay = runReplay;
            m_runScripted = runScripted;
        }

        public ulong[] RunScripted(bool record, Func<ISnapshotSource, ISnapshotSource> decorate) {
            return m_runScripted(arg: decorate);
        }
        public SnapshotRecording PackRecording(SnapshotRecording input) {
            return input;
        }
        public void WriteRecording(Stream stream, SnapshotRecording recording) {
            SnapshotRecording.Write(stream: stream, recording: recording, registry: m_registry);
        }
        public SnapshotRecording ReadRecording(Stream stream) {
            return SnapshotRecording.Read(stream: stream, registry: m_registry);
        }
        public ulong[] RunReplay(SnapshotRecording recording) {
            return m_runReplay(arg: recording);
        }
    }
}
