namespace Puck.World;

/// <summary>
/// The engine's published registry of context FAMILIES — the closed vocabulary a binding document's <c>contexts</c>
/// rows (<see cref="Puck.Commands.BindingContextDefinition"/>) are validated against. A family is admitted ONLY when
/// it is the output of a single per-seat state machine holding exactly one value at a time, so within-family
/// exclusivity is a property of where the value comes from, never an authoring convention. Two families are admitted:
/// <see cref="Roster"/> (the participant lifecycle the client roster's joined/claimed/pending booleans already
/// encode, made one value) and <see cref="Engagement"/> (a read over the server grant table's single-valued Control
/// route — <c>WorldGrants.SetControlRoute</c> drops any prior route). "Editor-ness" is deliberately NOT a family:
/// editor/sculpt are the seat's requested-group pointer itself — the mode context rows override — not states beside
/// it. States carry no expressions: the moment these carry logic, the document has grown a programming language.
/// </summary>
internal static class WorldContextFamilies {
    /// <summary>The roster family: a seat's participant-lifecycle state.</summary>
    public const string Roster = "roster";

    /// <summary>The roster state of a slot with no participant.</summary>
    public const string RosterUnjoined = "unjoined";
    /// <summary>The roster state of a slot under an exclusive programmatic claim (<c>PlayerRoster.TryClaimSlot</c>) —
    /// checked before pending/active because the claim overrides the participant's own lifecycle for gestures.</summary>
    public const string RosterClaimed = "claimed";
    /// <summary>The roster state of a joined participant still choosing a profile.</summary>
    public const string RosterPending = "pending";
    /// <summary>The roster state of a joined, confirmed participant. Deliberately ships with no default context row:
    /// active is the state where the seat's requested group (the mode) owns the seat.</summary>
    public const string RosterActive = "active";

    /// <summary>The engagement family: whether the seat's acting principal holds a Control route over a screen.</summary>
    public const string Engagement = "engagement";

    /// <summary>The engagement state while the seat's principal holds a Control route (<c>IWorldGrantsView.ControlRoute</c>
    /// answers a screen).</summary>
    public const string EngagementEngaged = "engaged";
    /// <summary>The engagement state while the seat's principal holds no Control route.</summary>
    public const string EngagementNone = "none";

    private static readonly string[] s_rosterStates = [RosterUnjoined, RosterClaimed, RosterPending, RosterActive];
    private static readonly string[] s_engagementStates = [EngagementEngaged, EngagementNone];

    /// <summary>The admitted family names, in the order the derivation read-back reports them.</summary>
    public static readonly IReadOnlyList<string> Families = [Roster, Engagement];

    /// <summary>The states <paramref name="family"/> publishes, or <see langword="null"/> when the family is not
    /// admitted.</summary>
    /// <param name="family">The family name to look up (ordinal).</param>
    public static IReadOnlyList<string>? StatesOf(string family) {
        return family switch {
            Roster => s_rosterStates,
            Engagement => s_engagementStates,
            _ => null,
        };
    }
}
