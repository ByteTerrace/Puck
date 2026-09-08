namespace Puck.World.Server;

/// <summary>Publishes this world's own counterpart claim payload to the platform oracle after a successful
/// <see cref="WorldOwnedWorldSync.Push"/> — the addressable-later half of a border proof: the document write is the
/// primary effect and a claim-post failure never fails the push itself (see
/// <see cref="WorldOwnedWorldSync.PushOne"/>'s own remarks).</summary>
public interface ICounterpartPublisher {
    /// <summary>Posts one world's counterpart claim payload.</summary>
    /// <param name="worldId">The owned-world id the platform route names — the same spelling
    /// <see cref="WorldOwnedWorldFileName"/> would produce, never the escaped filename.</param>
    /// <param name="payload">The exact <see cref="WorldCounterpartAttestationProtocol.Payload"/> bytes to sign and store.</param>
    /// <param name="detail">What happened — a short line the push outcome echoes either way.</param>
    /// <returns><see langword="true"/> when the platform accepted the claim.</returns>
    bool TryPublish(string worldId, ReadOnlyMemory<byte> payload, out string detail);
}
