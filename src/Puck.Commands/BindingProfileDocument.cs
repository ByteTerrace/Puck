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
    /// <summary>The schema version this engine build authors and accepts. A stored profile whose version differs must
    /// reseed: an older binding like <c>arcade.leave</c> does not match the current <c>overworld.*</c> command
    /// vocabulary, so its Left-bumper disengage would silently break.</summary>
    public const string CurrentVersion = "puck.bindings.v7";
}
