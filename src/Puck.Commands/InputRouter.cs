using System.Collections.Immutable;
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
/// handlers from the snapshot. Pre-resolved commands (a console/STDIN line, a peer, an AI) enter through a
/// <see cref="CommandInjectionSink"/> — one per producer, each bound to its own principal at construction — and fold
/// into the same lanes as captured signals, in one deterministic capture order, so command-line input is recorded and
/// replayed by the same machinery with no separate path.
/// <para>The mixer is also the principal door: every entry leaves this type carrying a <see cref="CommandPrincipal"/>.
/// An injected entry keeps the one its sink was constructed with; every captured entry is stamped from
/// <see cref="ICommandPrincipalResolver.PrincipalOf"/> for its lane. The lane's slot number is never turned into a
/// seat principal here — a claimed slot may be answering to a peer or a guest module, so only the host's roster can
/// say who it is.</para>
/// </remarks>
public sealed class InputRouter {
    private readonly IInputBindings m_bindings;
    private readonly IChordEdgeSource? m_chordEdges;
    private readonly Lock m_captureGate = new();
    private readonly List<Captured> m_captured = [];
    private readonly CommandInjectionSink m_consoleTextSink;
    // Simulation-thread scratch retained across ticks. Idle snapshots then allocate nothing; active snapshots allocate
    // only their immutable output. Capture remains independently protected by m_captureGate.
    private readonly List<Captured> m_due = [];
    private readonly IInputClock? m_clock;
    private readonly HashSet<HeldControl> m_heldControls = [];
    private readonly Dictionary<int, Dictionary<ushort, CommandEntry>> m_heldBySlot = [];
    // A BindingEntryMode.Toggle latch, keyed by (slot, commandId) — the destination's flip state, independent of
    // which physical control (or device) toggled it. Lives here, not in Puck.World.Server: the sim reads a plain
    // held channel either way (see BindingEntryMode's remarks).
    private readonly Dictionary<(int Slot, ushort CommandId), bool> m_toggleLatches = [];
    private readonly IInputSlotResolver? m_inputSlotResolver;
    private readonly ICommandPrincipalResolver m_principalResolver;
    private readonly CommandRegistry m_registry;
    private readonly Func<InputDeviceId, int> m_slotResolver;
    private readonly Dictionary<int, List<CommandEntry>> m_workingBySlot = [];
    private ulong m_sequence;

    // One captured item carries EITHER a raw signal (still needs a binding lookup) or a pre-resolved injection
    // (a console/peer command, already bound). Both share the capture tick + sequence, so they sort into one
    // deterministic order regardless of which kind they are.
    private readonly record struct Captured(ulong Sequence, ulong CaptureTick, InputSignal? Signal, CommandInjection? Injection);
    private readonly record struct HeldCommand(int Slot, ushort CommandId);
    private readonly record struct HeldControl(int Slot, InputDeviceId Device, string Source, ushort CommandId);

    /// <summary>Initializes a new instance of the <see cref="InputRouter"/> class.</summary>
    /// <param name="registry">The registry that interns command ids and gates by map.</param>
    /// <param name="bindings">The slot-aware binding resolver (per-player mappings layered over a default).</param>
    /// <param name="principalResolver">Answers who is acting through a slot. Required: the mixer stamps every captured
    /// entry from it and must never synthesize an identity of its own.</param>
    /// <param name="slotResolver">Maps a device to a logical player slot; defaults to a single local slot (<c>0</c>).</param>
    /// <param name="clock">The shared capture clock used to stamp an injected command that arrives without an explicit capture tick; optional.</param>
    /// <exception cref="ArgumentNullException"><paramref name="registry"/>, <paramref name="bindings"/>, or
    /// <paramref name="principalResolver"/> is <see langword="null"/>.</exception>
    public InputRouter(
        CommandRegistry registry,
        IInputBindings bindings,
        ICommandPrincipalResolver principalResolver,
        Func<InputDeviceId, int>? slotResolver = null,
        IInputClock? clock = null
    ) {
        ArgumentNullException.ThrowIfNull(bindings);
        ArgumentNullException.ThrowIfNull(principalResolver);
        ArgumentNullException.ThrowIfNull(registry);

        m_bindings = bindings;
        m_chordEdges = (bindings as IChordEdgeSource);
        m_clock = clock;
        m_principalResolver = principalResolver;
        m_registry = registry;
        m_slotResolver = (slotResolver ?? (static _ => 0));
        // The console text door, built once here so nothing outside can mint one bound to a principal of its choosing.
        // Slot 0 is the local lane a console impulse rides; the Console principal is what makes it NOT that seat.
        m_consoleTextSink = new CommandInjectionSink(router: this, principal: CommandPrincipal.Console, slot: 0);
    }

