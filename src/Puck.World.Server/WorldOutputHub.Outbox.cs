using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Puck.Abstractions.Memory;
using Puck.World.Protocol;

namespace Puck.World.Server;

// The outbox at the last hop. A delivery has two halves: composing it reads the authority (the fan-out's disclosure,
// a session sink's projection, the tick's cues, or a remote authority's socket frame) and stays on the thread that
// makes it; handing it to the receiving sink is the receiver's work and belongs to the host loop. While the hub records,
// the hand-off to every sink that is not an IWorldInlineSink, and every callback routed through here (a loopback
// submission's completion, the edit echo, a rule's save, a remote traveler's observed route), is recorded in order
// instead of made, and Collect replays the records on the collecting thread.
//
// The invariant: no non-inline sink and no routed callback runs while a tick of this hub's server executes, or, once a
// host has bound the hub to its boundary window, anywhere but on the host loop's thread inside that window
// (WorldLinkBoundary.DeliversHere). Enforced by the types: the hub, the attach primer, a session sink and a remote
// authority's observer each reach a receiving sink only through a WorldSinkRoute, which only this hub mints, and a
// routed callback is the delegate the caller stores, so no call site can reach the target around it. Where it stops: a
// server or remote authority no host bound to a boundary window defers only while a local tick executes.
public sealed partial class WorldOutputHub {
    private enum RecordKind : byte {
        Snapshot,
        Definition,
        Presentation,
        State,
        Cut,
        CloseSpan,
        Composition,
        SessionLever,
        Callback,
    }

    // One recorded hand-off. Payload holds the reference argument of the kind (a definition, a composition, a lever, a
    // callback's argument); Rows the presentation rows; Loan the copies of whatever memory the delivery lent.
    private struct Record {
        public RecordKind Kind;
        public WorldSinkRoute? Route;
        public RoutedCallback? Callback;
        public object? Payload;
        public WorldPresentationRows? Rows;
        public WorldSnapshot Snapshot;
        public WorldStateStamp Stamp;
        public WorldStreamStamp Stream;
        public WorldDocumentVersion Version;
        public long ArrivedAt;
        public bool Terminal;
        public Loan? Loan;
    }

    // The arrays a recorded delivery's lent memory was copied into, rented from the shared workspace arrays and returned
    // once the record is replayed. A lender (the server's snapshot arrays, a session sink's disclosure scratch, the fan-out's
    // projection) overwrites its memory when it composes its next delivery, which may come before the collect, so a
    // record never holds a loan. Within one fan-out, every record whose entries are the same lent span shares one entries
    // loan (its parent): the entries and their register file are held once a fan-out, and not copied at all when the
    // lender surrenders its storage (IWorldSnapshotLender) to that loan, which every record whose entries or cells live in
    // that storage then shares, and which returns it when its last holder is replayed.
    private sealed class Loan {
        private EntitySnapshot[] m_entries = [];
        private int m_entriesLength;
        private long[] m_states = [];
        private FieldCellDelta[] m_cells = [];
        private WorldInputPublication[] m_inputs = [];
        private int m_inputsLength;
        private WorldSolidScale[] m_scales = [];
        private WorldGameplayCue[] m_cues = [];
        private int m_cuesLength;
        private WorldInputAnswer[] m_answers = [];
        private int[] m_moved = [];
        // An entries loan: the span it holds, and the lender storage it adopted or null when it copied.
        private ReadOnlyMemory<EntitySnapshot> m_heldEntries;
        private WorldSnapshotStorage? m_adopted;
        private IWorldSnapshotLender? m_lender;
        // A record's loan that takes its entries from a shared entries loan.
        private Loan? m_parent;
        // The holders of this loan not yet released; it is released when the last is. Under the outbox lock.
        private int m_holders;

        public long Bytes { get; private set; }

