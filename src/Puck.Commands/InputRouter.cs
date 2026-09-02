using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Puck.Maths;

namespace Puck.Commands;

/// <summary>
/// The single capture point and per-tick snapshot producer. Every backend appends timestamped
/// <see cref="InputSignal"/>s here from any thread; the host's fixed-step loop pulls one
/// <see cref="CommandSnapshot"/> per tick, draining the captured signals whose
/// <see cref="InputSignal.CaptureTick"/> falls within the tick's window and folding them — through the binding
/// table — into per-slot lanes. Signals stamped at or beyond the window stay for a later tick, so a frame
/// spike delays, never misattributes, recent input.
/// </summary>
/// <remarks>
/// Held semantics mirror a physical control: digital presses and active analog values persist until a release or zero
/// sample clears them. Digital handlers dispatch only on their bound edges; analog handlers re-dispatch their carried
/// sample each tick so route-style consumers receive the continuous value. Text is transient. Dispatch is
/// <em>not</em> performed here — the router only produces the deterministic per-tick state; the consumer runs
/// handlers from the snapshot. A <see cref="CommandRouting.Simulation"/> administrative line enters through
/// <see cref="ConsoleTextSink"/>, which is bound to <see cref="CommandPrincipal.Console"/> at construction; a
/// host-minted <see cref="TextCommandSession"/> uses a sink fixed to its seat principal and slot. Authored interface
/// controls enter through <see cref="Activate"/>; other producers provide ordinary timestamped
/// <see cref="InputSignal"/> values through <see cref="Capture"/>. All forms join one deterministic capture order
/// before snapshot construction.
/// <para>The mixer is also the principal door: every entry leaves this type carrying a <see cref="CommandPrincipal"/>.
/// An injected entry keeps the one its sink was constructed with; every captured entry is stamped from
/// <see cref="ICommandPrincipalResolver.PrincipalOf"/> for its lane. The lane's slot number is never turned into a
/// seat principal here — a claimed slot may be answering to a peer or a guest module, so only the host's roster can
/// say who it is.</para>
/// </remarks>
public sealed class InputRouter : IDisposable {
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

    // The per-signal binding memos below are keyed by command id and bounded by the signal's own binding count, so
    // they live in a stack buffer sized before the fold loop. Beyond this bound (no authored profile comes close —
    // it is one source's bindings on one page plus the host plane) the buffer falls back to the heap rather than
    // growing the frame without limit.
    private const int MaxStackMemoCount = 32;

    private static readonly IComparer<CommandLane> LaneBySlotComparer = Comparer<CommandLane>.Create(comparison: static (left, right) => left.Slot.CompareTo(value: right.Slot));

    private readonly IAlwaysActiveInputBindings? m_alwaysActiveBindings;
    private readonly IInputBindings m_bindings;
    private readonly IChordEdgeSource? m_chordEdges;
    private readonly IInputClock? m_clock;
    private readonly CommandInjectionSink m_consoleTextSink;
    private readonly IInputSlotResolver? m_inputSlotResolver;
    private readonly ICommandPrincipalResolver m_principalResolver;
    private readonly CommandRegistry m_registry;
    private readonly Func<InputDeviceId, int> m_slotResolver;

    private bool m_disposed;
    private ulong m_sequence;
    private int m_snapshotLaneCount;

    private readonly Lock m_captureGate = new();
    private readonly CaptureQueue<CapturedInjection> m_capturedInjections = new(capacity: MaxCapturedInjections);
    private readonly CaptureQueue<CapturedSignal> m_capturedSignals = new(capacity: MaxCapturedSignals);
    // Simulation-thread scratch retained across ticks. Snapshot output uses the same borrowed-storage discipline, so
    // steady-state idle and active ticks allocate nothing — including the fold of a BOUND signal, whose two
    // per-signal memos are stack buffers rather than dictionaries. Capture remains independently protected by
    // m_captureGate.
    private readonly List<CapturedInjection> m_dueInjections = [];
    private readonly List<CommandInjection> m_duePendingInjections = [];
    private readonly List<CapturedSignal> m_dueSignals = [];
    private readonly Stack<HeldCommandState> m_freeHeldStates = [];
    private readonly Dictionary<int, Dictionary<ushort, HeldCommandState>> m_heldBySlot = [];
    private readonly Dictionary<int, ulong> m_lastInputTickBySlot = [];
    // Runtime modality is per logical slot. Missing slots share the registry's immutable Global-only default; a
    // transition compiles named maps to command-id activity once, leaving source resolution as one array read.
    private readonly Dictionary<int, CommandModality> m_modalityBySlot = [];
    // Router-SYNTHESIZED edges owed to the next tick: a transient impulse's inactive twin and every deterministic
    // cancellation. They are not captured input, so they carry no clock stamp at all — SnapshotForTick drains this
    // list at its top, before the tick's own due signals, which makes the delay exactly one tick by ordering alone
    // (the same construction that gives IChordEdgeSource.DrainScheduledEdges its one-tick delay). Stamping them
    // from the wall clock instead would defer them past every step of an N-step catch-up, making the gap between an
    // input's active and inactive edge a function of frame pacing.
    private readonly List<CommandInjection> m_pendingInjections = [];
    // Which physical controls are currently DEFLECTED, shared by both capture routes. The companion to
    // m_pressedControls, covering the continuous shapes a first-down latch cannot: an analog control never reports a
    // Started event, so only its own samples say whether a hand is on it — and only a control that was deflected is
    // RELEASING when it next reports zero.
    private readonly HashSet<HeldControlId> m_activeControls = [];
    // Physical first-down truth is shared by focused and focus-exempt capture. A console-opening press can move its
    // device between those routes before the OS emits repeats or the release; one latch must still recognize them as
    // the same press.
    private readonly HashSet<HeldControlId> m_pressedControls = [];
    // A BindingEntryMode.Toggle latch, keyed by (slot, commandId) — the destination's flip state, independent of
    // which physical control (or device) toggled it. Lives here, not in Puck.World.Server: the sim reads a plain
    // held channel either way (see BindingEntryMode's remarks).
    private readonly Dictionary<(int Slot, ushort CommandId), bool> m_toggleLatches = [];
    private readonly Dictionary<int, List<CommandEntry>> m_workingBySlot = [];
    // Binding lists are immutable runtime artifacts. Lower each list's command names to ids once per installed
    // profile/host plane, then keep the per-signal fold entirely in the registry's numeric namespace.
    // The resolver owns each immutable list identity. Cache lowered command ids only while that list remains live;
    // a mutable/custom resolver returning replacement identities cannot make the router retain every historical list.
    private readonly ConditionalWeakTable<IReadOnlyList<CommandBinding>, ResolvedBinding[]> m_resolvedBindingLists = new();
    private readonly Dictionary<string, ushort> m_resolvedCommandIds = new(comparer: StringComparer.OrdinalIgnoreCase);
    // Snapshot output is borrowed until the next SnapshotForTick call. Retain one entry array per observed slot and
    // one lane array for the router, growing only when a new high-water mark is reached.
    private readonly Dictionary<int, SnapshotEntryBuffer> m_snapshotEntriesBySlot = [];
    private CommandLane[] m_snapshotLanes = [];

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
    private readonly record struct HeldCommand(int Slot, ushort CommandId);
    // One physical control holding a command: the (Device, Source) identity a digital hold is tracked and de-duped
    // by. Slot and command id are the enclosing dictionary keys, so they are not repeated here.
    private readonly record struct HeldControlId(InputDeviceId Device, string Source);
    private readonly record struct HeldContribution(HeldControlId Control, CommandEntry Entry);
    // A toggle is owned by its logical destination rather than whichever physical control happened to flip it.
    // Precompute that synthetic source with the binding-list lowering so the per-signal path allocates nothing.
    private readonly record struct ResolvedBinding(CommandBinding Binding, ushort CommandId, string? ToggleSource);
    // One command id's per-signal memos, linear-probed out of a stack buffer sized by the signal's binding count.
    // Both facts a signal must remember across its own bindings are per COMMAND, and a signal names at most one
    // command per binding, so one small table serves both: OwnsHeldState is ownership judged as it stood when the
    // signal arrived (a press row's release bookkeeping must not strip the ownership the paired release row is
    // about to test), and the toggle flip is the direction the latch took, so a second binding on the same
    // destination reuses it instead of flipping back.
    private struct SignalMemo {
        internal ushort CommandId;
        internal bool HasToggleFlip;
        internal bool OwnsHeldState;
        internal CommandPhase TogglePhase;
    }
    private sealed class SnapshotEntryBuffer {
        internal int Count;
        internal CommandEntry[] Items = [];
    }
    // One held command's carried state within a slot. Entry/Controls model a single logical digital or synthesized
    // chord hold (first control down, last control up); Contributions models channel values independently by physical
    // control, because two keys feeding one axis must reassert and cancel separately. Mutable so both shapes update one
    // command-owned state in place without per-tick allocation.
    private sealed class HeldCommandState {
        public List<HeldContribution>? Contributions;
        public List<HeldControlId>? Controls;
        public CommandEntry Entry;
        public bool HasEntry;
        public bool HasPendingMomentaryRelease;
        // The payload a pending momentary release cancels with, kept SEPARATE from Entry: a tap and a live hold can
        // name one destination (a chord row and a page activator over the same channel), and folding the tap's press
        // into the hold's carried entry would make every later re-assertion replay the tap's dispatched Started edge.
        public CommandEntry MomentaryEntry;

        public bool IsEmpty => (!HasEntry && !HasPendingMomentaryRelease && (Contributions is not { Count: > 0 }));
        public bool IsHeld => (HasEntry || (Contributions is { Count: > 0 }));

        public void Reset() {
            Entry = default;
            HasEntry = false;
            HasPendingMomentaryRelease = false;
            MomentaryEntry = default;
            Controls?.Clear();
            Contributions?.Clear();
        }
    }

