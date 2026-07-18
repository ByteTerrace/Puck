using System.Collections.Concurrent;

namespace Puck.Commands;

/// <summary>
/// A stateful <see cref="IInputBindings"/> that resolves each signal against the page its slot's ACTIVE GROUP and
/// held modifier chord select, and fires the group's command-meaning chord rows as synthesized edges
/// (<see cref="IChordEdgeSource"/>). It sits exactly where a flat table sits today — inside the
/// <see cref="InputRouter"/>'s deterministic pre-snapshot fold — so recorded <see cref="CommandSnapshot"/>s
/// already contain chord-resolved commands and replay never re-resolves a binding.
/// </summary>
/// <remarks>
/// <para>The settled per-signal order when a signal drives a declared modifier: (1) the tracker advances (the
/// held order updates); (2) the active PAGE re-resolves — the deepest page row whose chord is a press-order
/// prefix of the held order (a page flip happens here); (3) chord-command transitions synthesize edges — first
/// the releases of broken armed rows, then the presses of rows the new held order completes exactly; (4) the
/// signal's own source lookup resolves against the post-flip page. So a page under a deeper command chord
/// (<c>[lt]</c> page beneath a <c>[lt, rt]</c> command) flips first and fires second, and the pass-through stays
/// coherent: sources keep answering through the deepest PAGE row while the command chord is held.</para>
/// <para>Two latches make transitions safe:</para>
/// <list type="bullet">
/// <item><description>A source press latches the binding list it resolved, and the matching release resolves to
/// that same list even if the page — or the ACTIVE GROUP — changed in between; a held action stays itself, new
/// presses use the new page. <see cref="SetActiveGroup"/> deliberately touches neither the latches nor the
/// tracker: a mode flip is a pointer-level switch.</description></item>
/// <item><description>A completed command chord stays ARMED until any member releases, regardless of page or
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
    private volatile CompiledBindingProfile m_profile;

    private sealed class SlotState {
        public required bool[] ArmedRows { get; init; }
        public required Dictionary<string, IReadOnlyList<CommandBinding>> Latches { get; init; }
        public required CompiledBindingProfile Profile { get; init; }
        public required BindingChordTracker Tracker { get; init; }

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

    /// <summary>Sets a slot's ACTIVE GROUP — the runtime mode flip. A pointer-level switch on the compiled
    /// profile: the active page re-resolves in the new group against the SAME held modifiers, while the press
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
            state.GroupIndex = groupIndex;
            state.PageRowIndex = state.Profile.PageRowOf(groupIndex: groupIndex, heldOrder: state.Tracker.HeldOrder);
            Publish(state: state);
        }

        return true;
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

    /// <summary>Releases a slot's chord, press latches, and armed command chords — wire to focus loss and device
    /// disconnect. Silent by design: the router's own held cancellation delivers the release edges.</summary>
    /// <param name="slot">The logical player slot.</param>
    public void Reset(int slot) {
        if (m_slots.TryGetValue(
            key: slot,
            value: out var state
        )) {
            state.Latches.Clear();
            state.Tracker.Reset();
            Array.Clear(array: state.ArmedRows);
            state.PendingEdgeCount = 0;
            state.PageRowIndex = state.Profile.RestingRowOf(groupIndex: state.GroupIndex);
            Publish(state: state);
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

        state.PageRowIndex = profile.PageRowOf(groupIndex: state.GroupIndex, heldOrder: held);
        Publish(state: state);
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