        // Holds a snapshot for one record. With a parent, a shared entries loan the caller has already counted this loan
        // in (Share), the record takes its entries from the parent, and keeps as they are the entries and cells that live
        // in the parent's adopted storage; everything else the snapshot lends is copied here.
        public WorldSnapshot Hold(in WorldSnapshot snapshot, Loan? parent = null) {
            Bytes = 0L;
            m_holders = 1;
            m_parent = parent;
            return snapshot with {
                Entries = ((parent is null) ? HoldEntries(snapshot.Entries) : (parent.Covers(snapshot.Entries) ? snapshot.Entries : parent.m_heldEntries)),
                FieldCells = (((parent is not null) && parent.Covers(snapshot.FieldCells)) ? snapshot.FieldCells : Hold(snapshot.FieldCells, ref m_cells)),
                Inputs = HoldReferences(snapshot.Inputs, ref m_inputs, out m_inputsLength),
                SolidScales = Hold(snapshot.SolidScales, ref m_scales),
                Cues = HoldReferences(snapshot.Cues, ref m_cues, out m_cuesLength),
                InputAnswers = Hold(snapshot.InputAnswers, ref m_answers),
            };
        }
        // Another record's loan holds this entries loan as its parent. Under the outbox lock.
        public void Share() => m_holders++;
        // Holds one entries span for a fan-out, adopting the lender's storage when the span lives there and copying it
        // otherwise. The fan-out is its first holder.
        public void HoldEntriesOf(in WorldSnapshot snapshot, IWorldSnapshotLender? lender) {
            Bytes = 0L;
            m_holders = 1;
            if ((lender is not null) && lender.Lends(entries: snapshot.Entries)) {
                m_adopted = lender.Surrender();
                m_lender = lender;
                m_heldEntries = snapshot.Entries;
                return;
            }
            m_heldEntries = HoldEntries(snapshot.Entries);
        }
        // Whether a record's span lives in the storage this loan adopted, which no one writes until the loan returns it,
        // so the record keeps the span as it is: the whole table, a projection sliced from it, or its cells.
        public bool Covers<T>(ReadOnlyMemory<T> span) =>
            (m_adopted is { } adopted) && !span.IsEmpty && MemoryMarshal.TryGetArray(memory: span, segment: out var segment) &&
            (ReferenceEquals(objA: segment.Array, objB: adopted.Entries) || ReferenceEquals(objA: segment.Array, objB: adopted.Cells));
        public WorldStateStamp Hold(in WorldStateStamp stamp) {
            Bytes = 0L;
            m_holders = 1;
            return stamp with { MovedRows = Hold(stamp.MovedRows, ref m_moved) };
        }
        // Drops one holder and, when it was the last, releases the loan and returns it, with the parent it released in
        // turn, to the free stack. Under the outbox lock.
        public void Unhold(Stack<Loan> free) {
            if (--m_holders != 0) { return; }
            if (m_entriesLength > 0) { m_entries.AsSpan(0, m_entriesLength).Clear(); m_entriesLength = 0; }
            if (m_inputsLength > 0) { m_inputs.AsSpan(0, m_inputsLength).Clear(); m_inputsLength = 0; }
            if (m_cuesLength > 0) { m_cues.AsSpan(0, m_cuesLength).Clear(); m_cuesLength = 0; }
            if (m_adopted is { } adopted) {
                m_lender!.Return(storage: adopted);
                m_adopted = null;
                m_lender = null;
            }
            m_heldEntries = default;
            Bytes = 0L;
            free.Push(this);
            if (m_parent is { } parent) {
                m_parent = null;
                parent.Unhold(free: free);
            }
        }

        // An entry lends its action-state registers as well: each entry's slice moves into one copied register array.
        private ReadOnlyMemory<EntitySnapshot> HoldEntries(ReadOnlyMemory<EntitySnapshot> source) {
            var held = HoldReferences(source, ref m_entries, out m_entriesLength);
            var registers = 0;
            foreach (ref readonly var entry in source.Span) { registers += entry.State.Length; }
            if (registers == 0) { return held; }

            var states = ArrayCapacity.EnsureScratch(ref m_states, registers);
            var entries = m_entries;
            var offset = 0;
            Bytes += ((long)registers * sizeof(long));
            for (var index = 0; index < held.Length; index++) {
                var state = entries[index].State;
                if (state.IsEmpty) { continue; }
                state.Span.CopyTo(states.AsSpan(offset));
                entries[index] = entries[index] with { State = states.AsMemory(offset, state.Length) };
                offset += state.Length;
            }
            return held;
        }
        private ReadOnlyMemory<T> Hold<T>(ReadOnlyMemory<T> source, ref T[] scratch) {
            if (source.IsEmpty) { return source; }
            var copy = ArrayCapacity.EnsureScratch(ref scratch, source.Length);
            source.Span.CopyTo(copy);
            Bytes += ((long)source.Length * Unsafe.SizeOf<T>());
            return copy.AsMemory(0, source.Length);
        }
        private ReadOnlyMemory<T> HoldReferences<T>(ReadOnlyMemory<T> source, ref T[] scratch, out int length) {
            length = source.Length;
            if (source.IsEmpty) { return source; }
            var copy = ArrayCapacity.EnsureScratch(ref scratch, source.Length);
            source.Span.CopyTo(copy);
            Bytes += ((long)source.Length * Unsafe.SizeOf<T>());
            return copy.AsMemory(0, source.Length);
        }
    }

