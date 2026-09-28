using Puck.Commands;
using Puck.World.Protocol;

namespace Puck.World;

/// <summary>
/// The editor's edits to the placements of one world activation, one line per placement, each line a small state
/// machine: at most one edit in flight, the edits queued behind it in order, and the last value the world confirmed
/// while no delivered document reflects it yet. Order is per world; the link and the principal are per edit, so seats
/// reaching one world through different endpoints share its lines, and each edit goes out through its own endpoint
/// (<see cref="Source"/>) under its own principal.
/// <para>Every transition (offer, settle, dispatch, delivery, abandonment, retirement) happens under the queue's lock,
/// against the world's newest known state (the newest document any of its endpoints delivered, read inside the lock,
/// never captured before it) and against the queue's retirement state. An offer reads the base, composes the edit on it and admits it in one step; an edit
/// that goes in flight is handed to <see cref="Sender"/> inside the same step, after checking that its source still
/// delivers this activation; a retired queue never sends. A queued edit supersedes the one queued before it only when
/// both are the same principal's.</para>
/// <para>Order comes from the world's own versions: a verdict carries the <see cref="WorldDocumentVersion"/> it
/// applied at and every delivered document the version it reflects, so a confirmed value is released at the first
/// delivered document at or past it (<see cref="Deliver"/>), whatever that document shows. A malformed version (no
/// activation, a negative sequence) is ignored.</para>
/// <para>Settlement is total and idempotent: a verdict names its submission's token, and one path serves a refusal
/// returned inline and one arriving later. Abandoning an edit abandons every edit queued behind it on its line, since
/// each was composed on top of it.</para>
/// </summary>
/// <param name="activation">The activation of the world this queue edits.</param>
/// <param name="send">Hands a submission to its source's link; called under the queue's lock.</param>
public sealed class WorldEditorEditQueue(Guid activation, WorldEditorEditQueue.Sender send) {
    /// <summary>Where an edit comes from: the world as its issuer's route names it, the endpoint link it goes out
    /// through, and the read of the document that endpoint delivers now.</summary>
    /// <param name="World">The world's name as the issuer's route names it.</param>
    /// <param name="Link">The endpoint link the edit goes out through.</param>
    /// <param name="Delivered">Reads the document the endpoint delivers now, with its version.</param>
    public sealed record Source(string World, IServerLink Link, Func<WorldDeliveredDocument> Delivered);
    /// <summary>One edit, validated and composed before it enters a line.</summary>
    /// <param name="Row">The placement row the edit writes.</param>
    /// <param name="Mutation">The upsert, composed under the principal that issued the edit.</param>
    /// <param name="Verb">The verb that issued it, which names its verdict.</param>
    /// <param name="Source">Where it comes from and goes out through.</param>
    /// <param name="Sent">Run once the edit is handed to its link, or <see langword="null"/>.</param>
    public sealed record Edit(WorldPlacement Row, WorldMutation Mutation, string Verb, Source Source, Action? Sent = null);
    /// <summary>An edit handed to its link.</summary>
    /// <param name="Token">The submission's own token, which its verdict names.</param>
    /// <param name="Edit">The edit.</param>
    public readonly record struct Submission(long Token, Edit Edit);
    /// <summary>What an offer did.</summary>
    /// <param name="Admitted">The edit admitted, or <see langword="null"/> when none was.</param>
    /// <param name="Queued">Whether it queued behind the edit in flight rather than going out.</param>
    /// <param name="Result">The refusal when nothing was admitted, or what the link answered when the edit went
    /// out.</param>
    public readonly record struct Admission(Edit? Admitted, bool Queued, CommandResult Result);
    /// <summary>What a verdict did to its line.</summary>
    /// <param name="Answered">Whether the verdict answered the edit in flight; a verdict for anything else changes
    /// nothing.</param>
    /// <param name="RolledBack">Whether the edit was refused and the line rolled back.</param>
    /// <param name="RolledBackTo">The value the line rolled back to: the confirmed value, else the delivered row, or
    /// <see langword="null"/> when the placement is not placed.</param>
    /// <param name="Dropped">How many queued edits the refusal dropped with it.</param>
    /// <param name="Next">The queued edit that went out next, or <see langword="null"/>.</param>
    /// <param name="NextSent">What the link answered <paramref name="Next"/> with.</param>
    /// <param name="Abandoned">The edits abandoned because the next one's source no longer delivers this
    /// activation.</param>
    public readonly record struct Settlement(bool Answered, bool RolledBack, WorldPlacement? RolledBackTo, int Dropped, Edit? Next, CommandResult NextSent, IReadOnlyList<Edit> Abandoned);

