namespace Puck.World.Protocol;

/// <summary>The stream a reader is bound to and the span of it the reader holds.</summary>
/// <param name="Stream">The bound stream's identity; zero while no spanned stream is bound.</param>
/// <param name="Generation">The span the reader holds: the generation of the last record it took.</param>
/// <param name="Ordinal">The ordinal of the last record the reader took.</param>
/// <param name="Closed">Whether the span closed: the stream ended or can no longer tell the reader of a cut.</param>
public readonly record struct WorldStreamSpan(ulong Stream, long Generation, long Ordinal, bool Closed);

/// <summary>A reader's ingress: the one writer of the span it holds, which decides whether a stamped record may move
/// the reader. A reader takes records from the one spanned stream that bound it, at or past the generation it holds and
/// strictly after the last ordinal it took; the first spanned record of an unbound reader binds it. A record of another
/// stream, a late record of an earlier generation, or one not ahead of the last ordinal is dropped and counted, so it
/// cannot bring an earlier generation back. A span that closes (a terminal record, or <see cref="Close"/> when the
/// stream can no longer reach the reader) unbinds the reader, and the stream that held it never binds it again; a new
/// stream reaches the reader only as a new admission does, with its own identity. An unspanned record (the authority's
/// own fan-out, <see cref="WorldStreamStamp.Unspanned"/>) moves only a reader no spanned stream holds.</summary>
/// <remarks>Records of one stream arrive from one thread at a time, in the order that stream sends them; the gate makes
/// each decision whole against a <see cref="Close"/> raised from another thread.</remarks>
public sealed class WorldStreamIngress {
    private readonly Lock m_gate = new();
    private WorldStreamSpan m_span;
    // The last stream whose span closed: it never binds this reader again.
    private ulong m_closed;
    private long m_refusals;

    /// <summary>Gets the stream the reader is bound to and the span it holds.</summary>
    public WorldStreamSpan Span { get { lock (m_gate) { return m_span; } } }
    /// <summary>Gets the number of records dropped.</summary>
    public long Refusals => Interlocked.Read(location: ref m_refusals);

    /// <summary>Decides whether a content record may move the reader, and takes it into the span when it may.</summary>
    /// <param name="stream">The record's stamp.</param>
    /// <returns><see langword="true"/> when the record may move the reader.</returns>
    public bool Admit(in WorldStreamStamp stream) {
        lock (m_gate) {
            return Take(stream: in stream);
        }
    }
    /// <summary>Decides whether a cut record of the bound stream moves the reader, and takes it: a cut moves the span
    /// to its generation, and a terminal record closes the span.</summary>
    /// <param name="stream">The record's stamp.</param>
    /// <param name="terminal">Whether the record ends its stream.</param>
    /// <returns><see langword="true"/> when the record belongs to the stream the reader is bound to and was
    /// taken.</returns>
    public bool Cut(in WorldStreamStamp stream, bool terminal) {
        lock (m_gate) {
            if (!stream.IsSpanned || (m_span.Stream != stream.Stream) || m_span.Closed || !Take(stream: in stream)) {
                if (stream.IsSpanned && (m_span.Stream != stream.Stream)) { Refuse(); }
                return false;
            }
            if (terminal) { CloseLocked(); }
            return true;
        }
    }
    /// <summary>Closes the span the reader holds, when one is open.</summary>
    /// <returns><see langword="true"/> when an open span closed.</returns>
    public bool Close() {
        lock (m_gate) {
            if ((m_span.Stream == 0UL) || m_span.Closed) { return false; }
            CloseLocked();
            return true;
        }
    }

    private void CloseLocked() {
        m_closed = m_span.Stream;
        m_span = m_span with { Closed = true };
    }
    private bool Take(in WorldStreamStamp stream) {
        if (!stream.IsSpanned) {
            if ((m_span.Stream == 0UL) || m_span.Closed) { return true; }
            Refuse();
            return false;
        }
        if ((m_span.Stream == 0UL) || m_span.Closed) {
            if (stream.Stream == m_closed) { Refuse(); return false; }
            m_span = new WorldStreamSpan(Closed: false, Generation: stream.Generation, Ordinal: stream.Ordinal, Stream: stream.Stream);
            return true;
        }
        if ((stream.Stream != m_span.Stream) || (stream.Generation < m_span.Generation) || (stream.Ordinal <= m_span.Ordinal)) {
            Refuse();
            return false;
        }
        m_span = m_span with { Generation = stream.Generation, Ordinal = stream.Ordinal };
        return true;
    }
    private void Refuse() => Interlocked.Increment(location: ref m_refusals);
}