    /// <summary>A callback the server stores already routed through the outbox: invoking it calls the target at once
    /// while the hub does not record, and records the call for the collect while it does.</summary>
    private abstract class RoutedCallback {
        public abstract void Replay(object? argument);
    }

    private sealed class RoutedCallback<T>(WorldOutputHub hub, Action<T> target) : RoutedCallback {
        public WorldOutputHub Hub => hub;

        public void Invoke(T argument) {
            if (hub.Records) {
                hub.Add(new Record { Kind = RecordKind.Callback, Callback = this, Payload = argument });
                return;
            }
            target(argument);
        }
        public override void Replay(object? argument) => target((T)argument!);
    }

    private readonly Lock m_outboxLock = new();
    private readonly Stack<Loan> m_loans = new();
    private List<Record> m_records = [];
    private List<Record> m_replay = [];
    // Nonzero while a tick of this hub's server executes. Only the thread running the tick changes it, under the
    // authority gate, so a delivery from any thread (each holds that gate) reads it settled.
    private int m_executing;
    private bool m_collecting;

    /// <summary>Gets or sets the boundary window of the host that steps this hub's server, or <see langword="null"/>
    /// for a server no host admitted.</summary>
    public WorldLinkBoundary? Boundary { get; set; }

    /// <summary>Gets whether a hand-off made now on the calling thread is recorded for the collect: a tick of this
    /// hub's server is executing, or the calling thread is not the host loop's thread inside its boundary
    /// window.</summary>
    public bool Records => ((Volatile.Read(location: ref m_executing) != 0) || (Boundary is { DeliversHere: false }));

    /// <summary>Gets the number of hand-offs recorded and not yet replayed.</summary>
    public int PendingRecords {
        get { lock (m_outboxLock) { return m_records.Count; } }
    }

    /// <summary>Marks the start of a tick's execution: every hand-off to a non-inline sink, and every routed callback,
    /// records until <see cref="EndExecute"/>.</summary>
    internal void BeginExecute() => Interlocked.Increment(location: ref m_executing);
    /// <summary>Marks the end of a tick's execution begun by <see cref="BeginExecute"/>.</summary>
    internal void EndExecute() => Interlocked.Decrement(location: ref m_executing);

    /// <summary>Replays every recorded hand-off, in the order it was made, on the calling thread: the host loop's,
    /// inside its boundary window. A sink that throws on a replayed delivery is handed to its route's fault handler; a
    /// routed callback that throws propagates, as it would have from the tick. Returns at once while a tick executes,
    /// since its records belong to its own collect, and inside a replay, whose loop takes what the replay
    /// records.</summary>
    /// <param name="completedAt">The stopwatch timestamp the tick whose records these are finished executing at, which
    /// a replayed snapshot's receiver stamps as its arrival (<see cref="WorldDeliveryClock"/>); zero stamps the time
    /// of the replay.</param>
    public void Collect(long completedAt = 0L) {
        if ((Volatile.Read(location: ref m_executing) != 0) || m_collecting) { return; }
        m_collecting = true;
        using var arrival = WorldDeliveryClock.ReplayAt(timestamp: completedAt);
        try {
            while (true) {
                List<Record> batch;
                lock (m_outboxLock) {
                    if (m_records.Count == 0) { return; }
                    batch = m_records;
                    m_records = m_replay;
                }
                try {
                    for (var index = 0; index < batch.Count; index++) {
                        Replay(in CollectionsMarshal.AsSpan(batch)[index], completedAt: completedAt);
                    }
                } finally {
                    lock (m_outboxLock) {
                        foreach (var record in batch) {
                            record.Loan?.Unhold(free: m_loans);
                        }
                        batch.Clear();
                        m_replay = batch;
                    }
                }
            }
        } finally {
            m_collecting = false;
        }
    }

