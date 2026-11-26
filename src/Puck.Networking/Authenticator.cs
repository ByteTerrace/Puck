namespace Puck.Networking;

/// <summary>The wire-facing shape and behavior a lane authenticator exposes, so a challenge/proof exchange never
/// hardcodes one scheme's byte widths against a second implementation. A caller reads
/// <see cref="ChallengeBytes"/> to size and validate a fresh challenge; proof-shape validation is
/// <see cref="VerifyAsync"/>'s own job, never a fixed length check upstream of it.</summary>
/// <remarks>
/// <see cref="Prove"/> carries no source-authority parameter and <see cref="VerifyAsync"/> returns one instead of
/// accepting one: the identity a verified proof names is a fact the proof itself establishes, never a claim the
/// caller supplies. An authenticator whose <see cref="Prove"/> took a namespace argument would let a caller ask to
/// be proven as anyone; an authenticator whose <see cref="VerifyAsync"/> took one back would let the door's caller
/// keep using an unverified label after verification succeeded on some other identity entirely. Both ends of the
/// exchange are identity-derived, not identity-supplied.
/// </remarks>
public interface IAuthenticator {
    /// <summary>Gets the exact byte length of a fresh challenge this authenticator issues and expects proven.</summary>
    int ChallengeBytes { get; }
    /// <summary>Gets a value indicating whether this authenticator is configured to authenticate at all.</summary>
    bool IsConfigured { get; }

    /// <summary>Issues a fresh challenge.</summary>
    /// <returns>The challenge bytes, <see cref="ChallengeBytes"/> long.</returns>
    byte[] NewChallenge();
    /// <summary>Proves this authenticator's own identity against a challenge it received.</summary>
    /// <param name="challenge">The challenge to prove against.</param>
    /// <returns>The proof bytes.</returns>
    byte[] Prove(ReadOnlySpan<byte> challenge);
    /// <summary>Verifies a presented proof against a challenge this authenticator issued, and names the identity
    /// the proof establishes.</summary>
    /// <param name="challenge">The challenge this authenticator issued.</param>
    /// <param name="proof">The presented proof.</param>
    /// <param name="ct">Cancels issuer metadata retrieval.</param>
    /// <returns>The verified namespace and its signed lifetime, or null when refused.</returns>
    ValueTask<VerifiedPeerIdentity?> VerifyAsync(ReadOnlyMemory<byte> challenge, ReadOnlyMemory<byte> proof, CancellationToken ct);
}
/// <summary>The proof's verified namespace and last inclusive Unix second of validity.</summary>
/// <param name="Authority">The namespace the proof establishes.</param>
/// <param name="ValidThrough">The last inclusive Unix second the proof's trust lasts.</param>
/// <param name="TrustIsCurrent">Whether the trust the proof rested on still stands, or <see langword="null"/> when
/// nothing can withdraw it.</param>
/// <param name="RefreshTrustAsync">Refreshes the trust the proof rests on, or <see langword="null"/>.</param>
/// <param name="Session">The session the proof signed for, or <see langword="null"/> for a proof that named an
/// authority alone. Only a verifier sets it, from the claim's own signed bytes and the trust path that verified them,
/// so a namespace that merely spells like a session's carries none.</param>
public sealed record VerifiedPeerIdentity(string Authority, long ValidThrough, Func<bool>? TrustIsCurrent = null,
    Func<CancellationToken, ValueTask>? RefreshTrustAsync = null, VerifiedSession? Session = null);
/// <summary>A session a verified proof signed for: a namespace of one signer's own, qualified by bytes the signer
/// chose and signed beside the connection's challenge. A session is reusable for as long as its signer's trust
/// lasts; nothing here says it belongs to one process run.</summary>
/// <param name="Domain">The fingerprint of the root that vouched for the signer, or of the signer's own pinned
/// key.</param>
/// <param name="Subject">The subject the signer is trusted as.</param>
/// <param name="Run">The signed session bytes, as lowercase hexadecimal.</param>
/// <param name="Vouched">Whether a root vouched for the signer's key through a chain, as an issuer does for a
/// player's key; <see langword="false"/> for a key the verifier pins directly.</param>
public sealed record VerifiedSession(string Domain, string Subject, string Run, bool Vouched);

/// <summary>Requires an authenticated transport identity to be pinned before disclosing a credential.</summary>
public interface IRemoteIdentityVerifier {
    /// <summary>Gets whether this instance has an explicit remote transport pin. A verifier-only server has none.</summary>
    bool HasRemoteIdentityPin { get; }
    /// <summary>Checks the remote transport's authenticated public-key fingerprint.</summary>
    /// <param name="keyHash">The SHA-256 fingerprint established by the peer handshake.</param>
    /// <returns>Whether this credential may be presented to that peer.</returns>
    bool AcceptsRemoteIdentity(string keyHash);
}
