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
public sealed class PagedInputBindings : IInputBindings, IChordEdgeSource {
    // Requested group names by slot — kept OUTSIDE the slot states so a Reload (which drops every state) re-applies
    // each slot's mode to the new profile instead of silently falling back to the default group.
    private readonly ConcurrentDictionary<int, string> m_requestedGroups = new();
    private readonly ConcurrentDictionary<int, SlotState> m_slots = new();
    // A Tapped row activator's deferred release (see DrainScheduledEdges) — populated during THIS tick's signal
    // processing, drained on the NEXT tick's call, before it folds its own due signals. Small and short-lived (at
    // most one entry per completed tap awaiting its release), so a plain list needs no pooling.
    private readonly List<(int Slot, BindingChordEdge Edge)> m_scheduledEdges = [];
    private volatile CompiledBindingProfile m_profile;

    private sealed class SlotState {
        public required bool[] ArmedRows { get; init; }
        public required Dictionary<string, IReadOnlyList<CommandBinding>> Latches { get; init; }
        public required CompiledBindingProfile Profile { get; init; }
        public required BindingChordTracker Tracker { get; init; }
        // One RowActivatorTracker per compiled activator entry (CompiledActivatorEntry.ActivatorIndex), lazily
        // instantiated on first use. Sized once at slot creation — see CompiledBindingProfile.ActivatorCount.
        public required RowActivatorTracker?[] ActivatorTrackers { get; init; }