    /// <summary>Hands a submission to its source's link, under the queue's lock.</summary>
    /// <param name="submission">The submission.</param>
    /// <returns>What the link answered.</returns>
    public delegate CommandResult Sender(Submission submission);
    /// <summary>Composes an edit on a placement's base, under the queue's lock.</summary>
    /// <param name="document">The world's newest known state: the newest document any of its endpoints delivered.</param>
    /// <param name="basis">The base: the line's latest value, else the delivered row; <see langword="null"/> when there
    /// is none.</param>
    /// <param name="queues">Whether the edit will queue behind one in flight rather than go out now.</param>
    /// <param name="refusal">The refusal, when no edit is composed.</param>
    /// <returns>The edit, or <see langword="null"/> to refuse.</returns>
    public delegate Edit? Composer(WorldDeliveredDocument document, WorldPlacement? basis, bool queues, out CommandResult refusal);
    /// <summary>Composes a new placement's edit, choosing its id, under the queue's lock.</summary>
    /// <param name="document">The world's newest known state: the newest document any of its endpoints delivered.</param>
    /// <param name="latest">Reads any placement's latest value, as <see cref="Offer"/> would base an edit on it.</param>
    /// <param name="taken">Whether an id is taken: the document holds it, or a line does.</param>
    /// <param name="mint">Mints the first free id numbered from 1 after a prefix.</param>
    /// <param name="refusal">The refusal, when no edit is composed.</param>
    /// <returns>The edit, or <see langword="null"/> to refuse.</returns>
    public delegate Edit? NewComposer(WorldDeliveredDocument document, Func<string, WorldPlacement?> latest, Func<string, bool> taken, Func<string, string> mint, out CommandResult refusal);

    private static readonly IReadOnlyList<Edit> None = [];

    private readonly Lock m_gate = new();
    private readonly Dictionary<string, Line> m_lines = new(comparer: StringComparer.Ordinal);
    private long m_delivered = -1L;

