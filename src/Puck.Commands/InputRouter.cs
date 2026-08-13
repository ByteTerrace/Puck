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
public sealed class InputRouter {
    private static readonly IComparer<CommandLane> LaneBySlotComparer = Comparer<CommandLane>.Create(comparison: static (left, right) => left.Slot.CompareTo(value: right.Slot));

    private readonly IAlwaysActiveInputBindings? m_alwaysActiveBindings;
    private readonly IInputBindings m_bindings;
    private readonly IChordEdgeSource? m_chordEdges;
    private readonly Lock m_captureGate = new();
    private readonly List<Captured> m_captured = [];
    private readonly CommandInjectionSink m_consoleTextSink;
    // Simulation-thread scratch retained across ticks. Idle snapshots then allocate nothing; active snapshots allocate
    // only their immutable output. Capture remains independently protected by m_captureGate.
    private readonly List<Captured> m_due = [];
    private readonly Stack<HeldCommandState> m_freeHeldStates = [];
    private readonly IInputClock? m_clock;
    private readonly Dictionary<int, Dictionary<ushort, HeldCommandState>> m_heldBySlot = [];
    private readonly Dictionary<int, ulong> m_lastInputTickBySlot = [];
    // Physical first-down truth is shared by focused and focus-exempt capture. A console-opening press can move its
    // device between those routes before the OS emits repeats or the release; one latch must still recognize them as
    // the same press.
    private readonly HashSet<HeldControlId> m_pressedControls = [];
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
    private readonly record struct Captured(ulong Sequence, ulong CaptureTick, InputSignal? Signal, CommandInjection? Injection, bool FocusExemptOnly = false);
    private readonly record struct HeldCommand(int Slot, ushort CommandId);
    // One physical control holding a command: the (Device, Source) identity a digital hold is tracked and de-duped
    // by. Slot and command id are the enclosing dictionary keys, so they are not repeated here.
    private readonly record struct HeldControlId(InputDeviceId Device, string Source);
    private readonly record struct HeldContribution(HeldControlId Control, CommandEntry Entry);

    // One held command's carried state within a slot. Entry/Controls model a single logical digital or synthesized
    // chord hold (first control down, last control up); Contributions models channel values independently by physical
    // control, because two keys feeding one axis must reassert and cancel separately. Mutable so both shapes update one
    // command-owned state in place without per-tick allocation.
    private sealed class HeldCommandState {
        public CommandEntry Entry;
        public bool HasEntry;
        public List<HeldControlId>? Controls;
        public List<HeldContribution>? Contributions;

        public bool IsEmpty => !HasEntry && (Contributions is not { Count: > 0 });

        public void Reset() {
            Entry = default;
            HasEntry = false;
            Controls?.Clear();
            Contributions?.Clear();
        }
    }

    /// <summary>Initializes a new instance of the <see cref="InputRouter"/> class.</summary>
    /// <param name="registry">The registry that interns command ids and gates by map.</param>
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
            reloadSource.Reloading += ReleaseHeld;
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
    /// <param name="registry">The registry that interns command ids and gates by map.</param>
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

    /// <summary>The console/STDIN text door's injection sink — the one a <see cref="CommandRegistry"/> is wired to
    /// through <see cref="CommandRegistry.RouteSimulationTo"/>. Bound to <see cref="CommandPrincipal.Console"/> at
    /// construction, so a submitted line acts as the console and cannot be made to act as anything else.</summary>
    public CommandInjectionSink ConsoleTextSink => m_consoleTextSink;

    internal CommandRegistry Registry => m_registry;

    internal CommandInjectionSink CreateSeatTextSink(int slot) => new(
        router: this,
        principal: CommandPrincipal.Seat(slot: slot),
        slot: slot
    );

    /// <summary>Appends a captured input signal. Thread-safe — backends call this from device I/O threads and the window pump.</summary>
    /// <param name="signal">The timestamped input signal to capture.</param>
    public void Capture(in InputSignal signal) {
        lock (m_captureGate) {
            m_captured.Add(item: new Captured(
                Sequence: m_sequence++,
                CaptureTick: signal.CaptureTick,
                Signal: signal,
                Injection: null
            ));
        }
    }

