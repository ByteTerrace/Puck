
namespace Puck.World.Server;

public sealed partial class WorldServer {
    private IWorldCrossingLog? m_crossingLog;
    private ulong? m_uncertainCrossing;
    private WorldCrossingRecord? m_uncertainRecord;
    // The one record in flight to the log, from its append until the authority settles it.
    private WorldCrossingStep? m_crossingStep;
    // The step the current DecideCrossingAsync decision waits on, and whether the decision opened it.
    private (WorldCrossingStep Step, bool Own)? m_parkedOn;
    // The fate of a resumed decision's own record, answered once in place of its first append.
    private WorldCrossingAppend? m_replayOutcome;

    /// <summary>Gets or sets the observer of every arrival a commit here decided with at least one traveler landed: the
    /// arrival as the commit bound it, and its outcome (each landed traveler's generation, and whether the commit rolled
    /// the landings back because a member was refused or its record could not be made durable). A commit lands under the
    /// authority gate on whichever thread carried it, the host's drain for a colocated source or a socket worker for a
    /// federated one, and this observer hears it there, at the commit's own position among the authority's inputs. A
    /// step and its tape close hold the same gate (<see cref="WorldServerStepShell.Step"/>), so an arrival that follows
    /// a step joins the tick group of the next one. The replay tape attaches here while it records, and its re-drive
    /// reproduces the outcome through <see cref="WorldTransferEscrow.TryReland"/>.</summary>
    public Action<WorldCrossingArrival, WorldArrivalOutcome>? ArrivalTap { get; set; }
    /// <summary>Gets or sets the observer of the federated device images each step applies: the input a forwarded or
    /// federated traveler drives this authority's body with, which reaches it through no loopback. It hears the held
    /// set at the start of every step while any image is held and once more as the set empties, on the stepping thread
    /// under the authority gate. The replay tape attaches here while it records.</summary>
    public Action<IReadOnlyList<WorldFederatedIntentCheckpoint>>? FederatedIntentTap { get; set; }
    /// <summary>Gets or sets the observer of every custody record this authority makes durable, at the position among
    /// its inputs where it became durable, under the authority gate. The replay tape attaches here while it records, and
    /// its re-drive folds each record through the shadow's custody reducer.</summary>
    public Action<WorldCrossingRecord>? CustodyTap { get; set; }
    /// <summary>Gets the first crossing sequence this authority has not yet made durable — the watermark a checkpoint
    /// captures and recovery redoes from.</summary>
    public ulong CrossingSequence => ExecuteAuthorityOperation(operation: () => m_transferEscrow.CrossingSequence);

    /// <summary>Returns the identity this authority's catalog owns for a seat that is its own: a local seat whose
    /// incarnation, when it has crossed, this authority minted at that very seat, carrying the id of an identity the
    /// catalog owns. An id alone proves nothing, since every catalog seeds its identities from its template. A seat
    /// coming home, a restored checkpoint and a source rollback after a restart rebind such a seat to the identity this
    /// returns; every other seat keeps the projection it carries.</summary>
    /// <param name="slot">The seat's body index.</param>
    /// <param name="mobility">The seat's mobility identity, or <see langword="null"/> for a seat that never crossed.</param>
    /// <param name="id">The id of the identity the seat carries.</param>
    /// <returns>The owned identity, or <see langword="null"/> when the seat is not this authority's own.</returns>
    public WorldIdentity? HomeSeatIdentity(int slot, WorldMobilityIdentity? mobility, string id) {
        if (
            (((uint)slot) >= ((uint)Population.LocalSeatCount)) ||
            ((mobility is { } crossed) && (
                (crossed.Incarnation.Index != slot) ||
                !string.Equals(
                    a: crossed.Incarnation.Authority,
                    b: AuthorityIdentity,
                    comparisonType: StringComparison.Ordinal
                )
            ))
        ) {
            return null;
        }

        return ((Profiles.FindById(id: id) is { Document: not null } owned)
            ? owned
            : null);
    }
    /// <summary>Replaces every held federated device image with a recorded set — the replay's door for the images a
    /// recorded step held.</summary>
    /// <param name="held">The images, each with the body index it drives.</param>
    /// <exception cref="ArgumentNullException"><paramref name="held"/> is <see langword="null"/>.</exception>
    public void ReplaceFederatedIntents(IReadOnlyList<WorldFederatedIntentCheckpoint> held) {
        ArgumentNullException.ThrowIfNull(argument: held);
        m_tick.ReplaceFederatedIntents(held: held);
    }
    /// <summary>Installs the durable crossing log before this authority takes part in any crossing. From then on a
    /// source's departure and settlement and a destination's arrival are refused when their record does not land.</summary>
    /// <param name="log">The log.</param>
    /// <exception cref="ArgumentNullException"><paramref name="log"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">A log is already installed.</exception>
    public void InstallCrossingLog(IWorldCrossingLog log) {
        ArgumentNullException.ThrowIfNull(argument: log);
        ExecuteAuthorityOperation(operation: () => {
            if (m_crossingLog is not null) {
                throw new InvalidOperationException(message: "a crossing log is already installed");
            }
            m_crossingLog = log;
        });
    }

