namespace Puck.Commands;

/// <summary>
/// The serializable root of a binding profile: the modifiers that select pages and the pages themselves.
/// This document is the single source of truth for a player's controller mapping — it loads straight from
/// JSON, compiles via <see cref="BindingProfile.Compile"/>, and an editor round-trips it back to storage.
/// </summary>
/// <param name="Version">The document schema version; currently <see cref="CurrentVersion"/>.</param>
/// <param name="Modifiers">The modifier declarations pages chord on.</param>
/// <param name="Pages">The binding pages; exactly one must carry the empty chord.</param>
public sealed record BindingProfileDocument(
    string Version,
    IReadOnlyList<BindingModifierDefinition> Modifiers,
    IReadOnlyList<BindingPageDefinition> Pages
) {
    /// <summary>The schema version this engine build authors and accepts. v7: the command vocabulary was renamed
    /// arcade.* → overworld.* — a stored v6 profile must reseed, or its Left-bumper would still bind the old
    /// <c>arcade.leave</c> (which no longer matches), silently breaking disengage. v6: the movement page gained the
    /// Left-bumper disengage that returns a seated player to free room movement.</summary>
    public const string CurrentVersion = "puck.bindings.v7";
}