    private static void Replay(in Record record, long completedAt) {
        var route = record.Route;
        switch (record.Kind) {
            case RecordKind.Callback: record.Callback!.Replay(record.Payload); break;
            case RecordKind.Snapshot: {
                if ((completedAt == 0L) && (record.ArrivedAt != 0L)) {
                    using var arrival = WorldDeliveryClock.ReplayAt(timestamp: record.ArrivedAt);
                    route!.ReplaySnapshot(in record.Snapshot, in record.Stream);
                } else {
                    route!.ReplaySnapshot(in record.Snapshot, in record.Stream);
                }
                break;
            }
            case RecordKind.Definition: route!.ReplayDefinition((WorldDefinition)record.Payload!, record.Version, in record.Stream); break;
            case RecordKind.Presentation: route!.ReplayPresentation((WorldDefinition)record.Payload!, record.Rows!, record.Version, in record.Stream); break;
            case RecordKind.State: route!.ReplayState((WorldDefinition)record.Payload!, record.Version, in record.Stamp, in record.Stream); break;
            case RecordKind.Cut: route!.ReplayCut(in record.Stream, record.Terminal); break;
            case RecordKind.CloseSpan: route!.ReplayCloseSpan(record.Stream.Stream); break;
            case RecordKind.Composition: route!.ReplayComposition((WorldComposition)record.Payload!); break;
            case RecordKind.SessionLever: route!.ReplaySessionLever((WorldSessionLever)record.Payload!); break;
        }
    }
    /// <summary>Returns <paramref name="action"/> routed through the outbox: the returned delegate calls it at once
    /// while the hub does not record and records the call for the collect while it does. A delegate this hub already
    /// routed is returned as it is.</summary>
    /// <typeparam name="T">The callback's argument type.</typeparam>
    /// <param name="action">The callback, or <see langword="null"/>.</param>
    /// <returns>The routed callback, or <see langword="null"/> when <paramref name="action"/> is.</returns>
    internal Action<T>? Route<T>(Action<T>? action) {
        if (action is null) { return null; }
        if (action.HasSingleTarget && (action.Target is RoutedCallback<T> routed) && ReferenceEquals(routed.Hub, this)) { return action; }
        return new RoutedCallback<T>(this, action).Invoke;
    }
    /// <summary>Mints the one route to <paramref name="sink"/> a composer may deliver through.</summary>
    /// <param name="sink">The receiving sink.</param>
    /// <param name="fault">The handler a replayed delivery's exception is handed to.</param>
    /// <returns>The route.</returns>
    public WorldSinkRoute RouteTo(IClientSink sink, Action<Exception> fault) => new(hub: this, sink: sink, fault: fault);

    // The fan-out of one snapshot on this hub: the thread delivering it, the lender whose storage the snapshot lends, and
    // the entries loans its recorded hand-offs took, one for each distinct entries span (the whole table, a projection),
    // which every later record of the same span in the same fan-out shares. Set and read only on the fanning-out thread
    // (m_fanOutThread); each entries loan holds itself for the fan-out until it ends, so no collect can release one a
    // later route still shares, and no later snapshot meets one. Sharing by span rests on the premise the interest
    // frame's own caches rest on (WorldInterestFrame keys its images and continuity variants by span within a tick):
    // within one fan-out, two equal spans hold equal rows. Every producer today hands a fresh array or one fixed for
    // the tick (the tick table, the frame's projections); a producer that rewrote one scratch span for two routes in one
    // fan-out would break it, and nothing but this note guards that.
    private int m_fanOutThread;
    private IWorldSnapshotLender? m_fanOutLender;
    private readonly List<(ReadOnlyMemory<EntitySnapshot> Entries, Loan Loan)> m_fanOutEntries = [];

