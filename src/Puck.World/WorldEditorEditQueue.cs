using Puck.Commands;
using Puck.World.Protocol;

namespace Puck.World;

/// <summary>
/// The editor's edits to the placements of one world activation, one line per placement, each line a small state
/// machine: at most one edit in flight, the edits queued behind it in order, and the last value the world confirmed
/// while no delivered document reflects it yet. An edit is based on the line's latest value, never on a delivered
/// document that trails an edit in flight.
/// <para>Admission is one atomic step (<see cref="Offer"/>, <see cref="OfferNew"/>): the base is read, the edit is
/// composed on it under the issuing principal, and the edit is admitted, all under the queue's lock, so no verdict can
/// settle between the read and the admission. A queued edit supersedes the one queued before it only when both are
/// the same principal's; another principal's edit queues behind it, and each goes out under its own principal.</para>
/// <para>Order comes from the world's own versions. A verdict carries the <see cref="WorldDocumentVersion"/> it applied
/// at and every delivered document the version it reflects, so a confirmed value is released at the first delivered
/// document at or past it (<see cref="Deliver"/>), whatever that document shows: an undo, a reload, or another
/// editor's write is simply a newer document. A document older than the confirmation never replaces it.</para>
/// <para>Settlement is total and idempotent: a verdict names its submission's token, so a verdict for anything but
/// the edit in flight changes nothing, and one path serves a refusal returned inline and one arriving later.
/// <see cref="Retire"/> ends the queue with its world: every edit in flight or queued is returned as abandoned and
/// nothing is retained.</para>
/// <para>Thread-safe: verdicts and deliveries arrive on any thread.</para>
/// </summary>
/// <param name="activation">The activation of the world this queue edits; a delivery of any other activation is not
/// this world's.</param>
public sealed class WorldEditorEditQueue(Guid activation) {
    /// <summary>One edit, validated and composed before it enters a line.</summary>
    /// <param name="Row">The placement row the edit writes.</param>
    /// <param name="Mutation">The upsert, composed under the principal that issued the edit.</param>
    /// <param name="Verb">The verb that issued it, which names its verdict.</param>
    public sealed record Edit(WorldPlacement Row, WorldMutation Mutation, string Verb);
    /// <summary>An edit handed to its world's link.</summary>
    /// <param name="Token">The submission's own token, which its verdict names.</param>
    /// <param name="Edit">The edit.</param>
    public readonly record struct Submission(long Token, Edit Edit);
    /// <summary>What an offer did.</summary>
    /// <param name="Submitted">The submission to hand to the world's link now, or <see langword="null"/>.</param>
    /// <param name="Queued">The edit queued behind the one in flight, or <see langword="null"/>.</param>
    /// <param name="Refusal">Why nothing was admitted: the composer's refusal, or the queue's own when its world is
    /// gone.</param>
    public readonly record struct Admission(Submission? Submitted, Edit? Queued, CommandResult Refusal) {
        /// <summary>Gets whether an edit was admitted, submitted or queued.</summary>
        public bool Admitted => ((Submitted is not null) || (Queued is not null));
    }
    /// <summary>What a verdict did to its line.</summary>
    /// <param name="Answered">Whether the verdict answered the edit in flight; a verdict for anything else changes
    /// nothing.</param>
    /// <param name="Next">The queued edit now in flight, which the caller submits, or <see langword="null"/>.</param>
    /// <param name="RolledBack">Whether the edit was refused and the line rolled back.</param>
    /// <param name="RolledBackTo">The confirmed value the line rolled back to, or <see langword="null"/> when it rolled
    /// back to the delivered document, which already reflects everything this line had confirmed.</param>
    /// <param name="Dropped">How many queued edits the refusal dropped with it.</param>
    public readonly record struct Settlement(bool Answered, Submission? Next, bool RolledBack, WorldPlacement? RolledBackTo, int Dropped);

    /// <summary>Composes an edit on the base a placement's line holds, under the queue's lock.</summary>
    /// <param name="basis">The base: the line's latest value, or the delivered row; <see langword="null"/> when there is
    /// none.</param>
    /// <param name="queues">Whether the edit will queue behind one in flight rather than go out now.</param>
    /// <param name="refusal">The refusal, when no edit is composed.</param>
    /// <returns>The edit, or <see langword="null"/> to refuse.</returns>
    public delegate Edit? Composer(WorldPlacement? basis, bool queues, out CommandResult refusal);
    /// <summary>Composes a new placement's edit for the id the queue chose, under the queue's lock.</summary>
    /// <param name="id">The placement id: the one asked for, or the first free one minted.</param>
    /// <param name="refusal">The refusal, when no edit is composed.</param>
    /// <returns>The edit, or <see langword="null"/> to refuse.</returns>
    public delegate Edit? NewComposer(string id, out CommandResult refusal);

    private readonly Lock m_gate = new();
    private readonly Dictionary<string, Line> m_lines = new(comparer: StringComparer.Ordinal);
    private long m_delivered = -1L;

