namespace Puck.World;

/// <summary>
/// The engine's published registry of built-in context FAMILIES. Built-in families are outputs
/// of one per-seat state machine holding exactly one value at a time: <see cref="Roster"/>, <see cref="Engagement"/>,
/// and <see cref="Editor"/>. A <c>state:&lt;row&gt;</c> family is data-driven instead: it publishes a declared world-state
/// row through <see cref="WorldStateBindingContext"/>, allowing ordinary gameplay-rule state writes to select a
/// control group without adding another engine mode. Context rows carry no expressions; they only compare one
/// published value with one authored state token.
/// </summary>
public static class WorldContextFamilies {
    /// <summary>The editor family: the seat's editor-session state, published by the session itself.</summary>
    public const string Editor = "editor";
    /// <summary>The editor state while the seat's session is active and the sculpt bench is closed.</summary>
    public const string EditorEditing = "editing";
    /// <summary>The editor state while the seat has no session.</summary>
    public const string EditorNone = "none";
    /// <summary>The editor state while the seat's sculpt bench is open.</summary>
    public const string EditorSculpting = "sculpting";
    /// <summary>The engagement family: whether the seat's acting principal holds a Control route over a screen.</summary>
    public const string Engagement = "engagement";
    /// <summary>The engagement state while the seat's principal holds a Control route (<c>IWorldGrantsView.ControlRoute</c>
    /// answers a screen).</summary>
    public const string EngagementEngaged = "engaged";
    /// <summary>The engagement state while the seat's principal holds no Control route.</summary>
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
    private static readonly string[] EditorStates = [EditorNone, EditorEditing, EditorSculpting];

    /// <summary>The admitted family names, in the order the derivation read-back reports them.</summary>
    public static readonly IReadOnlyList<string> Families = [Roster, Engagement, Editor];

    /// <summary>The fixed states a built-in <paramref name="family"/> publishes, or <see langword="null"/> for a
    /// state-backed or unknown family.</summary>
    /// <param name="family">The family name to look up (ordinal).</param>
    public static IReadOnlyList<string>? StatesOf(string family) {
        return family switch {
            Roster => RosterStates,
            Engagement => EngagementStates,
            Editor => EditorStates,
            _ => null,
        };
    }
}