    /// <summary>Initializes a new instance of the <see cref="InputRouter"/> class.</summary>
    /// <param name="registry">The registry that interns command ids and map metadata.</param>
    /// <param name="bindings">The slot-aware binding resolver (per-player mappings layered over a default).</param>
    /// <param name="principalResolver">Answers who is acting through a slot. Required: the mixer stamps every captured
    /// entry from it and must never synthesize an identity of its own.</param>
    /// <param name="slotResolver">Maps a device to a logical player slot; defaults to a single local slot (<c>0</c>).</param>
    /// <param name="clock">The shared capture clock used to stamp an injected command that arrives without an explicit capture tick; optional.</param>
    /// <param name="alwaysActiveBindings">Optional host-owned bindings resolved independently of authored pages.</param>
    /// <exception cref="ArgumentNullException"><paramref name="registry"/>, <paramref name="bindings"/>, or
    /// <paramref name="principalResolver"/> is <see langword="null"/>.</exception>
    public InputRouter(
        CommandRegistry registry,
        IInputBindings bindings,
        ICommandPrincipalResolver principalResolver,
        Func<InputDeviceId, int>? slotResolver = null,
        IInputClock? clock = null,
        IAlwaysActiveInputBindings? alwaysActiveBindings = null
    ) {
        ArgumentNullException.ThrowIfNull(bindings);
        ArgumentNullException.ThrowIfNull(principalResolver);
        ArgumentNullException.ThrowIfNull(registry);

        m_alwaysActiveBindings = alwaysActiveBindings;
        m_bindings = bindings;
        m_chordEdges = (bindings as IChordEdgeSource);
        m_clock = clock;
        m_principalResolver = principalResolver;
        m_registry = registry;
        m_slotResolver = (slotResolver ?? (static _ => 0));

        if (bindings is IInputBindingsReloadSource reloadSource) {
            reloadSource.Reloading += OnBindingsReloading;
        }
        // The console text door, built once here so nothing outside can mint one bound to a principal of its choosing.
        // Slot 0 is the local lane a console impulse rides; the Console principal is what makes it NOT that seat.
        m_consoleTextSink = new CommandInjectionSink(
            router: this,
            principal: CommandPrincipal.Console,
            slot: 0
        );
    }
    /// <summary>Initializes an input router whose device-to-slot resolver supports side-effect-free probing followed by
    /// an explicit commit after a binding is accepted.</summary>
    /// <param name="registry">The registry that interns command ids and map metadata.</param>
    /// <param name="bindings">The slot-aware binding resolver.</param>
    /// <param name="principalResolver">Answers who is acting through a slot.</param>
    /// <param name="slotResolver">The transactional device-to-slot resolver.</param>
    /// <param name="clock">The shared capture clock; optional.</param>
    /// <param name="alwaysActiveBindings">Optional host-owned bindings resolved independently of authored pages.</param>
    public InputRouter(
        CommandRegistry registry,
        IInputBindings bindings,
        ICommandPrincipalResolver principalResolver,
        IInputSlotResolver slotResolver,
        IInputClock? clock = null,
        IAlwaysActiveInputBindings? alwaysActiveBindings = null
    ) : this(
        registry: registry,
        bindings: bindings,
        principalResolver: principalResolver,
        slotResolver: (slotResolver ?? throw new ArgumentNullException(paramName: nameof(slotResolver))).ResolveSlot,
        clock: clock,
        alwaysActiveBindings: alwaysActiveBindings
    ) {
        m_inputSlotResolver = slotResolver;
        slotResolver.DeviceSlotChanging += ReleaseHeld;
    }

    internal CommandRegistry Registry => m_registry;

    /// <summary>The console/STDIN text door's injection sink — the one a <see cref="CommandRegistry"/> is wired to
    /// through <see cref="CommandRegistry.RouteSimulationTo"/>. Bound to <see cref="CommandPrincipal.Console"/> at
    /// construction, so a submitted line acts as the console and cannot be made to act as anything else.</summary>
    public CommandInjectionSink ConsoleTextSink => m_consoleTextSink;
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

