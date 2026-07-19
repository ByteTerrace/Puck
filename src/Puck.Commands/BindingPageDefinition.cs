namespace Puck.Commands;

/// <summary>
/// The page meaning of a <see cref="BindingChordDefinition"/>: a complete <c>source → command</c> table active
/// while the owning chord row is the deepest held page row of its group. The chord and group live on the chord
/// row; this payload is the table plus its display identity.
/// </summary>
/// <param name="Id">The profile-unique identifier of the page (e.g. <c>base</c>, <c>editor-camera</c>).</param>
/// <param name="Entries">The bindings active while this page is selected.</param>
/// <param name="Label">An optional display label for the UI layer; opaque to the engine.</param>
/// <param name="Icon">An optional display icon id for the UI layer; opaque to the engine.</param>
public sealed record BindingPageDefinition(
    string Id,
    IReadOnlyList<BindingPageEntryDefinition> Entries,
    string? Label = null,
    string? Icon = null
);
