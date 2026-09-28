using Puck.World.Protocol;

namespace Puck.World;

/// <summary>
/// The editor's edits to placements, one line per placement per world, each line a small state machine: at most one
/// edit in flight, the edits queued behind it in order, and the last value the world confirmed. An edit is based on the
/// line's latest value (<see cref="Latest"/>), never on the document as this host last saw it, which trails every edit
/// in flight.
/// <para>Every edit enters already validated and composed under its own principal (<see cref="Edit.Mutation"/>), so
/// it goes out exactly as its own issuer made it. A queued edit supersedes the one queued before it only when both are
/// the same principal's, since it was composed on top of it; another principal's edit queues behind it instead, and each
/// goes out in turn under its own principal.</para>
/// <para>Settlement is total and idempotent. A verdict names the submission it answers (<see cref="Submission.Token"/>),
/// so a verdict for anything but the edit in flight changes nothing, and one path serves a refusal returned inline and
/// one arriving later alike: the line drops every edit queued on the refused one and rolls back to the last value the
/// world confirmed.</para>
/// <para>A confirmed value only moves forward. The world stamps no version on the documents it delivers, and a
/// document can arrive after the verdict of an edit it predates, so a line cannot order a delivered document against
/// its verdicts by arrival. It orders by value instead: once the world has confirmed an edit, the line holds that value
/// until a delivered document shows it, and releases the placement to the delivered document from then on. Until then a
/// delivered document, whatever it shows, never replaces the confirmed value.</para>
/// <para>Thread-safe: verdicts may arrive on any thread.</para>
/// </summary>
public sealed class WorldEditorEditQueue {
    /// <summary>One edit, validated and composed before it enters a line.</summary>
    /// <param name="World">The world's instance name, as the seat's route names it.</param>
    /// <param name="Row">The placement row the edit writes.</param>
    /// <param name="Mutation">The upsert, composed under the principal that issued the edit.</param>
    /// <param name="Verb">The verb that issued it, which names its verdict.</param>
    public sealed record Edit(string World, WorldPlacement Row, WorldMutation Mutation, string Verb);
    /// <summary>An edit handed to its world's link.</summary>
    /// <param name="Token">The submission's own token, which its verdict names.</param>
    /// <param name="Edit">The edit.</param>
    public readonly record struct Submission(long Token, Edit Edit);
    /// <summary>What a verdict did to its line.</summary>
    /// <param name="Answered">Whether the verdict answered the edit in flight; a verdict for anything else changes
    /// nothing.</param>
    /// <param name="Next">The queued edit now in flight, which the caller submits, or <see langword="null"/>.</param>
    /// <param name="RolledBack">Whether the edit was refused and the line rolled back.</param>
    /// <param name="RolledBackTo">The value the line rolled back to: the last value the world confirmed, else the row
    /// before the line's first edit, or <see langword="null"/> for a placement that was never placed.</param>
    /// <param name="Dropped">How many queued edits the refusal dropped with it.</param>
    public readonly record struct Settlement(bool Answered, Submission? Next, bool RolledBack, WorldPlacement? RolledBackTo, int Dropped);

    private readonly Lock m_gate = new();
    private readonly Dictionary<(string World, string Id), Line> m_lines = [];

    private long m_nextToken;

    private sealed class Line {
        public WorldPlacement? Confirmed { get; set; }
        public bool HasConfirmed { get; set; }
        public Submission? InFlight { get; set; }
        public WorldPlacement? Origin { get; init; }
        public List<Edit> Queued { get; } = [];
    }

    // Two rows agree on everything an editor verb writes.
    private static bool Same(WorldPlacement? left, WorldPlacement? right) => (
        ((left is null) && (right is null)) ||
        ((left is not null) && (right is not null) &&
        string.Equals(a: left.Id, b: right.Id, comparisonType: StringComparison.Ordinal) &&
        string.Equals(a: left.PrototypeId, b: right.PrototypeId, comparisonType: StringComparison.Ordinal) &&
        (left.Position.Value == right.Position.Value) &&
        (left.YawDegrees == right.YawDegrees) &&
        (left.Scale == right.Scale))
    );

