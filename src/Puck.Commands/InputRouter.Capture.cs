namespace Puck.Commands;

// The capture half of the router: the two ingress queues, their bounds, and the drain that turns them into one
// deterministic per-tick stream. Split from the fold for length alone — nothing here is a separate concept.
public sealed partial class InputRouter {
    /// <summary>The number of captured raw signals the capture queue retains before the OLDEST is dropped to make
    /// room for a newer one. A well-behaved producer never reaches it: every stamp comes from the shared
    /// <see cref="IInputClock"/>, so a captured signal becomes due within a frame or two and the queue stays a few
    /// frames deep. A producer whose clock base has diverged — or a pump that has stopped advancing — instead
    /// future-dates everything it captures, and an unbounded queue would retain and re-scan that forever. Drops are
    /// counted in <see cref="DroppedCaptureCount"/> rather than being silent.</summary>
    public const int MaxCapturedSignals = 4_096;
    /// <summary>The number of pre-resolved command injections the injection queue retains before the OLDEST is
    /// dropped to make room for a newer one — the <see cref="MaxCapturedSignals"/> bound on the other capture door,
    /// for the same reason: an injection stamped ahead of every window the pump will reach is retained and re-scanned
    /// on every drain, so an unbounded queue grows without limit exactly when the producer is misbehaving. Drops are
    /// counted in <see cref="DroppedInjectionCount"/>.</summary>
    public const int MaxCapturedInjections = 4_096;

    // Raw signals and pre-resolved injections stay in separate typed buffers: no event carries the inactive half of a
    // pseudo-union. Both implement the same ordering header, so the due streams merge back into one deterministic
    // (capture tick, sequence) order before folding.
    private interface ICaptured {
        ulong CaptureTick { get; }
        ulong Sequence { get; }
    }
    private readonly record struct CapturedInjection(ulong Sequence, CommandInjection Injection) : ICaptured {
        public ulong CaptureTick => Injection.CaptureTick;
    }
    private readonly record struct CapturedSignal(ulong Sequence, InputSignal Signal, bool FocusExemptOnly = false) : ICaptured {
        public ulong CaptureTick => Signal.CaptureTick;
    }
    // A bounded FIFO of captured items with an O(1) append AND an O(1) drop-oldest. Compacting a List by removing its
    // first element moved the whole retained window on every drop — under the capture gate, on the producer thread,
    // in exactly the flooding case the cap exists to survive — so the window rides a ring and the oldest is dropped
    // by advancing the head instead. Storage grows on demand up to the cap rather than being reserved up front.
    private sealed class CaptureQueue<T>(int capacity) where T : struct, ICaptured {
        private readonly int m_capacity = capacity;

        private int m_count;
        private int m_head;

        private T[] m_items = [];

        internal long DroppedCount;

        internal void Add(in T item) {
            if (m_count == m_capacity) {
                m_items[m_head] = default;
                m_head = (((m_head + 1) == m_items.Length)
                    ? 0
                    : (m_head + 1)
                );
                m_count--;
                DroppedCount++;
            } else if (m_count == m_items.Length) {
                Grow();
            }

            m_items[Offset(index: m_count)] = item;
            m_count++;
        }
        internal void Clear() {
            Array.Clear(array: m_items);
            m_count = 0;
            m_head = 0;
        }
        // Moves every item the tick's window has reached into `due`, in queue order, and compacts the rest back
        // toward the head. A kept item only ever moves to a position already read this pass, so the compaction is
        // safe in place.
        internal void DrainDue(List<T> due, ulong windowEndTick) {
            if (m_count == 0) {
                return;
            }

            var kept = 0;

            for (var index = 0; (index < m_count); index++) {
                var item = m_items[Offset(index: index)];

                if (item.CaptureTick < windowEndTick) {
                    due.Add(item: item);
                } else {
                    m_items[Offset(index: kept++)] = item;
                }
            }

            // Release the drained items' references rather than leaving them reachable behind the live window.
            for (var index = kept; (index < m_count); index++) {
                m_items[Offset(index: index)] = default;
            }

            m_count = kept;
        }

        private void Grow() {
            var grown = new T[Math.Min(
                val1: m_capacity,
                val2: Math.Max(
                    val1: 4,
                    val2: (m_items.Length * 2)
                )
            )];

            for (var index = 0; (index < m_count); index++) {
                grown[index] = m_items[Offset(index: index)];
            }

            m_head = 0;
            m_items = grown;
        }
        private int Offset(int index) {
            var offset = (m_head + index);

            return ((offset >= m_items.Length)
                ? (offset - m_items.Length)
                : offset
            );
        }
    }