        public int GroupIndex;
        public int PageRowIndex;
        public BindingChordEdge[] PendingEdges;
        public int PendingEdgeCount;
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
            : null);
    }

    /// <inheritdoc/>
    public IReadOnlyList<CommandBinding>? Resolve(int slot, in InputSignal signal) {
        var state = StateFor(slot: slot);

        if (state.Tracker.Apply(signal: signal)) {
            SyncChordState(state: state);
        }

        // The active page's ROW ACTIVATORS, evaluated regardless of whether this signal matches this page's
        // per-source table below — an activator's trigger is its own ordered sequence, not necessarily the signal
        // that happens to be resolving right now (a Tapped tracker in particular must see every signal to detect
        // wrong input; see RowActivatorTracker).
        ApplyRowActivators(slot: slot, state: state, signal: in signal);

        if (signal.Phase is CommandPhase.Completed or CommandPhase.Canceled) {
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
            : null);

        if ((signal.Phase == CommandPhase.Started) && (resolved is not null)) {
            state.Latches[signal.Source] = resolved;
        }

        return resolved;
    }

    /// <inheritdoc/>
    public ReadOnlySpan<BindingChordEdge> DrainChordEdges(int slot) {
        if (!m_slots.TryGetValue(
            key: slot,
            value: out var state
        ) || (state.PendingEdgeCount == 0)) {
            return [];
        }

        var count = state.PendingEdgeCount;

        state.PendingEdgeCount = 0;

        return state.PendingEdges.AsSpan(start: 0, length: count);
    }

    /// <inheritdoc/>
    public IReadOnlyList<(int Slot, BindingChordEdge Edge)> DrainScheduledEdges() {
        if (m_scheduledEdges.Count == 0) {
            return [];
        }

        var due = m_scheduledEdges.ToArray();

        m_scheduledEdges.Clear();

        return due;
    }

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

        if ((group is not null) && !profile.TryGetGroup(
            group: group,
            groupIndex: out groupIndex
        )) {
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
            var previousPageRowIndex = state.PageRowIndex;

            state.GroupIndex = groupIndex;
            state.PageRowIndex = state.Profile.PageRowOf(groupIndex: groupIndex, heldOrder: state.Tracker.HeldOrder);

            // A page flip changes which row activators are IN SCOPE (see ApplyRowActivators) — abandon the
            // outgoing page's partial activator progress rather than let it complete silently after the player has
            // moved on.
            if (previousPageRowIndex != state.PageRowIndex) {
                ResetActivatorTrackers(state: state, pageRowIndex: previousPageRowIndex);
            }

            Publish(state: state);
        }

        return true;
    }

    /// <summary>Returns a value indicating whether the currently-loaded compiled profile declares <paramref name="group"/> — the probe a caller
    /// uses to validate a requested group without applying it (a context-derived override may currently shadow the
    /// request, so "apply and observe" cannot answer this).</summary>
    /// <param name="group">The group name to look up.</param>
    /// <returns><see langword="true"/> when the profile declares the group.</returns>
    public bool HasGroup(string group) {
        return m_profile.TryGetGroup(group: group, groupIndex: out _);
    }

    /// <summary>Gets the immutable view of the page a slot's active group and held chord currently select.</summary>
    /// <param name="slot">The logical player slot.</param>
    /// <returns>The active page's precomputed view.</returns>
    public BindingPageView ViewFor(int slot) {
        return ((m_slots.TryGetValue(
            key: slot,
            value: out var state
        ))
            ? state.View
            : m_profile.ViewOf(rowIndex: m_profile.RestingRowOf(groupIndex: ResolveGroupIndex(profile: m_profile, slot: slot))));
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

    /// <summary>Releases a slot's chord, press latches, and armed command chords — wired to focus loss (via
    /// <see cref="ResetAll"/>). Deliberately not wired to a single device's disconnect: <c>InputRouter</c>'s
    /// per-device release touches no binding state, because resetting a whole slot's chord tracker on one
    /// device's disconnect could wipe a different still-connected device's legitimately-held modifier on the same
    /// slot. Silent by design: the router's own held cancellation delivers the release edges.</summary>
    /// <param name="slot">The logical player slot.</param>
    public void Reset(int slot) {
        if (m_slots.TryGetValue(
            key: slot,
            value: out var state
        )) {
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
        foreach (var slot in m_slots.Keys) {
            Reset(slot: slot);
        }
    }

    /// <summary>Atomically swaps in a recompiled profile (an editor save), releasing every slot's chord and latches.
    /// Each slot's requested active group carries over — re-resolved against the new profile, falling back to its
    /// default group when the new profile no longer declares the name.</summary>
    /// <param name="profile">The compiled profile to resolve against from now on.</param>
    /// <exception cref="ArgumentNullException"><paramref name="profile"/> is <see langword="null"/>.</exception>
    public void Reload(CompiledBindingProfile profile) {
        ArgumentNullException.ThrowIfNull(profile);

        m_profile = profile;
        m_slots.Clear();
    }

    // Recompute a slot's chord-derived state after a tracker change: the deepest-page resolution, the published
    // view, and the command-row transition edges (releases of broken armed rows first, then fresh completions).
    private static void SyncChordState(SlotState state) {
        var held = state.Tracker.HeldOrder;
        var profile = state.Profile;

        for (var rowIndex = 0; (rowIndex < state.ArmedRows.Length); rowIndex++) {
            if (!state.ArmedRows[rowIndex]) {
                continue;
            }

            var row = profile.RowAt(rowIndex: rowIndex);

            if (!CompiledBindingProfile.IsPrefix(chord: row.Chord, heldOrder: held)) {
                state.ArmedRows[rowIndex] = false;
                AppendEdge(state: state, edge: new BindingChordEdge(
                    Command: row.Command!.Command,
                    Dispatch: row.Command.DispatchRelease,
                    Phase: CommandPhase.Completed,
                    Value: row.Command.ReleaseValue
                ));
            }
        }

        foreach (var rowIndex in profile.CommandRowsOf(groupIndex: state.GroupIndex)) {
            if (state.ArmedRows[rowIndex]) {
                continue;
            }

            var row = profile.RowAt(rowIndex: rowIndex);

            // A command chord fires on COMPLETION: the held order equals its chord exactly (a press only ever
            // appends to the held order, so completion is the exact-match moment).
            if (held.SequenceEqual(other: row.Chord)) {
                state.ArmedRows[rowIndex] = true;
                AppendEdge(state: state, edge: new BindingChordEdge(
                    Command: row.Command!.Command,
                    Dispatch: true,
                    Phase: CommandPhase.Started,
                    Value: row.Command.PressValue
                ));
            }
        }

        var previousPageRowIndex = state.PageRowIndex;

        state.PageRowIndex = profile.PageRowOf(groupIndex: state.GroupIndex, heldOrder: held);

        if (previousPageRowIndex != state.PageRowIndex) {
            ResetActivatorTrackers(state: state, pageRowIndex: previousPageRowIndex);
        }

        Publish(state: state);
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

        foreach (var activatorEntry in activators) {
            var tracker = (state.ActivatorTrackers[activatorEntry.ActivatorIndex] ??= new RowActivatorTracker(activator: activatorEntry.Activator));
            var transition = tracker.Apply(signal: in signal);

            switch (transition) {
                case RowActivatorTransition.Opened:
                    AppendEdge(state: state, edge: new BindingChordEdge(
                        Command: activatorEntry.Command,
                        Dispatch: true,
                        Phase: CommandPhase.Started,
                        Value: activatorEntry.PressValue
                    ));
                    break;
                case RowActivatorTransition.Closed:
                    AppendEdge(state: state, edge: new BindingChordEdge(
                        Command: activatorEntry.Command,
                        Dispatch: activatorEntry.DispatchRelease,
                        Phase: CommandPhase.Completed,
                        Value: activatorEntry.ReleaseValue
                    ));
                    break;
                case RowActivatorTransition.Completed:
                    AppendEdge(state: state, edge: new BindingChordEdge(
                        Command: activatorEntry.Command,
                        Dispatch: true,
                        Phase: CommandPhase.Started,
                        Value: activatorEntry.PressValue,
                        // MOMENTARY: its own release is already scheduled one tick below — marking THIS edge held
                        // too would make the tick the scheduled release lands on ALSO carry a stale, non-dispatching
                        // re-assertion of the press (harmless to a dispatch-gated reader, but not the clean single-
                        // entry pulse a tap is supposed to produce).
                        Momentary: true
                    ));
                    m_scheduledEdges.Add(item: (slot, new BindingChordEdge(
                        Command: activatorEntry.Command,
                        Dispatch: activatorEntry.DispatchRelease,
                        Phase: CommandPhase.Completed,
                        Value: activatorEntry.ReleaseValue
                    )));
                    break;
                case RowActivatorTransition.None:
                default:
                    break;
            }
        }
    }

    // Abandons a page's in-flight activator progress when it stops being the active page (see SyncChordState,
    // SetActiveGroup, Reset) — a partial Held/Tapped sequence must not silently complete after the player has
    // moved to a different page.
    private static void ResetActivatorTrackers(SlotState state, int pageRowIndex) {
        foreach (var activatorEntry in state.Profile.ActivatorsOf(rowIndex: pageRowIndex)) {
            state.ActivatorTrackers[activatorEntry.ActivatorIndex]?.Reset();
        }
    }
    private static void AppendEdge(SlotState state, in BindingChordEdge edge) {
        if (state.PendingEdgeCount == state.PendingEdges.Length) {
            Array.Resize(array: ref state.PendingEdges, newSize: (state.PendingEdges.Length * 2));
        }

        state.PendingEdges[state.PendingEdgeCount++] = edge;
    }
    private static void Publish(SlotState state) {
        state.View = state.Profile.ViewOf(rowIndex: state.PageRowIndex);
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
            : profile.DefaultGroupIndex);
    }
    private SlotState StateFor(int slot) {
        var profile = m_profile;

        if (m_slots.TryGetValue(
            key: slot,
            value: out var state
        ) && ReferenceEquals(objA: state.Profile, objB: profile)) {
            return state;
        }

        var groupIndex = ResolveGroupIndex(profile: profile, slot: slot);
        var created = new SlotState {
            ActivatorTrackers = new RowActivatorTracker?[profile.ActivatorCount],
            ArmedRows = new bool[profile.RowCount],
            GroupIndex = groupIndex,
            Latches = new Dictionary<string, IReadOnlyList<CommandBinding>>(comparer: StringComparer.OrdinalIgnoreCase),
            PageRowIndex = profile.RestingRowOf(groupIndex: groupIndex),
            Profile = profile,
            Tracker = new BindingChordTracker(profile: profile),
        };

        Publish(state: created);
        m_slots[slot] = created;

        return created;
    }
}
