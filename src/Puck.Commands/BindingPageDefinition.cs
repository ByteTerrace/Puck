namespace Puck.Commands;

/// <summary>
/// One binding page: a complete <c>source → command</c> table selected by a modifier chord. The chord is the
/// ORDERED sequence of held modifier ids — <c>["left", "right"]</c> and <c>["right", "left"]</c> are distinct
/// pages — and the empty chord is the no-modifier page (the movement/contextual baseline).
/// </summary>
/// <param name="Id">The profile-unique identifier of the page (e.g. <c>movement</c>).</param>
/// <param name="Chord">The ordered <see cref="BindingModifierDefinition.Id"/>s that must be held, in press order; empty for the no-modifier page.</param>
/// <param name="Entries">The bindings active while this page is selected.</param>
/// <param name="Label">An optional display label for the UI layer; opaque to the engine.</param>
/// <param name="Icon">An optional display icon id for the UI layer; opaque to the engine.</param>
public sealed record BindingPageDefinition(
    string Id,
    IReadOnlyList<string> Chord,
    IReadOnlyList<BindingPageEntryDefinition> Entries,
    string? Label = null,
    string? Icon = null
);