    /// <summary>The number of captured raw signals this router has DROPPED to keep its capture queue within
    /// <see cref="MaxCapturedSignals"/>. Zero for a well-behaved producer; a non-zero value means a producer is
    /// stamping capture ticks the host loop never reaches (a diverged clock base) or the fixed-step pump has
    /// stopped advancing. The dropped signals are always the oldest, so what survives is the most recent input.</summary>
    public long DroppedCaptureCount {
        get {
            lock (m_captureGate) {
                return m_capturedSignals.DroppedCount;
            }
        }
    }
    /// <summary>The number of pre-resolved injections this router has DROPPED to keep its injection queue within
    /// <see cref="MaxCapturedInjections"/> — <see cref="DroppedCaptureCount"/>'s twin for the injection door
    /// (<see cref="Activate"/> and every <see cref="CommandInjectionSink"/>), and non-zero for the same two reasons:
    /// a producer stamping capture ticks the host loop never reaches, or a pump that has stopped advancing. The
    /// dropped injections are always the oldest.</summary>
    public long DroppedInjectionCount {
        get {
            lock (m_captureGate) {
                return m_capturedInjections.DroppedCount;
            }
        }
    }

    // Queues one pre-resolved command. INTERNAL, and reachable only through a CommandInjectionSink: the injection's
    // principal and lane are the sink's construction-time facts, so there is no signature here a caller could hand a
    // principal of its own choosing to.
    internal void Enqueue(in CommandInjection injection) {
        ObjectDisposedException.ThrowIf(
            condition: m_disposed,
            instance: this
        );

        // An injection's effect mutates the simulation, so it must attribute to a fixed-step tick. An explicit
        // capture tick (a deterministic script / replay harness) is honored; otherwise the shared capture clock
        // stamps it now, exactly as a backend stamps a physical signal — making console input share one timeline
        // with controllers. Replay records the server input stream and restores its order rather than trying to
        // reproduce live arrival time (the same guarantee a gamepad press already has).
        var captureTick = ((injection.CaptureTick != 0UL)
            ? injection.CaptureTick
            : (m_clock?.NowTicks ?? 0UL)
        );

        lock (m_captureGate) {
            m_capturedInjections.Add(item: new CapturedInjection(
                Sequence: m_sequence++,
                Injection: (injection with { CaptureTick = captureTick, })
            ));
        }
    }

