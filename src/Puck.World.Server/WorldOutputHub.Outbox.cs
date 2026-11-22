using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Puck.Abstractions.Memory;
using Puck.World.Protocol;

namespace Puck.World.Server;

// The outbox at the last hop. A delivery has two halves: composing it reads the authority (the fan-out's disclosure,
// a session sink's projection, the tick's cues) and stays on the thread that makes it; handing it to the receiving
// sink is the receiver's work and belongs to the host loop. While the hub records, the hand-off to every sink that
// is not an IWorldInlineSink, and every callback the server routed through here (a loopback submission's completion,
// the edit echo, a rule's save), is recorded in order instead of made, and Collect replays the records on the
// collecting thread.
//
// The invariant: no non-inline sink and no routed callback runs while a tick of this hub's server executes, or on any
// thread other than the collector once one has collected. Enforced by the types: the hub, the attach primer and a
// session sink reach a receiving sink only through a WorldSinkRoute, which only this hub mints, and a routed callback
// is the delegate the server stores, so no call site can reach the target around it. Where it stops: a sink reached
// by a path that holds the raw IClientSink (WorldRemoteAuthority's own receive loop, which delivers another process's
// stream) is outside it.
public sealed partial class WorldOutputHub {
    private enum RecordKind : byte {
        Snapshot,
        Definition,
        Presentation,
        State,
        Cut,
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
        public bool Terminal;
        public Loan? Loan;
    }

    // The arrays a recorded delivery's lent memory was copied into, rented from the shared workspace arrays and returned
    // once the record is replayed. A lender (the server's snapshot arrays, a session sink's disclosure scratch, the fan-out's
    // projection) overwrites its memory when it composes its next delivery, which may come before the collect, so a
    // record never holds a loan.
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

        public long Bytes { get; private set; }