    internal void RecordSnapshot(WorldSinkRoute route, in WorldSnapshot snapshot, in WorldStreamStamp stream) {
        var loan = RentLoan();
        WorldSnapshot held;
        var bytes = 0L;

        if ((m_fanOutThread == Environment.CurrentManagedThreadId) && !snapshot.Entries.IsEmpty) {
            Loan? parent = null;

            foreach (var (entries, shared) in m_fanOutEntries) {
                if (entries.Equals(snapshot.Entries) || shared.Covers(snapshot.Entries)) { parent = shared; break; }
            }
            if (parent is null) {
                parent = RentLoan();
                parent.HoldEntriesOf(in snapshot, m_fanOutLender);
                bytes += parent.Bytes;
                m_fanOutEntries.Add((snapshot.Entries, parent));
            }
            lock (m_outboxLock) { parent.Share(); }
            held = loan.Hold(in snapshot, parent);
        } else {
            held = loan.Hold(in snapshot);
        }
        Add(new Record { Kind = RecordKind.Snapshot, Route = route, Snapshot = held, Stream = stream, ArrivedAt = WorldDeliveryClock.Arrival, Loan = loan }, bytes + loan.Bytes);
    }
    // Opens a fan-out of one snapshot on the calling thread and returns what EndFanOut restores for the fan-out it nests
    // in: the thread, the lender and how many entries loans the outer fan-out holds.
    private (int Thread, IWorldSnapshotLender? Lender, int Held) BeginFanOut(IWorldSnapshotLender? lender) {
        var outer = (m_fanOutThread, m_fanOutLender, m_fanOutEntries.Count);

        m_fanOutThread = Environment.CurrentManagedThreadId;
        m_fanOutLender = lender;

        return outer;
    }
    private void EndFanOut((int Thread, IWorldSnapshotLender? Lender, int Held) outer) {
        lock (m_outboxLock) {
            for (var index = (m_fanOutEntries.Count - 1); (index >= outer.Held); index--) {
                m_fanOutEntries[index].Loan.Unhold(free: m_loans);
            }
        }
        m_fanOutEntries.RemoveRange(index: outer.Held, count: (m_fanOutEntries.Count - outer.Held));
        (m_fanOutThread, m_fanOutLender) = (outer.Thread, outer.Lender);
    }
    internal void RecordState(WorldSinkRoute route, WorldDefinition definition, WorldDocumentVersion version, in WorldStateStamp stamp, in WorldStreamStamp stream) {
        Loan? loan = null;
        var held = stamp;
        if (!stamp.MovedRows.IsEmpty) {
            loan = RentLoan();
            held = loan.Hold(in stamp);
        }
        Add(new Record { Kind = RecordKind.State, Route = route, Payload = definition, Version = version, Stamp = held, Stream = stream, Loan = loan }, loan?.Bytes ?? 0L);
    }
    internal void RecordDefinition(WorldSinkRoute route, WorldDefinition definition, WorldDocumentVersion version, in WorldStreamStamp stream) =>
        Add(new Record { Kind = RecordKind.Definition, Route = route, Payload = definition, Version = version, Stream = stream });
    internal void RecordPresentation(WorldSinkRoute route, WorldDefinition definition, WorldPresentationRows rows, WorldDocumentVersion version, in WorldStreamStamp stream) =>
        Add(new Record { Kind = RecordKind.Presentation, Route = route, Payload = definition, Rows = rows, Version = version, Stream = stream });
    internal void RecordCut(WorldSinkRoute route, in WorldStreamStamp stream, bool terminal) =>
        Add(new Record { Kind = RecordKind.Cut, Route = route, Stream = stream, Terminal = terminal });
    internal void RecordCloseSpan(WorldSinkRoute route, ulong span) =>
        Add(new Record { Kind = RecordKind.CloseSpan, Route = route, Stream = new WorldStreamStamp(Stream: span, Generation: 0L, Ordinal: 0L) });
    internal void RecordComposition(WorldSinkRoute route, WorldComposition composition) =>
        Add(new Record { Kind = RecordKind.Composition, Route = route, Payload = composition });
    internal void RecordSessionLever(WorldSinkRoute route, WorldSessionLever lever) =>
        Add(new Record { Kind = RecordKind.SessionLever, Route = route, Payload = lever });

    private Loan RentLoan() {
        lock (m_outboxLock) { return m_loans.TryPop(out var loan) ? loan : new Loan(); }
    }
    private void Add(Record record, long bytes = 0L) {
        lock (m_outboxLock) { m_records.Add(record); }
        WorldBootWork.Count(WorldBootWork.OutboxRecords);
        if (bytes != 0L) { WorldBootWork.Count(WorldBootWork.OutboxBytesCopied, bytes); }
    }
}

