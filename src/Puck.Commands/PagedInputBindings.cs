using System.Collections.Concurrent;

namespace Puck.Commands;

/// <summary>
/// A stateful <see cref="IInputBindings"/> that resolves each signal against the profile page its slot's held
/// modifier chord selects. It sits exactly where a flat table sits today — inside the <see cref="InputRouter"/>'s
/// deterministic pre-snapshot fold — so recorded <see cref="CommandSnapshot"/>s already contain page-resolved
/// commands and replay never re-resolves a binding.
/// </summary>
/// <remarks>
/// Two behaviors beyond a plain per-page lookup:
/// <list type="bullet">
/// <item><description>A modifier source advances the slot's chord tracker before lookup, so the very signal that
/// crosses a threshold selects the page for everything after it in capture order. A modifier source may itself
/// carry page entries; most profiles leave it unbound.</description></item>
/// <item><description>A press latches the binding list it resolved, and the matching release resolves to that
/// same list even if the page changed in between — otherwise the router's held bookkeeping (which clears by
/// command id) would leak a held command whenever a modifier lifted before the button. A held action stays
/// itself; new presses use the new page.</description></item>
/// </list>
/// All state mutates on the router's single snapshot thread; only the published <see cref="BindingPageView"/>
/// reference crosses threads (the render-side UI reads it via <see cref="ViewFor"/>).
/// </remarks>
public sealed class PagedInputBindings : IInputBindings {
    private readonly ConcurrentDictionary<int, SlotState> m_slots = new();
    private volatile CompiledBindingProfile m_profile;

    private sealed class SlotState {
        public required Dictionary<string, IReadOnlyList<CommandBinding>> Latches { get; init; }
        public required CompiledBindingProfile Profile { get; init; }
        public required BindingChordTracker Tracker { get; init; }
        public volatile BindingPageView View;

        public SlotState() {
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

        return (state.Profile.TableOf(pageIndex: state.Tracker.ActivePageIndex).TryGetValue(
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
            Publish(state: state);
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

        var resolved = ((state.Profile.TableOf(pageIndex: state.Tracker.ActivePageIndex).TryGetValue(
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

    /// <summary>Gets the immutable view of the page a slot's held chord currently selects.</summary>
    /// <param name="slot">The logical player slot.</param>
    /// <returns>The active page's precomputed view.</returns>
    public BindingPageView ViewFor(int slot) {
        return ((m_slots.TryGetValue(
            key: slot,
            value: out var state
        ))
            ? state.View
            : m_profile.ViewOf(pageIndex: m_profile.BasePageIndex));
    }

    /// <summary>Releases a slot's chord and press latches — wire to focus loss and device disconnect.</summary>
    /// <param name="slot">The logical player slot.</param>
    public void Reset(int slot) {
        if (m_slots.TryGetValue(
            key: slot,
            value: out var state
        )) {
            state.Latches.Clear();
            state.Tracker.Reset();
            Publish(state: state);
        }
    }

    /// <summary>Atomically swaps in a recompiled profile (an editor save), releasing every slot's chord and latches.</summary>
    /// <param name="profile">The compiled profile to resolve against from now on.</param>
    /// <exception cref="ArgumentNullException"><paramref name="profile"/> is <see langword="null"/>.</exception>
    public void Reload(CompiledBindingProfile profile) {
        ArgumentNullException.ThrowIfNull(profile);

        m_profile = profile;
        m_slots.Clear();
    }

    private static void Publish(SlotState state) {
        state.View = state.Profile.ViewOf(pageIndex: state.Tracker.ActivePageIndex);
    }

    private SlotState StateFor(int slot) {
        var profile = m_profile;

        if (m_slots.TryGetValue(
            key: slot,
            value: out var state
        ) && ReferenceEquals(objA: state.Profile, objB: profile)) {
            return state;
        }

        var created = new SlotState {
            Latches = new Dictionary<string, IReadOnlyList<CommandBinding>>(comparer: StringComparer.OrdinalIgnoreCase),
            Profile = profile,
            Tracker = new BindingChordTracker(profile: profile),
        };

        Publish(state: created);
        m_slots[slot] = created;

        return created;
    }
}
