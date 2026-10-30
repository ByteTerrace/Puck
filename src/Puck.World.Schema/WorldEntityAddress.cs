using System.Text.Json.Serialization;

namespace Puck.World.Protocol;

/// <summary>A durable entity address: authority identity, population slot, and that slot's activation generation.
/// Slot reuse therefore never aliases an entity that has already left or died. The type lives in Schema because
/// authored social references and protocol messages share exactly the same identity vocabulary.</summary>
/// <param name="Authority">The authority that minted the address.</param>
/// <param name="Index">The population slot.</param>
/// <param name="Generation">The slot's activation generation.</param>
/// <param name="Titled">Whether the minting authority holds the title of the entity minted here (an owned traveller
/// such as a companion): it moves between authorities only under a hop that authority grants, and an authority that
/// cannot verify such a hop never lands it. The mark is part of the identity, so an address stripped of it names a
/// different entity, of which the minting authority holds no title.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public readonly record struct WorldEntityAddress(string Authority, int Index, int Generation, [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool Titled = false) {
    /// <inheritdoc/>
    public override string ToString() => (Titled
        ? $"{Authority}/{Index}:{Generation}:titled"
        : $"{Authority}/{Index}:{Generation}");
}