/// <summary>The one way a composer hands a delivery to a receiving sink: the hub's fan-out, a newly attached sink's
/// primer and a session sink's observer each hold a route, never the sink. An <see cref="IWorldInlineSink"/> is called
/// at once on the delivering thread; any other sink is called at once only while its hub does not record, and is
/// otherwise handed the delivery when the hub collects.</summary>
public sealed class WorldSinkRoute {
    private readonly WorldOutputHub m_hub;
    private readonly IClientSink m_sink;
    private readonly IWorldDetachableSink? m_detachable;
    private readonly Action<Exception> m_fault;
    private volatile bool m_faulted;

    internal WorldSinkRoute(WorldOutputHub hub, IClientSink sink, Action<Exception> fault) {
        ArgumentNullException.ThrowIfNull(argument: sink);
        m_hub = hub;
        m_sink = sink;
        m_detachable = (sink as IWorldDetachableSink);
        m_fault = fault;
        Inline = (sink is IWorldInlineSink);
    }

    /// <summary>Gets whether the sink receives on the delivering thread.</summary>
    public bool Inline { get; }
    /// <summary>Gets whether the sink can end its own subscription (<see cref="IWorldDetachableSink"/>).</summary>
    public bool Detachable => (m_detachable is not null);
    /// <summary>Gets the reason the sink ended its own subscription with, or <see langword="null"/> while it takes
    /// deliveries or cannot end one.</summary>
    public string? DetachReason => m_detachable?.DetachReason;
    /// <summary>Gets the sink's type name, for narration.</summary>
    public string SinkName => m_sink.GetType().Name;
    internal bool ObservesInterest => (Inline && (m_sink is IWorldInterestSink));

    private bool Defers => (!Inline && m_hub.Records);

    /// <summary>Hands the sink a snapshot; a recorded hand-off holds copies of the memory it lends.</summary>
    /// <param name="snapshot">The snapshot.</param>
    /// <param name="stream">The stamp of the record in its stream.</param>
    public void DeliverSnapshot(in WorldSnapshot snapshot, in WorldStreamStamp stream) {
        if (m_faulted) { return; }
        if (Defers) { m_hub.RecordSnapshot(this, in snapshot, in stream); } else { m_sink.DeliverSnapshot(in snapshot, in stream); }
    }
    internal void DeliverSnapshot(in WorldSnapshot snapshot, WorldInterestFrame frame) {
        if (m_faulted) { return; }
        if (m_sink is IWorldInterestSink observer && Inline) { observer.DeliverSnapshot(in snapshot, frame); } else { DeliverSnapshot(in snapshot, WorldStreamStamp.Unspanned); }
    }
    /// <summary>Hands the sink a definition.</summary>
    /// <param name="definition">The definition.</param>
    /// <param name="version">The version of <paramref name="definition"/>.</param>
    /// <param name="stream">The stamp of the record in its stream.</param>
    public void DeliverDefinition(WorldDefinition definition, WorldDocumentVersion version, in WorldStreamStamp stream) {
        if (m_faulted) { return; }
        if (Defers) { m_hub.RecordDefinition(this, definition, version, in stream); } else { m_sink.DeliverDefinition(definition, version, in stream); }
    }
    /// <summary>Hands the sink changed creation and shade rows.</summary>
    /// <param name="definition">The definition the rows complete.</param>
    /// <param name="rows">The changed rows.</param>
    /// <param name="version">The version of <paramref name="definition"/>.</param>
    /// <param name="stream">The stamp of the record in its stream.</param>
    public void DeliverPresentation(WorldDefinition definition, WorldPresentationRows rows, WorldDocumentVersion version, in WorldStreamStamp stream) {
        if (m_faulted) { return; }
        if (Defers) { m_hub.RecordPresentation(this, definition, rows, version, in stream); } else { m_sink.DeliverPresentation(definition, rows, version, in stream); }
    }
    /// <summary>Hands the sink a value-only definition; a recorded hand-off holds a copy of the moved rows.</summary>
    /// <param name="definition">The definition.</param>
    /// <param name="version">The version of <paramref name="definition"/>.</param>
    /// <param name="stamp">The tick the values hold as of and the rows whose values moved.</param>
    /// <param name="stream">The stamp of the record in its stream.</param>
    public void DeliverState(WorldDefinition definition, WorldDocumentVersion version, in WorldStateStamp stamp, in WorldStreamStamp stream) {
        if (m_faulted) { return; }
        if (Defers) { m_hub.RecordState(this, definition, version, in stamp, in stream); } else { m_sink.DeliverState(definition, version, in stamp, in stream); }
    }
    /// <summary>Hands the sink a cut, or the end of its stream.</summary>
    /// <param name="stream">The stamp of the record.</param>
    /// <param name="terminal">Whether the stream ends.</param>
    public void DeliverCut(in WorldStreamStamp stream, bool terminal) {
        if (m_faulted) { return; }
        if (Defers) { m_hub.RecordCut(this, in stream, terminal); } else { m_sink.DeliverCut(in stream, terminal); }
    }
    /// <summary>Closes the span the sink was reading from <paramref name="stream"/>.</summary>
    /// <param name="stream">The stream identifier.</param>
    public void CloseSpan(ulong stream) {
        if (m_faulted) { return; }
        if (Defers) { m_hub.RecordCloseSpan(this, stream); } else { m_sink.CloseSpan(stream); }
    }
    /// <summary>Hands the sink an accepted composition override.</summary>
    /// <param name="composition">The composition override.</param>
    public void DeliverComposition(WorldComposition composition) {
        if (m_faulted) { return; }
        if (Defers) { m_hub.RecordComposition(this, composition); } else { m_sink.DeliverComposition(composition); }
    }
    /// <summary>Hands the sink an accepted session lever.</summary>
    /// <param name="lever">The lever write.</param>
    public void DeliverSessionLever(WorldSessionLever lever) {
        if (m_faulted) { return; }
        if (Defers) { m_hub.RecordSessionLever(this, lever); } else { m_sink.DeliverSessionLever(lever); }
    }

