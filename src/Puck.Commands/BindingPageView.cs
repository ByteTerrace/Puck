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
/// This is the lookup a presentation layer joining physical controls against the page should read — a bar with a
/// socket per placed control asks once per socket per bank per seat per frame, and answering that by scanning
/// <paramref name="Buttons"/> walks every row's own <see cref="BindingPageButtonView.Sources"/> list in turn for an
/// answer the compiler can table once. Entries triggered by an activator name no source and so appear only in
/// <paramref name="Buttons"/>.</param>
/// <param name="Modifiers">Every modifier the profile declares, flagged with whether this page's row requires it.</param>
/// <param name="CommandChords">The command-meaning chord rows of this page's group, in profile order — the hints a
/// binding bar renders so a player can discover a chord-fired act (a group that binds one; a group whose chords are
/// all pages carries none).</param>
/// <param name="Openers">Each input source id that, held on top of this page's own modifiers, selects a deeper page of
/// the group, mapped to that page (<c>OrdinalIgnoreCase</c>; the first page in profile order wins a source two pages
/// open). A binding bar reads it for a control this page leaves unbound, so a plate whose job is to open a page
/// (a shoulder that opens the play wheel) still shows what it does. <see langword="null"/> reads as no openers.</param>
public sealed record BindingPageView(
    string PageId,
    string Group,
    string? Label,
    string? Icon,
    IReadOnlyList<BindingPageButtonView> Buttons,
    FrozenDictionary<string, BindingPageButtonView> ButtonsBySource,
    IReadOnlyList<BindingModifierView> Modifiers,
    IReadOnlyList<BindingChordCommandView> CommandChords,
    FrozenDictionary<string, BindingPageOpenerView>? Openers = null
);
/// <summary>The page a held control opens, as the UI presents it.</summary>
/// <param name="PageId">The profile-unique identifier of the page the control opens.</param>
/// <param name="Label">The opened page's display label, if any; opaque to the engine.</param>
/// <param name="Icon">The opened page's display icon id, if any; opaque to the engine.</param>
public sealed record BindingPageOpenerView(
    string PageId,
    string? Label,
    string? Icon
);
/// <summary>One bound source as the UI presents it.</summary>
/// <param name="Source">The row's trigger label — its source ids comma-joined, or an activator label. A DISPLAY
/// string: it names the whole row, so it never identifies one physical control. Ask
/// <see cref="BindingPageView.ButtonsBySource"/> to answer "is this control bound here".</param>
/// <param name="Sources">The row's input source ids, individually — the keys this row is reachable under in
/// <see cref="BindingPageView.ButtonsBySource"/>. Empty for an activator row, whose trigger is a sequence rather
/// than a set of sources.</param>
/// <param name="Command">The ROUTED command name the source activates on this page — for a channel row this is the
/// routing ordinal name the engine mints per channel, which no author writes.</param>
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
/// <param name="Required">Whether the page's own row requires this modifier to be held — either as an unordered
/// <c>held</c> member or as a member of its ordered <c>chord</c>. Both lists must be down for the page to be the
/// selected one, so both mark their modifiers required.</param>
/// <param name="Declared">Whether the profile's own <c>modifiers</c> section declares this modifier, rather than the
/// compiler minting it for a raw source a chord or held row names.</param>
public sealed record BindingModifierView(
    string Id,
    IReadOnlyList<string> Sources,
    string? Label,
    string? Icon,
    bool Required,
    bool Declared = true
);
/// <summary>One command-meaning row as the UI presents it — a binding bar's chord hint.</summary>
/// <param name="Chord">The modifier ids that must have been pressed in this order.</param>
/// <param name="Sources">The members' input source ids — <paramref name="Held"/> then <paramref name="Chord"/> (glyph resolution).</param>
/// <param name="Command">The name of the command the chord fires.</param>
/// <param name="Label">The row's display label, if any; opaque to the engine.</param>
/// <param name="Icon">The row's display icon id, if any; opaque to the engine.</param>
/// <param name="HoldRelease">Whether the command dispatches on both edges (see <see cref="BindingCommandDefinition.HoldRelease"/>).</param>
/// <param name="Held">The modifier ids that must be down, in any order.</param>
/// <param name="Action">The AUTHORED action the row names — its <c>command</c>, else its named <c>channel</c> — the key a
/// binding bar's icon row looks the row up by, as it looks a page entry up by its action.</param>
public sealed record BindingChordCommandView(
    IReadOnlyList<string> Chord,
    IReadOnlyList<string> Sources,
    string Command,
    string? Label,
    string? Icon,
    bool HoldRelease,
    IReadOnlyList<string>? Held = null,
    string? Action = null
);
