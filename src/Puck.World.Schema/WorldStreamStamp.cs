namespace Puck.World.Protocol;

/// <summary>Names one record of one delivery stream: the stream it belongs to, the span of that stream it was composed
/// in, and its place in the stream. The stream's lineage issues every value (<see cref="WorldStreamLineage.Stamp"/>,
/// reached through <see cref="WorldDeliveryCursor.Stamp"/>), and a spanned mirror's ingress accepts a record only from
/// the stream it is bound to, at or past the generation it holds and strictly after the last ordinal it took.</summary>
/// <remarks>The stream identity is minted at random per lineage and derived from no principal, policy or table, and the
/// generation moves only on a cut the recipient's own delivery shows (<see cref="WorldDeliveryCursor"/>) or on a
/// handoff of the stream to another authority (<see cref="WorldStreamLineage.Open"/>), so neither tells a recipient
/// anything about state it cannot read. Delivery metadata, never simulation state: no hash, checkpoint or replay reads
/// it.</remarks>
/// <param name="Stream">The stream's opaque identity; zero for the authority's own unfiltered fan-out, which has one
/// span that never ends and is ordered by its single delivering thread.</param>
/// <param name="Generation">The stream's span: zero at admission, one more at each cut.</param>
/// <param name="Ordinal">The record's place in the stream, rising by one with every record the stream sends.</param>
public readonly record struct WorldStreamStamp(ulong Stream, long Generation, long Ordinal) {
    /// <summary>Gets the stamp of the authority's own unfiltered fan-out, which no ingress orders and no cut ends.</summary>
    public static WorldStreamStamp Unspanned => default;

    /// <summary>Gets whether the record belongs to a spanned stream, one whose ingress orders it and whose cuts end its
    /// spans.</summary>
    public bool IsSpanned => (Stream != 0UL);
}

/// <summary>One delivery stream's identity and counters, which outlive any one authority that stamps its records. A
/// recipient's stream is served by one authority at a time, a segment; when the recipient's traveler moves to another
/// authority, the next segment opens the same lineage and goes on from its counters, so the recipient's one ingress
/// orders both authorities' records as one stream.</summary>
/// <remarks><para>Invariant: every record of the stream carries the stream's one identity, a generation no lower than
/// any earlier record's and an ordinal above every earlier record's, whichever authority composed it; and a segment's
/// first record opens a generation no earlier segment stamped. A record a previous segment composed therefore can
/// never pass an ingress that has taken the next segment's cut, and a recipient never pairs one authority's document
/// with another's deltas.</para>
/// <para>What enforces it: the counters live here and nowhere else, <see cref="Open"/> is the one way to become the
/// segment that stamps, and it moves the generation before it returns (a segment in another process opens its own
/// copy, taken up from <see cref="Lend"/>, the same way). <see cref="Stamp"/> refuses an owner that a
/// later <see cref="Open"/> replaced, and <see cref="Cut"/> from such an owner changes nothing, so a segment that has
/// been handed on cannot move the stream it no longer serves. Where it stops: the stream's records reach the wire in
/// the order their segments wrote them, which the one door that opens a traveler's segments keeps by running them one
/// after another (<c>WorldTravelerProjection</c>).</para></remarks>
public sealed class WorldStreamLineage {
    private readonly Lock m_gate = new();
    private object? m_owner;
    private long m_generation;
    private long m_ordinal;

    /// <summary>Starts a stream no record was stamped in, under an identity minted at random.</summary>
    public WorldStreamLineage() => Stream = Puck.Maths.SecureRandom.NextUInt<ulong>(maximum: ulong.MaxValue, minimum: 1UL);
    /// <summary>Takes up a stream another authority's process started, from the last stamp its records carried.</summary>
    /// <param name="last">The stream's identity, its generation and the last ordinal it stamped; an ordinal of zero
    /// for a stream nothing was stamped in.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="last"/> names no spanned stream, or a negative
    /// generation or ordinal.</exception>
    public WorldStreamLineage(in WorldStreamStamp last) {
        if (!last.IsSpanned || (last.Generation < 0L) || (last.Ordinal < 0L)) {
            throw new ArgumentOutOfRangeException(paramName: nameof(last), message: "a stream is taken up from a spanned identity and counters that are not negative");
        }
        Stream = last.Stream;
        m_generation = last.Generation;
        m_ordinal = last.Ordinal;
    }

