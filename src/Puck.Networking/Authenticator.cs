namespace Puck.Networking;

/// <summary>The wire-facing shape and behavior a lane authenticator exposes, so a challenge/proof exchange never
/// hardcodes one scheme's byte widths against a second implementation. A caller reads
/// <see cref="ChallengeBytes"/> to size and validate a fresh challenge; proof-shape validation is
/// <see cref="TryVerify"/>'s own job, never a fixed length check upstream of it.</summary>
/// <remarks>
/// <see cref="Prove"/> carries no source-authority parameter and <see cref="TryVerify"/> returns one instead of
/// accepting one: the identity a verified proof names is a fact the proof itself establishes, never a claim the
/// caller supplies. An authenticator whose <see cref="Prove"/> took a namespace argument would let a caller ask to
/// be proven as anyone; an authenticator whose <see cref="TryVerify"/> took one back would let the door's caller
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
    /// <param name="sourceAuthority">The verified source namespace the proof establishes, or
    /// <see langword="null"/> on failure. Never derived from anything the caller asserted.</param>
    /// <returns><see langword="true"/> only when the proof verifies.</returns>
    bool TryVerify(ReadOnlySpan<byte> challenge, ReadOnlySpan<byte> proof, out string? sourceAuthority);
}