    private WorldDeliveredDocument? m_newest;
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
    private static bool Valid(WorldDocumentVersion version) => (version.IsDelivered && (version.Sequence >= 0L));
    // Records a delivered sequence and releases every confirmed value it reflects; a line left with nothing is removed.
    private void DeliverLocked(long sequence) {
        if (sequence <= m_delivered) {
            return;
        }

        m_delivered = sequence;

        List<string>? released = null;

        foreach (var (id, line) in m_lines) {
            if (line.HasConfirmed && (line.ConfirmedAt <= sequence)) {
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
    }
    // Takes a delivered document of this activation as the world's newest known state when it is newer than any seen.
    private void AcceptLocked(WorldDeliveredDocument document) {
        if ((m_newest is null) || (document.Version.Sequence > m_newest.Version.Sequence)) {
            m_newest = document;
        }

        DeliverLocked(sequence: document.Version.Sequence);
    }
    // Reads the document a source delivers now and returns the world's newest known state, which that read may advance:
    // null when the source's document is not a valid delivery of this activation, so the source no longer reaches this
    // queue's world. A source that lags behind another is based on the newest state all of them delivered.
    private WorldDeliveredDocument? ReadLocked(Source source) {
        var document = source.Delivered();

        if (!Valid(version: document.Version) || (document.Version.Activation != activation)) {
            return null;
        }

        AcceptLocked(document: document);

        return m_newest;
    }
    private WorldPlacement? LatestLocked(string id, WorldDeliveredDocument document) {
        var delivered = WorldDefinitionRows.FindPlacement(id: id, placements: document.Definition.Placements);

        return (m_lines.GetValueOrDefault(key: id) switch {
            null => delivered,
            { Queued.Count: > 0 } line => line.Queued[^1].Row,
            { InFlight: { } inFlight } => inFlight.Edit.Row,
            { HasConfirmed: true } line => line.Confirmed,
            _ => delivered,
        });
    }
    private Admission AdmitLocked(string id, Edit edit) {
        if (!m_lines.TryGetValue(key: id, value: out var line)) {
            line = new Line();
            m_lines[id] = line;
        }

        if (line.InFlight is not null) {
            if ((line.Queued.Count > 0) && (line.Queued[^1].Mutation.Principal == edit.Mutation.Principal)) {
                line.Queued[^1] = edit;
            } else {
                line.Queued.Add(item: edit);
            }

            return new Admission(Admitted: edit, Queued: true, Result: CommandResult.None);
        }

        var submission = new Submission(Edit: edit, Token: ++m_nextToken);

        line.InFlight = submission;

        return new Admission(Admitted: edit, Queued: false, Result: send(submission: submission));
    }
    // Abandons the edits of a line from one position on (0 is the edit in flight, 1 the first queued): each was composed
    // on the one before it. A line left with nothing is removed.
    private void AbandonLocked(string id, Line line, int from, List<Edit> abandoned) {
        var inFlight = ((line.InFlight is null) ? 0 : 1);

        if ((from == 0) && (line.InFlight is { } flying)) {
            abandoned.Add(item: flying.Edit);
            line.InFlight = null;
        }

        var first = Math.Max(val1: 0, val2: (from - inFlight));

        abandoned.AddRange(collection: line.Queued.Skip(count: first));
        line.Queued.RemoveRange(count: (line.Queued.Count - first), index: first);

        if (line.Idle && !line.HasConfirmed) {
            _ = m_lines.Remove(key: id);
        }
    }

    /// <summary>Records a document one of the world's endpoints delivered: the newest across every endpoint is the world's
    /// known state, and every confirmed value it reflects is released to it. A malformed version is ignored.</summary>
    /// <param name="document">The delivered document and its version.</param>
    /// <returns><see langword="false"/> when the version is a valid delivery of another activation, so the document is
    /// not this queue's world.</returns>
    public bool Deliver(WorldDeliveredDocument document) {
        ArgumentNullException.ThrowIfNull(argument: document);

        if (!Valid(version: document.Version)) {
            return true;
        }

        lock (m_gate) {
            if (document.Version.Activation != activation) {
                return false;
            }

            if (!m_retired) {
                AcceptLocked(document: document);
            }

            return true;
        }
    }
    /// <summary>Returns the value an edit to a placement would be based on, admitting nothing.</summary>
    /// <param name="id">The placement id.</param>
    /// <param name="source">The source whose delivered document is read.</param>
    /// <returns>The last queued edit's row, else the one in flight's, else the confirmed value no delivered document
    /// reflects yet, else the delivered row; <see langword="null"/> when there is none or the source no longer reaches
    /// this world.</returns>
    public WorldPlacement? Latest(string id, Source source) {
        ArgumentNullException.ThrowIfNull(argument: source);

        lock (m_gate) {
            return ((ReadLocked(source: source) is { } document) ? LatestLocked(document: document, id: id) : null);
        }
    }
    /// <summary>Reads a placement's base, composes an edit on it and admits it, as one step under the queue's lock: sent
    /// at once when nothing is in flight, else queued behind the edit in flight.</summary>
    /// <param name="id">The placement id.</param>
    /// <param name="verb">The verb, which names a refusal of the queue's own.</param>
    /// <param name="source">Where the edit comes from and goes out through.</param>
    /// <param name="compose">Composes the edit on the base.</param>
    /// <returns>What the offer did.</returns>
    public Admission Offer(string id, string verb, Source source, Composer compose) {
        ArgumentNullException.ThrowIfNull(argument: source);
        ArgumentNullException.ThrowIfNull(argument: compose);

        lock (m_gate) {
            if (m_retired || (ReadLocked(source: source) is not { } document)) {
                return new Admission(Admitted: null, Queued: false, Result: Gone(verb: verb));
            }

            if (compose(
                basis: LatestLocked(document: document, id: id),
                document: document,
                queues: (m_lines.GetValueOrDefault(key: id)?.InFlight is not null),
                refusal: out var refusal
            ) is not { } edit) {
                return new Admission(Admitted: null, Queued: false, Result: refusal);
            }

            return AdmitLocked(edit: edit, id: id);
        }
    }
    /// <summary>Composes a new placement's edit, choosing its id, and admits it, as one step under the queue's lock. An
    /// id is taken when the world's newest known state holds it or a line does; an edit for a taken id is refused
    /// by name.</summary>
    /// <param name="verb">The verb, which names a refusal.</param>
    /// <param name="source">Where the edit comes from and goes out through.</param>
    /// <param name="compose">Composes the edit and chooses its id.</param>
    /// <returns>What the offer did.</returns>
    public Admission OfferNew(string verb, Source source, NewComposer compose) {
        ArgumentNullException.ThrowIfNull(argument: source);
        ArgumentNullException.ThrowIfNull(argument: compose);

        lock (m_gate) {
            if (m_retired || (ReadLocked(source: source) is not { } document)) {
                return new Admission(Admitted: null, Queued: false, Result: Gone(verb: verb));
            }

            bool Taken(string id) => (m_lines.ContainsKey(key: id) || (WorldDefinitionRows.FindPlacement(id: id, placements: document.Definition.Placements) is not null));
            string Mint(string prefix) {
                for (var n = 1; ; n++) {
                    var id = $"{prefix}{n}";

                    if (!Taken(id: id)) {
                        return id;
                    }
                }
            }

            if (compose(
                document: document,
                latest: id => LatestLocked(document: document, id: id),
                mint: Mint,
                refusal: out var refusal,
                taken: Taken
            ) is not { } edit) {
                return new Admission(Admitted: null, Queued: false, Result: refusal);
            }

            if (Taken(id: edit.Row.Id)) {
                return new Admission(Admitted: null, Queued: false, Result: CommandResult.Error(output: $"[{verb}: a placement '{edit.Row.Id}' already exists]"));
            }

            return AdmitLocked(edit: edit, id: edit.Row.Id);
        }
    }    /// <summary>Abandons every edit that goes out through one link, with every edit queued behind it on its line: its
         /// endpoint closed or reaches another world now.</summary>
         /// <param name="link">The link.</param>
         /// <returns>The edits abandoned, in line order.</returns>
    public IReadOnlyList<Edit> Abandon(IServerLink link) {
        ArgumentNullException.ThrowIfNull(argument: link);

        lock (m_gate) {
            var abandoned = new List<Edit>();

            foreach (var (id, line) in m_lines.ToArray()) {
                var edits = ((line.InFlight is { } flying) ? [flying.Edit] : Array.Empty<Edit>()).Concat(second: line.Queued).ToList();
                var from = edits.FindIndex(match: edit => ReferenceEquals(objA: edit.Source.Link, objB: link));

                if (from >= 0) {
                    AbandonLocked(abandoned: abandoned, from: from, id: id, line: line);
                }
            }

            return abandoned;
        }
    }
    /// <summary>Ends the queue with its world: every line is removed, and nothing is admitted or sent afterwards.</summary>
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
    /// verdict's version, held until a delivered document reflects it, and the first queued edit goes out, unless its
    /// source no longer reaches this world, which abandons it and everything queued behind it. Refused (any verdict but
    /// applied, or a submission that failed), every queued edit is dropped with it, since each was composed on top of
    /// it, and the line rolls back. A verdict that names any submission but the one in flight changes nothing.</summary>
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
                return new Settlement(Abandoned: None, Answered: false, Dropped: 0, Next: null, NextSent: CommandResult.None, RolledBack: false, RolledBackTo: null);
            }

            if (!applied) {
                var dropped = line.Queued.Count;
                var rolledBackTo = (line.HasConfirmed
                    ? line.Confirmed
                    : ((ReadLocked(source: settled.Edit.Source) is { } document)
                        ? WorldDefinitionRows.FindPlacement(id: id, placements: document.Definition.Placements)
                        : null));

                line.InFlight = null;
                line.Queued.Clear();

                if (!line.HasConfirmed) {
                    _ = m_lines.Remove(key: id);
                }

                return new Settlement(Abandoned: None, Answered: true, Dropped: dropped, Next: null, NextSent: CommandResult.None, RolledBack: true, RolledBackTo: rolledBackTo);
            }

            line.Confirmed = settled.Edit.Row;
            line.ConfirmedAt = version.Sequence;
            line.HasConfirmed = !(Valid(version: version) && (version.Activation == activation) && (version.Sequence <= m_delivered));
            line.InFlight = null;

            // The document the edit's source already holds may reflect it (the console's own world is delivered live).
            _ = ReadLocked(source: settled.Edit.Source);

            if (line.Queued.Count == 0) {
                if (!line.HasConfirmed) {
                    _ = m_lines.Remove(key: id);
                }

                return new Settlement(Abandoned: None, Answered: true, Dropped: 0, Next: null, NextSent: CommandResult.None, RolledBack: false, RolledBackTo: null);
            }

            var next = line.Queued[0];

            if (ReadLocked(source: next.Source) is null) {
                var abandoned = new List<Edit>();

                AbandonLocked(abandoned: abandoned, from: 0, id: id, line: line);

                return new Settlement(Abandoned: abandoned, Answered: true, Dropped: 0, Next: null, NextSent: CommandResult.None, RolledBack: false, RolledBackTo: null);
            }

            line.Queued.RemoveAt(index: 0);

            var submission = new Submission(Edit: next, Token: ++m_nextToken);

            line.InFlight = submission;

            return new Settlement(Abandoned: None, Answered: true, Dropped: 0, Next: next, NextSent: send(submission: submission), RolledBack: false, RolledBackTo: null);
        }
    }
}
