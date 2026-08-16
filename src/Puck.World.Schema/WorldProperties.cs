using System.Text.Json.Serialization;

namespace Puck.World;

/// <summary>
/// The <c>properties</c> document section — an authored VOCABULARY of valid property names, the validated-vocabulary
/// pattern <c>WorldDefinitionValidator.ValidateGroups</c> already established for a group KIND name, reused here for
/// a carrier PROPERTY name. There is no built-in element enum: "hot", "cold", "wet", "charged" are CONTENT a world
/// authors, never a case this engine branches on.
/// </summary>
/// <remarks>
/// <para>A property is backed by a keyed <c>int</c> state row of the same name. The <c>state</c> section is
/// already the substrate's one per-carrier tag storage (see <c>WorldRules.cs</c>'s <c>$argmax:</c>/<c>$argmin:</c>
/// remarks — "author a keyed row whose cell keys are body indices"), so a property does not invent a second storage
/// kind: registering <c>hot</c> here requires a declared <c>state</c> row named <c>hot</c>, kind <c>int</c>, keyed
/// (<see cref="WorldStateRow.IsKeyed"/>) — its cells are the carriers (0-based body indices, spelled as plain
/// integers) that presently carry the tag, with a nonzero value meaning "on". Reading, writing, journaling, undoing,
/// and echoing a carrier's tag are therefore the ordinary <c>state</c> substrate (<c>world.state.cell.set</c>/
/// <c>.remove</c>, <c>world.state</c>) — nothing new to build there.</para>
/// <para><see cref="WorldInteraction"/>/<c>WorldRuleCompiler.CompileAllInteractions</c> validates an interaction's
/// <c>left</c>/<c>right</c> property reference against this list — refusing an unknown or typo'd name by name, at
/// the type — rather than against the state section directly.</para>
/// </remarks>
/// <param name="Names">The declared property vocabulary — unique, non-empty, each naming a declared keyed
/// <c>int</c> state row of the same name.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WorldPropertyRegistrySection(IReadOnlyList<string> Names) {
    /// <summary>Gets the empty section — every mutation composer's fallback for a document that declared no
    /// <c>properties</c> section at all (<c>current.Properties ?? Empty</c>, the identical <c>current.Rules ?? []</c>/
    /// <c>current.Groups ?? WorldGroupsSection.Empty</c> fallback the sibling optional sections already use).</summary>
    public static WorldPropertyRegistrySection Empty { get; } = new(Names: []);
}
/// <summary>Capacity constants for the property registry — a made-up, sensible fixture ceiling (this is a generic
/// engine primitive; a genre world authors its own property names, never a size drawn from a specific game's
/// vocabulary).</summary>
public static class WorldPropertyCapacity {
    /// <summary>The maximum declared property names a document may carry.</summary>
    public const int MaxProperties = 64;
}