    private long m_nextToken;
    private bool m_retired;

    private sealed class Line {
        public WorldPlacement? Confirmed { get; set; }
        public long ConfirmedAt { get; set; }
        public bool HasConfirmed { get; set; }
        public bool Idle => ((InFlight is null) && (Queued.Count == 0));
        public Submission? InFlight { get; set; }
        public List<Edit> Queued { get; } = [];
    }

    /// <summary>Gets the activation of the world this queue edits.</summary>
    public Guid Activation => activation;
    /// <summary>Gets how many placements have a line: an edit in flight or queued, or a confirmed value no delivered
    /// document reflects yet.</summary>
    public int Lines {
        get {
            lock (m_gate) {
                return m_lines.Count;
            }
        }
    }

    private static CommandResult Gone(string verb) => CommandResult.Error(output: $"[{verb}: the world this edit was made in is gone; edit again]");
    // Records a delivered document's version and releases every idle line whose confirmed value it reflects.
    private bool DeliverLocked(WorldDocumentVersion version) {
        if (version.Activation != activation) {
            return false;
        }

        if (version.Sequence <= m_delivered) {
            return true;
        }

        m_delivered = version.Sequence;

        List<string>? released = null;

        foreach (var (id, line) in m_lines) {
            if (line.HasConfirmed && (line.ConfirmedAt <= version.Sequence)) {
                line.HasConfirmed = false;
                line.Confirmed = null;
            }

            if (line.Idle && !line.HasConfirmed) {
                (released ??= []).Add(item: id);
            }
        }

        if (released is not null) {
            foreach (var id in released) {
                _ = m_lines.Remove(key: id);
            }
        }

        return true;
    }
    private WorldPlacement? LatestLocked(Line? line, WorldPlacement? delivered) => (line switch {
        null => delivered,
        { Queued.Count: > 0 } => line.Queued[^1].Row,
        { InFlight: { } inFlight } => inFlight.Edit.Row,
        { HasConfirmed: true } => line.Confirmed,
        _ => delivered,
    });
    private Admission AdmitLocked(string id, Edit edit) {
        if (!m_lines.TryGetValue(key: id, value: out var line)) {
            line = new Line();
            m_lines[id] = line;
        }

        if (line.InFlight is null) {
            var submission = new Submission(Edit: edit, Token: ++m_nextToken);

            line.InFlight = submission;

            return new Admission(Queued: null, Refusal: CommandResult.None, Submitted: submission);
        }

        if ((line.Queued.Count > 0) && (line.Queued[^1].Mutation.Principal == edit.Mutation.Principal)) {
            line.Queued[^1] = edit;
        } else {
            line.Queued.Add(item: edit);
        }

        return new Admission(Queued: edit, Refusal: CommandResult.None, Submitted: null);
    }