    /// <summary>Gets the stream's opaque identity.</summary>
    public ulong Stream { get; }
    /// <summary>Gets the stream's current span.</summary>
    public long Generation { get { lock (m_gate) { return m_generation; } } }
    /// <summary>Makes <paramref name="owner"/> the one segment that stamps the stream. A stream that has stamped a
    /// record opens a new generation for the segment, which owes its reader the cut and the whole of what it discloses
    /// before anything else.</summary>
    /// <param name="owner">The segment's identity.</param>
    /// <returns><see langword="true"/> when the segment owes a cut: the stream had stamped a record.</returns>
    public bool Open(object owner) {
        ArgumentNullException.ThrowIfNull(argument: owner);
        lock (m_gate) {
            m_owner = owner;
            if (m_ordinal == 0L) {
                return false;
            }
            m_generation = checked(m_generation + 1L);

            return true;
        }
    }
    /// <summary>Makes <paramref name="owner"/> the segment that relays the stream from another process. That process
    /// takes the stream up from the returned stamp and stamps its records itself; this lineage follows them as they
    /// are relayed (<see cref="Follow"/>), and its own counters do not move until they are.</summary>
    /// <param name="owner">The relaying segment's identity.</param>
    /// <returns>The stream's identity, its generation and the last ordinal it stamped.</returns>
    public WorldStreamStamp Lend(object owner) {
        ArgumentNullException.ThrowIfNull(argument: owner);
        lock (m_gate) {
            m_owner = owner;

            return new WorldStreamStamp(Generation: m_generation, Ordinal: m_ordinal, Stream: Stream);
        }
    }
    /// <summary>Stamps the stream's next record.</summary>
    /// <param name="owner">The segment stamping.</param>
    /// <returns>The record's stamp.</returns>
    /// <exception cref="InvalidOperationException"><paramref name="owner"/> is not the segment that last opened the
    /// stream.</exception>
    public WorldStreamStamp Stamp(object owner) {
        lock (m_gate) {
            if (!ReferenceEquals(objA: m_owner, objB: owner)) {
                throw new InvalidOperationException(message: "a delivery stream is stamped only by the segment that last opened it");
            }

            return new WorldStreamStamp(Generation: m_generation, Ordinal: (m_ordinal = checked(m_ordinal + 1L)), Stream: Stream);
        }
    }
    /// <summary>Opens the stream's next generation for the segment that serves it.</summary>
    /// <param name="owner">The segment cutting.</param>
    /// <returns><see langword="true"/> when the generation moved; <see langword="false"/> for a segment a later
    /// <see cref="Open"/> replaced, which decides nothing for the stream.</returns>
    public bool Cut(object owner) {
        lock (m_gate) {
            if (!ReferenceEquals(objA: m_owner, objB: owner)) {
                return false;
            }
            m_generation = checked(m_generation + 1L);

            return true;
        }
    }
    /// <summary>Records a stamp a segment in another process issued for this stream, as its record is relayed, so the
    /// next segment here goes on after it.</summary>
    /// <param name="owner">The relaying segment, which last opened the stream.</param>
    /// <param name="relayed">The relayed record's stamp.</param>
    /// <returns><see langword="true"/> when the stamp belongs to this stream, from the relaying segment, at or past
    /// the generation and after the ordinal the stream stands at; a relay ends on any other.</returns>
    public bool Follow(object owner, in WorldStreamStamp relayed) {
        lock (m_gate) {
            if (
                !ReferenceEquals(objA: m_owner, objB: owner) ||
                (relayed.Stream != Stream) ||
                (relayed.Generation < m_generation) ||
                (relayed.Ordinal <= m_ordinal)
            ) {
                return false;
            }
            m_generation = relayed.Generation;
            m_ordinal = relayed.Ordinal;

            return true;
        }
    }
}
