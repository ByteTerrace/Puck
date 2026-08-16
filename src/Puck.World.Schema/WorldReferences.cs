using System.Text.Json.Serialization;

namespace Puck.World;

/// <summary>
/// One row of the <c>references</c> section — a world NAMING another world, the primitive a wall-mounted portal
/// placement (or any future consumer) resolves against. Authored data only: a row asserts nothing about the named
/// world's existence or shape at boot — resolution is a consumer's job, not this section's (no boot-time
/// existence check). Names its target through exactly one of two mutually exclusive spellings — a local document
/// path (<see cref="Document"/>), or a remote owner-named world (<see cref="Owner"/>+<see cref="World"/> together,
/// since worlds ARE users) — never both, never neither; <see cref="WorldDefinitionValidator.ValidateReferences"/>
/// refuses any other shape. <see cref="NeighbourKey"/> folds whichever spelling was authored into the one opaque
/// string every <see cref="IWorldNeighbourResolver"/> call site already passes around.
/// </summary>
/// <param name="Name">The reference's own name — <see cref="WorldSafeName"/>-shaped, unique within the section.</param>
/// <param name="Document">The referenced world's document path (e.g. <c>"dive.world.json"</c>), authored verbatim.
/// Mutually exclusive with <see cref="Owner"/>/<see cref="World"/>.</param>
/// <param name="Owner">The remote world's owning platform user id (an Entra oid) — worlds ARE users, so naming the
/// owner names the world's account. Required together with <see cref="World"/>; refused alone.</param>
/// <param name="World">The remote world's own <see cref="WorldSafeName"/>-shaped id within its owner's account.
/// Required together with <see cref="Owner"/>; refused alone.</param>
public sealed record WorldReference(WorldSafeName Name, string? Document = null, Guid? Owner = null, WorldSafeName? World = null) {
    /// <summary>
    /// Gets the opaque key every <see cref="IWorldNeighbourResolver"/> call site resolves against — the authored
    /// <see cref="Document"/> path verbatim, or, for the owner-named arm, <c>"owner/{Owner:D}/{World}"</c>. Never
    /// serialized: this is a derived read of whichever arm the row authored, not a third spelling a document could
    /// author directly (<see cref="WorldDefinitionValidator.ValidateReferences"/> refuses a <see cref="Document"/>
    /// beginning with the reserved <c>"owner/"</c> prefix for exactly this reason — the local file resolver would
    /// otherwise satisfy an owner-shaped key with no signature ever checked).
    /// </summary>
    [JsonIgnore]
    public string NeighbourKey => (Document ?? $"owner/{Owner:D}/{World}");
}