    /// <summary>Records a delivered document's version: every confirmed value it reflects is released to the delivered
    /// document.</summary>
    /// <param name="version">The delivered document's version.</param>
    /// <returns><see langword="false"/> when the version belongs to another activation, so the document is not this
    /// queue's world.</returns>
    public bool Deliver(WorldDocumentVersion version) {
        lock (m_gate) {
            return DeliverLocked(version: version);
        }
    }
    /// <summary>Returns the value an edit to a placement would be based on, admitting nothing.</summary>
    /// <param name="id">The placement id.</param>
    /// <param name="delivered">The placement's row in the delivered document, or <see langword="null"/>.</param>
    /// <param name="version">The delivered document's version, recorded first.</param>
    /// <returns>The last queued edit's row, else the one in flight's, else the confirmed value no delivered document
    /// reflects yet, else <paramref name="delivered"/>.</returns>
    public WorldPlacement? Latest(string id, WorldPlacement? delivered, WorldDocumentVersion version) {
        lock (m_gate) {
            _ = DeliverLocked(version: version);

            return LatestLocked(delivered: delivered, line: m_lines.GetValueOrDefault(key: id));
        }
    }
    /// <summary>Reads a placement's base, composes an edit on it and admits it, as one step under the queue's lock: in
    /// flight at once when nothing is, else queued behind the edit in flight.</summary>
    /// <param name="id">The placement id.</param>
    /// <param name="delivered">The placement's row in the delivered document, or <see langword="null"/>.</param>
    /// <param name="version">The delivered document's version, recorded first.</param>
    /// <param name="verb">The verb, which names a refusal of the queue's own.</param>
    /// <param name="compose">Composes the edit on the base.</param>
    /// <returns>What the offer did.</returns>
    public Admission Offer(string id, WorldPlacement? delivered, WorldDocumentVersion version, string verb, Composer compose) {
        ArgumentNullException.ThrowIfNull(argument: compose);

        lock (m_gate) {
            if (m_retired) {
                return new Admission(Queued: null, Refusal: Gone(verb: verb), Submitted: null);
            }

            _ = DeliverLocked(version: version);

            var line = m_lines.GetValueOrDefault(key: id);

            if (compose(
                basis: LatestLocked(delivered: delivered, line: line),
                queues: (line?.InFlight is not null),
                refusal: out var refusal
            ) is not { } edit) {
                return new Admission(Queued: null, Refusal: refusal, Submitted: null);
            }

            return AdmitLocked(edit: edit, id: id);
        }
    }
    /// <summary>Chooses a new placement's id, composes its edit and admits it, as one step under the queue's lock. An
    /// id is taken when the delivered document holds it or a line does.</summary>
    /// <param name="named">The id asked for, or <see langword="null"/> to mint the first free
    /// <paramref name="prefix"/><c>n</c>.</param>
    /// <param name="prefix">The prefix a minted id numbers from 1.</param>
    /// <param name="inDocument">Whether the delivered document holds an id.</param>
    /// <param name="version">The delivered document's version, recorded first.</param>
    /// <param name="verb">The verb, which names a refusal.</param>
    /// <param name="compose">Composes the edit for the chosen id.</param>
    /// <returns>What the offer did.</returns>
    public Admission OfferNew(string? named, string prefix, Func<string, bool> inDocument, WorldDocumentVersion version, string verb, NewComposer compose) {
        ArgumentNullException.ThrowIfNull(argument: inDocument);
        ArgumentNullException.ThrowIfNull(argument: compose);

        lock (m_gate) {
            if (m_retired) {
                return new Admission(Queued: null, Refusal: Gone(verb: verb), Submitted: null);
            }

            _ = DeliverLocked(version: version);

            var id = named;

            if (id is null) {
                for (var n = 1; ; n++) {
                    var candidate = $"{prefix}{n}";

                    if (!m_lines.ContainsKey(key: candidate) && !inDocument(arg: candidate)) {
                        id = candidate;

                        break;
                    }
                }
            } else if (m_lines.ContainsKey(key: id) || inDocument(arg: id)) {
                return new Admission(Queued: null, Refusal: CommandResult.Error(output: $"[{verb}: a placement '{id}' already exists]"), Submitted: null);
            }

            if (compose(id: id, refusal: out var refusal) is not { } edit) {
                return new Admission(Queued: null, Refusal: refusal, Submitted: null);
            }

            return AdmitLocked(edit: edit, id: id);
        }
    }
    /// <summary>Ends the queue with its world: every line is removed and nothing is admitted afterwards.</summary>
    /// <returns>Every edit that was in flight or queued, in line order, now abandoned.</returns>
    public IReadOnlyList<Edit> Retire() {
        lock (m_gate) {
            m_retired = true;

            var abandoned = new List<Edit>();

            foreach (var line in m_lines.Values) {
                if (line.InFlight is { } inFlight) {
                    abandoned.Add(item: inFlight.Edit);
                }

                abandoned.AddRange(collection: line.Queued);
            }

            m_lines.Clear();

            return abandoned;
        }
    }
    /// <summary>Settles the edit in flight on a line with its world's verdict. Applied, its value is confirmed at the
    /// verdict's version, held until a delivered document reflects it, and the first queued edit goes in flight.
    /// Refused (any verdict but applied, or a submission that failed), every queued edit is dropped with it, since each
    /// was composed on top of it, and the line rolls back. A verdict that names any submission but the one in flight
    /// changes nothing.</summary>
    /// <param name="id">The placement id.</param>
    /// <param name="token">The token of the submission the verdict answers.</param>
    /// <param name="applied">Whether the world applied it.</param>
    /// <param name="version">The version the verdict carries: the document the edit's install produced.</param>
    /// <returns>What the verdict did.</returns>
    public Settlement Settle(string id, long token, bool applied, WorldDocumentVersion version) {
        lock (m_gate) {
            if (
                !m_lines.TryGetValue(key: id, value: out var line) ||
                (line.InFlight is not { } settled) ||
                (settled.Token != token)
            ) {
                return default;
            }

            if (!applied) {
                var dropped = line.Queued.Count;
                var rolledBackTo = (line.HasConfirmed ? line.Confirmed : null);

                line.InFlight = null;
                line.Queued.Clear();

                if (!line.HasConfirmed) {
                    _ = m_lines.Remove(key: id);
                }

                return new Settlement(Answered: true, Dropped: dropped, Next: null, RolledBack: true, RolledBackTo: rolledBackTo);
            }

            line.Confirmed = settled.Edit.Row;
            line.ConfirmedAt = version.Sequence;
            line.HasConfirmed = ((version.Activation != activation) || (version.Sequence > m_delivered));
            line.InFlight = null;

            if (line.Queued.Count > 0) {
                line.InFlight = new Submission(Edit: line.Queued[0], Token: ++m_nextToken);
                line.Queued.RemoveAt(index: 0);
            } else if (!line.HasConfirmed) {
                _ = m_lines.Remove(key: id);
            }

            return new Settlement(Answered: true, Dropped: 0, Next: line.InFlight, RolledBack: false, RolledBackTo: null);
        }
    }
}