    /// <summary>Gets the crossing sequence whose record this activation's log answered
    /// <see cref="WorldCrossingDurability.Uncertain"/>, or <see langword="null"/> while every record is known. Once
    /// set it stays set: the authority records no further crossing step until a recovered activation replaces it.</summary>
    public ulong? UncertainCrossing => ExecuteAuthorityOperation(operation: () => m_uncertainCrossing);

    /// <summary>Makes one source-side crossing record durable under this authority's crossing sequence. Succeeds
    /// without writing anything when no log is installed. A source acts on a record only once it is durable, so an
    /// uncertain record refuses its step here exactly as a refused one does; recovery redoes it if it landed. A record
    /// still in flight answers <see langword="false"/> too: its fold happens when the authority settles it.</summary>
    /// <param name="record">The record.</param>
    /// <param name="reason">Why the record is not known to be durable, when it is not.</param>
    /// <returns><see langword="true"/> once the record is durable or no log is installed.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="record"/> is <see langword="null"/>.</exception>
    public bool TryRecordCrossing(WorldCrossingRecord record, out string reason) => (RecordCrossing(
        opened: out _,
        reason: out reason,
        record: record
    ) == WorldCrossingDurability.Durable);
    // The door for a step that runs off the authority gate (the host's drain): takes the gate for the one record.
    internal WorldCrossingDurability RecordCrossing(WorldCrossingRecord record, out string reason, out WorldCrossingStep? opened, Action<WorldCrossingStep>? continuation = null) {
        ArgumentNullException.ThrowIfNull(argument: record);

        var refusal = string.Empty;
        WorldCrossingStep? step = null;
        var durability = ExecuteAuthorityOperation(operation: () => RecordCrossingHeld(
            continuation: continuation,
            opened: out step,
            reason: out refusal,
            record: record
        ));

        reason = refusal;
        opened = step;
        return durability;
    }

    /// <summary>Gets whether a crossing record of this authority is in flight to its log. No other record is sent and
    /// no checkpoint is captured until it settles. The caller holds the authority gate.</summary>
    internal bool CrossingInFlight => (m_crossingStep is not null);

