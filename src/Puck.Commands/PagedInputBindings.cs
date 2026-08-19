using System.Collections.Concurrent;

namespace Puck.Commands;

/// <summary>
/// A stateful <see cref="IInputBindings"/> that resolves each signal against the page its slot's active group and
/// held modifier chord select, and fires the group's command-meaning chord rows as synthesized edges
/// (<see cref="IChordEdgeSource"/>). It sits exactly where a flat table sits today — inside the
/// <see cref="InputRouter"/>'s deterministic pre-snapshot fold — so recorded <see cref="CommandSnapshot"/>s
/// already contain chord-resolved commands and replay never re-resolves a binding.
/// </summary>
/// <remarks>
/// <para>The settled per-signal order when a signal drives a declared modifier: (1) the tracker advances (the
/// held order updates); (2) the active page re-resolves — the deepest page row whose chord is a press-order
/// prefix of the held order (a page flip happens here); (3) chord-command transitions synthesize edges — first
/// the releases of broken armed rows, then the presses of rows the new held order completes exactly; (4) the
/// signal's own source lookup resolves against the post-flip page. So a page under a deeper command chord
/// (<c>[lt]</c> page beneath a <c>[lt, rt]</c> command) flips first and fires second, and the pass-through stays
/// coherent: sources keep answering through the deepest page row while the command chord is held.</para>
/// <para>Two latches make transitions safe:</para>
/// <list type="bullet">
/// <item><description>A source press latches the binding list it resolved, and the matching release resolves to
/// that same list even if the page — or the active group — changed in between; a held action stays itself, new
/// presses use the new page. <see cref="SetActiveGroup"/> deliberately touches neither the latches nor the
/// tracker: a mode flip is a pointer-level switch.</description></item>
/// <item><description>A completed command chord stays armed until any member releases, regardless of page or
/// group flips in between — its release edge always fires against the row that pressed.</description></item>
/// </list>
/// All state mutates on the router's single snapshot thread; only the published <see cref="BindingPageView"/>
/// reference crosses threads (the render-side UI reads it via <see cref="ViewFor"/>).
/// </remarks>
public sealed class PagedInputBindings : IInputBindings, IChordEdgeSource, IInputBindingsReloadSource {
    // Requested group names by slot — kept OUTSIDE the slot states so a Reload (which drops every state) re-applies
    // each slot's mode to the new profile instead of silently falling back to the default group.
    private readonly ConcurrentDictionary<int, string> m_requestedGroups = new();
    private readonly ConcurrentDictionary<int, SlotState> m_slots = new();
    // A Tapped row activator's deferred release (see DrainScheduledEdges) — populated during THIS tick's signal
    // processing, drained on the NEXT tick's call, before it folds its own due signals. The list is retained across
    // drains; the next scheduled edge clears the already-consumed contents before appending a fresh batch.
    private readonly List<(int Slot, BindingChordEdge Edge)> m_scheduledEdges = [];

    private volatile CompiledBindingProfile m_profile;
    private bool m_scheduledEdgesPending;

    event Action<int?> IInputBindingsReloadSource.Reloading {
        add => Reloading += value;
        remove => Reloading -= value;
    }

    private event Action<int?>? Reloading;

    private sealed class SlotState {
        // One RowActivatorTracker per compiled activator entry (CompiledActivatorEntry.ActivatorIndex), lazily
        // instantiated on first use. Sized once at slot creation — see CompiledBindingProfile.ActivatorCount.
        public required RowActivatorTracker?[] ActivatorTrackers { get; init; }
        public required bool[] ArmedRows { get; init; }
        // The sources whose press FIRED a command chord and is therefore consumed by it: the page's own binding for
        // that source does not also resolve, and neither does its release. Cleared as each source releases.
        public required HashSet<string> ChordConsumed { get; init; }
        public required Dictionary<string, IReadOnlyList<CommandBinding>> Latches { get; init; }
        public required CompiledBindingProfile Profile { get; init; }
        public required BindingChordTracker Tracker { get; init; }

        public int GroupIndex;
        public int PageRowIndex;
        public int PendingEdgeCount;
        public BindingChordEdge[] PendingEdges;
        public volatile BindingPageView View;

