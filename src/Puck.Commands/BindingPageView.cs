namespace Puck.Commands;

/// <summary>
/// The immutable, UI-facing snapshot of one binding page: what each source is bound to, the display metadata the
/// profile carried, the group the page belongs to, and the group's command-chord hints. Every page's view is
/// precomputed by <see cref="BindingProfile.Compile"/>, so reading the active view
/// (<see cref="PagedInputBindings.ViewFor"/>) is a single reference read — zero allocation per frame.
/// </summary>
/// <param name="PageId">The profile-unique identifier of the page.</param>
/// <param name="Group">The page-group the page belongs to (the seat's active group while this view is live).</param>
/// <param name="Label">The page's display label, if any; opaque to the engine.</param>
/// <param name="Icon">The page's display icon id, if any; opaque to the engine.</param>
/// <param name="Buttons">The page's bindings, in profile order.</param>
/// <param name="Modifiers">Every modifier the profile declares, flagged with whether this page's chord requires it.</param>
/// <param name="CommandChords">The command-meaning chord rows of this page's group, in profile order — the hints a
/// binding bar renders so a player can discover a chord-fired act (a group that binds one; a group whose chords are
/// all pages carries none).</param>
public sealed record BindingPageView(
    string PageId,
    string Group,
    string? Label,
    string? Icon,
    IReadOnlyList<BindingPageButtonView> Buttons,
    IReadOnlyList<BindingModifierView> Modifiers,
    IReadOnlyList<BindingChordCommandView> CommandChords
);
/// <summary>One bound source as the UI presents it.</summary>
/// <param name="Source">The row's trigger label — its source ids comma-joined, or an activator label. A DISPLAY
/// string: it names the whole row, so it never identifies one physical control. Match <paramref name="Sources"/>
/// to answer "is this control bound here".</param>
/// <param name="Sources">The row's input source ids, individually — the lookup key a per-control consumer (the
/// binding bar, which places ONE plate per physical control) needs. Empty for an activator row, whose trigger is a
/// sequence rather than a set of sources.</param>
/// <param name="Command">The ROUTED command name the source activates on this page — for a channel row this is the
/// routing ordinal (<c>channel.ordinal.N</c>), an engine-internal name no author writes.</param>
/// <param name="Action">The AUTHORED action name this row names — its <c>command</c>, else its <c>channel</c>.</param>
/// <param name="Id">The row's authored identity, if any (<see cref="BindingPageEntryDefinition.Id"/>).</param>
/// <param name="Toggle">Whether the row latches (<see cref="BindingEntryMode.Toggle"/>): its command stays held
/// after the press, so its held state is a fact about the seat, not about which page is live.</param>
/// <remarks>A presentation surface keys a row by <see cref="Id"/> when it has one, else by <see cref="Action"/> — the
/// same rule a wheel sector follows — so the row carries no presentation, and two rows sharing a command stay
/// distinguishable. <see cref="Key"/> is that rule, written once.</remarks>
/// <param name="Label">The binding's display label, if any; opaque to the engine.</param>
public sealed record BindingPageButtonView(
    string Source,
    IReadOnlyList<string> Sources,
    string Command,
    string? Action,
    string? Id,
    string? Label,
    bool Toggle = false
) {
    /// <summary>Gets the key a presentation surface looks this row up by: <see cref="Id"/>, else <see cref="Action"/>.</summary>
    public string? Key => (Id ?? Action);
}
/// <summary>One declared modifier as the UI presents it.</summary>
/// <param name="Id">The modifier's profile-unique identifier.</param>
/// <param name="Sources">The provider-neutral input source ids that drive the modifier.</param>
/// <param name="Label">The modifier's display label, if any; opaque to the engine.</param>
/// <param name="Icon">The modifier's display icon id, if any; opaque to the engine.</param>
/// <param name="Required">Whether the page's chord requires this modifier to be held.</param>
public sealed record BindingModifierView(
    string Id,
    IReadOnlyList<string> Sources,
    string? Label,
    string? Icon,
    bool Required
);
/// <summary>One command-meaning row as the UI presents it — a binding bar's chord hint.</summary>
/// <param name="Chord">The modifier ids that must have been pressed in this order.</param>
/// <param name="Sources">The members' input source ids — <paramref name="Held"/> then <paramref name="Chord"/> (glyph resolution).</param>
/// <param name="Command">The name of the command the chord fires.</param>
/// <param name="Label">The row's display label, if any; opaque to the engine.</param>
/// <param name="Icon">The row's display icon id, if any; opaque to the engine.</param>
/// <param name="HoldRelease">Whether the command dispatches on both edges (see <see cref="BindingCommandDefinition.HoldRelease"/>).</param>
/// <param name="Held">The modifier ids that must be down, in any order.</param>
public sealed record BindingChordCommandView(
    IReadOnlyList<string> Chord,
    IReadOnlyList<string> Sources,
    string Command,
    string? Label,
    string? Icon,
    bool HoldRelease,
    IReadOnlyList<string>? Held = null
);