    /// <summary>Initializes an input router whose device-to-slot resolver supports side-effect-free probing followed by
    /// an explicit commit after a binding is accepted.</summary>
    /// <param name="registry">The registry that interns command ids and gates by map.</param>
    /// <param name="bindings">The slot-aware binding resolver.</param>
    /// <param name="principalResolver">Answers who is acting through a slot.</param>
    /// <param name="slotResolver">The transactional device-to-slot resolver.</param>
    /// <param name="clock">The shared capture clock; optional.</param>
    public InputRouter(
        CommandRegistry registry,
        IInputBindings bindings,
        ICommandPrincipalResolver principalResolver,
        IInputSlotResolver slotResolver,
        IInputClock? clock = null
    ) : this(
        registry: registry,
        bindings: bindings,
        principalResolver: principalResolver,
        slotResolver: (slotResolver ?? throw new ArgumentNullException(paramName: nameof(slotResolver))).ResolveSlot,
        clock: clock
    ) {
        m_inputSlotResolver = slotResolver;
        slotResolver.DeviceSlotChanging += ReleaseHeld;
    }

    /// <summary>The console/STDIN text door's injection sink — the one a <see cref="CommandRegistry"/> is wired to
    /// through <see cref="CommandRegistry.RouteSimulationTo"/>. Bound to <see cref="CommandPrincipal.Console"/> at
    /// construction, so a submitted line acts as the console and cannot be made to act as anything else.</summary>
    public CommandInjectionSink ConsoleTextSink => m_consoleTextSink;

    /// <summary>Appends a captured input signal. Thread-safe — backends call this from device I/O threads and the window pump.</summary>
    /// <param name="signal">The timestamped input signal to capture.</param>
    public void Capture(in InputSignal signal) {
        lock (m_captureGate) {
            m_captured.Add(item: new Captured(Sequence: m_sequence++, CaptureTick: signal.CaptureTick, Signal: signal, Injection: null));
        }
    }

    /// <summary>Queues an authored interactive-presentation activation into a seat's ordinary deterministic lane.
    /// The activation is compiler-minted and opaque to the presenter; the resulting entry is deliberately
    /// unstamped so snapshot construction resolves the seat's current principal exactly like physical input.</summary>
    /// <param name="slot">The logical seat whose presentation was activated.</param>
    /// <param name="activation">The compiled binding activation.</param>
    /// <returns><see langword="false"/> when the command is not registered in this router.</returns>
    public bool Activate(int slot, BindingActivation activation) {
        ArgumentNullException.ThrowIfNull(activation);

        if (!m_registry.TryGetId(name: activation.Command, id: out var commandId)) {
            return false;
        }

        Enqueue(injection: new CommandInjection(
            CommandId: commandId,
            Value: activation.Value,
            Phase: activation.Phase,
            Principal: default,
            Slot: slot,
            Source: BindingActivation.RadialSource
        ));

        return true;
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
        return (m_registry.TryGetId(
            name: command,
            id: out var commandId
        )
            && m_heldBySlot.TryGetValue(
                key: slot,
                value: out var held
            )
            && held.ContainsKey(key: commandId));
    }

    /// <summary>Queues one deterministic cancellation per carried logical command, then clears every carried digital
    /// and analog value, AND releases every slot's chord/modifier state (<see cref="IInputBindings.ResetAll"/>).
    /// Hosts call this on focus loss because platforms do not guarantee release events afterward — a swallowed
    /// modifier release is the same hazard as a swallowed command release, just invisible to <c>m_heldBySlot</c>
    /// because a bare page modifier need not be bound to any command.</summary>
    public void ReleaseHeld() {
        var cancellations = new List<CommandInjection>();

        foreach (var (slot, held) in m_heldBySlot) {
            foreach (var entry in held.Values) {
                // Unstamped on purpose: a synthesized release belongs to the SLOT that held the input, so the
                // snapshot build resolves its principal like any other captured entry rather than freezing whoever
                // was acting when the hold began.
                cancellations.Add(item: new CommandInjection(
                    CommandId: entry.CommandId,
                    Value: CommandValue.Inactive(kind: entry.Value.Kind),
                    Phase: CommandPhase.Canceled,
                    Principal: default,
                    Slot: slot
                ));
            }
        }

        m_heldControls.Clear();
        m_heldBySlot.Clear();
        m_toggleLatches.Clear();
        m_bindings.ResetAll();

        QueueCancellations(cancellations: cancellations, discardCapturedSignals: true);
    }