        public SlotState() {
            PendingEdges = new BindingChordEdge[4];
            View = null!;
        }
    }

    /// <summary>Initializes a new instance of the <see cref="PagedInputBindings"/> class.</summary>
    /// <param name="profile">The compiled profile to resolve against.</param>
    /// <exception cref="ArgumentNullException"><paramref name="profile"/> is <see langword="null"/>.</exception>
    public PagedInputBindings(CompiledBindingProfile profile) {
        ArgumentNullException.ThrowIfNull(profile);

        m_profile = profile;
    }

    private static void AppendEdge(SlotState state, in BindingChordEdge edge) {
        if (state.PendingEdgeCount == state.PendingEdges.Length) {
            Array.Resize(
                array: ref state.PendingEdges,
                newSize: (state.PendingEdges.Length * 2)
            );
        }

        state.PendingEdges[state.PendingEdgeCount++] = edge;
    }
    private void AppendScheduledEdge(int slot, in BindingChordEdge edge) {
        if (!m_scheduledEdgesPending) {
            m_scheduledEdges.Clear();
            m_scheduledEdgesPending = true;
        }

        m_scheduledEdges.Add(item: (slot, edge));
    }
    // Evaluates the ACTIVE page's row activators against one signal, synthesizing chord-style edges (drained the
    // same way a group-level command chord's are — see DrainChordEdges) on a Held gate open/close. A Tapped
    // completion is a PULSE: the press edge fires NOW, but the release is SCHEDULED for the next tick
    // (DrainScheduledEdges) rather than firing in the same batch — a same-tick press+release pair is invisible to
    // a downstream reader (a channel's held state, sampled once between ticks) that never observes the moment in
    // between, which would make a completed tap either never fire or — worse, if the release's Dispatch ever went
    // true without the deferral — never actually clear (see BindingProfile's DispatchRelease remarks).
    private void ApplyRowActivators(int slot, SlotState state, in InputSignal signal) {
        var activators = state.Profile.ActivatorsOf(rowIndex: state.PageRowIndex);

        if (activators.Count == 0) {
            return;
        }

        for (var activatorIndex = 0; (activatorIndex < activators.Count); activatorIndex++) {
            var activatorEntry = activators[activatorIndex];
            var tracker = (state.ActivatorTrackers[activatorEntry.ActivatorIndex] ??= new RowActivatorTracker(activator: activatorEntry.Activator));
            var transition = tracker.Apply(signal: in signal);

            switch (transition) {
                case RowActivatorTransition.Opened:
                    AppendEdge(
                        state: state,
                        edge: new BindingChordEdge(
                            Command: activatorEntry.Edge.Command,
                            Source: activatorEntry.Edge.Source,
                            Dispatch: true,
                            DispatchRelease: activatorEntry.Edge.DispatchRelease,
                            Mode: activatorEntry.Edge.Mode,
                            Phase: CommandPhase.Started,
                            Value: activatorEntry.Edge.PressValue
                        )
                    );
                    break;
                case RowActivatorTransition.Closed:
                    if (activatorEntry.Edge.Mode == BindingEntryMode.Toggle) {
                        break;
                    }
                    AppendEdge(
                        state: state,
                        edge: new BindingChordEdge(
                            Command: activatorEntry.Edge.Command,
                            Source: activatorEntry.Edge.Source,
                            Dispatch: activatorEntry.Edge.DispatchRelease,
                            Mode: activatorEntry.Edge.Mode,
                            Phase: CommandPhase.Completed,
                            Value: activatorEntry.Edge.ReleaseValue
                        )
                    );
                    break;
                case RowActivatorTransition.Completed:
                    if (activatorEntry.Edge.Mode == BindingEntryMode.Toggle) {
                        AppendEdge(
                            state: state,
                            edge: new BindingChordEdge(
                                Command: activatorEntry.Edge.Command,
                                Source: activatorEntry.Edge.Source,
                                Dispatch: true,
                                DispatchRelease: activatorEntry.Edge.DispatchRelease,
                                Mode: BindingEntryMode.Toggle,
                                Phase: CommandPhase.Started,
                                Value: activatorEntry.Edge.PressValue
                            )
                        );
                        break;
                    }
                    AppendEdge(
                        state: state,
                        edge: new BindingChordEdge(
                        Command: activatorEntry.Edge.Command,
                        Source: activatorEntry.Edge.Source,
                        Dispatch: true,
                        Phase: CommandPhase.Started,
                        Value: activatorEntry.Edge.PressValue,
                        // MOMENTARY: its own release is already scheduled one tick below — marking THIS edge held
                        // too would make the tick the scheduled release lands on ALSO carry a stale, non-dispatching
                        // re-assertion of the press (harmless to a dispatch-gated reader, but not the clean single-
                        // entry pulse a tap is supposed to produce).
                        Momentary: true,
                        DispatchRelease: activatorEntry.Edge.DispatchRelease
                    )
                    );
                    AppendScheduledEdge(
                        slot: slot,
                        edge: new BindingChordEdge(
                            Command: activatorEntry.Edge.Command,
                            Source: activatorEntry.Edge.Source,
                            Dispatch: activatorEntry.Edge.DispatchRelease,
                            Phase: CommandPhase.Completed,
                            Value: activatorEntry.Edge.ReleaseValue
                        )
                    );
                    break;
                case RowActivatorTransition.None:
                default:
                    break;
            }
        }
    }
    private static void Publish(SlotState state) {
        state.View = state.Profile.ViewOf(rowIndex: state.PageRowIndex);
    }
    // Abandons a page's in-flight activator progress when it stops being the active page (see SyncChordState,
    // SetActiveGroup, Reset) — a partial Held/Tapped sequence must not silently complete after the player has
    // moved to a different page.
    private static void ResetActivatorTrackers(SlotState state, int pageRowIndex) {
        foreach (var activatorEntry in state.Profile.ActivatorsOf(rowIndex: pageRowIndex)) {
            state.ActivatorTrackers[activatorEntry.ActivatorIndex]?.Reset();
        }
    }
    // Re-resolves and publishes the page selected by the slot's current group and held order. A page flip abandons
    // the outgoing row activators' partial progress before the new view becomes visible.
    private static void ResolveAndPublishPage(SlotState state) {
        var previousPageRowIndex = state.PageRowIndex;

        state.PageRowIndex = state.Profile.PageRowOf(
            groupIndex: state.GroupIndex,
            heldOrder: state.Tracker.HeldOrder
        );

        if (previousPageRowIndex != state.PageRowIndex) {
            ResetActivatorTrackers(
                pageRowIndex: previousPageRowIndex,
                state: state
            );
        }

        Publish(state: state);
    }
    private int ResolveGroupIndex(CompiledBindingProfile profile, int slot) {
        return ((m_requestedGroups.TryGetValue(
            key: slot,
            value: out var requested
        ) && profile.TryGetGroup(
            group: requested,
            groupIndex: out var groupIndex
        ))
            ? groupIndex
            : profile.DefaultGroupIndex
        );
    }
    private SlotState StateFor(int slot) {
        var profile = m_profile;

        if (
            m_slots.TryGetValue(
            key: slot,
            value: out var state
        ) &&
            ReferenceEquals(
            objA: state.Profile,
            objB: profile
        )
        ) {
            return state;
        }

        var groupIndex = ResolveGroupIndex(
            profile: profile,
            slot: slot
        );
        var created = new SlotState {
            ActivatorTrackers = new RowActivatorTracker?[profile.ActivatorCount],
            ArmedRows = new bool[profile.RowCount],
            GroupIndex = groupIndex,
            Latches = new Dictionary<string, IReadOnlyList<CommandBinding>>(comparer: StringComparer.OrdinalIgnoreCase),
            ChordConsumed = new HashSet<string>(comparer: StringComparer.OrdinalIgnoreCase),
            PageRowIndex = profile.RestingRowOf(groupIndex: groupIndex),
            Profile = profile,
            Tracker = new BindingChordTracker(profile: profile),
        };

        Publish(state: created);
        m_slots[slot] = created;

        return created;
    }
    // Recompute a slot's chord-derived state after a tracker change: the deepest-page resolution, the published
    // view, and the command-row transition edges (releases of broken armed rows first, then fresh completions).
    // Returns whether a command row FIRED on this signal — the caller consumes the signal, so the source's own page
    // binding does not also resolve (the most specific match wins: LT+RB recenters, it does not also jetpack).
    private static bool SyncChordState(SlotState state, bool isDigitalReassertion) {
        var fired = false;
        var held = state.Tracker.HeldOrder;
        var profile = state.Profile;

        for (var rowIndex = 0; (rowIndex < state.ArmedRows.Length); rowIndex++) {
            if (!state.ArmedRows[rowIndex]) {
                continue;
            }

            var row = profile.RowAt(rowIndex: rowIndex);

            if (!CompiledBindingProfile.Matches(
                heldOrder: held,
                row: row
            )) {
                state.ArmedRows[rowIndex] = false;
                if (row.Command!.Mode == BindingEntryMode.Toggle) {
                    continue;
                }
                AppendEdge(
                    state: state,
                    edge: new BindingChordEdge(
                        Command: row.Command!.Command,
                        Source: row.Command.Source,
                        Dispatch: row.Command.DispatchRelease,
                        Phase: CommandPhase.Completed,
                        Value: row.Command.ReleaseValue
                    )
                );
            }
        }

        foreach (var rowIndex in profile.CommandRowsOf(groupIndex: state.GroupIndex)) {
            if (state.ArmedRows[rowIndex]) {
                continue;
            }

            var row = profile.RowAt(rowIndex: rowIndex);

            // A command row fires on COMPLETION: the down set is exactly its members with the sequence satisfied (a
            // press only ever appends to the held order, so completion is that exact moment).
            if (CompiledBindingProfile.Completes(
                heldOrder: held,
                row: row
            )) {
                var command = row.Command!;

                if (
                    isDigitalReassertion &&
                    !command.Reassertable
                ) {
                    continue;
                }

                fired = true;
                state.ArmedRows[rowIndex] = true;
                AppendEdge(
                    state: state,
                    edge: new BindingChordEdge(
                        Command: command.Command,
                        Source: command.Source,
                        Dispatch: true,
                        DispatchRelease: command.DispatchRelease,
                        Mode: command.Mode,
                        Phase: (isDigitalReassertion
                    ? CommandPhase.Active
                    : CommandPhase.Started),
                        Value: command.PressValue
                    )
                );
            }
        }

        ResolveAndPublishPage(state: state);

        return fired;
    }

