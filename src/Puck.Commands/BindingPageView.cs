using System.Collections.Frozen;

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
/// <param name="ButtonsBySource">Each source id the page binds mapped to the <paramref name="Buttons"/> entry that
/// source triggers (<c>OrdinalIgnoreCase</c>; first entry in profile order wins a source two entries both name).
/// This is the lookup a presentation layer joining physical controls against the page should read — a bar with
/// twelve sockets asks once per socket per frame, and scanning <paramref name="Buttons"/> instead both cost a
/// linear pass and MISSED every multi-source entry, whose <see cref="BindingPageButtonView.Source"/> is the row's
/// comma-joined trigger label rather than any one source id. Entries triggered by an activator name no source and
/// so appear only in <paramref name="Buttons"/>.</param>
/// <param name="Modifiers">Every modifier the profile declares, flagged with whether this page's row requires it.</param>
/// <param name="CommandChords">The command-meaning chord rows of this page's group, in profile order — the hints a
/// binding bar renders so a player can discover a chord-fired act (a group that binds one; a group whose chords are
/// all pages carries none).</param>
public sealed record BindingPageView(
    string PageId,
    string Group,
    string? Label,
    string? Icon,
    IReadOnlyList<BindingPageButtonView> Buttons,
    FrozenDictionary<string, BindingPageButtonView> ButtonsBySource,
    IReadOnlyList<BindingModifierView> Modifiers,
    IReadOnlyList<BindingChordCommandView> CommandChords
);
/// <summary>One bound source as the UI presents it.</summary>
/// <param name="Source">The row's trigger LABEL — its source ids comma-joined, or an activator label. A display
/// string, not a key: match a physical control through <see cref="BindingPageView.ButtonsBySource"/> instead.</param>
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
/// <param name="Sources">The provider-neutral input source ids that drive the modifier.</param>
/// <param name="Label">The modifier's display label, if any; opaque to the engine.</param>
/// <param name="Icon">The modifier's display icon id, if any; opaque to the engine.</param>
/// <param name="Required">Whether the page's own row requires this modifier to be held — either as an unordered
/// <c>held</c> member or as a member of its ordered <c>chord</c>. Both lists must be down for the page to be the
/// selected one, so both mark their modifiers required.</param>
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
