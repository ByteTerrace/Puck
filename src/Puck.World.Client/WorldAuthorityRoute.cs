using Puck.World.Protocol;

namespace Puck.World.Client;

/// <summary>
/// One seat's immutable authority claim: endpoint, entity index within that authority, and monotonically increasing
/// epoch. <see cref="WorldSeatAuthorityRouter"/> publishes the whole value with one CAS, so every consumer observes
/// either the complete old claim or the complete new one.
/// </summary>
public sealed record WorldAuthorityRoute(WorldAuthorityEndpoint Endpoint, WorldEntityAddress Entity, ulong Epoch) {
    public int EntityIndex => Entity.Index;
}
