namespace Puck.World;

/// <summary>
/// One row of the <c>references</c> section — a world NAMING another world by document path, the primitive a
/// wall-mounted portal placement (or any future consumer) resolves against. Authored data only: a row asserts
/// nothing about the named document's existence or shape at boot — resolution is a consumer's job, not this
/// section's (no boot-time file-existence check).
/// </summary>
/// <param name="Name">The reference's own name — <see cref="WorldSafeName"/>-shaped, unique within the section.</param>
/// <param name="Document">The referenced world's document path (e.g. <c>"dive.world.json"</c>), authored verbatim.</param>
public sealed record WorldReference(WorldSafeName Name, string Document);