    /// <summary>Clears one slot's held commands and <see cref="BindingEntryMode.Toggle"/> latches — the input-layer
    /// half of a deliberate, full "stop": queues one deterministic cancellation per carried command (so a held
    /// channel's handler actually runs its release, exactly as a physical release would — see
    /// <see cref="CommandRegistry.ApplySnapshot"/>'s <c>Dispatch</c> gate), and drops every toggle latch the slot
    /// carries so a later press starts fresh rather than reading as "already on".</summary>
    /// <remarks>
    /// Distinct from <see cref="ReleaseHeld()"/> (every slot, wired to OS focus loss) and the private per-device
    /// overload (a disconnect): this is the per-slot seam a caller reaches for on a named, deliberate stop, never
    /// wired implicitly. It does not touch <see cref="IInputBindings"/> chord/modifier state
    /// (<see cref="PagedInputBindings.Reset(int)"/> is that seam) and does not discard already-captured signals
    /// for the slot.
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

        _ = m_heldControls.RemoveWhere(match: control => (control.Slot == slot));

        var cancellations = new List<CommandInjection>();

        if (m_heldBySlot.TryGetValue(
            key: slot,
            value: out var held
        )) {
            foreach (var entry in held.Values) {
                // Unstamped: same reasoning as ReleaseHeld's own cancellations — the slot owns the identity.
                cancellations.Add(item: new CommandInjection(
                    CommandId: entry.CommandId,
                    Value: CommandValue.Inactive(kind: entry.Value.Kind),
                    Phase: CommandPhase.Canceled,
                    Principal: default,
                    Slot: slot
                ));
            }

            _ = m_heldBySlot.Remove(key: slot);
        }

        QueueCancellations(cancellations: cancellations, discardCapturedSignals: false);