    /// <inheritdoc/>
    public ReadOnlySpan<BindingChordEdge> DrainChordEdges(int slot) {
        if (
            !m_slots.TryGetValue(
            key: slot,
            value: out var state
        ) ||
            (state.PendingEdgeCount == 0)
        ) {
            return [];
        }

        var count = state.PendingEdgeCount;

        state.PendingEdgeCount = 0;

        return state.PendingEdges.AsSpan(
            length: count,
            start: 0
        );
    }
    /// <inheritdoc/>
    public IReadOnlyList<(int Slot, BindingChordEdge Edge)> DrainScheduledEdges() {
        if (!m_scheduledEdgesPending) {
            return [];
        }

        m_scheduledEdgesPending = false;

        return m_scheduledEdges;
    }
    /// <summary>Returns a value indicating whether the currently-loaded compiled profile declares <paramref name="group"/> — the probe a caller
    /// uses to validate a requested group without applying it (a context-derived override may currently shadow the
    /// request, so "apply and observe" cannot answer this).</summary>
    /// <param name="group">The group name to look up.</param>
    /// <returns><see langword="true"/> when the profile declares the group.</returns>
    public bool HasGroup(string group) {
        return m_profile.TryGetGroup(
            group: group,
            groupIndex: out _
        );
    }
    /// <summary>Atomically swaps in a recompiled profile (an editor save), releasing every slot's chord and latches.
    /// Each slot's requested active group carries over — re-resolved against the new profile, falling back to its
    /// default group when the new profile no longer declares the name.</summary>
    /// <param name="profile">The compiled profile to resolve against from now on.</param>
    /// <exception cref="ArgumentNullException"><paramref name="profile"/> is <see langword="null"/>.</exception>
    public void Reload(CompiledBindingProfile profile) {
        ArgumentNullException.ThrowIfNull(profile);

        Reloading?.Invoke(obj: null);
        m_scheduledEdges.Clear();
        m_scheduledEdgesPending = false;
        m_profile = profile;
        m_slots.Clear();
    }
    /// <summary>Releases a slot's chord, press latches, and armed command chords — wired to focus loss (via
    /// <see cref="ResetAll"/>). Deliberately not wired to a single device's disconnect: <c>InputRouter</c>'s
    /// per-device release touches no binding state, because resetting a whole slot's chord tracker on one
    /// device's disconnect could wipe a different still-connected device's legitimately-held modifier on the same
    /// slot. Silent by design: the router's own held cancellation delivers the release edges.</summary>
    /// <param name="slot">The logical player slot.</param>
    public void Reset(int slot) {
        _ = m_scheduledEdges.RemoveAll(match: scheduled => (scheduled.Slot == slot));

        if (m_scheduledEdges.Count == 0) {
            m_scheduledEdgesPending = false;
        }

        if (m_slots.TryGetValue(
            key: slot,
            value: out var state
        )) {
            state.ChordConsumed.Clear();
            state.Latches.Clear();
            state.Tracker.Reset();
            Array.Clear(array: state.ArmedRows);

            foreach (var tracker in state.ActivatorTrackers) {
                tracker?.Reset();
            }

            state.PendingEdgeCount = 0;
            state.PageRowIndex = state.Profile.RestingRowOf(groupIndex: state.GroupIndex);
            Publish(state: state);
        }
    }
    /// <summary>Releases every slot this instance currently tracks — the all-slots twin of <see cref="Reset(int)"/>,
    /// wired to OS window focus loss (see <see cref="IInputBindings.ResetAll"/>).</summary>
    public void ResetAll() {
        m_scheduledEdges.Clear();
        m_scheduledEdgesPending = false;

        foreach (var slot in m_slots.Keys) {
            Reset(slot: slot);
        }
    }
    /// <inheritdoc/>
    public IReadOnlyList<CommandBinding>? Resolve(int slot, string source) {
        // The stateless view: the active page's table, with no tracker advance and no latch. Legacy callers
        // that only know a source id get the same answer a fresh press would.
        var state = StateFor(slot: slot);

        return (state.Profile.TableOf(rowIndex: state.PageRowIndex).TryGetValue(
            key: source,
            value: out var bindings
        )
            ? bindings
            : null
        );
    }
    /// <inheritdoc/>
    public IReadOnlyList<CommandBinding>? Resolve(int slot, in InputSignal signal) {
        var state = StateFor(slot: slot);
        var isDigitalReassertion = ((signal.Phase == CommandPhase.Active) && (signal.Value.Kind == CommandValueKind.Digital));

        if (
            state.Tracker.Apply(signal: signal) &&
            SyncChordState(
            isDigitalReassertion: isDigitalReassertion,
            state: state
        )
        ) {
            // This press completed a command chord: the chord owns it. Remember the source so its RELEASE is owned
            // too — a page binding that never took the press must not be handed the release.
            _ = state.ChordConsumed.Add(item: signal.Source);

            return null;
        }

        // The active page's ROW ACTIVATORS, evaluated regardless of whether this signal matches this page's
        // per-source table below — an activator's trigger is its own ordered sequence, not necessarily the signal
        // that happens to be resolving right now (a Tapped tracker in particular must see every signal to detect
        // wrong input; see RowActivatorTracker).
        // Activators are gestures, not held-state destinations. Reassertions may rebuild modifier/page state but
        // never advance or complete a Held/Tapped sequence without a real physical edge.
        if (!isDigitalReassertion) {
            ApplyRowActivators(
                signal: in signal,
                slot: slot,
                state: state
            );
        }

        if (signal.Phase is CommandPhase.Completed or CommandPhase.Canceled) {
            if (state.ChordConsumed.Remove(item: signal.Source)) {
                return null;
            }

            // A release resolves to whatever its press resolved to (see remarks), then the latch clears.
            if (state.Latches.Remove(
                key: signal.Source,
                value: out var latched
            )) {
                return latched;
            }
        }

        var resolved = ((state.Profile.TableOf(rowIndex: state.PageRowIndex).TryGetValue(
            key: signal.Source,
            value: out var bindings
        ))
            ? bindings
            : null
        );

        // A held source whose press the chord consumed keeps being consumed while it is down (a digital control
        // re-dispatches its hold every tick).
        if (state.ChordConsumed.Contains(item: signal.Source)) {
            return null;
        }

        if (
            ((signal.Phase == CommandPhase.Started) || isDigitalReassertion) &&
            (resolved is not null) &&
            !state.Latches.ContainsKey(key: signal.Source)
        ) {
            // The first real press owns the release mapping. After a reload/reset, the first reassertion establishes
            // the current mapping's release ownership; later repeats/page flips cannot overwrite either latch.
            state.Latches[signal.Source] = resolved;
        }

        return resolved;
    }
    /// <summary>Gets a group's resting page id in the currently-loaded compiled profile, or <see langword="null"/>
    /// when the profile declares no such group.</summary>
    /// <param name="group">The group name to look up.</param>
    public string? RestingPageIdOf(string group) => m_profile.RestingPageIdOf(group: group);
    /// <summary>Attempts to resolve a page's view by id in the currently-loaded compiled profile, independent of
    /// which page is currently active.</summary>
    /// <param name="pageId">The page id to look up.</param>
    /// <param name="view">The page's view, when found.</param>
    public bool TryGetPageView(string pageId, out BindingPageView view) => m_profile.TryGetPageView(
        pageId: pageId,
        view: out view
    );
    /// <summary>Sets a slot's active group — the runtime mode flip. A pointer-level switch on the compiled
    /// profile: the active page re-resolves in the new group against the same held modifiers, while the press
    /// latches, the chord tracker, and any armed command chords survive untouched (see remarks). The request is
    /// remembered per slot, so a later <see cref="Reload"/> re-applies it to the new profile.</summary>
    /// <param name="slot">The logical player slot.</param>
    /// <param name="group">The group name to activate, or <see langword="null"/> for the profile's default group.</param>
    /// <returns><see langword="false"/> when the profile declares no such group (the slot keeps its current group).</returns>
    public bool SetActiveGroup(int slot, string? group) {
        var profile = m_profile;
        var groupIndex = profile.DefaultGroupIndex;

        if (
            (group is not null) &&
            !profile.TryGetGroup(
            group: group,
            groupIndex: out groupIndex
        )
        ) {
            return false;
        }

        if (group is null) {
            _ = m_requestedGroups.TryRemove(
                key: slot,
                value: out _
            );
        } else {
            m_requestedGroups[slot] = group;
        }

        var state = StateFor(slot: slot);

        if (state.GroupIndex != groupIndex) {
            state.GroupIndex = groupIndex;
            ResolveAndPublishPage(state: state);
        }

        return true;
    }
    /// <summary>Gets the immutable view of the page a slot's active group and held chord currently select.</summary>
    /// <param name="slot">The logical player slot.</param>
    /// <returns>The active page's precomputed view.</returns>
    public BindingPageView ViewFor(int slot) {
        var profile = m_profile;

        return ((m_slots.TryGetValue(
            key: slot,
            value: out var state
        ) && ReferenceEquals(
            objA: state.Profile,
            objB: profile
        ))
            ? state.View
            : profile.ViewOf(rowIndex: profile.RestingRowOf(groupIndex: ResolveGroupIndex(
                profile: profile,
                slot: slot
            )))
        );
    }
    /// <summary>Gets the wheel the slot's active page presents, or <see langword="null"/> when the active page is
    /// no wheel's hold page — the radial presenter's one open/closed read (a slot holds a wheel open exactly while
    /// its held chord keeps the hold page selected, so this needs no state of its own).</summary>
    /// <param name="slot">The logical player slot.</param>
    /// <returns>The active wheel view, or <see langword="null"/>.</returns>
    public BindingWheelView? WheelFor(int slot) {
        var state = StateFor(slot: slot);

        return state.Profile.WheelOfRow(rowIndex: state.PageRowIndex);
    }
}
