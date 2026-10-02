using Puck.World.Protocol;

namespace Puck.World.Server;

public sealed partial class WorldServer {
    private IWorldCrossingLog? m_crossingLog;
    private ulong? m_uncertainCrossing;

    // Set while the escrow lands a committed cohort: the admissions it makes are the arrival's own consequences.
    internal bool LandingArrival { get; set; }

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

    /// <summary>Gets the crossing sequence whose record this activation's log answered
    /// <see cref="WorldCrossingDurability.Uncertain"/>, or <see langword="null"/> while every record is known. Once
    /// set it stays set: the authority records no further crossing step until a recovered activation replaces it.</summary>
    public ulong? UncertainCrossing => ExecuteAuthorityOperation(operation: () => m_uncertainCrossing);

    /// <summary>Makes one source-side crossing record durable under this authority's crossing sequence. Succeeds
    /// without writing anything when no log is installed. A source acts on a record only once it is durable, so an
    /// uncertain record refuses its step here exactly as a refused one does; recovery redoes it if it landed.</summary>
    /// <param name="record">The record.</param>
    /// <param name="reason">Why the record is not known to be durable, when it is not.</param>
    /// <returns><see langword="true"/> once the record is durable or no log is installed.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="record"/> is <see langword="null"/>.</exception>
    public bool TryRecordCrossing(WorldCrossingRecord record, out string reason) {
        ArgumentNullException.ThrowIfNull(argument: record);

        var refusal = string.Empty;
        var durability = ExecuteAuthorityOperation(operation: () => RecordCrossingHeld(
            reason: out refusal,
            record: record
        ));

        reason = refusal;
        return (durability == WorldCrossingDurability.Durable);
    }

    // Callers hold the authority gate: the sequence a record takes and the state it describes are one atomic fact.
    internal WorldCrossingDurability RecordCrossingHeld(WorldCrossingRecord record, out string reason) {
        if (m_crossingLog is not { } log) {
            reason = string.Empty;
            return WorldCrossingDurability.Durable;
        }
        if (m_uncertainCrossing is { } uncertain) {
            reason = $"crossing record {uncertain} is uncertain; this authority records nothing more until it recovers";
            return WorldCrossingDurability.Refused;
        }

        var entry = new WorldCrossingEntry(
            Record: record,
            Sequence: m_transferEscrow.CrossingSequence,
            Tick: (NextInputTick - 1UL)
        );
        var durability = log.Append(
            entry: in entry,
            reason: out reason
        );

        if (durability == WorldCrossingDurability.Durable) {
            m_transferEscrow.AdvanceCrossingSequence();
        } else if (durability == WorldCrossingDurability.Uncertain) {
            m_uncertainCrossing = entry.Sequence;
        }
        return durability;
    }
}