    // The collect's half: the recorded hand-off made now, on the collector. A sink that throws is handed to the fault
    // handler its composer gave, which detaches it, rather than unwinding the collect past the records after it.
    internal void ReplaySnapshot(in WorldSnapshot snapshot, in WorldStreamStamp stream) {
        if (m_faulted) { return; }
        try { m_sink.DeliverSnapshot(in snapshot, in stream); } catch (Exception exception) { m_faulted = true; m_fault(exception); }
    }
    internal void ReplayDefinition(WorldDefinition definition, WorldDocumentVersion version, in WorldStreamStamp stream) {
        if (m_faulted) { return; }
        try { m_sink.DeliverDefinition(definition, version, in stream); } catch (Exception exception) { m_faulted = true; m_fault(exception); }
    }
    internal void ReplayPresentation(WorldDefinition definition, WorldPresentationRows rows, WorldDocumentVersion version, in WorldStreamStamp stream) {
        if (m_faulted) { return; }
        try { m_sink.DeliverPresentation(definition, rows, version, in stream); } catch (Exception exception) { m_faulted = true; m_fault(exception); }
    }
    internal void ReplayState(WorldDefinition definition, WorldDocumentVersion version, in WorldStateStamp stamp, in WorldStreamStamp stream) {
        if (m_faulted) { return; }
        try { m_sink.DeliverState(definition, version, in stamp, in stream); } catch (Exception exception) { m_faulted = true; m_fault(exception); }
    }
    internal void ReplayCut(in WorldStreamStamp stream, bool terminal) {
        if (m_faulted) { return; }
        try { m_sink.DeliverCut(in stream, terminal); } catch (Exception exception) { m_faulted = true; m_fault(exception); }
    }
    internal void ReplayCloseSpan(ulong stream) {
        if (m_faulted) { return; }
        try { m_sink.CloseSpan(stream); } catch (Exception exception) { m_faulted = true; m_fault(exception); }
    }
    internal void ReplayComposition(WorldComposition composition) {
        if (m_faulted) { return; }
        try { m_sink.DeliverComposition(composition); } catch (Exception exception) { m_faulted = true; m_fault(exception); }
    }
    internal void ReplaySessionLever(WorldSessionLever lever) {
        if (m_faulted) { return; }
        try { m_sink.DeliverSessionLever(lever); } catch (Exception exception) { m_faulted = true; m_fault(exception); }
    }
}