        return clearedLatches;
    }

    private void ReleaseHeld(InputDeviceId device) {
        var affected = new HashSet<HeldCommand>();

        foreach (var control in m_heldControls) {
            if (control.Device == device) {
                _ = affected.Add(item: new HeldCommand(Slot: control.Slot, CommandId: control.CommandId));
            }
        }

        foreach (var (slot, held) in m_heldBySlot) {
            foreach (var entry in held.Values) {
                if (entry.Device == device) {
                    _ = affected.Add(item: new HeldCommand(Slot: slot, CommandId: entry.CommandId));
                }
            }
        }

        if (affected.Count == 0) {
            return;
        }

        _ = m_heldControls.RemoveWhere(match: control => (control.Device == device));

        var cancellations = new List<CommandInjection>(capacity: affected.Count);

        foreach (var affectedCommand in affected) {
            if (!m_heldBySlot.TryGetValue(key: affectedCommand.Slot, value: out var held) ||
                !held.TryGetValue(key: affectedCommand.CommandId, value: out var entry)) {
                continue;
            }

            if (TryGetHeldDevice(slot: affectedCommand.Slot, commandId: affectedCommand.CommandId, device: out var remainingDevice)) {
                // Another physical control still owns this logical hold. Keep it carried and keep its process-local
                // device annotation truthful for live consumers such as rumble routing.
                held[affectedCommand.CommandId] = (entry with { Device = remainingDevice, });
                continue;
            }

            _ = held.Remove(key: affectedCommand.CommandId);
            _ = m_toggleLatches.Remove(key: (affectedCommand.Slot, affectedCommand.CommandId));

            if (held.Count == 0) {
                _ = m_heldBySlot.Remove(key: affectedCommand.Slot);
            }

            // Unstamped: same reasoning as the focus-loss release above — the slot owns the identity, not the hold.
            cancellations.Add(item: new CommandInjection(
                CommandId: affectedCommand.CommandId,
                Value: CommandValue.Inactive(kind: entry.Value.Kind),
                Phase: CommandPhase.Canceled,
                Principal: default,
                Slot: affectedCommand.Slot
            ));
        }

        QueueCancellations(cancellations: cancellations, discardCapturedSignals: false);
    }
    private void QueueCancellations(List<CommandInjection> cancellations, bool discardCapturedSignals) {
        if ((cancellations.Count == 0) && !discardCapturedSignals) {
            return;
        }

        cancellations.Sort(comparison: static (left, right) => {
            var bySlot = left.Slot.CompareTo(value: right.Slot);

            return ((bySlot != 0) ? bySlot : left.CommandId.CompareTo(value: right.CommandId));
        });

        lock (m_captureGate) {
            if (discardCapturedSignals) {
                // A physical press captured just before focus loss must not become a fresh held input afterward.
                // Console/peer injections are not focus-owned and remain queued.
                m_captured.RemoveAll(match: static captured => (captured.Signal is not null));
            }

            var captureTick = (m_clock?.NowTicks ?? 0UL);

            foreach (var cancellation in cancellations) {
                m_captured.Add(item: new Captured(
                    Sequence: m_sequence++,
                    CaptureTick: captureTick,
                    Signal: null,
                    Injection: cancellation
                ));
            }
        }
    }

    // Queues one pre-resolved command. INTERNAL, and reachable only through a CommandInjectionSink: the injection's
    // principal and lane are the sink's construction-time facts, so there is no signature here a caller could hand a
    // principal of its own choosing to.
    internal void Enqueue(in CommandInjection injection) {
        // An injection's effect mutates the simulation, so it must attribute to a fixed-step tick. An explicit
        // capture tick (a deterministic script / replay harness) is honored; otherwise the shared capture clock
        // stamps it now, exactly as a backend stamps a physical signal — making console input share one timeline
        // with controllers. Determinism comes from recording the resulting snapshot, not from reproducing the
        // live arrival time (the same guarantee a gamepad press already has).
        var captureTick = ((injection.CaptureTick != 0UL)
            ? injection.CaptureTick
            : (m_clock?.NowTicks ?? 0UL));

        lock (m_captureGate) {
            m_captured.Add(item: new Captured(Sequence: m_sequence++, CaptureTick: captureTick, Signal: null, Injection: injection));
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
        var due = DrainDue(windowEndTick: windowEndTick);

        due.Sort(comparison: static (left, right) => {
            var byTime = left.CaptureTick.CompareTo(value: right.CaptureTick);

            return ((byTime != 0)
                ? byTime
                : left.Sequence.CompareTo(value: right.Sequence));
        });

        // Working per-slot ordered state for this tick. Seeded from carried held state (held digitals re-assert as
        // Active), then every due signal is appended in order; repeated commands stay repeated.
        foreach (var working in m_workingBySlot.Values) {
            working.Clear();
        }

        foreach (var (slot, held) in m_heldBySlot) {
            if (held.Count == 0) {
                continue;
            }

            var working = WorkingFor(workingBySlot: m_workingBySlot, slot: slot);

            foreach (var heldEntry in held.Values) {
                // The held entry is already phase Active — a held digital re-asserts each tick.
                working.Add(item: heldEntry);
            }

            working.Sort(comparison: static (left, right) => left.CommandId.CompareTo(value: right.CommandId));
        }

        // Scheduled edges (a Tapped row activator's deferred release — see IChordEdgeSource.DrainScheduledEdges)
        // fold in BEFORE this tick's own due signals are processed, so anything scheduled DURING that processing
        // below cannot be seen by this call — only by the NEXT tick's. That ordering alone is what makes the
        // release land exactly one tick after its press with no clock or tick arithmetic involved.
        if (m_chordEdges is not null) {
            foreach (var (slot, edge) in m_chordEdges.DrainScheduledEdges()) {
                ApplyChordEdge(workingBySlot: m_workingBySlot, slot: slot, device: default, edge: in edge);
            }
        }

        foreach (var captured in due) {
            if (captured.Signal is InputSignal signal) {
                ApplySignal(workingBySlot: m_workingBySlot, signal: signal);
            } else if (captured.Injection is CommandInjection injection) {
                ApplyInjection(workingBySlot: m_workingBySlot, injection: injection);
            }
        }

        return Build(tick: tick, workingBySlot: m_workingBySlot, principalResolver: m_principalResolver);
    }

    private List<Captured> DrainDue(ulong windowEndTick) {
        m_due.Clear();

        lock (m_captureGate) {
            if (m_captured.Count == 0) {
                return m_due;
            }

            var kept = 0;

            for (var index = 0; (index < m_captured.Count); index++) {
                var captured = m_captured[index];

                if (captured.CaptureTick < windowEndTick) {
                    m_due.Add(item: captured);
                } else {
                    m_captured[kept++] = captured;
                }
            }

            m_captured.RemoveRange(index: kept, count: (m_captured.Count - kept));
        }

        return m_due;
    }

    // Folds a pre-resolved command directly into its slot's lane for this tick — no binding lookup (it is already
    // bound) and no held bookkeeping: an injection is one-shot, present only in the tick its capture window placed
    // it, with the caller-chosen edge. A held console input is expressed as an explicit Started/Completed pair.
    private static void ApplyInjection(Dictionary<int, List<CommandEntry>> workingBySlot, CommandInjection injection) {
        var working = WorkingFor(workingBySlot: workingBySlot, slot: injection.Slot);

        working.Add(item: new CommandEntry(
            commandId: injection.CommandId,
            device: default,
            dispatch: true,
            phase: injection.Phase,
            principal: injection.Principal,
            source: injection.Source,
            text: injection.Text,
            value: injection.Value
        ) {
            CompletesTextSubmission = injection.CompletesTextSubmission,
        });
    }
    private void ApplySignal(Dictionary<int, List<CommandEntry>> workingBySlot, InputSignal signal) {
        // Resolve the device's slot first, then ask for THAT slot's bindings — so each player's mapping (an
        // optional override layered over the engine default) drives their own input.
        var slot = m_slotResolver(arg: signal.DeviceId);

        if (slot < 0) {
            return;
        }

        var bindings = m_bindings.Resolve(slot: slot, signal: signal);

        if (m_chordEdges is not null) {
            // Chord-command edges synthesized by this signal's resolve fold into the same lane with their OWN
            // phase and value (the physical signal's phase may be a mid-sweep Active) — see IChordEdgeSource.
            foreach (var edge in m_chordEdges.DrainChordEdges(slot: slot)) {
                ApplyChordEdge(workingBySlot: workingBySlot, slot: slot, device: signal.DeviceId, edge: in edge);
            }
        }

        if (bindings is null) {
            return;
        }

        var assignedSlot = false;
        var acceptedBinding = false;
        // A held-channel entry conventionally authors a PAIR of bindings on the same source (ActivateOn: null for
        // the press/active edge, ActivateOn: Completed for the release edge — see BindingPageEntryDefinition), so
        // one physical signal reaches a Toggle-mode command TWICE. The latch must flip exactly ONCE per signal —
        // this remembers the flip's resolved phase per command id so the second binding reuses it instead of
        // flipping again (which would net a silent no-op).
        Dictionary<ushort, CommandPhase>? toggleFlipsThisSignal = null;

        foreach (var binding in bindings) {
            if (!m_registry.TryGetId(
                name: binding.Command,
                id: out var commandId
            )) {
                continue;
            }

            if (!m_registry.IsSourceCommandActive(commandId: commandId)) {
                continue;
            }

            if (!acceptedBinding) {
                assignedSlot = (m_inputSlotResolver?.CommitSlot(device: signal.DeviceId, slot: slot) ?? false);
                acceptedBinding = true;
            }

            var value = ResolveValue(binding: in binding, signal: in signal);
            var working = WorkingFor(workingBySlot: workingBySlot, slot: slot);
            var isDigital = (value.Kind == CommandValueKind.Digital);
            var phase = signal.Phase;

            // A Toggle-mode binding never reads the physical control's own phase directly: a press FLIPS the
            // latch and the flip's direction becomes the effective phase every line below reasons about (Started
            // when turning on, Completed when turning off); the physical release/active phases that would
            // otherwise re-drive this logic are ignored outright — the latch, not the control, owns "held" now.
            // Gated on the SIGNAL's own kind, not `isDigital` (the DESTINATION's resolved value kind): a channel
            // destination's ChannelScale always resolves to Axis1D (see ResolveValue), even from a digital source,
            // so `isDigital` alone would never see Toggle mode's primary case — a digital key toggling a channel.
            if ((signal.Value.Kind == CommandValueKind.Digital) && (binding.Mode == BindingEntryMode.Toggle)) {
                if (signal.Phase != CommandPhase.Started) {
                    continue;
                }

                if (toggleFlipsThisSignal?.TryGetValue(key: commandId, value: out var memoized) ?? false) {
                    phase = memoized;
                } else {
                    var latchKey = (slot, commandId);
                    var turningOn = !m_toggleLatches.GetValueOrDefault(key: latchKey);

                    m_toggleLatches[latchKey] = turningOn;
                    phase = (turningOn ? CommandPhase.Started : CommandPhase.Completed);
                    (toggleFlipsThisSignal ??= [])[commandId] = phase;
                }
            }

            var dispatch = ((binding.ActivateOn is { } required)
                ? (phase == required)
                : (phase is CommandPhase.Started or CommandPhase.Active));
            var heldControl = new HeldControl(Slot: slot, Device: signal.DeviceId, Source: signal.Source, CommandId: commandId);
            var wasCommandHeld = (isDigital && IsCommandHeld(slot: slot, commandId: commandId));
            var active = ((phase is CommandPhase.Started or CommandPhase.Active) && value.IsActive && (signal.Text is null));

            if ((binding.Mode == BindingEntryMode.Toggle) && !active) {
                // The toggle-off transition: the flip already decided this is a release, independent of the live
                // signal's own value (a toggle-off arrives ON a fresh PRESS, so signal.Value still reads active).
                value = CommandValue.Inactive(kind: value.Kind);
            }

            if (isDigital) {
                if (active) {
                    _ = m_heldControls.Add(item: heldControl);

                    // Two physical controls may bind the same logical command (W + Up). The logical press edge fires
                    // only when the first control goes down.
                    if (wasCommandHeld) {
                        dispatch = false;
                    }
                } else {
                    _ = m_heldControls.Remove(item: heldControl);

                    // Likewise, the logical release edge fires only when the last bound control goes up.
                    if (IsCommandHeld(slot: slot, commandId: commandId)) {
                        dispatch = false;
                        value = m_heldBySlot[slot][commandId].Value;
                        phase = CommandPhase.Active;
                    }
                }
            }

            var entry = new CommandEntry(
                commandId: commandId,
                device: signal.DeviceId,
                dispatch: dispatch,
                phase: phase,
                source: signal.Source,
                value: value,
                assignedSlot: assignedSlot
            );

            working.Add(item: entry);

            // Persist held digitals and the latest active analog sample. Reassertions never redispatch handlers; a
            // release/cancel or inactive analog sample clears the carried value.
            if (
                active
            ) {
                HeldFor(slot: slot)[commandId] = (entry with {
                    Dispatch = (value.Kind != CommandValueKind.Digital),
                    Phase = CommandPhase.Active,
                });
            } else if (((signal.Phase is CommandPhase.Completed or CommandPhase.Canceled) || !value.IsActive) &&
                (!isDigital || !IsCommandHeld(slot: slot, commandId: commandId))) {
                if (m_heldBySlot.TryGetValue(
                    key: slot,
                    value: out var held
                )) {
                    _ = held.Remove(key: commandId);

                    if (held.Count == 0) {
                        _ = m_heldBySlot.Remove(key: slot);
                    }
                }
            }
        }
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
            var componentSample = ((component == AxisComponent.X) ? axis2.X : axis2.Y);
            var componentFixed = FixedQ4816.FromDouble(value: componentSample);
            var componentScale = FixedQ4816.FromDouble(value: channelScale);

            return CommandValue.Axis(value: (float)(double)(componentFixed * componentScale));
        }

        if (signal.Value.Kind != CommandValueKind.Axis1D) {
            return CommandValue.Axis(value: channelScale);
        }

        var sample = FixedQ4816.FromDouble(value: signal.Value.AsAxis1D);
        var scale = FixedQ4816.FromDouble(value: channelScale);

        return CommandValue.Axis(value: (float)(double)(sample * scale));
    }
    // Folds one synthesized chord-command edge into the slot's lane. The press carries held bookkeeping (so
    // IsCommandHeld lights and focus-loss cancellation covers a chord-held command); the release clears it. The
    // command-availability gate matches the bound path — an inactive-map command's chord is inert, not an error.
    private void ApplyChordEdge(Dictionary<int, List<CommandEntry>> workingBySlot, int slot, InputDeviceId device, in BindingChordEdge edge) {
        if (!m_registry.TryGetId(
            name: edge.Command,
            id: out var commandId
        ) || !m_registry.IsSourceCommandActive(commandId: commandId)) {
            return;
        }

        var entry = new CommandEntry(
            commandId: commandId,
            device: device,
            dispatch: edge.Dispatch,
            phase: edge.Phase,
            value: edge.Value
        );

        WorkingFor(workingBySlot: workingBySlot, slot: slot).Add(item: entry);

        // A MOMENTARY press (a Tapped activator's completion — see BindingChordEdge.Momentary) touches neither
        // branch below: it must not be marked held (there is nothing sustaining it — its own release is already
        // scheduled one tick later), and it must not run the release-side removal either (it never marked
        // anything to remove, and nothing else's held entry should be disturbed by an edge that isn't a real
        // Completed transition).
        if (edge.Phase == CommandPhase.Started) {
            if (!edge.Momentary) {
                HeldFor(slot: slot)[commandId] = (entry with {
                    Dispatch = false,
                    Phase = CommandPhase.Active,
                });
            }
        } else if (m_heldBySlot.TryGetValue(
            key: slot,
            value: out var held
        )) {
            _ = held.Remove(key: commandId);

            if (held.Count == 0) {
                _ = m_heldBySlot.Remove(key: slot);
            }
        }
    }
    private static CommandSnapshot Build(ulong tick, Dictionary<int, List<CommandEntry>> workingBySlot, ICommandPrincipalResolver principalResolver) {
        if (workingBySlot.Count == 0) {
            return CommandSnapshot.Empty(tick: tick);
        }

        var activeLaneCount = 0;

        foreach (var working in workingBySlot.Values) {
            if (working.Count != 0) {
                activeLaneCount++;
            }
        }

        if (activeLaneCount == 0) {
            return CommandSnapshot.Empty(tick: tick);
        }

        var lanes = ImmutableArray.CreateBuilder<CommandLane>(initialCapacity: activeLaneCount);

        foreach (var (slot, working) in workingBySlot) {
            if (working.Count == 0) {
                continue;
            }

            var entries = ImmutableArray.CreateBuilder<CommandEntry>(initialCapacity: working.Count);
            // THE STAMP. Ask the host who is acting through this lane — once per lane, because the answer is a
            // property of the slot, not of the entry. A slot may be claimed by a peer or a guest module, so the slot
            // number is never turned into a seat here.
            var lanePrincipal = principalResolver.PrincipalOf(slot: slot);

            // Entry order is semantic: held state is emitted first in command-id order, then due signals/injections in
            // their deterministic capture order. In particular, repeated console verbs in one host frame must remain
            // repeated and FIFO — collapsing by command id would silently drop scripted tape segments.
            foreach (var entry in working) {
                // An injected entry already carries the identity its sink was BOUND to (the console door rides slot 0
                // without becoming that seat); everything captured is stamped from the lane.
                entries.Add(item: (entry.Principal.IsStamped
                    ? entry
                    : (entry with { Principal = lanePrincipal, })));
            }

            lanes.Add(item: new CommandLane(entries: entries.DrainToImmutable(), slot: slot));
        }

        // Order lanes by slot for a deterministic snapshot layout.
        lanes.Sort(comparison: static (left, right) => left.Slot.CompareTo(value: right.Slot));

        return new CommandSnapshot(lanes: lanes.DrainToImmutable(), tick: tick);
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
    private Dictionary<ushort, CommandEntry> HeldFor(int slot) {
        if (!m_heldBySlot.TryGetValue(
            key: slot,
            value: out var held
        )) {
            held = [];
            m_heldBySlot[slot] = held;
        }

        return held;
    }
    private bool IsCommandHeld(int slot, ushort commandId) {
        foreach (var control in m_heldControls) {
            if ((control.Slot == slot) && (control.CommandId == commandId)) {
                return true;
            }
        }

        return false;
    }
    private bool TryGetHeldDevice(int slot, ushort commandId, out InputDeviceId device) {
        foreach (var control in m_heldControls) {
            if ((control.Slot == slot) && (control.CommandId == commandId)) {
                device = control.Device;

                return true;
            }
        }

        device = default;

        return false;
    }
}
