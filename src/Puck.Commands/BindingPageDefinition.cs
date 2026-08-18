using System.Text.Json.Serialization;

namespace Puck.Commands;

/// <summary>
/// The page meaning of a <see cref="BindingChordDefinition"/>: a <c>source → command</c> table active while the
/// owning chord row is the deepest held page row of its group. A page may inherit another page in the same group;
/// compilation flattens the tables once, with this page replacing inherited entries source by source. The chord
/// and group live on the chord row; this payload is the table plus its display identity.
/// </summary>
/// <param name="Id">The profile-unique identifier of the page (e.g. <c>base</c>, <c>editor-camera</c>).</param>
/// <param name="Entries">The bindings active while this page is selected.</param>
/// <param name="Label">An optional display label for the UI layer; opaque to the engine.</param>
/// <param name="Icon">An optional display icon id for the UI layer; opaque to the engine.</param>
/// <param name="Inherits">The profile-unique id of another page in the same group whose entries provide this
/// page's fallback table, or <see langword="null"/> for no inheritance.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record BindingPageDefinition(
    string Id,
    IReadOnlyList<BindingPageEntryDefinition> Entries,
    string? Label = null,
    string? Icon = null,
    string? Inherits = null
);
