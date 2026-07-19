namespace Puck.World.Protocol;

/// <summary>The World client/server wire contract's version. A <see cref="SessionRequest.Join"/> carries the client's
/// value; <see cref="Puck.World.Server.WorldServer.ApplySession"/> rejects a mismatch with a distinct reason. This is
/// the whole wire-versioning story today; the socket transport (serialization, deltas, auth) is unimplemented.</summary>
internal static class WorldProtocol {
    /// <summary>The current protocol version. Bumped whenever the client/server message shapes change incompatibly.</summary>
    public const int Version = 1;
}
