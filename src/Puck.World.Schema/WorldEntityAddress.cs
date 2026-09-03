using System.Text.Json.Serialization;

namespace Puck.World.Protocol;

/// <summary>A durable entity address: authority identity, population slot, and that slot's activation generation.
/// Slot reuse therefore never aliases an entity that has already left or died. The type lives in Schema because
/// authored social references and protocol messages share exactly the same identity vocabulary.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public readonly record struct WorldEntityAddress(string Authority, int Index, int Generation) {
    /// <inheritdoc/>
    public override string ToString() => $"{Authority}/{Index}:{Generation}";
}