    // Callers hold the authority gate: the sequence a record takes and the state it describes are one atomic fact.
    internal WorldCrossingDurability RecordCrossingHeld(WorldCrossingRecord record, out string reason, Action<WorldCrossingStep>? continuation = null) => RecordCrossingHeld(
        continuation: continuation,
        opened: out _,
        reason: out reason,
        record: record
    );
    // The one door every crossing record passes. Two invariants hold here and nowhere else:
    // - Dense sequence: a record is sent only while no other is in flight (m_crossingStep), under the sequence the
    //   last durable record left; only a durable answer advances it. A refused record frees its sequence for the next.
    // - Durability before effect: a step acts on its record only once the record is durable. A synchronous answer is
    //   acted on here; an asynchronous one opens a step, answers Pending, and the authority settles it at a tick
    //   boundary (SettleCrossing), folding the record and running the step's continuation in one gate hold.
    // A decision resumed after its own record settled any other way answers that record's fate again in place of its
    // first append (m_replayOutcome), so it decides exactly as it would have had the log answered at once.
    internal WorldCrossingDurability RecordCrossingHeld(WorldCrossingRecord record, out string reason, out WorldCrossingStep? opened, Action<WorldCrossingStep>? continuation = null) {
        opened = null;
        if (m_crossingLog is not { } log) {
            reason = string.Empty;
            FoldDurable(record: record);
            return WorldCrossingDurability.Durable;
        }
        if (m_replayOutcome is { } replay) {
            m_replayOutcome = null;
            reason = replay.Reason;
            return replay.Durability;
        }
        if (m_uncertainCrossing is { } uncertain) {
            reason = $"crossing record {uncertain} is uncertain; this authority records nothing more until it recovers";
            return WorldCrossingDurability.Refused;
        }
        if (ParkOnCrossingInFlight(reason: out reason)) {
            return WorldCrossingDurability.Pending;
        }

        var entry = new WorldCrossingEntry(
            Record: record,
            Sequence: m_transferEscrow.CrossingSequence,
            Tick: (NextInputTick - 1UL)
        );
        var append = log.Append(entry: in entry);

        if (append.IsCompleted) {
            var answer = AnswerOf(append: append);

            reason = answer.Reason;
            Conclude(
                durability: answer.Durability,
                record: record,
                sequence: entry.Sequence
            );
            return answer.Durability;
        }

        var step = new WorldCrossingStep(
            answer: append.AsTask(),
            continuation: continuation,
            record: record,
            sequence: entry.Sequence
        );

        m_crossingStep = step;
        m_parkedOn = (step, true);
        opened = step;
        // The answer arrives off the tick thread. The authority acts on it at its next mailbox drain, which this wakes,
        // or at the next checkpoint capture of a frozen activation that no longer drains.
        _ = step.Answer.ContinueWith(
            cancellationToken: CancellationToken.None,
            continuationOptions: TaskContinuationOptions.ExecuteSynchronously,
            continuationAction: _ => m_pending.Writer.TryWrite(item: () => {
                using (EnterAtTickBoundary()) {
                    SettleCrossingIfAnswered();
                }
            }),
            scheduler: TaskScheduler.Default
        );
        reason = $"crossing record {entry.Sequence} is in flight";
        return WorldCrossingDurability.Pending;
    }
    /// <summary>Gets a task that completes once the crossing log has answered the record in flight, or a completed task
    /// when none is. The authority acts on the answer at its next tick boundary, or when a checkpoint is next captured,
    /// so a caller retiring a frozen activation awaits this before capturing its final image.</summary>
    public Task CrossingAnswered {
        get {
            using (EnterAtTickBoundary()) {
                return ((Task?)m_crossingStep?.Answer ?? Task.CompletedTask);
            }
        }
    }
    // Whether a crossing record is in flight, and if so notes it as the one the current decision waits on. Callers
    // hold the authority gate.
    internal bool ParkOnCrossingInFlight(out string reason) {
        if (m_crossingStep is not { } open) {
            reason = string.Empty;
            return false;
        }
        m_parkedOn ??= (open, false);
        reason = $"crossing record {open.Sequence} is in flight; this step waits for it";
        return true;
    }
    // A durable record whose step this activation could not carry out: the authority records nothing more, as for an
    // uncertain record, and captures no image, since its state is behind its log; a recovered activation redoes the
    // record from the log. Callers hold the authority gate.
    internal void LatchCrossing(WorldCrossingStep step) {
        m_uncertainCrossing ??= step.Sequence;
        m_uncertainRecord ??= step.Record;
        UnappliedCrossing ??= step.Sequence;
    }
    /// <summary>Gets the sequence of a durable crossing record this activation could not carry out, or
    /// <see langword="null"/>. Once set the activation captures no checkpoint: only a recovery from the log brings its
    /// state level with what the log holds. The caller holds the authority gate.</summary>
    internal ulong? UnappliedCrossing { get; private set; }
    // A log never answers Pending, and one whose answer faulted may or may not have taken the record.
    private static WorldCrossingAppend AnswerOf(ValueTask<WorldCrossingAppend> append) {
        if (!append.IsCompletedSuccessfully) {
            return new WorldCrossingAppend(
                Durability: WorldCrossingDurability.Uncertain,
                Reason: $"the crossing log's answer failed ({append.AsTask().Exception?.GetBaseException().Message ?? "canceled"})"
            );
        }
        var answer = append.Result;

        return ((answer.Durability == WorldCrossingDurability.Pending)
            ? answer with { Durability = WorldCrossingDurability.Uncertain }
            : answer);
    }
    private void Conclude(ulong sequence, WorldCrossingRecord record, WorldCrossingDurability durability) {
        if (durability == WorldCrossingDurability.Durable) {
            m_transferEscrow.AdvanceCrossingSequence();
            FoldDurable(record: record);
        } else if (durability == WorldCrossingDurability.Uncertain) {
            m_uncertainCrossing = sequence;
            m_uncertainRecord = record;
        }
    }
    // Runs under the authority gate at a tick boundary (DrainMailbox) or before a checkpoint capture: once the log has
    // answered, the in-flight record's fate becomes this authority's state and its step's continuation finishes the
    // step, in one hold.
    internal void SettleCrossingIfAnswered() {
        if (m_crossingStep is not { Answer.IsCompleted: true } step) {
            return;
        }

        var settled = AnswerOf(append: new ValueTask<WorldCrossingAppend>(task: step.Answer));

        m_crossingStep = null;
        Conclude(
            durability: settled.Durability,
            record: step.Record,
            sequence: step.Sequence
        );
        try {
            step.Settle(answer: settled);
        } catch (InvalidOperationException error) when (settled.Durability == WorldCrossingDurability.Durable) {
            // A durable record whose step could not finish here (the activation is retiring, or its tick is held): this
            // activation's state is behind its log, so it records and captures nothing more until a recovery.
            LatchCrossing(step: step);
            if (Output.HasNarrationSink) {
                Output.Narrate(
                    channel: "world.transfer",
                    text: $"[world.transfer: crossing record {step.Sequence} is durable but its step could not finish — {error.Message}; this authority records nothing more until it recovers]"
                );
            }
        }
    }