    /// <summary>Captures a signal from a device whose ordinary terminal focus is released. Only bindings whose
    /// destination declares <see cref="CommandInputScope.FocusExempt"/> may dispatch; only the host-owned
    /// <see cref="IAlwaysActiveInputBindings"/> plane is consulted, so typed keys cannot mutate gameplay pages,
    /// chords, or press latches while suppressed.</summary>
    /// <param name="signal">The raw signal to capture.</param>
    public void CaptureFocusExempt(in InputSignal signal) {
        lock (m_captureGate) {
            m_captured.Add(item: new Captured(
                Sequence: m_sequence++,
                CaptureTick: signal.CaptureTick,
                Signal: signal,
                Injection: null,
                FocusExemptOnly: true
            ));
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

        if (!m_registry.TryGetId(
            name: activation.Command,
            id: out var commandId
        )) {
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
        return (
            m_registry.TryGetId(
            name: command,
            id: out var commandId
        ) &&
            m_heldBySlot.TryGetValue(
            key: slot,
            value: out var held
        ) &&
            held.ContainsKey(key: commandId)
        );
    }

    /// <summary>Gets the simulation tick at which a seat most recently produced a physical or synthesized raw input
    /// signal.</summary>
    /// <param name="slot">The logical player slot.</param>
    /// <param name="tick">The last input tick, meaningful only when this returns <see langword="true"/>.</param>
    /// <returns><see langword="true"/> when the seat has produced an input signal.</returns>
    /// <remarks>Pump-thread only, on the same terms as <see cref="IsCommandHeld(int, string)"/>.</remarks>
    public bool TryGetLastInputTick(int slot, out ulong tick) => m_lastInputTickBySlot.TryGetValue(
        key: slot,
        value: out tick
    );

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
                    slot: slot,
                    state: state
                );
                RecycleHeldState(state: state);
            }
        }

        m_heldBySlot.Clear();
        m_pressedControls.Clear();
        m_toggleLatches.Clear();
        m_bindings.ResetAll();

        QueueCancellations(
            cancellations: cancellations,
            discardCapturedSignals: true
        );
    }

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

    /// <summary>Releases held commands owned by one physical device without disturbing other seats or devices.</summary>
    /// <param name="device">The device whose held state is being withdrawn.</param>
    public void ReleaseHeld(InputDeviceId device) => ReleaseHeld(device: device, preservePressedControls: false);

    /// <summary>Releases a device's gameplay holds for a focus handoff while preserving its physical first-down
    /// latches until the corresponding releases arrive. This prevents an OS repeat of the console opener from
    /// becoming a second toggle after focus moves.</summary>
    /// <param name="device">The device being suppressed from ordinary input.</param>
    public void SuppressHeld(InputDeviceId device) => ReleaseHeld(device: device, preservePressedControls: true);

    private void ReleaseHeld(InputDeviceId device, bool preservePressedControls) {
        var cancellations = new List<CommandInjection>();
        var toDrop = new List<HeldCommand>();

        if (!preservePressedControls) {
            m_pressedControls.RemoveWhere(match: control => control.Device == device);
        }

        foreach (var (slot, held) in m_heldBySlot) {
            foreach (var (commandId, state) in held) {
                if (preservePressedControls && m_registry.IsFocusExemptCommand(commandId: commandId)) {
                    continue;
                }

                if (state.Contributions is { } contributions) {
                    for (var index = contributions.Count - 1; index >= 0; index--) {
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
                    var removedControls = controls.RemoveAll(match: control => control.Device == device);

                    if ((removedControls > 0) && (controls.Count == 0) && state.HasEntry) {
                        cancellations.Add(item: CancellationFor(
                            entry: state.Entry,
                            slot: slot
                        ));
                        state.Entry = default;
                        state.HasEntry = false;
                    } else if ((removedControls > 0) && (controls.Count > 0)) {
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
                        Slot: slot,
                        CommandId: commandId
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
            ));
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
        // with controllers. Replay records the server input stream and restores its order rather than trying to
        // reproduce live arrival time (the same guarantee a gamepad press already has).
        var captureTick = ((injection.CaptureTick != 0UL)
            ? injection.CaptureTick
            : (m_clock?.NowTicks ?? 0UL));

        lock (m_captureGate) {
            m_captured.Add(item: new Captured(
                Sequence: m_sequence++,
                CaptureTick: captureTick,
                Signal: null,
                Injection: injection
            ));
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

            var working = WorkingFor(
                workingBySlot: m_workingBySlot,
                slot: slot
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
                ));
            });
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
                    workingBySlot: m_workingBySlot,
                    slot: slot,
                    device: default,
                    edge: in edge
                );
            }
        }

        foreach (var captured in due) {
            if (captured.Signal is InputSignal signal) {
                ApplySignal(
                    workingBySlot: m_workingBySlot,
                    signal: signal,
                    tick: tick,
                    focusExemptOnly: captured.FocusExemptOnly
                );
            } else if (captured.Injection is CommandInjection injection) {
                ApplyInjection(
                    workingBySlot: m_workingBySlot,
                    injection: injection
                );
            }
        }

        return Build(
            registry: m_registry,
            tick: tick,
            workingBySlot: m_workingBySlot,
            principalResolver: m_principalResolver
        );
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

            m_captured.RemoveRange(
                index: kept,
                count: (m_captured.Count - kept)
            );
        }

        return m_due;
    }

    // Folds a pre-resolved command directly into its slot's lane for this tick — no binding lookup (it is already
    // bound) and no held bookkeeping: an injection is one-shot, present only in the tick its capture window placed
    // it, with the caller-chosen edge. A held console input is expressed as an explicit Started/Completed pair.
    private static void ApplyInjection(Dictionary<int, List<CommandEntry>> workingBySlot, CommandInjection injection) {
        var working = WorkingFor(
            workingBySlot: workingBySlot,
            slot: injection.Slot
        );

        working.Add(item: new CommandEntry(
            commandId: injection.CommandId,
            device: default,
            dispatch: true,
            phase: injection.Phase,
            principal: injection.Principal,
            source: injection.Source,
            text: injection.Text,
            value: injection.Value,
            dispatchWhenMapInactive: injection.DispatchWhenMapInactive
        ) {
            CompletesTextSubmission = injection.CompletesTextSubmission,
            SubmissionBarrier = injection.SubmissionBarrier,
        });
    }
    private void ApplySignal(Dictionary<int, List<CommandEntry>> workingBySlot, InputSignal signal, ulong tick, bool focusExemptOnly) {
        // Resolve activity before repeat de-duplication: an OS repeat is not a second command edge, but it is still
        // fresh physical activity for idle/away accounting.
        var slot = m_slotResolver(arg: signal.DeviceId);

        if (slot < 0) {
            return;
        }

        m_lastInputTickBySlot[slot] = tick;

        var physicalControl = new HeldControlId(
            Device: signal.DeviceId,
            Source: signal.Source
        );

        if (signal.Phase == CommandPhase.Started) {
            // OS key repeat is another Started event. It must not re-run an edge command (especially a toggle), and
            // opening a console between the first event and a repeat must not make that repeat look like a new press.
            if (!m_pressedControls.Add(item: physicalControl)) {
                return;
            }
        } else if (signal.Phase is CommandPhase.Completed or CommandPhase.Canceled) {
            _ = m_pressedControls.Remove(item: physicalControl);
        }

        // Focus-exempt capture deliberately never consults the current authored page. Host-owned terminal bindings
        // live in their own always-active plane, so a page override cannot accidentally remove the escape hatch.
        var pageBindings = (focusExemptOnly ? null : m_bindings.Resolve(slot: slot, signal: signal));
        var alwaysActiveBindings = m_alwaysActiveBindings?.Resolve(slot: slot, source: signal.Source);

        if (!focusExemptOnly && (m_chordEdges is not null)) {
            // Chord-command edges synthesized by this signal's resolve fold into the same lane with their OWN
            // phase and value (the physical signal's phase may be a mid-sweep Active) — see IChordEdgeSource.
            foreach (var edge in m_chordEdges.DrainChordEdges(slot: slot)) {
                ApplyChordEdge(
                    workingBySlot: workingBySlot,
                    slot: slot,
                    device: signal.DeviceId,
                    edge: in edge
                );
            }
        }

        var pageBindingCount = (pageBindings?.Count ?? 0);
        var alwaysActiveBindingCount = (alwaysActiveBindings?.Count ?? 0);

        if ((pageBindingCount + alwaysActiveBindingCount) == 0) {
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

        for (var bindingIndex = 0; (bindingIndex < (pageBindingCount + alwaysActiveBindingCount)); bindingIndex++) {
            var binding = ((bindingIndex < pageBindingCount)
                ? pageBindings![bindingIndex]
                : alwaysActiveBindings![bindingIndex - pageBindingCount]);

            if (!m_registry.TryGetId(
                name: binding.Command,
                id: out var commandId
            )) {
                continue;
            }

            if (
                focusExemptOnly &&
                (!m_registry.TryGetMetadata(name: binding.Command, metadata: out var metadata) ||
                 (metadata.InputScope != CommandInputScope.FocusExempt))
            ) {
                continue;
            }

            var value = ResolveValue(
                binding: in binding,
                signal: in signal
            );
            var controlId = new HeldControlId(
                Device: signal.DeviceId,
                Source: signal.Source
            );
            var sourceCommandActive = m_registry.IsSourceCommandActive(commandId: commandId);
            var ownsHeldState = IsHeldByControl(
                commandId: commandId,
                control: controlId,
                slot: slot
            );

            // Map gating blocks new ownership, but never swallows the release of ownership acquired while the map
            // was active. The registry likewise admits only Completed/Canceled cleanup edges through a closed map.
            if (!sourceCommandActive) {
                if (
                    (binding.Mode == BindingEntryMode.Toggle) ||
                    !ownsHeldState ||
                    ((signal.Phase is CommandPhase.Started or CommandPhase.Active) && value.IsActive)
                ) {
                    continue;
                }
            }

            if (sourceCommandActive && !acceptedBinding) {
                assignedSlot = (m_inputSlotResolver?.CommitSlot(
                    device: signal.DeviceId,
                    slot: slot
                ) ?? false);
                acceptedBinding = true;
            }

            var working = WorkingFor(
                workingBySlot: workingBySlot,
                slot: slot
            );
            var isContribution = ((binding.ChannelScale is not null) && (binding.Mode == BindingEntryMode.Hold));
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

                if (toggleFlipsThisSignal?.TryGetValue(
                    key: commandId,
                    value: out var memoized
                ) ?? false) {
                    phase = memoized;
                } else {
                    var latchKey = (slot, commandId);
                    var turningOn = !m_toggleLatches.GetValueOrDefault(key: latchKey);

                    m_toggleLatches[latchKey] = turningOn;
                    phase = (turningOn
                        ? CommandPhase.Started
                        : CommandPhase.Completed);
                    (toggleFlipsThisSignal ??= [])[commandId] = phase;
                }
            }

            // A channel destination is a held contribution, so its ordinary (ActivateOn:null) binding owns BOTH
            // halves of the hold. In particular, an axis such as a trigger commonly ends with Completed+zero. If
            // that edge is filtered like an ordinary press-bound verb, this router clears its carried sample while
            // the destination never hears the release and retains the last non-zero contribution forever. Authors
            // should not have to duplicate every channel row with an ActivateOn:Completed twin merely to make a
            // physical control stop. An explicit ActivateOn remains exactly edge-selective.
            var dispatch = ((binding.ActivateOn is { } required)
                ? (phase == required)
                : ((phase is CommandPhase.Started or CommandPhase.Active) ||
                    ((binding.ChannelScale is not null) && (phase is CommandPhase.Completed or CommandPhase.Canceled))));
            var wasCommandHeld = (isDigital && IsControlDownFor(
                slot: slot,
                commandId: commandId
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
                        slot: slot,
                        commandId: commandId
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
                phase: phase,
                source: signal.Source,
                value: value,
                assignedSlot: assignedSlot,
                dispatchWhenMapInactive: (ownsHeldState && (phase is CommandPhase.Completed or CommandPhase.Canceled))
            );

            working.Add(item: entry);

            // Channel destinations carry one contribution per physical control. Two keys sharing a destination
            // therefore remain independently owned: releasing one cannot erase the other's reassertion or later
            // focus-loss cancellation.
            if (isContribution) {
                if (active) {
                    SetContribution(
                        commandId: commandId,
                        control: controlId,
                        entry: entry with {
                            Dispatch = true,
                            Phase = CommandPhase.Active,
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
            } else if (active) {
                var state = HeldFor(
                    commandId: commandId,
                    slot: slot
                );

                state.Entry = (entry with {
                    Dispatch = (value.Kind != CommandValueKind.Digital),
                    Phase = CommandPhase.Active,
                });
                state.HasEntry = true;
            } else if (
                ((signal.Phase is CommandPhase.Completed or CommandPhase.Canceled) || !value.IsActive) &&
                (!isDigital || !IsControlDownFor(
                slot: slot,
                commandId: commandId
            ))
            ) {
                DropHeld(
                    commandId: commandId,
                    slot: slot
                );
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
            var componentSample = ((component == AxisComponent.X)
                ? axis2.X
                : axis2.Y);

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

        return CommandValue.Axis(value: (float)(double)(s * k));
    }
    // Folds one synthesized chord-command edge into the slot's lane. The press carries held bookkeeping (so
    // IsCommandHeld lights and focus-loss cancellation covers a chord-held command); the release clears it. The
    // command-availability gate matches the bound path — an inactive-map command's chord is inert, not an error.
    private void ApplyChordEdge(Dictionary<int, List<CommandEntry>> workingBySlot, int slot, InputDeviceId device, in BindingChordEdge edge) {
        if (!m_registry.TryGetId(
            name: edge.Command,
            id: out var commandId
        )) {
            return;
        }

        var sourceCommandActive = m_registry.IsSourceCommandActive(commandId: commandId);
        var wasHeld = IsHeld(commandId: commandId, slot: slot);

        if (
            !sourceCommandActive &&
            ((edge.Phase == CommandPhase.Started) || !wasHeld)
        ) {
            return;
        }

        var entry = new CommandEntry(
            commandId: commandId,
            device: device,
            dispatch: edge.Dispatch,
            phase: edge.Phase,
            value: edge.Value,
            dispatchWhenMapInactive: (wasHeld && (edge.Phase is CommandPhase.Completed or CommandPhase.Canceled))
        );

        WorkingFor(
            workingBySlot: workingBySlot,
            slot: slot
        ).Add(item: entry);

        // A MOMENTARY press (a Tapped activator's completion — see BindingChordEdge.Momentary) touches neither
        // branch below: it must not be marked held (there is nothing sustaining it — its own release is already
        // scheduled one tick later), and it must not run the release-side removal either (it never marked
        // anything to remove, and nothing else's held entry should be disturbed by an edge that isn't a real
        // Completed transition).
        if (edge.Phase == CommandPhase.Started) {
            if (!edge.Momentary) {
                var state = HeldFor(
                    commandId: commandId,
                    slot: slot
                );

                state.Entry = (entry with {
                    Dispatch = false,
                    Phase = CommandPhase.Active,
                });
                state.HasEntry = true;
            }
        } else {
            DropHeld(
                commandId: commandId,
                slot: slot
            );
        }
    }
    private static CommandSnapshot Build(CommandRegistry registry, ulong tick, Dictionary<int, List<CommandEntry>> workingBySlot, ICommandPrincipalResolver principalResolver) {
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

            lanes.Add(item: new CommandLane(
                entries: entries.DrainToImmutable(),
                slot: slot
            ));
        }

        // Order lanes by slot for a deterministic snapshot layout.
        lanes.Sort(comparer: LaneBySlotComparer);

        return new CommandSnapshot(
            lanes: lanes.DrainToImmutable(),
            registry: registry,
            tick: tick
        );
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
                : new HeldCommandState());
            held[commandId] = state;
        }

        return state;
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
    private void SetContribution(int slot, ushort commandId, HeldControlId control, CommandEntry entry) {
        var state = HeldFor(
            commandId: commandId,
            slot: slot
        );
        var contributions = (state.Contributions ??= new List<HeldContribution>(capacity: 2));

        for (var index = 0; index < contributions.Count; index++) {
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

        for (var index = contributions.Count - 1; index >= 0; index--) {
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
    // Returns one dropped state to this router's retained scratch. Clearing releases its entry/source references and
    // logical contents while preserving a small Controls list's capacity for the next digital hold.
    private void RecycleHeldState(HeldCommandState state) {
        state.Reset();
        m_freeHeldStates.Push(item: state);
    }
    // One deterministic cancellation for a carried command. Unstamped on purpose: a synthesized release belongs to the
    // SLOT that held the input, so snapshot construction resolves its principal like any other captured entry rather
    // than freezing whoever was acting when the hold began.
    private static CommandInjection CancellationFor(int slot, CommandEntry entry) => new(
        CommandId: entry.CommandId,
        Value: CommandValue.Inactive(kind: entry.Value.Kind),
        Phase: CommandPhase.Canceled,
        Principal: default,
        Slot: slot,
        Source: entry.Source
    ) {
        DispatchWhenMapInactive = true,
    };
    private static void AppendCancellations(List<CommandInjection> cancellations, int slot, HeldCommandState state) {
        if (state.HasEntry) {
            cancellations.Add(item: CancellationFor(
                entry: state.Entry,
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
    private bool IsHeld(ushort commandId, int slot) {
        return (
            m_heldBySlot.TryGetValue(
            key: slot,
            value: out var held
        ) &&
            held.ContainsKey(key: commandId)
        );
    }
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
    // Whether any physical control is still down for a DIGITAL command in a slot — the logical-hold test the
    // first-down / last-up edge logic reads. An analog or chord hold carries no controls and answers false here even
    // though it is carried; IsCommandHeld(int, string) is the "carried at all" test.
    private bool IsControlDownFor(int slot, ushort commandId) => TryGetHeldDevice(
        slot: slot,
        commandId: commandId,
        device: out _
    );
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
}
