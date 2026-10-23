using Puck.Networking;
using System.Security.Cryptography;

namespace Puck.World.Server;

public static partial class WorldFederationCodec {
    /// <summary>The maximum nonce length accepted by the federation handshake.</summary>
    public const int MaxChallengeBytes = 256;

    /// <summary>Binds an application proof to both nonces, both authenticated transport keys, its direction, and
    /// the destination namespace. The nonce width is preserved for the configured authenticator.</summary>
    /// <param name="challenge">The nonce issued by the verifier of this proof.</param>
    /// <param name="counterChallenge">The nonce issued by the prover.</param>
    /// <param name="clientKeyHash">The authenticated connecting peer's key fingerprint.</param>
    /// <param name="serverKeyHash">The authenticated accepting peer's key fingerprint.</param>
    /// <param name="serverProof">Whether this proves the accepting authority.</param>
    /// <param name="authority">The destination namespace for a server proof; empty for a client proof.</param>
    /// <returns>The transcript-bound challenge.</returns>
    /// <exception cref="ArgumentException">Either nonce is empty or exceeds the nonce ceiling.</exception>
    public static byte[] BindProofChallenge(ReadOnlySpan<byte> challenge, ReadOnlySpan<byte> counterChallenge,
        string clientKeyHash, string serverKeyHash, bool serverProof, string authority = "") {
        if ((challenge.Length == 0) || (challenge.Length > MaxChallengeBytes) || (counterChallenge.Length == 0) || (counterChallenge.Length > MaxChallengeBytes)) {
            throw new ArgumentException(message: "federation challenges must have a bounded, nonzero length");
        }
        var context = new WireWriter();
        context.WriteString(value: "puck.world.federation-proof.v2");
        context.WriteBoolean(value: serverProof);
        context.WriteString(value: clientKeyHash);
        context.WriteString(value: serverKeyHash);
        context.WriteString(value: authority);
        context.WriteBlock(value: counterChallenge);
        var bound = new byte[challenge.Length];
        HKDF.DeriveKey(hashAlgorithmName: HashAlgorithmName.SHA256, ikm: challenge,
            output: bound, salt: default, info: context.WrittenSpan);
        return bound;
    }
    /// <summary>Encodes the destination's authority namespace and its proof in the authentication acknowledgement.</summary>
    /// <param name="authority">The destination's nonempty authority namespace.</param>
    /// <param name="proof">The destination proof, empty only when a client must authenticate by its transport pin.</param>
    /// <returns>The encoded acknowledgement body.</returns>
    /// <exception cref="ArgumentException">The namespace is null or empty.</exception>
    public static byte[] EncodeAuthorityIdentity(string authority, ReadOnlySpan<byte> proof) {
        ArgumentException.ThrowIfNullOrEmpty(authority);
        var writer = new WireWriter();

        writer.WriteString(value: authority);
        writer.WriteBlock(value: proof);
        return writer.ToArray();
    }
    /// <summary>Decodes one nonempty destination namespace and its bounded proof.</summary>
    /// <param name="body">The untrusted acknowledgement bytes.</param>
    /// <param name="authority">The decoded namespace on success.</param>
    /// <param name="proof">The decoded destination proof.</param>
    /// <param name="failure">The named decoding refusal.</param>
    /// <returns>True only when the complete body is valid.</returns>
    public static bool TryDecodeAuthorityIdentity(ReadOnlySpan<byte> body, out string authority, out byte[] proof, out WireFailure failure) {
        var reader = new WireReader(bytes: body);

        authority = reader.ReadRequiredString("destination authority");
        proof = reader.ReadBlock(field: "destination proof", maxBytes: MaxProofBytes);
        return reader.TryFinish(failure: out failure);
    }
}
