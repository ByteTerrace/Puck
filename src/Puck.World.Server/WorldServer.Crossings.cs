using Puck.World.Protocol;

namespace Puck.World.Server;

public sealed partial class WorldServer {
    private readonly List<WorldCrossingArrival> m_arrivalNotes = [];

    private IWorldCrossingLog? m_crossingLog;

    // Set while the escrow lands a committed cohort: the admissions it makes are the arrival's own consequences.
    internal bool LandingArrival { get; set; }

    /// <summary>Gets or sets the observer of every arrival this authority lands. A commit lands under the authority
    /// gate on whichever thread carried it — the host's drain for a colocated source, a socket worker for a federated
    /// one — so the arrival is held and handed to this observer at the start of the next step, on the stepping thread,
    /// in landing order. The replay tape attaches here while it records, so the arrival joins the tick group the step
    /// it preceded applies.</summary>
    public Action<WorldCrossingArrival>? ArrivalTap { get; set; }
    /// <summary>Gets or sets the observer of the federated device images each step applies: the input a forwarded or
    /// federated traveler drives this authority's body with, which reaches it through no loopback. It hears the held
    /// set at the start of every step while any image is held and once more as the set empties, on the stepping thread
    /// under the authority gate. The replay tape attaches here while it records.</summary>
    public Action<IReadOnlyList<(int Index, IntentSubmission Submission)>>? FederatedIntentTap { get; set; }
    /// <summary>Gets the durable crossing log this authority writes ahead through, or <see langword="null"/> when it
    /// keeps no crossing beyond its own process.</summary>
    public IWorldCrossingLog? CrossingLog => m_crossingLog;
    /// <summary>Gets the first crossing sequence this authority has not yet made durable — the watermark a checkpoint
    /// captures and recovery redoes from.</summary>
    public ulong CrossingSequence => ExecuteAuthorityOperation(operation: () => m_transferEscrow.CrossingSequence);

    /// <summary>Replaces every held federated device image with a recorded set — the replay's door for the images a
    /// recorded step held.</summary>
    /// <param name="held">The images, each with the body index it drives.</param>
    /// <exception cref="ArgumentNullException"><paramref name="held"/> is <see langword="null"/>.</exception>
    public void ReplaceFederatedIntents(IReadOnlyList<(int Index, IntentSubmission Submission)> held) {
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
    /// <summary>Makes one source-side crossing record durable under this authority's crossing sequence. Succeeds
    /// without writing anything when no log is installed.</summary>
    /// <param name="record">The record.</param>
    /// <param name="reason">Why the record did not land, when it did not.</param>
    /// <returns><see langword="true"/> once the record is durable or no log is installed.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="record"/> is <see langword="null"/>.</exception>
    public bool TryRecordCrossing(WorldCrossingRecord record, out string reason) {
        ArgumentNullException.ThrowIfNull(argument: record);

        var refusal = string.Empty;
        var landed = ExecuteAuthorityOperation(operation: () => RecordCrossingHeld(
            reason: out refusal,
            record: record
        ));

        reason = refusal;
        return landed;
    }

    // Callers hold the authority gate: the sequence a record takes and the state it describes are one atomic fact.
    internal bool RecordCrossingHeld(WorldCrossingRecord record, out string reason) {
        if (m_crossingLog is not { } log) {
            reason = string.Empty;
            return true;
        }

        var entry = new WorldCrossingEntry(
            Record: record,
            Sequence: m_transferEscrow.CrossingSequence,
            Tick: (NextInputTick - 1UL)
        );

        if (!log.TryAppend(
            entry: in entry,
            reason: out reason
        )) {
            return false;
        }
        m_transferEscrow.AdvanceCrossingSequence();
        return true;
    }
    // Callers hold the authority gate.
    internal void NoteArrival(WorldCrossingArrival arrival) {
        if (ArrivalTap is not null) {
            m_arrivalNotes.Add(item: arrival);
        }
    }
    // Runs at the start of a step, under the authority gate, on the stepping thread.
    internal void FlushArrivalNotes() {
        if (m_arrivalNotes.Count == 0) {
            return;
        }
        if (ArrivalTap is { } tap) {
            foreach (var arrival in m_arrivalNotes) {
                tap(obj: arrival);
            }
        }
        m_arrivalNotes.Clear();
    }
}
