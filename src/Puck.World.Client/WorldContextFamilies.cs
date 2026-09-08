namespace Puck.World;

/// <summary>
/// The engine's published registry of built-in context FAMILIES. Built-in families are outputs
/// of one per-seat state machine holding exactly one value at a time: <see cref="Roster"/>, <see cref="Engagement"/>,
/// and <see cref="Layout"/>. A <c>state:&lt;row&gt;</c> family is data-driven instead: it publishes a
/// declared world-state row through <see cref="WorldStateBindingContext"/>, allowing ordinary gameplay-rule state
/// writes to select a control group without adding another engine mode. A world may additionally AUTHOR its own
/// per-seat mode families (<see cref="WorldSeatModeFamily"/>), flipped by the generic <c>player.mode</c> verb — the
/// engine holds no built-in "editing" family; a world that wants one declares it.
/// Context rows carry no expressions; they only compare one published value with one authored state token.
/// </summary>
public static class WorldContextFamilies {
    /// <summary>The layout family: the window composer's active layout selection — an authored
    /// <c>views.layouts</c> row's name, or <see cref="LayoutBuiltin"/> for the built-in seat ladder. Its states are
    /// OPEN (authored layout names), so <see cref="StatesOf"/> answers <see langword="null"/> and admission rides
    /// <see cref="IsOpenStates"/>; a context row naming a layout the world never authors simply never matches.</summary>
    public const string Layout = "layout";
    /// <summary>The layout state while the built-in seat ladder composes the window.</summary>
    public const string LayoutBuiltin = "builtin";
    /// <summary>The engagement family: whether the seat's acting principal has composed any control application
    /// beyond its own body.</summary>
    public const string Engagement = "engagement";
    /// <summary>The engagement state while the seat's principal's <c>IWorldGrantsView.Applications</c> set names any
    /// target other than its own body.</summary>
    public const string EngagementEngaged = "engaged";
    /// <summary>The engagement state while the seat's principal holds only its own-body application.</summary>
    public const string EngagementNone = "none";
    /// <summary>The roster family: a seat's participant-lifecycle state.</summary>
    public const string Roster = "roster";
    /// <summary>The roster state of a joined, confirmed participant. Deliberately ships with no default context row:
    /// active is the state where the seat's requested group (the mode) owns the seat.</summary>
    public const string RosterActive = "active";
    /// <summary>The roster state of a slot under an exclusive programmatic claim (<c>PlayerRoster.TryClaimSlot</c>) —
    /// checked before pending/active because the claim overrides the participant's own lifecycle for gestures.</summary>
    public const string RosterClaimed = "claimed";
    /// <summary>The roster state of a joined participant still choosing a profile.</summary>
    public const string RosterPending = "pending";
    /// <summary>The roster state of a slot with no participant.</summary>
    public const string RosterUnjoined = "unjoined";

    private static readonly string[] RosterStates = [RosterUnjoined, RosterClaimed, RosterPending, RosterActive];
    private static readonly string[] EngagementStates = [EngagementEngaged, EngagementNone];

    /// <summary>The built-in admitted family names, in the order the derivation read-back reports them — an
    /// authored <see cref="WorldSeatModeFamily"/>'s name is never one of these (the validator refuses the
    /// collision).</summary>
    public static readonly IReadOnlyList<string> Families = [Roster, Engagement, Layout];

    /// <summary>Whether <paramref name="family"/> is a built-in family whose state set is open (world-authored
    /// values rather than a fixed engine list) — admitted everywhere a closed family is, with no state-token
    /// check.</summary>
    /// <param name="family">The family name to look up (ordinal).</param>
    public static bool IsOpenStates(string family) => string.Equals(
        a: family,
        b: Layout,
        comparisonType: StringComparison.Ordinal
    );
    /// <summary>The fixed states a built-in <paramref name="family"/> publishes, or <see langword="null"/> for an
    /// open-states (<see cref="IsOpenStates"/>), state-backed, or unknown family.</summary>
    /// <param name="family">The family name to look up (ordinal).</param>
    public static IReadOnlyList<string>? StatesOf(string family) {
        return family switch {
            Roster => RosterStates,
            Engagement => EngagementStates,
            _ => null,
        };
    }
}