    internal CommandInjectionSink CreateSeatTextSink(int slot) => new(
        router: this,
        principal: CommandPrincipal.Seat(slot: slot),
        slot: slot
    );
    // Queues one pre-resolved command. INTERNAL, and reachable only through a CommandInjectionSink: the injection's
    // principal and lane are the sink's construction-time facts, so there is no signature here a caller could hand a
    // principal of its own choosing to.
    internal void Enqueue(in CommandInjection injection) {
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

    // Records that a physical control now holds a digital command, creating the command's state if needed. De-duped
    // by control id, so an already-held control pressing again is idempotent (matching the old HashSet semantics).
    private void AddControl(int slot, ushort commandId, HeldControlId control) {
        var state = HeldFor(
            commandId: commandId,
            slot: slot
        );
        var controls = (state.Controls ??= new List<HeldControlId>(capacity: 2));

        if (!controls.Contains(item: control)) {
            controls.Add(item: control);
        }
    }
    // ONE cancellation per carried command, never two: a destination carrying both a live hold and a tap's pending
    // momentary release is one command owing one release, and the hold's own payload is the one that describes it.
    //
    // A pending momentary release is owed by the resolver's SCHEDULED edge, not by this table, so it is synthesized
    // here only when the caller is about to destroy that edge (dischargesScheduledEdges). A caller that leaves
    // IInputBindings alone leaves the obligation with its owner instead: cancelling it as well would deliver two
    // releases for one tap.
    private static void AppendCancellations(List<CommandInjection> cancellations, int slot, HeldCommandState state, bool dischargesScheduledEdges) {
        if (state.HasEntry) {
            cancellations.Add(item: CancellationFor(
                entry: state.Entry,
                slot: slot
            ));
        } else if (
            state.HasPendingMomentaryRelease &&
            dischargesScheduledEdges
        ) {
            cancellations.Add(item: CancellationFor(
                entry: state.MomentaryEntry,
                slot: slot
            ));
        }

        if (state.Contributions is { } contributions) {
            foreach (var contribution in contributions) {
                cancellations.Add(item: CancellationFor(
                    entry: contribution.Entry,
                    slot: slot
                ));
            }
        }
    }
    // Folds one synthesized chord-command edge into the slot's lane. The press carries held bookkeeping (so
    // IsCommandHeld lights and focus-loss cancellation covers a chord-held command); the release clears it. The
    // command-availability gate matches the bound path — an inactive-map command's chord is inert, not an error.
    private void ApplyChordEdge(Dictionary<int, List<CommandEntry>> workingBySlot, int slot, InputDeviceId device, in BindingChordEdge edge) {
        if (!TryResolveCommandId(
            name: edge.Command,
            id: out var commandId
        )) {
            return;
        }

        var sourceCommandActive = IsSourceCommandActive(
            commandId: commandId,
            slot: slot
        );
        var hadCarriedState = HasCarriedState(
            commandId: commandId,
            slot: slot
        );

        if (
            !sourceCommandActive &&
            ((edge.Phase is CommandPhase.Started or CommandPhase.Active) || !hadCarriedState)
        ) {
            return;
        }

        var dispatch = edge.Dispatch;
        var phase = edge.Phase;
        var value = edge.Value;

        if (edge.Mode == BindingEntryMode.Toggle) {
            if (phase != CommandPhase.Started) {
                return;
            }

            var latchKey = (slot, commandId);
            var turningOn = !m_toggleLatches.GetValueOrDefault(key: latchKey);

            m_toggleLatches[latchKey] = turningOn;
            phase = (turningOn
                ? CommandPhase.Started
                : CommandPhase.Completed
            );
            dispatch = (turningOn || edge.DispatchRelease);
            if (!turningOn) {
                value = CommandValue.Inactive(kind: value.Kind);
            }
        }

        var entry = new CommandEntry(
            commandId: commandId,
            device: device,
            dispatch: dispatch,
            origin: CommandOrigin.Binding,
            phase: phase,
            source: edge.Source,
            text: TextLine(
                command: edge.Command,
                dispatch: dispatch,
                phase: phase,
                text: edge.Text
            ),
            value: value
        );

        WorkingFor(
            slot: slot,
            workingBySlot: workingBySlot
        ).Add(item: entry);

        // A MOMENTARY press (a Tapped activator's completion — see BindingChordEdge.Momentary) touches neither
        // branch below: it must not be marked held (there is nothing sustaining it — its own release is already
        // scheduled one tick later), and it must not run the release-side removal either (it never marked
        // anything to remove, and nothing else's held entry should be disturbed by an edge that isn't a real
        // Completed transition).
        if (phase is CommandPhase.Started or CommandPhase.Active) {
            if (!edge.Momentary) {
                var state = HeldFor(
                    commandId: commandId,
                    slot: slot
                );

                state.Entry = (entry with {
                    Dispatch = false,
                    Phase = CommandPhase.Active,
                    Text = null,
                });
                state.HasEntry = true;
            } else if (
                (phase == CommandPhase.Started) &&
                edge.DispatchRelease
            ) {
                // A tapped channel carries no Active reassertion, but its scheduled release still owns
                // cleanup. Retain only the cancellation payload — in its OWN slot, so a live hold on the same
                // destination keeps re-asserting its own entry — so a map transition between the two ticks cannot
                // strand the handler after its Started edge.
                var state = HeldFor(
                    commandId: commandId,
                    slot: slot
                );

                state.HasPendingMomentaryRelease = true;
                state.MomentaryEntry = entry;
            }
        } else {
            DropHeld(
                commandId: commandId,
                slot: slot
            );
        }
    }
    // Folds a pre-resolved command directly into its slot's lane for this tick — no binding lookup (it is already
    // bound) and no held bookkeeping: an injection is one-shot, present only in the tick its capture window placed
    // it, with the caller-chosen edge. A held console input is expressed as an explicit Started/Completed pair.
    private void ApplyInjection(Dictionary<int, List<CommandEntry>> workingBySlot, CommandInjection injection) {
        if (
            (injection.Origin == CommandOrigin.Binding) &&
            !IsSourceCommandActive(
            slot: injection.Slot,
            commandId: injection.CommandId
        ) &&
            !injection.DispatchWhenMapInactive
        ) {
            return;
        }

        var working = WorkingFor(
            workingBySlot: workingBySlot,
            slot: injection.Slot
        );

        working.Add(item: new CommandEntry(
            commandId: injection.CommandId,
            device: default,
            dispatch: true,
            origin: injection.Origin,
            phase: injection.Phase,
            principal: injection.Principal,
            source: injection.Source,
            text: injection.Text,
            value: injection.Value
        ) {
            SubmissionBarrier = injection.SubmissionBarrier,
        });
    }
    private void ApplySignal(Dictionary<int, List<CommandEntry>> workingBySlot, InputSignal signal, ulong tick, bool focusExemptOnly) {
        // Resolve activity before repeat de-duplication: an OS repeat is not a second command edge, but it is still
        // fresh physical activity for idle/away accounting.
        // An authored lane (InputSignal.Slot) is never a device: it bypasses the resolver, seats nothing, and does not
        // count as the player's own activity.
        var authoredLane = (signal.Slot >= 0);

        // Classify the signal's device kind BEFORE any slot resolution runs for it — a kind-aware seating policy
        // (PlayerRoster's couch-sharing rule) reads this while deciding the very slot being resolved below, not
        // after. An authored lane carries no real device to classify.
        if (!authoredLane) {
            m_inputSlotResolver?.ObserveDeviceKind(
                device: signal.DeviceId,
                kind: ClassifyDeviceKind(source: signal.Source)
            );
        }

        var slot = (authoredLane ? signal.Slot : m_slotResolver(arg: signal.DeviceId));

        if (slot < 0) {
            return;
        }

        // Activity is a PRESS, a RELEASE, or an analog sample deflected past the rest band — never a device merely
        // reporting, and never a posture reading (an accelerometer carries gravity in every report). Counting
        // those as the player's activity would mean a paired pad never goes idle (the binding bar's "recently
        // SeatInput" would hold forever).
        if (
            !authoredLane &&
            IsActivity(signal: in signal)
        ) {
            m_lastInputTickBySlot[slot] = tick;
        }
        var activeCommands = ModalityFor(slot: slot).ActiveCommands;

        var physicalControl = new HeldControlId(
            Device: signal.DeviceId,
            Source: signal.Source
        );
        var isDigitalReassertion = ((signal.Phase == CommandPhase.Active) && (signal.Value.Kind == CommandValueKind.Digital));

        // A text-bearing signal is not a physical control transition at all: the platform emits one Started per typed
        // character and never a release (see InputSignal.Typed), so latching it would seat its source permanently —
        // swallowing every character after the first as an "OS repeat" and leaving the latch stuck down forever. The
        // fold already treats a text signal as never-active for the same reason.
        var latchesPress = (signal.Text is null);

        if (signal.Phase == CommandPhase.Started) {
            // OS key repeat is another Started event. It must not re-run an edge command (especially a toggle), and
            // opening a console between the first event and a repeat must not make that repeat look like a new press.
            if (
                latchesPress &&
                !m_pressedControls.Add(item: physicalControl)
            ) {
                return;
            }
        } else if (signal.Phase is CommandPhase.Completed or CommandPhase.Canceled) {
            _ = m_pressedControls.Remove(item: physicalControl);
        }

        // Deflection truth, updated for BOTH capture routes so a control pressed while focused is still known to be
        // down when its release arrives on the focus-exempt one. A transient impulse is an event, not a held control,
        // so it never enters — nothing would ever take it out again.
        var wasActive = m_activeControls.Contains(item: physicalControl);

        if (latchesPress) {
            if (
                signal.Value.IsActive &&
                !signal.Transient &&
                (signal.Phase is not (CommandPhase.Completed or CommandPhase.Canceled))
            ) {
                _ = m_activeControls.Add(item: physicalControl);
            } else {
                _ = m_activeControls.Remove(item: physicalControl);
            }
        }

        // A control going inactive is a RELEASE — the one shape that must reach the resolver even under focus
        // exemption (see below), and the one that releases stranded holds.
        var isReleasing = ((signal.Phase is CommandPhase.Completed or CommandPhase.Canceled) || !signal.Value.IsActive);
        // Focus-exempt capture deliberately never DISPATCHES through the current authored page. Host-owned terminal
        // bindings live in their own always-active plane, so a page override cannot accidentally remove the escape
        // hatch. A release is still forwarded through the resolver and its answer discarded: the resolver carries the
        // chord/modifier tracker, the press latches, and the armed command rows, and a release those never see leaves
        // the page flipped and the row armed for as long as the seat console stays open. Presses stay withheld —
        // nothing may flip a page or arm a row while the device's focus is released.
        // A RELEASE here is narrower than isReleasing: a continuous producer streams inactive samples forever (a stick
        // sitting at centre reports every frame), and those are the device REPORTING, not a release. Forwarding them
        // would consult the authored page — creating slot state, advancing the chord tracker and driving row
        // activators — on every frame a seat console stays open. Only a control this router has seen DEFLECTED is
        // releasing when it reports inactive.
        var forwardsToResolver = (
            !focusExemptOnly ||
            (signal.Phase is CommandPhase.Completed or CommandPhase.Canceled) ||
            (!signal.Value.IsActive && wasActive)
        );
        var resolvedPageBindings = (forwardsToResolver
            ? m_bindings.Resolve(
                signal: signal,
                slot: slot
            )
            : null
        );
        var pageBindings = ResolveBindings(bindings: (focusExemptOnly
            ? null
            : resolvedPageBindings
        ));
        var alwaysActiveBindings = ResolveBindings(bindings: m_alwaysActiveBindings?.Resolve(
            slot: slot,
            source: signal.Source
        ));

        if (
            forwardsToResolver &&
            (m_chordEdges is not null)
        ) {
            // Chord-command edges synthesized by this signal's resolve fold into the same lane with their OWN
            // phase and value (the physical signal's phase may be a mid-sweep Active) — see IChordEdgeSource.
            foreach (var edge in m_chordEdges.DrainChordEdges(slot: slot)) {
                // Under focus exemption this drain exists to deliver what the RELEASE owes — the broken row's
                // completion — never to press something new. A member release can leave a SHORTER row exactly
                // satisfied (releasing left out of [left, right] completes [right]), and that row's press must
                // neither dispatch nor latch a command that never declared CommandInputScope.FocusExempt: a latched
                // press would re-assert for as long as the seat console stays open.
                if (
                    focusExemptOnly &&
                    (edge.Phase is not (CommandPhase.Completed or CommandPhase.Canceled)) &&
                    !IsFocusExemptEdge(edge: in edge)
                ) {
                    continue;
                }

                ApplyChordEdge(
                    workingBySlot: workingBySlot,
                    slot: slot,
                    device: signal.DeviceId,
                    edge: in edge
                );
            }
        }

        // A control going inactive releases every hold it feeds, whatever the current page resolves it to: a page
        // flip between the press and the release must not strand the earlier page's command with a stale sample.
        // A control observed while its device's focus is released (a terminal open) is the same case: the page
        // resolves nothing for it, so its page-bound holds drop now and re-establish from the still-down reassert
        // once focus returns.
        if (
            focusExemptOnly ||
            isReleasing
        ) {
            ReleaseStrandedHolds(
                alwaysActiveBindings: alwaysActiveBindings,
                control: physicalControl,
                pageBindings: pageBindings,
                slot: slot,
                workingBySlot: workingBySlot
            );
        }

        var pageBindingCount = pageBindings.Length;
        var alwaysActiveBindingCount = alwaysActiveBindings.Length;

        if ((pageBindingCount + alwaysActiveBindingCount) == 0) {
            return;
        }

        var assignedSlot = false;
        var acceptedBinding = false;
        var bindingCount = (pageBindingCount + alwaysActiveBindingCount);
        // The signal's per-command memos (see SignalMemo). A held-channel entry conventionally authors a PAIR of
        // bindings on the same source (ActivateOn: null for the press/active edge, ActivateOn: Completed for the
        // release edge — see BindingPageEntryDefinition), so one physical signal reaches a Toggle-mode command TWICE
        // and touches one command's held ownership twice. Both memos are loop-local and bounded by the binding
        // count, so this is a stack buffer rather than the pair of dictionaries it replaced — a bound signal is on
        // the steady-state path and must not allocate.
        var memos = ((bindingCount <= MaxStackMemoCount)
            ? stackalloc SignalMemo[MaxStackMemoCount]
            : new SignalMemo[bindingCount].AsSpan()
        );
        var memoCount = 0;

        for (var bindingIndex = 0; (bindingIndex < bindingCount); bindingIndex++) {
            var resolved = ((bindingIndex < pageBindingCount)
                ? pageBindings[bindingIndex]
                : alwaysActiveBindings[(bindingIndex - pageBindingCount)]
            );
            var binding = resolved.Binding;
            var commandId = resolved.CommandId;
            var dispatchSource = (resolved.ToggleSource ?? signal.Source);

            if (
                focusExemptOnly &&
                !m_registry.IsFocusExemptCommand(commandId: commandId)
            ) {
                continue;
            }

            var value = ResolveValue(
                binding: in binding,
                signal: in signal
            );
            var controlId = physicalControl;
            var sourceCommandActive = ((commandId < activeCommands.Length) && activeCommands[commandId]);

            var memoIndex = IndexOfMemo(
                commandId: commandId,
                memoCount: memoCount,
                memos: memos
            );

            if (memoIndex < 0) {
                // Ownership is judged as it stood when the signal ARRIVED, for every binding alike: the press row's
                // own release bookkeeping (RemoveControl below) must not strip the ownership the release row is
                // about to test — else the release row is skipped and the hold sticks.
                memoIndex = memoCount++;
                memos[memoIndex] = new SignalMemo {
                    CommandId = commandId,
                    OwnsHeldState = IsHeldByControl(
                        commandId: commandId,
                        control: controlId,
                        slot: slot
                    ),
                };
            }

            var ownsHeldState = memos[memoIndex].OwnsHeldState;
            var isContribution = ((binding.ChannelScale is not null) && (binding.Mode == BindingEntryMode.Hold));

            // A digital Active sample is state recovery, never a command edge. Continuous channel destinations may
            // establish/refresh their contribution; ordinary commands and toggles wait for a real Started edge.
            if (
                isDigitalReassertion &&
                !isContribution
            ) {
                continue;
            }

            // Map gating blocks new ownership, but never swallows the release of ownership acquired while the map
            // was active. Only Completed/Canceled cleanup edges cross a closed map.
            if (!sourceCommandActive) {
                if (
                    (binding.Mode == BindingEntryMode.Toggle) ||
                    !ownsHeldState ||
                    ((signal.Phase is CommandPhase.Started or CommandPhase.Active) && value.IsActive)
                ) {
                    continue;
                }
            }

            // A digital release belongs to the mapping that observed its press (or recovered channel ownership).
            // Reload/reset clears that ownership, so a release-only edge verb cannot fire merely because a held key
            // acquired a different meaning while down.
            if (
                (signal.Value.Kind == CommandValueKind.Digital) &&
                (signal.Phase is CommandPhase.Completed or CommandPhase.Canceled) &&
                !ownsHeldState
            ) {
                continue;
            }

            if (
                sourceCommandActive &&
                !acceptedBinding
            ) {
                assignedSlot = (!authoredLane && (m_inputSlotResolver?.CommitSlot(
                    device: signal.DeviceId,
                    slot: slot
                ) ?? false));
                acceptedBinding = true;
            }

            var working = WorkingFor(
                slot: slot,
                workingBySlot: workingBySlot
            );
            var isDigital = ((value.Kind == CommandValueKind.Digital) && !isContribution);
            var phase = signal.Phase;

            if (
                !sourceCommandActive &&
                (phase is not (CommandPhase.Completed or CommandPhase.Canceled))
            ) {
                phase = CommandPhase.Canceled;
            }

            // A Toggle-mode binding never reads the physical control's own phase directly: a press FLIPS the
            // latch and the flip's direction becomes the effective phase every line below reasons about (Started
            // when turning on, Completed when turning off); the physical release/active phases that would
            // otherwise re-drive this logic are ignored outright — the latch, not the control, owns "held" now.
            // Gated on the SIGNAL's own kind, not `isDigital` (the DESTINATION's resolved value kind): a channel
            // destination's ChannelScale always resolves to Axis1D (see ResolveValue), even from a digital source,
            // so `isDigital` alone would never see Toggle mode's primary case — a digital key toggling a channel.
            if (
                (signal.Value.Kind == CommandValueKind.Digital) &&
                (binding.Mode == BindingEntryMode.Toggle)
            ) {
                if (signal.Phase != CommandPhase.Started) {
                    continue;
                }

                if (memos[memoIndex].HasToggleFlip) {
                    // The latch must flip exactly ONCE per signal: the paired release row reuses the flip's
                    // resolved phase instead of flipping again, which would net a silent no-op.
                    phase = memos[memoIndex].TogglePhase;
                } else {
                    var latchKey = (slot, commandId);
                    var turningOn = !m_toggleLatches.GetValueOrDefault(key: latchKey);

                    m_toggleLatches[latchKey] = turningOn;
                    phase = (turningOn
                        ? CommandPhase.Started
                        : CommandPhase.Completed
                    );
                    memos[memoIndex].HasToggleFlip = true;
                    memos[memoIndex].TogglePhase = phase;
                }
            }

            // A channel destination — and a HELD verb (CommandMetadata.Held) — is a held contribution, so its ordinary
            // (ActivateOn:null) binding owns BOTH halves of the hold. In particular, an axis such as a trigger commonly
            // ends with Completed+zero. If that edge is filtered like an ordinary press-bound verb, this router clears
            // its carried sample while the destination never hears the release and retains the last non-zero
            // contribution forever. Authors bind a hold once, never a release twin. An explicit ActivateOn remains
            // exactly edge-selective.
            var dispatch = ((binding.ActivateOn is { } required)
                ? (phase == required)
                : ((phase is CommandPhase.Started or CommandPhase.Active) ||
                    (((binding.ChannelScale is not null) || m_registry.IsHeldCommand(commandId: commandId)) && (phase is CommandPhase.Completed or CommandPhase.Canceled)))
            );
            var wasCommandHeld = (isDigital && IsControlDownFor(
                commandId: commandId,
                slot: slot
            ));
            var active = ((phase is CommandPhase.Started or CommandPhase.Active) && value.IsActive && (signal.Text is null));

            if (
                (binding.Mode == BindingEntryMode.Toggle) &&
                !active
            ) {
                // The toggle-off transition: the flip already decided this is a release, independent of the live
                // signal's own value (a toggle-off arrives ON a fresh PRESS, so signal.Value still reads active).
                value = CommandValue.Inactive(kind: value.Kind);
            }

            if (isDigital) {
                if (active) {
                    AddControl(
                        commandId: commandId,
                        control: controlId,
                        slot: slot
                    );

                    // Two physical controls may bind the same logical command (W + Up). The logical press edge fires
                    // only when the first control goes down.
                    if (wasCommandHeld) {
                        dispatch = false;
                    }
                } else {
                    RemoveControl(
                        commandId: commandId,
                        control: controlId,
                        slot: slot
                    );

                    // Likewise, the logical release edge fires only when the last bound control goes up.
                    if (IsControlDownFor(
                        commandId: commandId,
                        slot: slot
                    )) {
                        dispatch = false;
                        value = m_heldBySlot[slot][commandId].Entry.Value;
                        phase = CommandPhase.Active;
                    }
                }
            }

            var entry = new CommandEntry(
                commandId: commandId,
                device: signal.DeviceId,
                dispatch: dispatch,
                origin: CommandOrigin.Binding,
                phase: phase,
                source: dispatchSource,
                text: TextLine(
                    command: binding.Command,
                    dispatch: dispatch,
                    phase: phase,
                    text: binding.Text
                ),
                value: value,
                assignedSlot: assignedSlot
            );

            working.Add(item: entry);

            // Channel destinations carry one contribution per physical control. Two keys sharing a destination
            // therefore remain independently owned: releasing one cannot erase the other's reassertion or later
            // focus-loss cancellation.
            if (isContribution) {
                if (
                    active &&
                    signal.Transient
                ) {
                    // An impulse never becomes carried state, even when an edge-selective ActivateOn suppresses its
                    // dispatch. When dispatched, its active value is visible for this tick and an ordered inactive
                    // edge follows on the NEXT tick — exactly the next, because the twin is queued as a pending
                    // synthesized edge rather than stamped from the clock (see m_pendingInjections) — so the channel
                    // handler cannot retain the final delta indefinitely. It crosses a map close because cleanup is
                    // still owed.
                    if (dispatch) {
                        EnqueuePending(injection: new CommandInjection(
                            CommandId: commandId,
                            Value: CommandValue.Inactive(kind: value.Kind),
                            Phase: CommandPhase.Completed,
                            Origin: CommandOrigin.Binding,
                            Principal: default,
                            Slot: slot,
                            Source: dispatchSource
                        ) {
                            DispatchWhenMapInactive = true,
                        });
                    }
                } else if (active) {
                    SetContribution(
                        commandId: commandId,
                        control: controlId,
                        entry: entry with {
                            Dispatch = true,
                            Phase = CommandPhase.Active,
                            Text = null,
                        },
                        slot: slot
                    );
                } else {
                    RemoveContribution(
                        commandId: commandId,
                        control: controlId,
                        slot: slot
                    );
                }
            } else if (
                active &&
                !signal.Transient
            ) {
                var state = HeldFor(
                    commandId: commandId,
                    slot: slot
                );

                state.Entry = (entry with {
                    Dispatch = (value.Kind != CommandValueKind.Digital),
                    Phase = CommandPhase.Active,
                    Text = null,
                });
                state.HasEntry = true;
            } else if (
                ((signal.Phase is CommandPhase.Completed or CommandPhase.Canceled) || !value.IsActive) &&
                (!isDigital || !IsControlDownFor(
                commandId: commandId,
                slot: slot
            ))
            ) {
                DropHeld(
                    commandId: commandId,
                    slot: slot
                );
            }
        }
    }
    private CommandSnapshot Build(ulong tick) {
        var workingBySlot = m_workingBySlot;

        if (workingBySlot.Count == 0) {
            return CommandSnapshot.Empty(tick: tick);
        }

        var activeLaneCount = 0;

        foreach (var (slot, working) in workingBySlot) {
            if (working.Count != 0) {
                activeLaneCount++;
            } else if (
                m_snapshotEntriesBySlot.TryGetValue(
                key: slot,
                value: out var idleBuffer
            ) &&
                (idleBuffer.Count != 0)
            ) {
                Array.Clear(
                    array: idleBuffer.Items,
                    index: 0,
                    length: idleBuffer.Count
                );
                idleBuffer.Count = 0;
            }
        }

        if (activeLaneCount == 0) {
            ClearRetiredLanes(activeLaneCount: 0);

            return CommandSnapshot.Empty(tick: tick);
        }

        if (m_snapshotLanes.Length < activeLaneCount) {
            Array.Resize(
                array: ref m_snapshotLanes,
                newSize: Math.Max(
                    val1: activeLaneCount,
                    val2: Math.Max(
                        val1: 4,
                        val2: (m_snapshotLanes.Length * 2)
                    )
                )
            );
        }

        var laneIndex = 0;

        foreach (var (slot, working) in workingBySlot) {
            if (working.Count == 0) {
                continue;
            }

            if (!m_snapshotEntriesBySlot.TryGetValue(
                key: slot,
                value: out var buffer
            )) {
                buffer = new SnapshotEntryBuffer();
                m_snapshotEntriesBySlot[slot] = buffer;
            }

            if (buffer.Items.Length < working.Count) {
                Array.Resize(
                    array: ref buffer.Items,
                    newSize: Math.Max(
                        val1: working.Count,
                        val2: Math.Max(
                            val1: 4,
                            val2: (buffer.Items.Length * 2)
                        )
                    )
                );
            }

            var entries = buffer.Items;
            // THE STAMP. Ask the host who is acting through this lane — once per lane, because the answer is a
            // property of the slot, not of the entry. A slot may be claimed by a peer or a guest module, so the slot
            // number is never turned into a seat here.
            var lanePrincipal = m_principalResolver.PrincipalOf(slot: slot);

            // Entry order is semantic: held state is emitted first in command-id order, then due signals/injections in
            // their deterministic capture order. In particular, repeated console verbs in one host frame must remain
            // repeated and FIFO — collapsing by command id would silently drop scripted tape segments.
            for (var entryIndex = 0; (entryIndex < working.Count); entryIndex++) {
                var entry = working[entryIndex];

                // An injected entry already carries the identity its sink was BOUND to (the console door rides slot 0
                // without becoming that seat); everything captured is stamped from the lane.
                entries[entryIndex] = (entry.Principal.IsStamped
                    ? entry
                    : (entry with { Principal = lanePrincipal, })
                );
            }

            if (buffer.Count > working.Count) {
                Array.Clear(
                    array: entries,
                    index: working.Count,
                    length: (buffer.Count - working.Count)
                );
            }

            buffer.Count = working.Count;

            m_snapshotLanes[laneIndex++] = new CommandLane(
                entries: new CommandBuffer<CommandEntry>(
                    items: entries,
                    count: working.Count
                ),
                slot: slot
            );
        }

        ClearRetiredLanes(activeLaneCount: activeLaneCount);

        // Order lanes by slot for a deterministic snapshot layout.
        Array.Sort(
            array: m_snapshotLanes,
            comparer: LaneBySlotComparer,
            index: 0,
            length: activeLaneCount
        );

        return new CommandSnapshot(
            lanes: new CommandBuffer<CommandLane>(
                count: activeLaneCount,
                items: m_snapshotLanes
            ),
            registry: m_registry,
            tick: tick
        );
    }
    // One deterministic cancellation for a carried command. Unstamped on purpose: a synthesized release belongs to the
    // SLOT that held the input, so snapshot construction resolves its principal like any other captured entry rather
    // than freezing whoever was acting when the hold began.
    private static CommandInjection CancellationFor(int slot, CommandEntry entry) => new(
        CommandId: entry.CommandId,
        Value: CommandValue.Inactive(kind: entry.Value.Kind),
        Phase: CommandPhase.Canceled,
        Origin: entry.Origin,
        Principal: default,
        Slot: slot,
        Source: entry.Source
    ) {
        DispatchWhenMapInactive = true,
    };
    // The one door both capture entry points share: refuse a signal that names no control, then append it under the
    // gate, dropping the OLDEST retained signal first when the queue has reached its cap.
    private void CaptureSignal(in InputSignal signal, bool focusExemptOnly) {
        if (string.IsNullOrEmpty(value: signal.Source)) {
            // Refused HERE rather than surfacing later as a NullReferenceException on the pump thread, inside the
            // device classification of a signal no longer attributable to the producer that captured it.
            throw new ArgumentException(
                message: "A captured input signal must name the source control that produced it.",
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
    private void ClearRetiredLanes(int activeLaneCount) {
        if (m_snapshotLaneCount > activeLaneCount) {
            Array.Clear(
                array: m_snapshotLanes,
                index: activeLaneCount,
                length: (m_snapshotLaneCount - activeLaneCount)
            );
        }

        m_snapshotLaneCount = activeLaneCount;
    }
    // The device-kind family test for ObserveDeviceKind: every InputSources id is prefixed by its physical-control
    // group ("keyboard.", "mouse.", "gamepad.") — see Puck.Input.InputSources — mirrored here as literal prefixes
    // rather than a reference to that vocabulary, since Puck.Commands sits below Puck.Input in the dependency
    // layering. Anything else (a probe source, an authored/injected source) classifies as Gamepad, the roster's own
    // defensive floor for a device it cannot otherwise place.
    private static InputDeviceKind ClassifyDeviceKind(string source) {
        if (source.StartsWith(comparisonType: StringComparison.Ordinal, value: "keyboard.")) {
            return InputDeviceKind.Keyboard;
        }

        if (source.StartsWith(comparisonType: StringComparison.Ordinal, value: "mouse.")) {
            return InputDeviceKind.Mouse;
        }

        return InputDeviceKind.Gamepad;
    }
    private static int CompareCaptureOrder(ulong leftTick, ulong leftSequence, ulong rightTick, ulong rightSequence) {
        var byTime = leftTick.CompareTo(value: rightTick);

        return ((byTime != 0)
            ? byTime
            : leftSequence.CompareTo(value: rightSequence)
        );
    }
    // The emission order for a stranded release: command id, then source — identical to the comparator the held
    // seeding sorts by, so a slot's entries read the same way whichever path produced them.
    private static int CompareStrandedOrder((ushort CommandId, CommandEntry Entry) left, (ushort CommandId, CommandEntry Entry) right) {
        var byCommand = left.CommandId.CompareTo(value: right.CommandId);

        return ((byCommand != 0)
            ? byCommand
            : StringComparer.Ordinal.Compare(
                x: left.Entry.Source,
                y: right.Entry.Source
            )
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
    // Removes one command from a slot's held table and drops the now-empty slot entry — the single remove-and-prune
    // idiom every release path (focus loss, device disconnect, an inactive analog sample, a chord release) shares.
    private void DropHeld(int slot, ushort commandId) {
        if (m_heldBySlot.TryGetValue(
            key: slot,
            value: out var held
        )) {
            if (held.Remove(
                key: commandId,
                value: out var state
            )) {
                RecycleHeldState(state: state);
            }

            if (held.Count == 0) {
                _ = m_heldBySlot.Remove(key: slot);
            }
        }
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
    private bool HasCarriedState(ushort commandId, int slot) {
        return (
            m_heldBySlot.TryGetValue(
            key: slot,
            value: out var held
        ) &&
            held.ContainsKey(key: commandId)
        );
    }
    // Gets (creating if absent) the carried state for one command in a slot.
    private HeldCommandState HeldFor(int slot, ushort commandId) {
        if (!m_heldBySlot.TryGetValue(
            key: slot,
            value: out var held
        )) {
            held = [];
            m_heldBySlot[slot] = held;
        }

        if (!held.TryGetValue(
            key: commandId,
            value: out var state
        )) {
            state = (m_freeHeldStates.TryPop(result: out var recycled)
                ? recycled
                : new HeldCommandState()
            );
            held[commandId] = state;
        }

        return state;
    }
    // Finds one command id's row in a signal's memo table, or -1 when the signal has not reached that command yet.
    // A linear probe: the table holds at most one row per binding the signal resolved to, which is a handful.
    private static int IndexOfMemo(ReadOnlySpan<SignalMemo> memos, int memoCount, ushort commandId) {
        for (var index = 0; (index < memoCount); index++) {
            if (memos[index].CommandId == commandId) {
                return index;
            }
        }

        return -1;
    }
    // Whether any physical control is still down for a DIGITAL command in a slot — the logical-hold test the
    // first-down / last-up edge logic reads. An analog or chord hold carries no controls and answers false here even
    // though it is carried; IsCommandHeld(int, string) is the "carried at all" test.
    // Whether a synthesized chord edge names a command the host declared reachable without ordinary terminal focus.
    // An edge whose command this router cannot resolve names nothing dispatchable, so it is not exempt either.
    private bool IsFocusExemptEdge(in BindingChordEdge edge) {
        return (
            TryResolveCommandId(
            id: out var commandId,
            name: edge.Command
        ) &&
            m_registry.IsFocusExemptCommand(commandId: commandId)
        );
    }
    private bool IsControlDownFor(int slot, ushort commandId) => TryGetHeldDevice(
        commandId: commandId,
        device: out _,
        slot: slot
    );
    private bool IsHeldByControl(int slot, ushort commandId, HeldControlId control) {
        if (
            !m_heldBySlot.TryGetValue(
            key: slot,
            value: out var held
        ) ||
            !held.TryGetValue(
            key: commandId,
            value: out var state
        )
        ) {
            return false;
        }

        if (state.Controls?.Contains(item: control) ?? false) {
            return true;
        }

        if (state.Contributions is { } contributions) {
            foreach (var contribution in contributions) {
                if (contribution.Control == control) {
                    return true;
                }
            }
        }

        return (
            state.HasEntry &&
            (state.Entry.Device == control.Device) &&
            string.Equals(
            a: state.Entry.Source,
            b: control.Source,
            comparisonType: StringComparison.Ordinal
        )
        );
    }
    private bool IsSourceCommandActive(int slot, ushort commandId) {
        var activity = ModalityFor(slot: slot).ActiveCommands;

        return (
            (commandId < activity.Length) &&
            activity[commandId]
        );
    }
    private CommandModality ModalityFor(int slot) {
        return (m_modalityBySlot.TryGetValue(
            key: slot,
            value: out var modality
        )
            ? modality
            : m_registry.DefaultModality
        );
    }
    private void OnBindingsReloading(int? slot) {
        m_resolvedBindingLists.Clear();

        if (slot is { } affectedSlot) {
            ArgumentOutOfRangeException.ThrowIfNegative(affectedSlot);
            _ = ClearSlotHeld(slot: affectedSlot);
        } else {
            ReleaseHeld();
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
    // Returns one dropped state to this router's retained scratch. Clearing releases its entry/source references and
    // logical contents while preserving a small Controls list's capacity for the next digital hold.
    private void RecycleHeldState(HeldCommandState state) {
        state.Reset();
        m_freeHeldStates.Push(item: state);
    }
    private void ReleaseHeld(InputDeviceId device, bool preservePressedControls) {
        var cancellations = new List<CommandInjection>();
        var toDrop = new List<HeldCommand>();

        if (!preservePressedControls) {
            m_activeControls.RemoveWhere(match: control => (control.Device == device));
            m_pressedControls.RemoveWhere(match: control => (control.Device == device));
        }

        foreach (var (slot, held) in m_heldBySlot) {
            foreach (var (commandId, state) in held) {
                if (
                    preservePressedControls &&
                    m_registry.IsFocusExemptCommand(commandId: commandId)
                ) {
                    continue;
                }

                if (state.Contributions is { } contributions) {
                    for (var index = (contributions.Count - 1); (index >= 0); index--) {
                        var contribution = contributions[index];

                        if (contribution.Control.Device != device) {
                            continue;
                        }

                        cancellations.Add(item: CancellationFor(
                            entry: contribution.Entry,
                            slot: slot
                        ));
                        contributions.RemoveAt(index: index);
                    }
                }

                if (state.Controls is { } controls) {
                    var removedControls = controls.RemoveAll(match: control => (control.Device == device));

                    if (
                        (removedControls > 0) &&
                        (controls.Count == 0) &&
                        state.HasEntry
                    ) {
                        cancellations.Add(item: CancellationFor(
                            entry: state.Entry,
                            slot: slot
                        ));
                        state.Entry = default;
                        state.HasEntry = false;
                    } else if (
                        (removedControls > 0) &&
                        (controls.Count > 0)
                    ) {
                        state.Entry = state.Entry with {
                            Device = controls[0].Device,
                            Source = controls[0].Source,
                        };
                    } else if (
                        state.HasEntry &&
                        (state.Entry.Device == device) &&
                        (controls.Count > 0)
                    ) {
                        // A suppressed release can leave the carried annotation naming the releasing device even
                        // though another device owns the logical hold. A later disconnect still repairs it.
                        state.Entry = state.Entry with {
                            Device = controls[0].Device,
                            Source = controls[0].Source,
                        };
                    }
                } else if (
                    state.HasEntry &&
                    (state.Entry.Device == device)
                ) {
                    cancellations.Add(item: CancellationFor(
                        entry: state.Entry,
                        slot: slot
                    ));
                    state.Entry = default;
                    state.HasEntry = false;
                }

                if (state.IsEmpty) {
                    _ = m_toggleLatches.Remove(key: (slot, commandId));
                    toDrop.Add(item: new HeldCommand(
                        CommandId: commandId,
                        Slot: slot
                    ));
                }
            }
        }

        foreach (var heldCommand in toDrop) {
            DropHeld(
                commandId: heldCommand.CommandId,
                slot: heldCommand.Slot
            );
        }

        QueueCancellations(
            cancellations: cancellations,
            discardCapturedSignals: false
        );
    }
    // Drops every non-contribution hold this control fed whose command the signal no longer resolves to, folding
    // an inactive entry into the lane so the command hears its release.
    private void ReleaseStrandedHolds(Dictionary<int, List<CommandEntry>> workingBySlot, int slot, HeldControlId control, ResolvedBinding[] pageBindings, ResolvedBinding[] alwaysActiveBindings) {
        if (!m_heldBySlot.TryGetValue(
            key: slot,
            value: out var held
        )) {
            return;
        }

        List<(ushort CommandId, CommandEntry Entry)>? stranded = null;
        List<(ushort CommandId, CommandEntry Entry)>? strandedContributions = null;

        foreach (var (commandId, state) in held) {
            // A toggle latch is destination-owned after its press. Its carried entry retains the originating
            // device/source only for diagnostics and focus/device cancellation; a later physical release — including
            // one consumed by a more-specific chord — must never classify the latch as that control's stranded hold.
            var feedsEntry = (
                state.HasEntry &&
                !m_toggleLatches.GetValueOrDefault(key: (slot, commandId)) &&
                (state.Entry.Device == control.Device) &&
                string.Equals(
                a: state.Entry.Source,
                b: control.Source,
                comparisonType: StringComparison.Ordinal
            )
            );
            CommandEntry? contribution = null;

            if (state.Contributions is { } contributions) {
                foreach (var candidate in contributions) {
                    if (candidate.Control == control) {
                        contribution = candidate.Entry;

                        break;
                    }
                }
            }

            if (
                !feedsEntry &&
                (contribution is null)
            ) {
                continue;
            }

            var resolvesHere = false;

            foreach (var resolved in pageBindings) {
                if (resolved.CommandId == commandId) {
                    resolvesHere = true;

                    break;
                }
            }

            foreach (var resolved in alwaysActiveBindings) {
                if (resolved.CommandId == commandId) {
                    resolvesHere = true;

                    break;
                }
            }

            if (resolvesHere) {
                continue;
            }

            if (feedsEntry) {
                (stranded ??= []).Add(item: (commandId, state.Entry));
            }

            if (contribution is { } strandedContribution) {
                (strandedContributions ??= []).Add(item: (commandId, strandedContribution));
            }
        }

        if (
            (stranded is null) &&
            (strandedContributions is null)
        ) {
            return;
        }

        var working = WorkingFor(
            slot: slot,
            workingBySlot: workingBySlot
        );

        // Both lists were gathered by walking a Dictionary, whose enumeration order is an implementation detail of
        // its insertion/removal history. Every other release path emits in (command id, source) order — the same
        // comparator the held seeding uses — so this one sorts before emitting rather than being the single place a
        // snapshot's entry order could differ between two runs of the same input.
        if (stranded is not null) {
            stranded.Sort(comparison: CompareStrandedOrder);

            foreach (var (commandId, entry) in stranded) {
                working.Add(item: entry with {
                    Dispatch = true,
                    Phase = CommandPhase.Completed,
                    Value = CommandValue.Inactive(kind: entry.Value.Kind),
                });
                DropHeld(
                    commandId: commandId,
                    slot: slot
                );
            }
        }

        if (strandedContributions is not null) {
            strandedContributions.Sort(comparison: CompareStrandedOrder);

            foreach (var (commandId, entry) in strandedContributions) {
                working.Add(item: entry with {
                    Dispatch = true,
                    Phase = CommandPhase.Completed,
                    Value = CommandValue.Inactive(kind: entry.Value.Kind),
                });
                RemoveContribution(
                    commandId: commandId,
                    control: control,
                    slot: slot
                );
            }
        }
    }
    private void RemoveContribution(int slot, ushort commandId, HeldControlId control) {
        if (
            !m_heldBySlot.TryGetValue(
            key: slot,
            value: out var held
        ) ||
            !held.TryGetValue(
            key: commandId,
            value: out var state
        ) ||
            (state.Contributions is not { } contributions)
        ) {
            return;
        }

        for (var index = (contributions.Count - 1); (index >= 0); index--) {
            if (contributions[index].Control == control) {
                contributions.RemoveAt(index: index);
            }
        }

        if (state.IsEmpty) {
            DropHeld(
                commandId: commandId,
                slot: slot
            );
        }
    }
    // Drops one physical control from a command's held state, if present. Does NOT remove the state itself: the
    // last-up release path (ApplySignal's DropHeld branch) owns dropping a command once no control remains.
    private void RemoveControl(int slot, ushort commandId, HeldControlId control) {
        if (
            m_heldBySlot.TryGetValue(
            key: slot,
            value: out var held
        ) &&
            held.TryGetValue(
            key: commandId,
            value: out var state
        )
        ) {
            _ = state.Controls?.Remove(item: control);
        }
    }
    private ResolvedBinding[] ResolveBindings(IReadOnlyList<CommandBinding>? bindings) {
        if (
            (bindings is null) ||
            (bindings.Count == 0)
        ) {
            return [];
        }

        if (m_resolvedBindingLists.TryGetValue(
            key: bindings,
            value: out var resolved
        )) {
            return resolved;
        }

        var validCount = 0;
        var candidates = new ResolvedBinding[bindings.Count];

        for (var index = 0; (index < bindings.Count); index++) {
            var binding = bindings[index];

            if (!TryResolveCommandId(
                name: binding.Command,
                id: out var commandId
            )) {
                continue;
            }

            candidates[validCount++] = new ResolvedBinding(
                Binding: binding,
                CommandId: commandId,
                ToggleSource: ((binding.Mode == BindingEntryMode.Toggle)
                ? BindingSourceIdentity.ForCommand(command: binding.Command)
                : null)
            );
        }

        if (validCount != candidates.Length) {
            Array.Resize(
                array: ref candidates,
                newSize: validCount
            );
        }

        m_resolvedBindingLists.Add(
            key: bindings,
            value: candidates
        );

        return candidates;
    }
    // The dispatched value for one bound signal: an ordinary binding's Value is an UNCONDITIONAL override (or null,
    // meaning pass the signal through verbatim) — untouched here. A channel destination's ChannelScale is instead
    // applied by the signal's OWN value kind, never guessed from nullability: a digital source (a key) has no
    // magnitude, so the declared scale IS the whole contribution; an analog (Axis1D) source's contribution is its
    // own sample TIMES the scale, via FixedQ4816's multiply (nearest, ties to even) — never the scale replacing the
    // sample. Any other live kind bound to a channel (no current binding does this) falls back to the constant.
    private static CommandValue ResolveValue(in CommandBinding binding, in InputSignal signal) {
        if (binding.ChannelScale is not { } channelScale) {
            return (binding.Value ?? signal.Value);
        }

        if (binding.Component is { } component) {
            // An axis-component source decomposes an Axis2D sample into one scalar component BEFORE the exact same
            // scale multiply the Axis1D branch below applies — the live stick magnitude feeds the channel instead
            // of falling back to the constant scale (the gap a bare Axis2D source hits).
            if (signal.Value.Kind != CommandValueKind.Axis2D) {
                return CommandValue.Axis(value: channelScale);
            }

            var axis2 = signal.Value.AsAxis2D;
            var componentSample = ((component == AxisComponent.X)
                ? axis2.X
                : axis2.Y
            );

            return ScaleThroughChannel(
                channelScale: channelScale,
                sample: componentSample
            );
        }

        if (signal.Value.Kind != CommandValueKind.Axis1D) {
            return CommandValue.Axis(value: channelScale);
        }

        return ScaleThroughChannel(
            channelScale: channelScale,
            sample: signal.Value.AsAxis1D
        );
    }
    // The one axis-through-channel conversion the component and Axis1D branches share: sample times the declared
    // scale in FixedQ4816 (nearest, ties to even), never a float multiply, so a channel's contribution rounds
    // identically regardless of which branch produced the sample.
    private static CommandValue ScaleThroughChannel(double sample, double channelScale) {
        var s = FixedQ4816.FromDouble(value: sample);
        var k = FixedQ4816.FromDouble(value: channelScale);

        return CommandValue.Axis(value: ((float)((double)(s * k))));
    }
    private void SetContribution(int slot, ushort commandId, HeldControlId control, CommandEntry entry) {
        var state = HeldFor(
            commandId: commandId,
            slot: slot
        );
        var contributions = (state.Contributions ??= new List<HeldContribution>(capacity: 2));

        for (var index = 0; (index < contributions.Count); index++) {
            if (contributions[index].Control == control) {
                contributions[index] = new HeldContribution(
                    Control: control,
                    Entry: entry
                );

                return;
            }
        }

        contributions.Add(item: new HeldContribution(
            Control: control,
            Entry: entry
        ));
    }
    private bool TryGetHeldDevice(int slot, ushort commandId, out InputDeviceId device) {
        if (
            m_heldBySlot.TryGetValue(
            key: slot,
            value: out var held
        ) &&
            held.TryGetValue(
            key: commandId,
            value: out var state
        ) &&
            (state.Controls is { Count: > 0 } controls)
        ) {
            device = controls[0].Device;

            return true;
        }

        device = default;

        return false;
    }
    private bool TryResolveCommandId(string name, out ushort id) {
        if (m_resolvedCommandIds.TryGetValue(
            key: name,
            value: out id
        )) {
            return true;
        }

        if (!m_registry.TryGetId(
            id: out id,
            name: name
        )) {
            return false;
        }

        m_resolvedCommandIds[name] = id;

        return true;
    }
    private static List<CommandEntry> WorkingFor(Dictionary<int, List<CommandEntry>> workingBySlot, int slot) {
        if (!workingBySlot.TryGetValue(
            key: slot,
            value: out var working
        )) {
            working = [];
            workingBySlot[slot] = working;
        }

        return working;
    }

    /// <summary>Queues an authored interactive-presentation activation into a seat's ordinary deterministic lane.
    /// The activation is compiler-minted and opaque to the presenter; the resulting entry is deliberately
    /// unstamped so snapshot construction resolves the seat's current principal exactly like physical input.</summary>
    /// <param name="slot">The logical seat whose presentation was activated.</param>
    /// <param name="activation">The compiled binding activation.</param>
    /// <returns><see langword="false"/> when the command is not registered in this router.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="activation"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="slot"/> is negative.</exception>
    public bool Activate(int slot, BindingActivation activation) {
        ArgumentNullException.ThrowIfNull(activation);
        // A negative slot is not a lane: it would mint one on the working side and ask the host to name a principal
        // for a seat that cannot exist. Every other slot-taking member on this type refuses one at the door.
        ArgumentOutOfRangeException.ThrowIfNegative(slot);

        if (!m_registry.TryGetId(
            name: activation.Command,
            id: out var commandId
        )) {
            return false;
        }

        // A text-bearing activation (a wheel sector with an authored payload) submits its line exactly as a bound
        // press does: "<command> <text>", dispatched by the registry under the seat's principal.
        Enqueue(injection: new CommandInjection(
            CommandId: commandId,
            Value: activation.Value,
            Phase: activation.Phase,
            Origin: CommandOrigin.Binding,
            Principal: default,
            Slot: slot,
            Text: ((activation.Text is { Length: > 0 } text)
                ? $"{activation.Command} {text}"
                : null)
        ));

        return true;
    }
    /// <summary>Appends a captured input signal. Thread-safe — backends call this from device I/O threads and the window pump.</summary>
    /// <param name="signal">The timestamped input signal to capture.</param>
    /// <exception cref="ArgumentException"><paramref name="signal"/> carries no <see cref="InputSignal.Source"/>.</exception>
    public void Capture(in InputSignal signal) => CaptureSignal(
        focusExemptOnly: false,
        signal: in signal
    );
    /// <summary>Captures a signal from a device whose ordinary terminal focus is released. Only bindings whose
    /// destination declares <see cref="CommandInputScope.FocusExempt"/> may dispatch and only the host-owned
    /// <see cref="IAlwaysActiveInputBindings"/> plane answers, so a typed key cannot press a gameplay page's
    /// binding. A RELEASE still reaches the page resolver — its answer discarded — because the resolver holds the
    /// chord tracker, the press latches, and the armed command rows, and a release those never observe strands a
    /// flipped page or an armed row for as long as the seat console stays open.</summary>
    /// <param name="signal">The raw signal to capture.</param>
    /// <exception cref="ArgumentException"><paramref name="signal"/> carries no <see cref="InputSignal.Source"/>.</exception>
    public void CaptureFocusExempt(in InputSignal signal) => CaptureSignal(
        focusExemptOnly: true,
        signal: in signal
    );
    /// <summary>Clears one slot's held commands and <see cref="BindingEntryMode.Toggle"/> latches — the input-layer
    /// half of a deliberate, full "stop": queues deterministic cancellation for each carried hold contribution (so
    /// every held channel source actually runs its release, exactly as a physical release would — see
    /// <see cref="CommandRegistry.ApplySnapshot"/>'s <c>Dispatch</c> gate), and drops every toggle latch the slot
    /// carries so a later press starts fresh rather than reading as "already on".</summary>
    /// <remarks>
    /// Distinct from <see cref="ReleaseHeld()"/> (every slot, wired to OS focus loss) and the private per-device
    /// overload (a disconnect): this is the per-slot seam a caller reaches for on a named, deliberate stop, never
    /// wired implicitly. It does not touch <see cref="IInputBindings"/> chord/modifier state
    /// (<see cref="PagedInputBindings.Reset(int)"/> is that seam) and does not discard already-captured signals
    /// for the slot.
    /// <para>Because it leaves the resolver alone, a <see cref="BindingActivatorMode.Tapped"/> completion's already
    /// SCHEDULED release (see <see cref="IChordEdgeSource.DrainScheduledEdges"/>) is still in flight and delivers
    /// itself on the next tick; this call does not also synthesize a cancellation for it, so one tap still produces
    /// exactly one release. <see cref="SetActiveMaps"/> and <see cref="ReleaseHeld()"/> DO synthesize it, because
    /// each of them resets the resolver and destroys the scheduled edge first.</para>
    /// </remarks>
    /// <param name="slot">The logical player slot to clear.</param>
    /// <returns>The number of toggle latches this slot carried in the on state and cleared; 0 when the slot had
    /// none latched. A caller with a user-facing "totality" echo (a panic verb) should fold this into its own
    /// tally.</returns>
    public int ClearSlotHeld(int slot) {
        var latchKeysToRemove = new List<(int Slot, ushort CommandId)>();
        var clearedLatches = 0;

        foreach (var (key, isOn) in m_toggleLatches) {
            if (key.Slot != slot) {
                continue;
            }

            latchKeysToRemove.Add(item: key);

            if (isOn) {
                clearedLatches++;
            }
        }

        foreach (var key in latchKeysToRemove) {
            _ = m_toggleLatches.Remove(key: key);
        }

        var cancellations = new List<CommandInjection>();

        if (m_heldBySlot.TryGetValue(
            key: slot,
            value: out var held
        )) {
            foreach (var state in held.Values) {
                AppendCancellations(
                    cancellations: cancellations,
                    dischargesScheduledEdges: false,
                    slot: slot,
                    state: state
                );
                RecycleHeldState(state: state);
            }

            _ = m_heldBySlot.Remove(key: slot);
        }

        QueueCancellations(
            cancellations: cancellations,
            discardCapturedSignals: false
        );

        return clearedLatches;
    }
    /// <summary>Detaches this router from the collaborators it subscribed to at construction — the binding
    /// resolver's <see cref="IInputBindingsReloadSource.Reloading"/> edge and the slot resolver's
    /// <see cref="IInputSlotResolver.DeviceSlotChanging"/> edge — and drops every queue and held table it carries.
    /// A host that REPLACES a router must dispose the old one: those two edges are owned by objects that outlive it,
    /// so an undisposed predecessor stays reachable and keeps mutating its own held tables on every profile reload
    /// and every device disconnect. A router owned for the process lifetime needs no explicit call (a container
    /// that resolved it disposes it with the host).</summary>
    /// <remarks>Idempotent; safe to call on a router already detached. Not thread-safe against a concurrent
    /// <see cref="Capture(in InputSignal)"/> — dispose on the pump thread, after the producers have stopped.</remarks>
    public void Dispose() {
        if (m_disposed) {
            return;
        }

        m_disposed = true;

        if (m_bindings is IInputBindingsReloadSource reloadSource) {
            reloadSource.Reloading -= OnBindingsReloading;
        }

        if (m_inputSlotResolver is not null) {
            m_inputSlotResolver.DeviceSlotChanging -= ReleaseHeld;
        }

        lock (m_captureGate) {
            m_capturedInjections.Clear();
            m_capturedSignals.Clear();
            m_pendingInjections.Clear();
        }

        m_activeControls.Clear();
        m_freeHeldStates.Clear();
        m_heldBySlot.Clear();
        m_lastInputTickBySlot.Clear();
        m_modalityBySlot.Clear();
        m_pressedControls.Clear();
        m_resolvedBindingLists.Clear();
        m_toggleLatches.Clear();
        m_workingBySlot.Clear();
    }
    /// <summary>Whether a logical command is currently carried held for a slot — a bound digital pressed and not yet
    /// released, or an analog channel with an active carried sample. The read seam an input-state UI (a binding bar's
    /// pressed chips) lights from, so held truth has ONE owner instead of a parallel tracker per consumer.</summary>
    /// <param name="slot">The logical player slot.</param>
    /// <param name="command">The command name to test.</param>
    /// <returns><see langword="true"/> when the slot carries the command held.</returns>
    /// <remarks>Pump-thread only: the held tables mutate inside <see cref="SnapshotForTick"/> and the focus-loss
    /// release on the same single thread that produces frames, so this read is safe there and nowhere else.</remarks>
    public bool IsCommandHeld(int slot, string command) {
        return (
            m_registry.TryGetId(
            id: out var commandId,
            name: command
        ) &&
            m_heldBySlot.TryGetValue(
            key: slot,
            value: out var held
        ) &&
            held.TryGetValue(
            key: commandId,
            value: out var state
        ) &&
            state.IsHeld
        );
    }
    /// <summary>Determines whether a registered command map is active for a logical slot.</summary>
    /// <param name="slot">The logical player slot.</param>
    /// <param name="map">The registered map name.</param>
    /// <returns><see langword="true"/> when the map is active. Unknown maps return <see langword="false"/>.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="slot"/> is negative.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="map"/> is <see langword="null"/>.</exception>
    public bool IsMapActive(int slot, string map) {
        ArgumentOutOfRangeException.ThrowIfNegative(slot);
        ArgumentNullException.ThrowIfNull(map);

        return m_registry.IsMapActive(
            modality: ModalityFor(slot: slot),
            map: map
        );
    }
    /// <summary>Queues deterministic cancellations for every carried logical hold and per-control channel
    /// contribution, then clears every carried digital and analog value, AND releases every slot's chord/modifier
    /// state (<see cref="IInputBindings.ResetAll"/>).
    /// Hosts call this on focus loss because platforms do not guarantee release events afterward — a swallowed
    /// modifier release is the same hazard as a swallowed command release, just invisible to <c>m_heldBySlot</c>
    /// because a bare page modifier need not be bound to any command.</summary>
    public void ReleaseHeld() {
        var cancellations = new List<CommandInjection>();

        foreach (var (slot, held) in m_heldBySlot) {
            foreach (var state in held.Values) {
                AppendCancellations(
                    cancellations: cancellations,
                    dischargesScheduledEdges: true,
                    slot: slot,
                    state: state
                );
                RecycleHeldState(state: state);
            }
        }

        m_activeControls.Clear();
        m_heldBySlot.Clear();
        m_pressedControls.Clear();
        m_toggleLatches.Clear();
        m_bindings.ResetAll();

        QueueCancellations(
            cancellations: cancellations,
            discardCapturedSignals: true
        );
    }
    /// <summary>Releases held commands owned by one physical device without disturbing other seats or devices.</summary>
    /// <param name="device">The device whose held state is being withdrawn.</param>
    public void ReleaseHeld(InputDeviceId device) => ReleaseHeld(
        device: device,
        preservePressedControls: false
    );
    /// <summary>Atomically replaces one logical slot's active command maps. <see cref="CommandMaps.Global"/> remains
    /// active implicitly. Commands held in maps that leave the set receive deterministic cancellation on the next
    /// snapshot; other slots and commands in retained maps are unchanged.</summary>
    /// <param name="slot">The logical player slot whose modality changes.</param>
    /// <param name="maps">The complete non-global active map set. Names are matched case-insensitively and must be
    /// registered by at least one command.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="slot"/> is negative.</exception>
    /// <exception cref="ArgumentException"><paramref name="maps"/> contains an unregistered or null map.</exception>
    /// <remarks>Pump-thread only. The replacement affects snapshots built after this call; an already-built snapshot
    /// retains the modality decision made while it was constructed.</remarks>
    public void SetActiveMaps(int slot, ReadOnlySpan<string> maps) {
        ArgumentOutOfRangeException.ThrowIfNegative(slot);

        var previous = ModalityFor(slot: slot);
        var next = m_registry.CreateModality(activeMaps: maps);

        if (previous.ActiveMaps.AsSpan().SequenceEqual(other: next.ActiveMaps)) {
            return;
        }

        if (ReferenceEquals(
            objA: next,
            objB: m_registry.DefaultModality
        )) {
            _ = m_modalityBySlot.Remove(key: slot);
        } else {
            m_modalityBySlot[slot] = next;
        }

        List<CommandInjection>? cancellations = null;

        _ = m_heldBySlot.TryGetValue(
            key: slot,
            value: out var held
        );

        // ONE decision per carried command, so a destination carrying two obligations is never cancelled twice for
        // one transition. A command whose map goes inactive is cancelled and dropped whole. A command whose map
        // SURVIVES still owes its pending momentary release (a Tapped activator's completion — see ApplyChordEdge):
        // that is a ONE-TICK obligation, not a modality-scoped hold, and the edge that would deliver it lives in the
        // resolver's scheduled queue, which the Reset below deletes — leaving the handler that consumed the tap's
        // press waiting forever for a completion nothing can now produce. Both run BEFORE that Reset.
        if (held is not null) {
            List<ushort>? commandsToDrop = null;

            foreach (var (commandId, state) in held) {
                if (!next.ActiveCommands[commandId]) {
                    cancellations ??= [];

                    AppendCancellations(
                        cancellations: cancellations,
                        dischargesScheduledEdges: true,
                        slot: slot,
                        state: state
                    );
                    (commandsToDrop ??= []).Add(item: commandId);

                    continue;
                }

                if (!state.HasPendingMomentaryRelease) {
                    continue;
                }

                (cancellations ??= []).Add(item: CancellationFor(
                    entry: state.MomentaryEntry,
                    slot: slot
                ));
                state.HasPendingMomentaryRelease = false;
                state.MomentaryEntry = default;

                if (state.IsEmpty) {
                    (commandsToDrop ??= []).Add(item: commandId);
                }
            }

            if (commandsToDrop is not null) {
                foreach (var commandId in commandsToDrop) {
                    DropHeld(
                        commandId: commandId,
                        slot: slot
                    );
                }
            }
        }

        // Map transitions invalidate page/chord release ownership for this slot. Edge-reported controls remain
        // physically held at the input source and reassert through the new modality in press order next frame.
        m_bindings.Reset(slot: slot);

        List<(int Slot, ushort CommandId)>? latchesToDrop = null;

        foreach (var (key, _) in m_toggleLatches) {
            if (
                (key.Slot == slot) &&
                !next.ActiveCommands[key.CommandId]
            ) {
                (latchesToDrop ??= []).Add(item: key);
            }
        }

        if (latchesToDrop is not null) {
            foreach (var key in latchesToDrop) {
                _ = m_toggleLatches.Remove(key: key);
            }
        }

        if (cancellations is not null) {
            QueueCancellations(
                cancellations: cancellations,
                discardCapturedSignals: false
            );
        }
    }
    /// <summary>Produces the snapshot for <paramref name="tick"/> from captured input.</summary>
    /// <param name="tick">The fixed-step tick to produce input for.</param>
    /// <param name="windowEndTick">
    /// The engine-tick time at which this tick's window closes. Captured input whose
    /// <see cref="InputSignal.CaptureTick"/> precedes it is consumed; later-stamped input waits for a future tick.
    /// </param>
    public CommandSnapshot SnapshotForTick(ulong tick, ulong windowEndTick) {
        // Take this tick's due signals (CaptureTick before the window close), leaving later-stamped signals for
        // a future tick. Total order: capture time, then the unique capture sequence — deterministic for a given
        // captured set, so the recorded snapshot reproduces the run exactly.
        DrainDue(windowEndTick: windowEndTick);

        // Working per-slot ordered state for this tick. Seeded from carried held state (held digitals re-assert as
        // Active), then every due signal is appended in order; repeated commands stay repeated.
        foreach (var working in m_workingBySlot.Values) {
            working.Clear();
        }

        foreach (var (slot, held) in m_heldBySlot) {
            if (held.Count == 0) {
                continue;
            }

            var working = WorkingFor(
                slot: slot,
                workingBySlot: m_workingBySlot
            );

            foreach (var state in held.Values) {
                if (state.HasEntry) {
                    // The held entry is already phase Active — a held digital re-asserts each tick.
                    working.Add(item: state.Entry);
                }

                if (state.Contributions is { } contributions) {
                    foreach (var contribution in contributions) {
                        working.Add(item: contribution.Entry);
                    }
                }
            }

            working.Sort(comparison: static (left, right) => {
                var byCommand = left.CommandId.CompareTo(value: right.CommandId);

                return ((byCommand != 0)
                    ? byCommand
                    : StringComparer.Ordinal.Compare(
                        x: left.Source,
                        y: right.Source
                    )
                );
            });
        }

        // Router-synthesized edges owed from an EARLIER tick fold next: a transient impulse's inactive twin and every
        // deterministic cancellation. Draining them HERE — after the held seeding, before this tick's own due
        // signals — is what makes their delay exactly one tick: anything synthesized during the fold below is
        // visible only to the NEXT call, by ordering alone, with no clock or tick arithmetic (see
        // m_pendingInjections, and the identical construction behind DrainScheduledEdges).
        m_duePendingInjections.Clear();

        lock (m_captureGate) {
            if (m_pendingInjections.Count != 0) {
                m_duePendingInjections.AddRange(collection: m_pendingInjections);
                m_pendingInjections.Clear();
            }
        }

        for (var pendingIndex = 0; (pendingIndex < m_duePendingInjections.Count); pendingIndex++) {
            ApplyInjection(
                injection: m_duePendingInjections[pendingIndex],
                workingBySlot: m_workingBySlot
            );
        }

        // Scheduled edges (a Tapped row activator's deferred release — see IChordEdgeSource.DrainScheduledEdges)
        // fold in BEFORE this tick's own due signals are processed, so anything scheduled DURING that processing
        // below cannot be seen by this call — only by the NEXT tick's. That ordering alone is what makes the
        // release land exactly one tick after its press with no clock or tick arithmetic involved.
        if (m_chordEdges is not null) {
            var scheduledEdges = m_chordEdges.DrainScheduledEdges();

            for (var scheduledIndex = 0; (scheduledIndex < scheduledEdges.Count); scheduledIndex++) {
                var (slot, edge) = scheduledEdges[scheduledIndex];

                ApplyChordEdge(
                    device: default,
                    edge: in edge,
                    slot: slot,
                    workingBySlot: m_workingBySlot
                );
            }
        }

        var signalIndex = 0;
        var injectionIndex = 0;
        var dueSignals = CollectionsMarshal.AsSpan(list: m_dueSignals);
        var dueInjections = CollectionsMarshal.AsSpan(list: m_dueInjections);

        while (
            (signalIndex < dueSignals.Length) ||
            (injectionIndex < dueInjections.Length)
        ) {
            if (
                (injectionIndex >= dueInjections.Length) ||
                (
                    (signalIndex < dueSignals.Length) &&
                    (CompareCaptureOrder(
                leftTick: dueSignals[signalIndex].CaptureTick,
                leftSequence: dueSignals[signalIndex].Sequence,
                rightTick: dueInjections[injectionIndex].CaptureTick,
                rightSequence: dueInjections[injectionIndex].Sequence
            ) < 0)
                )
            ) {
                ref readonly var captured = ref dueSignals[signalIndex++];

                ApplySignal(
                    workingBySlot: m_workingBySlot,
                    signal: captured.Signal,
                    tick: tick,
                    focusExemptOnly: captured.FocusExemptOnly
                );
            } else {
                ref readonly var captured = ref dueInjections[injectionIndex++];

                ApplyInjection(
                    workingBySlot: m_workingBySlot,
                    injection: captured.Injection
                );
            }
        }

        return Build(tick: tick);
    }
    /// <summary>Releases a device's gameplay holds for a focus handoff while preserving its physical first-down
    /// latches until the corresponding releases arrive. This prevents an OS repeat of the console opener from
    /// becoming a second toggle after focus moves.</summary>
    /// <param name="device">The device being suppressed from ordinary input.</param>
    public void SuppressHeld(InputDeviceId device) => ReleaseHeld(
        device: device,
        preservePressedControls: true
    );

    /// <summary>The analog magnitude below which a sample is a device at rest, not a hand on it — stick centring
    /// slop and gyro noise sit well under this; the lightest deliberate deflection sits well over it.</summary>
    public const float ActivityRestBand = 0.15f;

    // A bound row's authored text payload rides the PRESS as a submitted line — "<command> <text>", dispatched by
    // the registry exactly as a typed line under the pressing seat's principal — so a wire-args verb is bindable
    // with authored arguments. A release, or a row with no payload, carries no line.
    private static string? TextLine(string command, string? text, CommandPhase phase, bool dispatch) =>
        (((text is { Length: > 0 }) && dispatch && (phase == CommandPhase.Started))
            ? $"{command} {text}"
            : null);
    private static bool IsActivity(in InputSignal signal) {
        if (signal.Posture) {
            return false;
        }

        if (signal.Phase is CommandPhase.Started or CommandPhase.Completed or CommandPhase.Canceled) {
            return true;
        }

        return (signal.Value.Kind switch {
            CommandValueKind.Digital => signal.Value.AsDigital,
            CommandValueKind.Axis1D => (MathF.Abs(x: signal.Value.AsAxis1D) >= ActivityRestBand),
            CommandValueKind.Axis2D => (signal.Value.AsAxis2D.LengthSquared() >= (ActivityRestBand * ActivityRestBand)),
            CommandValueKind.Axis3D => (signal.Value.AsAxis3D.LengthSquared() >= (ActivityRestBand * ActivityRestBand)),
            _ => false,
        });
    }

    /// <summary>Gets the simulation tick at which a seat most recently produced a physical press/release or a live
    /// digital/analog sample outside the rest band. Authored-lane and posture samples do not count.</summary>
    /// <param name="slot">The logical player slot.</param>
    /// <param name="tick">The last input tick, meaningful only when this returns <see langword="true"/>.</param>
    /// <returns><see langword="true"/> when the seat has produced an input signal.</returns>
    /// <remarks>Pump-thread only, on the same terms as <see cref="IsCommandHeld(int, string)"/>.</remarks>
    public bool TryGetLastInputTick(int slot, out ulong tick) => m_lastInputTickBySlot.TryGetValue(
        key: slot,
        value: out tick
    );
}
