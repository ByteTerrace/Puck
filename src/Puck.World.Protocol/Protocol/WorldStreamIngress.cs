using Puck.Abstractions.Presentation;

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
/// transport that carried the stream lost it) unbinds the reader, and no stream that ever closed on it binds it again;
/// a new stream reaches the reader only as a new admission does, with its own identity. An unspanned record (the
/// authority's own fan-out, <see cref="WorldStreamStamp.Unspanned"/>) moves only a reader no spanned stream has ever
/// bound: once a reader has held a span it takes spanned records alone, so nothing unordered reaches it after a
/// close.</summary>
/// <remarks>Records of one stream arrive from one thread at a time, in the order that stream sends them; the gate makes
/// each decision whole against a <see cref="Close"/> raised from another thread. Every stream that closed is
/// remembered for the reader's life: a reader is bound once per admission, so the set grows by one per admitted
/// stream.
/// <para>Invariant: content a reader held under one delivery span never carries a later one. Every move of the span (a
/// cut, a generation step, a first bind, a close) ends the span the reader held, runs the reader's withdrawal, and only
/// then opens the next span, all inside the gate, so a presentation that reads <see cref="DeliverySpan"/> between the
/// two reads an ended span and shows nothing rather than pairing the old content with the new span.</para></remarks>
/// <param name="withdraw">Drops everything the reader holds of the span that just ended, or <see langword="null"/> for
/// a reader whose caller drops it itself on a <see langword="true"/> answer. Runs on the delivering thread inside the
/// ingress, after the span ended and before the next one opens.</param>
public sealed class WorldStreamIngress(Action? withdraw = null) {
    private readonly Lock m_gate = new();
    private readonly DeliverySpanSource m_deliverySpans = new();
    private WorldStreamSpan m_span;
    // Every stream whose span closed on this reader: none binds it again.
    private readonly HashSet<ulong> m_closed = [];
    private long m_refusals;

    /// <summary>Gets the stream the reader is bound to and the span it holds.</summary>
    public WorldStreamSpan Span { get { lock (m_gate) { return m_span; } } }
    /// <summary>Gets the delivery span of what the reader holds now: ended from the moment a cut, a generation step, a
    /// first bind or a close is accepted until the reader's withdrawal has run, and ended for good once the span
    /// closes.</summary>
    public DeliverySpan DeliverySpan => m_deliverySpans.Span;
    /// <summary>Gets the number of records dropped.</summary>
    public long Refusals => Interlocked.Read(location: ref m_refusals);

    /// <summary>Decides whether a content record may move the reader, and takes it into the span when it may.</summary>
    /// <param name="stream">The record's stamp.</param>
    /// <returns><see langword="true"/> when the record may move the reader.</returns>
    public bool Admit(in WorldStreamStamp stream) {
        lock (m_gate) {
            return Take(stepped: out _, stream: in stream);
        }
    }
    /// <summary>Decides whether a cut record of the bound stream moves the reader, and takes it: a cut moves the span
    /// to its generation, and a terminal record closes the span.</summary>
    /// <param name="stream">The record's stamp.</param>
    /// <param name="terminal">Whether the record ends its stream.</param>
    /// <returns><see langword="true"/> when the record belongs to the open span the reader is bound to and was
    /// taken.</returns>
    public bool Cut(in WorldStreamStamp stream, bool terminal) {
        lock (m_gate) {
            if (!stream.IsSpanned || (m_span.Stream != stream.Stream) || m_span.Closed) {
                Refuse();
                return false;
            }
            if (!Take(stepped: out var stepped, stream: in stream)) { return false; }
            if (terminal) {
                CloseLocked();
            } else if (!stepped) {
                MoveSpan(withdraws: true);
            }
            return true;
        }
    }
    /// <summary>Closes the span the reader holds of <paramref name="stream"/>, when that span is open: the transport
    /// that carried the stream lost it, so the stream can no longer tell the reader of a cut. A close names its stream
    /// so that a late close of a stream the reader already let go of cannot close the span of the next one.</summary>
    /// <param name="stream">The stream whose transport ended.</param>
    /// <returns><see langword="true"/> when an open span closed.</returns>
    public bool Close(ulong stream) {
        lock (m_gate) {
            if ((stream == 0UL) || (m_span.Stream != stream) || m_span.Closed) { return false; }
            CloseLocked();
            return true;
        }
    }

    private void CloseLocked() {
        _ = m_closed.Add(item: m_span.Stream);
        m_span = m_span with { Closed = true };
        m_deliverySpans.Close();
        withdraw?.Invoke();
    }
    // Ends the span the reader held, drops what it held of it when it may hold anything, and opens the next span.
    private void MoveSpan(bool withdraws) {
        m_deliverySpans.Close();
        if (withdraws) {
            withdraw?.Invoke();
        }
        _ = m_deliverySpans.Advance();
    }
    private bool Take(in WorldStreamStamp stream, out bool stepped) {
        if (!stream.IsSpanned) {
            stepped = false;
            if (m_span.Stream == 0UL) { return true; }
            Refuse();
            return false;
        }
        if ((m_span.Stream == 0UL) || m_span.Closed) {
            if (m_closed.Contains(item: stream.Stream)) {
                stepped = false;
                Refuse();
                return false;
            }
            // A reader that closed dropped what it held then; one never bound may hold unspanned records.
            var held = !m_span.Closed;

            m_span = new WorldStreamSpan(Closed: false, Generation: stream.Generation, Ordinal: stream.Ordinal, Stream: stream.Stream);
            MoveSpan(withdraws: held);
            stepped = true;
            return true;
        }
        if ((stream.Stream != m_span.Stream) || (stream.Generation < m_span.Generation) || (stream.Ordinal <= m_span.Ordinal)) {
            stepped = false;
            Refuse();
            return false;
        }
        stepped = (stream.Generation > m_span.Generation);
        m_span = m_span with { Generation = stream.Generation, Ordinal = stream.Ordinal };
        if (stepped) {
            MoveSpan(withdraws: true);
        }
        return true;
    }
    private void Refuse() => Interlocked.Increment(location: ref m_refusals);
}