        public WorldSnapshot Hold(in WorldSnapshot snapshot) {
            Bytes = 0L;
            return snapshot with {
                Entries = HoldEntries(snapshot.Entries),
                FieldCells = Hold(snapshot.FieldCells, ref m_cells),
                Inputs = HoldReferences(snapshot.Inputs, ref m_inputs, out m_inputsLength),
                SolidScales = Hold(snapshot.SolidScales, ref m_scales),
                Cues = HoldReferences(snapshot.Cues, ref m_cues, out m_cuesLength),
                InputAnswers = Hold(snapshot.InputAnswers, ref m_answers),
            };
        }
        public WorldStateStamp Hold(in WorldStateStamp stamp) {
            Bytes = 0L;
            return stamp with { MovedRows = Hold(stamp.MovedRows, ref m_moved) };
        }
        public void Release() {
            if (m_entriesLength > 0) { m_entries.AsSpan(0, m_entriesLength).Clear(); m_entriesLength = 0; }
            if (m_inputsLength > 0) { m_inputs.AsSpan(0, m_inputsLength).Clear(); m_inputsLength = 0; }
            if (m_cuesLength > 0) { m_cues.AsSpan(0, m_cuesLength).Clear(); m_cuesLength = 0; }
            Bytes = 0L;
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
    // The managed thread id of the thread that last collected, or zero before the first collect: the one thread a
    // non-inline sink and a routed callback run on once the host has started collecting.
    private int m_collector;
    private bool m_collecting;

    /// <summary>Gets whether a hand-off made now on the calling thread is recorded for the collect: a tick of this
    /// hub's server is executing, or a collector exists and the calling thread is another.</summary>
    internal bool Records {
        get {
            if (Volatile.Read(location: ref m_executing) != 0) { return true; }
            var collector = Volatile.Read(location: ref m_collector);
            return ((collector != 0) && (collector != Environment.CurrentManagedThreadId));
        }
    }

    /// <summary>Gets the number of hand-offs recorded and not yet replayed.</summary>
    public int PendingRecords {
        get { lock (m_outboxLock) { return m_records.Count; } }
    }

    /// <summary>Marks the start of a tick's execution: every hand-off to a non-inline sink, and every routed callback,
    /// records until <see cref="EndExecute"/>.</summary>
    internal void BeginExecute() => Interlocked.Increment(location: ref m_executing);
    /// <summary>Marks the end of a tick's execution begun by <see cref="BeginExecute"/>.</summary>
    internal void EndExecute() => Interlocked.Decrement(location: ref m_executing);

    /// <summary>Replays every recorded hand-off, in the order it was made, on the calling thread, which becomes this
    /// hub's collector. A sink that throws on a replayed delivery is handed to its route's fault handler; a routed
    /// callback that throws propagates, as it would have from the tick. Returns at once while a tick executes, since
    /// its records belong to its own collect, and inside a replay, whose loop takes what the replay records.</summary>
    internal void Collect() {
        if ((Volatile.Read(location: ref m_executing) != 0) || m_collecting) { return; }
        Volatile.Write(location: ref m_collector, value: Environment.CurrentManagedThreadId);
        m_collecting = true;
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
                        Replay(in CollectionsMarshal.AsSpan(batch)[index]);
                    }
                } finally {
                    lock (m_outboxLock) {
                        foreach (var record in batch) {
                            if (record.Loan is { } loan) {
                                loan.Release();
                                m_loans.Push(loan);
                            }
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

    private static void Replay(in Record record) {
        var route = record.Route;
        switch (record.Kind) {
            case RecordKind.Callback: record.Callback!.Replay(record.Payload); break;
            case RecordKind.Snapshot: route!.ReplaySnapshot(in record.Snapshot, in record.Stream); break;
            case RecordKind.Definition: route!.ReplayDefinition((WorldDefinition)record.Payload!, record.Version, in record.Stream); break;
            case RecordKind.Presentation: route!.ReplayPresentation((WorldDefinition)record.Payload!, record.Rows!, record.Version, in record.Stream); break;
            case RecordKind.State: route!.ReplayState((WorldDefinition)record.Payload!, record.Version, in record.Stamp, in record.Stream); break;
            case RecordKind.Cut: route!.ReplayCut(in record.Stream, record.Terminal); break;
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
    internal WorldSinkRoute RouteTo(IClientSink sink, Action<Exception> fault) => new(hub: this, sink: sink, fault: fault);

    internal void RecordSnapshot(WorldSinkRoute route, in WorldSnapshot snapshot, in WorldStreamStamp stream) {
        var loan = RentLoan();
        var held = loan.Hold(in snapshot);
        Add(new Record { Kind = RecordKind.Snapshot, Route = route, Snapshot = held, Stream = stream, Loan = loan }, loan.Bytes);
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
        if (Defers) { m_hub.RecordSnapshot(this, in snapshot, in stream); } else { m_sink.DeliverSnapshot(in snapshot, in stream); }
    }
    internal void DeliverSnapshot(in WorldSnapshot snapshot, WorldInterestFrame frame) {
        if (m_sink is IWorldInterestSink observer && Inline) { observer.DeliverSnapshot(in snapshot, frame); } else { DeliverSnapshot(in snapshot, WorldStreamStamp.Unspanned); }
    }
    /// <summary>Hands the sink a definition.</summary>
    /// <param name="definition">The definition.</param>
    /// <param name="version">The version of <paramref name="definition"/>.</param>
    /// <param name="stream">The stamp of the record in its stream.</param>
    public void DeliverDefinition(WorldDefinition definition, WorldDocumentVersion version, in WorldStreamStamp stream) {
        if (Defers) { m_hub.RecordDefinition(this, definition, version, in stream); } else { m_sink.DeliverDefinition(definition, version, in stream); }
    }
    /// <summary>Hands the sink changed creation and shade rows.</summary>
    /// <param name="definition">The definition the rows complete.</param>
    /// <param name="rows">The changed rows.</param>
    /// <param name="version">The version of <paramref name="definition"/>.</param>
    /// <param name="stream">The stamp of the record in its stream.</param>
    public void DeliverPresentation(WorldDefinition definition, WorldPresentationRows rows, WorldDocumentVersion version, in WorldStreamStamp stream) {
        if (Defers) { m_hub.RecordPresentation(this, definition, rows, version, in stream); } else { m_sink.DeliverPresentation(definition, rows, version, in stream); }
    }
    /// <summary>Hands the sink a value-only definition; a recorded hand-off holds a copy of the moved rows.</summary>
    /// <param name="definition">The definition.</param>
    /// <param name="version">The version of <paramref name="definition"/>.</param>
    /// <param name="stamp">The tick the values hold as of and the rows whose values moved.</param>
    /// <param name="stream">The stamp of the record in its stream.</param>
    public void DeliverState(WorldDefinition definition, WorldDocumentVersion version, in WorldStateStamp stamp, in WorldStreamStamp stream) {
        if (Defers) { m_hub.RecordState(this, definition, version, in stamp, in stream); } else { m_sink.DeliverState(definition, version, in stamp, in stream); }
    }
    /// <summary>Hands the sink a cut, or the end of its stream.</summary>
    /// <param name="stream">The stamp of the record.</param>
    /// <param name="terminal">Whether the stream ends.</param>
    public void DeliverCut(in WorldStreamStamp stream, bool terminal) {
        if (Defers) { m_hub.RecordCut(this, in stream, terminal); } else { m_sink.DeliverCut(in stream, terminal); }
    }
    /// <summary>Hands the sink an accepted composition override.</summary>
    /// <param name="composition">The composition override.</param>
    public void DeliverComposition(WorldComposition composition) {
        if (Defers) { m_hub.RecordComposition(this, composition); } else { m_sink.DeliverComposition(composition); }
    }
    /// <summary>Hands the sink an accepted session lever.</summary>
    /// <param name="lever">The lever write.</param>
    public void DeliverSessionLever(WorldSessionLever lever) {
        if (Defers) { m_hub.RecordSessionLever(this, lever); } else { m_sink.DeliverSessionLever(lever); }
    }

    // The collect's half: the recorded hand-off made now, on the collector. A sink that throws is handed to the fault
    // handler its composer gave, which detaches it, rather than unwinding the collect past the records after it.
    internal void ReplaySnapshot(in WorldSnapshot snapshot, in WorldStreamStamp stream) {
        try { m_sink.DeliverSnapshot(in snapshot, in stream); } catch (Exception exception) { m_fault(exception); }
    }
    internal void ReplayDefinition(WorldDefinition definition, WorldDocumentVersion version, in WorldStreamStamp stream) {
        try { m_sink.DeliverDefinition(definition, version, in stream); } catch (Exception exception) { m_fault(exception); }
    }
    internal void ReplayPresentation(WorldDefinition definition, WorldPresentationRows rows, WorldDocumentVersion version, in WorldStreamStamp stream) {
        try { m_sink.DeliverPresentation(definition, rows, version, in stream); } catch (Exception exception) { m_fault(exception); }
    }
    internal void ReplayState(WorldDefinition definition, WorldDocumentVersion version, in WorldStateStamp stamp, in WorldStreamStamp stream) {
        try { m_sink.DeliverState(definition, version, in stamp, in stream); } catch (Exception exception) { m_fault(exception); }
    }
    internal void ReplayCut(in WorldStreamStamp stream, bool terminal) {
        try { m_sink.DeliverCut(in stream, terminal); } catch (Exception exception) { m_fault(exception); }
    }
    internal void ReplayComposition(WorldComposition composition) {
        try { m_sink.DeliverComposition(composition); } catch (Exception exception) { m_fault(exception); }
    }
    internal void ReplaySessionLever(WorldSessionLever lever) {
        try { m_sink.DeliverSessionLever(lever); } catch (Exception exception) { m_fault(exception); }
    }
}