    // The one door both capture entry points share: refuse a signal that names no control, then append it under the
    // gate, dropping the OLDEST retained signal first when the queue has reached its cap.
    private void CaptureSignal(in InputSignal signal, bool focusExemptOnly) {
        // A disposed router has been REPLACED, and a producer still holding the stale reference would otherwise keep
        // feeding held tables and pressed latches nothing will ever read or release — silently, since the router has
        // by then unsubscribed from the reload and disconnect edges that used to clear them.
        ObjectDisposedException.ThrowIf(
            condition: m_disposed,
            instance: this
        );

        if (string.IsNullOrEmpty(value: signal.Source)) {
            // Refused HERE rather than surfacing later as a NullReferenceException on the pump thread, inside the
            // device classification of a signal no longer attributable to the producer that captured it.
            throw new ArgumentException(
                message: "A captured input signal must name the source control that produced it.",
                paramName: nameof(signal)
            );
        }

        if (
            (signal.Slot < 0) &&
            (signal.Slot != InputSignal.UnresolvedSlot)
        ) {
            // UnresolvedSlot is THE sentinel. Any other negative would otherwise be read as "resolve the lane from
            // the device" and land the signal in whatever seat the resolver names — a silently wrong lane rather
            // than the authored one the caller asked for.
            throw new ArgumentException(
                message: $"A captured input signal addresses a lane by a non-negative slot, or {nameof(InputSignal.UnresolvedSlot)} ({InputSignal.UnresolvedSlot}) to resolve one from its device; {signal.Slot} is neither.",
                paramName: nameof(signal)
            );
        }

        lock (m_captureGate) {
            m_capturedSignals.Add(item: new CapturedSignal(
                Sequence: m_sequence++,
                Signal: signal,
                FocusExemptOnly: focusExemptOnly
            ));
        }
    }
    private static int CompareCaptureOrder(ulong leftTick, ulong leftSequence, ulong rightTick, ulong rightSequence) {
        var byTime = leftTick.CompareTo(value: rightTick);

        return ((byTime != 0)
            ? byTime
            : leftSequence.CompareTo(value: rightSequence)
        );
    }
    private void DrainDue(ulong windowEndTick) {
        m_dueSignals.Clear();
        m_dueInjections.Clear();

        // Drain both typed streams under one gate: a producer cannot land between them and make a later sequence
        // eligible for this tick while an earlier one waits for the next tick.
        lock (m_captureGate) {
            m_capturedSignals.DrainDue(
                due: m_dueSignals,
                windowEndTick: windowEndTick
            );
            m_capturedInjections.DrainDue(
                due: m_dueInjections,
                windowEndTick: windowEndTick
            );
        }

        m_dueSignals.Sort(comparison: static (left, right) => CompareCaptureOrder(
            leftTick: left.CaptureTick,
            leftSequence: left.Sequence,
            rightTick: right.CaptureTick,
            rightSequence: right.Sequence
        ));
        m_dueInjections.Sort(comparison: static (left, right) => CompareCaptureOrder(
            leftTick: left.CaptureTick,
            leftSequence: left.Sequence,
            rightTick: right.CaptureTick,
            rightSequence: right.Sequence
        ));
    }
    // Queues one router-SYNTHESIZED edge for the next tick. Distinct from Enqueue, which stamps a CAPTURED
    // injection (a console line, a peer submission) onto the shared capture timeline: nothing here arrived from
    // outside, so nothing here reads a clock — SnapshotForTick drains this list at its top and the ordering IS the
    // one-tick delay (see m_pendingInjections).
    private void EnqueuePending(in CommandInjection injection) {
        lock (m_captureGate) {
            m_pendingInjections.Add(item: injection);
        }
    }
    private void QueueCancellations(List<CommandInjection> cancellations, bool discardCapturedSignals) {
        if (
            (cancellations.Count == 0) &&
            !discardCapturedSignals
        ) {
            return;
        }

        cancellations.Sort(comparison: static (left, right) => {
            var bySlot = left.Slot.CompareTo(value: right.Slot);

            if (bySlot != 0) {
                return bySlot;
            }

            var byCommand = left.CommandId.CompareTo(value: right.CommandId);

            return ((byCommand != 0)
                ? byCommand
                : StringComparer.Ordinal.Compare(
                    x: left.Source,
                    y: right.Source
                )
            );
        });

        lock (m_captureGate) {
            if (discardCapturedSignals) {
                // A physical press captured just before focus loss must not become a fresh held input afterward.
                // Console/peer injections are not focus-owned and remain queued.
                m_capturedSignals.Clear();
            }

            // A cancellation is synthesized, not captured, so it carries no clock stamp: the next SnapshotForTick
            // drains it at its top. Stamping from the wall clock instead deferred it past every step of a catch-up,
            // because a step's window close is at or before the clock's now by construction.
            m_pendingInjections.AddRange(collection: cancellations);
        }
    }

    /// <summary>Appends a captured input signal. Thread-safe — backends call this from device I/O threads and the window pump.</summary>
    /// <param name="signal">The timestamped input signal to capture.</param>
    /// <exception cref="ArgumentException"><paramref name="signal"/> carries no <see cref="InputSignal.Source"/>.</exception>
    /// <exception cref="ObjectDisposedException">This router has been disposed.</exception>
    public void Capture(in InputSignal signal) => CaptureSignal(
        focusExemptOnly: false,
        signal: in signal
    );
    /// <summary>Captures a signal from a device whose ordinary terminal focus is released. Only bindings whose
    /// destination declares <see cref="CommandInputScope.FocusExempt"/> may dispatch and only the host-owned
    /// <see cref="IAlwaysActiveInputBindings"/> plane answers, so a typed key cannot press a gameplay page's
    /// binding. A RELEASE still reaches the page resolver — its answer discarded — because the resolver holds the
    /// chord tracker, the press latches, and the armed command rows, and a release those never observe strands a
    /// flipped page or an armed row for as long as the seat console stays open. It is forwarded with
    /// <c>pressesWithheld</c> set (see <see cref="IInputBindings.Resolve(int, in InputSignal, bool)"/>), so the
    /// resolver delivers what the release owes and arms nothing new. An inactive CONTINUOUS sample is forwarded only
    /// when <see cref="IInputBindings.HoldsSource"/> says the resolver is holding that source down: a stick sitting
    /// at centre reports every frame and is the device reporting, not a release.</summary>
    /// <param name="signal">The raw signal to capture.</param>
    /// <exception cref="ArgumentException"><paramref name="signal"/> carries no <see cref="InputSignal.Source"/>.</exception>
    /// <exception cref="ObjectDisposedException">This router has been disposed.</exception>
    public void CaptureFocusExempt(in InputSignal signal) => CaptureSignal(
        focusExemptOnly: true,
        signal: in signal
    );
}