    /// <summary>Decides one crossing operation under the authority gate and answers once its record is durable. A
    /// decision that sends a record the log cannot answer at once, or finds another record in flight, is decided again
    /// when that record settles, in the same gate hold at the same tick boundary: against the state its durable record
    /// left, or, when its own record was refused or lost, with the log's answer standing in for its append. The caller
    /// never blocks on the log, and a decision is matched to its record by the step it opened, never by the record's
    /// value.</summary>
    /// <typeparam name="T">The decision's answer.</typeparam>
    /// <param name="decide">The decision; it runs under the authority gate and may run more than once.</param>
    /// <param name="ct">The token that releases the caller from waiting; a decision not yet resumed is then skipped.</param>
    /// <returns>The decision's answer once no crossing record it depends on is in flight.</returns>
    internal Task<T> DecideCrossingAsync<T>(Func<T> decide, CancellationToken ct = default) {
        var (answer, parked) = DecideOnce(
            decide: decide,
            replay: null
        );

        if (parked is not { } waiting) {
            return Task.FromResult(result: answer);
        }

        var result = new TaskCompletionSource<T>(creationOptions: TaskCreationOptions.RunContinuationsAsynchronously);

        ExecuteAuthorityOperation(operation: () => Park(parked: waiting));
        return (ct.CanBeCanceled
            ? result.Task.WaitAsync(cancellationToken: ct)
            : result.Task);

        // Callers hold the authority gate. A step that settled between the decision and here resumes at once.
        void Park((WorldCrossingStep Step, bool Own) parked) {
            if (parked.Step.IsSettled) {
                Resume(parked: parked);
            } else {
                parked.Step.ResumeWhenSettled(resume: () => Resume(parked: parked));
            }
        }
        void Resume((WorldCrossingStep Step, bool Own) parked) {
            if (ct.IsCancellationRequested) {
                result.TrySetCanceled(cancellationToken: ct);
                return;
            }

            var replay = ((parked.Own && (parked.Step.Outcome != WorldCrossingDurability.Durable))
                ? new WorldCrossingAppend(Durability: parked.Step.Outcome!.Value, Reason: parked.Step.Reason)
                : (WorldCrossingAppend?)null);

            try {
                var (resumed, next) = DecideOnce(
                    decide: decide,
                    replay: replay
                );

                if (next is { } again) {
                    Park(parked: again);
                } else {
                    result.TrySetResult(result: resumed);
                }
            } catch (Exception error) {
                result.TrySetException(exception: error);
            }
        }
    }
    private (T Answer, (WorldCrossingStep Step, bool Own)? Parked) DecideOnce<T>(Func<T> decide, WorldCrossingAppend? replay) => ExecuteAuthorityOperation(operation: () => {
        m_parkedOn = null;
        m_replayOutcome = replay;
        try {
            var answer = decide();

            return (answer, m_parkedOn);
        } finally {
            m_parkedOn = null;
            m_replayOutcome = null;
        }
    });
    // A durable record's custody effect. An arrival folds where it lands (live, recovery and re-drive alike), so only
    // the other records fold here.
    private void FoldDurable(WorldCrossingRecord record) {
        if ((record is not WorldCrossingRecord.Arrival) && m_transferEscrow.Custody.Concerns(record: record)) {
            ApplyCustodyRecord(record: record);
            CustodyTap?.Invoke(obj: record);
        }
    }