    /// <summary>Returns whether a placement id has a line in a world: an edit in flight or queued, or a confirmed value no
    /// delivered document has shown yet, so a new placement may not take the id.</summary>
    /// <param name="world">The world's instance name.</param>
    /// <param name="id">The placement id.</param>
    /// <returns><see langword="true"/> when a line holds the id.</returns>
    public bool IsReserved(string world, string id) {
        lock (m_gate) {
            return m_lines.ContainsKey(key: (world, id));
        }
    }
    /// <summary>Returns whether a placement has an edit in flight in a world, so a new edit to it queues.</summary>
    /// <param name="world">The world's instance name.</param>
    /// <param name="id">The placement id.</param>
    /// <returns><see langword="true"/> when an edit is in flight.</returns>
    public bool IsInFlight(string world, string id) {
        lock (m_gate) {
            return (m_lines.TryGetValue(key: (world, id), value: out var line) && (line.InFlight is not null));
        }
    }
    /// <summary>Returns the value an edit to a placement is based on: the last queued edit's, else the one in flight's,
    /// else the last value the world confirmed until a delivered document shows it, else the delivered row. A line whose
    /// confirmed value the delivered document shows is released.</summary>
    /// <param name="world">The world's instance name.</param>
    /// <param name="id">The placement id.</param>
    /// <param name="delivered">The placement's row in the document as this host last saw it, or <see langword="null"/>
    /// when it holds none.</param>
    /// <returns>The row, or <see langword="null"/> when there is none.</returns>
    public WorldPlacement? Latest(string world, string id, WorldPlacement? delivered) {
        lock (m_gate) {
            if (!m_lines.TryGetValue(key: (world, id), value: out var line)) {
                return delivered;
            }

            if (line.Queued.Count > 0) {
                return line.Queued[^1].Row;
            }

            if (line.InFlight is { } inFlight) {
                return inFlight.Edit.Row;
            }

            if (!line.HasConfirmed || Same(left: delivered, right: line.Confirmed)) {
                _ = m_lines.Remove(key: (world, id));

                return delivered;
            }

            return line.Confirmed;
        }
    }
    /// <summary>Offers an edit to its placement's line: in flight at once when nothing is, else queued behind the edit in
    /// flight, superseding the last queued edit when that is the same principal's.</summary>
    /// <param name="edit">The edit, already validated and composed.</param>
    /// <param name="origin">The placement's row before the edit, which a line this edit opens rolls back to when its world
    /// confirms nothing; <see langword="null"/> for a placement not yet placed.</param>
    /// <returns>The submission to hand to the world's link now, or <see langword="null"/> when the edit was queued.</returns>
    public Submission? Offer(Edit edit, WorldPlacement? origin) {
        ArgumentNullException.ThrowIfNull(argument: edit);

        var key = (edit.World, edit.Row.Id);

        lock (m_gate) {
            if (!m_lines.TryGetValue(key: key, value: out var line)) {
                line = new Line { Origin = origin };
                m_lines[key] = line;
            }

            if (line.InFlight is null) {
                var submission = new Submission(Edit: edit, Token: ++m_nextToken);

                line.InFlight = submission;

                return submission;
            }

            if ((line.Queued.Count > 0) && (line.Queued[^1].Mutation.Principal == edit.Mutation.Principal)) {
                line.Queued[^1] = edit;
            } else {
                line.Queued.Add(item: edit);
            }

            return null;
        }
    }
    /// <summary>Settles the edit in flight on a line with its world's verdict. Applied, its value becomes the confirmed one
    /// and the first queued edit goes in flight. Refused (any verdict but applied, or a submission that failed), every
    /// queued edit is dropped with it, since each was composed on top of it, and the line rolls back to the last confirmed
    /// value. A verdict that names any submission but the one in flight changes nothing.</summary>
    /// <param name="world">The world's instance name.</param>
    /// <param name="id">The placement id.</param>
    /// <param name="token">The token of the submission the verdict answers.</param>
    /// <param name="applied">Whether the world applied it.</param>
    /// <returns>What the verdict did.</returns>
    public Settlement Settle(string world, string id, long token, bool applied) {
        lock (m_gate) {
            if (
                !m_lines.TryGetValue(key: (world, id), value: out var line) ||
                (line.InFlight is not { } settled) ||
                (settled.Token != token)
            ) {
                return default;
            }

            if (!applied) {
                var dropped = line.Queued.Count;
                var rolledBackTo = (line.HasConfirmed ? line.Confirmed : line.Origin);

                line.InFlight = null;
                line.Queued.Clear();

                if (!line.HasConfirmed) {
                    _ = m_lines.Remove(key: (world, id));
                }

                return new Settlement(Answered: true, Dropped: dropped, Next: null, RolledBack: true, RolledBackTo: rolledBackTo);
            }

            line.Confirmed = settled.Edit.Row;
            line.HasConfirmed = true;
            line.InFlight = null;

            if (line.Queued.Count > 0) {
                line.InFlight = new Submission(Edit: line.Queued[0], Token: ++m_nextToken);
                line.Queued.RemoveAt(index: 0);
            }

            return new Settlement(Answered: true, Dropped: 0, Next: line.InFlight, RolledBack: false, RolledBackTo: null);
        }
    }
}
