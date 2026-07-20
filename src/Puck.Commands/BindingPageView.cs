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
/// <param name="Source">The provider-neutral input source id.</param>
/// <param name="Command">The name of the command the source activates on this page.</param>
/// <param name="Label">The binding's display label, if any; opaque to the engine.</param>
/// <param name="Icon">The binding's display icon id, if any; opaque to the engine.</param>
public sealed record BindingPageButtonView(
    string Source,
    string Command,
    string? Label,
    string? Icon
);

/// <summary>One declared modifier as the UI presents it.</summary>
/// <param name="Id">The modifier's profile-unique identifier.</param>
/// <param name="Source">The provider-neutral input source id that drives the modifier.</param>
/// <param name="Label">The modifier's display label, if any; opaque to the engine.</param>
/// <param name="Icon">The modifier's display icon id, if any; opaque to the engine.</param>
/// <param name="Required">Whether the page's chord requires this modifier to be held.</param>
public sealed record BindingModifierView(
    string Id,
    string Source,
    string? Label,
    string? Icon,
    bool Required
);

/// <summary>One command-meaning chord row as the UI presents it — a binding bar's chord hint.</summary>
/// <param name="Chord">The ordered modifier ids that fire the command, in press order.</param>
/// <param name="Sources">The chord members' input source ids, parallel to <paramref name="Chord"/> (glyph resolution).</param>
/// <param name="Command">The name of the command the chord fires.</param>
/// <param name="Label">The row's display label, if any; opaque to the engine.</param>
/// <param name="Icon">The row's display icon id, if any; opaque to the engine.</param>
/// <param name="HoldRelease">Whether the command dispatches on both edges (see <see cref="BindingCommandDefinition.HoldRelease"/>).</param>
public sealed record BindingChordCommandView(
    IReadOnlyList<string> Chord,
    IReadOnlyList<string> Sources,
    string Command,
    string? Label,
    string? Icon,
    bool HoldRelease
);