    /// <summary>Makes one custody record durable and, once it is, folds it through this authority's custody reducer
    /// and reports it to <see cref="CustodyTap"/>. A record that is refused or uncertain is never folded: an
    /// uncertain one leaves the authority latched, answering about its key as uncertain, until a recovered activation
    /// reads whether it landed. One call appends one record, so a crash between two steps finds the state between
    /// them.</summary>
    /// <param name="record">The custody record (<see cref="WorldCompanionCustody.Folds"/>, never an arrival).</param>
    /// <param name="reason">Why the record is not known to be durable, when it is not.</param>
    /// <returns>What became of the record.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="record"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="record"/> is not a custody record.</exception>
    public WorldCrossingDurability RecordCustody(WorldCrossingRecord record, out string reason) {
        ArgumentNullException.ThrowIfNull(argument: record);
        if (!WorldCompanionCustody.Folds(record: record) || (record is WorldCrossingRecord.Arrival)) {
            throw new ArgumentException(
                message: $"'{record.GetType().Name}' is not a custody record",
                paramName: nameof(record)
            );
        }

        var refusal = string.Empty;
        var durability = ExecuteAuthorityOperation(operation: () => RecordCustodyHeld(
            reason: out refusal,
            record: record
        ));

        reason = refusal;
        return durability;
    }
    /// <summary>Returns whether this authority is latched on an uncertain crossing record about
    /// <paramref name="key"/>: it cannot yet call its answer about that key durable.</summary>
    /// <param name="key">The transfer key.</param>
    /// <returns><see langword="true"/> while an uncertain record about the key awaits recovery.</returns>
    public bool IsUncertainAbout(WorldTransferKey key) => ExecuteAuthorityOperation(operation: () => IsUncertainAboutHeld(key: key));

    // Callers hold the authority gate.
    internal WorldCrossingDurability RecordCustodyHeld(WorldCrossingRecord record, out string reason) => RecordCrossingHeld(
        reason: out reason,
        record: record
    );
}
