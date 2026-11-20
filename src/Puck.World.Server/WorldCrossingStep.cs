namespace Puck.World.Server;

/// <summary>The one crossing record an authority has sent its log and not yet heard back about, and the step that waits
/// on it. The authority opens at most one at a time (<see cref="WorldServer.RecordCrossing"/>), so its crossing
/// sequence stays dense: a record that is refused frees its sequence for the next one, and no record
/// is sent behind one whose fate is unknown. The authority settles the step at a tick boundary, under its gate, once
/// the log answers: a durable record advances the sequence and folds there, and the step's continuation applies the
/// rest of its effect in that same hold. A step that comes back for its record later is matched by holding this
/// object, never by comparing the record it would build again.</summary>
internal sealed class WorldCrossingStep {
    private readonly Action<WorldCrossingStep>? m_continuation;

    /// <summary>Initializes a new instance of the <see cref="WorldCrossingStep"/> class for a record just sent.</summary>
    /// <param name="sequence">The crossing sequence the record was sent under.</param>
    /// <param name="record">The record exactly as it was sent.</param>
    /// <param name="continuation">The rest of the step's effect, run under the authority gate once the record's fate is
    /// known, or <see langword="null"/> for a step whose whole effect is the record's fold.</param>
    /// <param name="answer">The log's answer, which completes off the tick thread.</param>
    internal WorldCrossingStep(ulong sequence, WorldCrossingRecord record, Action<WorldCrossingStep>? continuation, Task<WorldCrossingAppend> answer) {
        Sequence = sequence;
        Record = record;
        Answer = answer;
        m_continuation = continuation;
    }

    /// <summary>Gets the log's answer, which completes off the tick thread; the authority acts on it only when it
    /// settles the step.</summary>
    public Task<WorldCrossingAppend> Answer { get; }

    /// <summary>Gets the crossing sequence the record was sent under.</summary>
    public ulong Sequence { get; }
    /// <summary>Gets the record exactly as it was sent: the one recovery redoes if it landed, whatever a retried step
    /// would build now.</summary>
    public WorldCrossingRecord Record { get; }
    /// <summary>Gets what became of the record, or <see langword="null"/> while it is in flight.</summary>
    public WorldCrossingDurability? Outcome { get; private set; }
    /// <summary>Gets why the record is not known to be durable, when it settled any other way; empty otherwise.</summary>
    public string Reason { get; private set; } = string.Empty;
    /// <summary>Gets whether the record's fate is known.</summary>
    public bool IsSettled => Outcome is not null;

    // Decisions waiting on this step (WorldServer.DecideCrossingAsync), resumed in order when it settles.
    private List<Action>? m_resumes;

    // Queues a decision to resume when the step settles. The caller holds the authority gate and has checked that the
    // step has not settled yet.
    internal void ResumeWhenSettled(Action resume) => (m_resumes ??= []).Add(item: resume);
    // The authority's settle, under its gate: records the fate, runs the continuation, then resumes every waiting
    // decision in the order it parked.
    internal void Settle(WorldCrossingAppend answer) {
        Outcome = answer.Durability;
        Reason = answer.Reason;
        var resumes = m_resumes;

        m_resumes = null;
        m_continuation?.Invoke(obj: this);
        foreach (var resume in (resumes ?? [])) {
            resume();
        }
    }
}
