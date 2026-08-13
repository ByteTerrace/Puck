using System.Security.Cryptography;

namespace Puck.Attestation;

/// <summary>
/// Mints attestations. Minting is randomised (ECDSA signing draws fresh randomness per signature)
/// and happens outside the tick (README.md, "Signed attestation") — nothing here is deterministic,
/// and nothing here needs to be; only verification does.
/// </summary>
public static class AttestationSigner {
    /// <summary>
    /// Signs an attestation. <paramref name="signingAlgorithm"/> drives the actual cryptographic operation and
    /// is independent of <paramref name="header"/>'s own <see cref="AttestationHeader.Algorithm"/>
    /// field — in honest minting code the two are always equal, but keeping them as separate parameters is
    /// what lets the adversarial tests construct the algorithm-confusion attack (an attestation that claims one
    /// algorithm while a different, correctly-pinned one actually produced the signature).
    /// </summary>
    /// <param name="codec">The serialisation that produces the exact signed-portion bytes passed to <paramref name="signingKey"/>.</param>
    /// <param name="header">The context header to sign. Its own <see cref="AttestationHeader.Algorithm"/> is written as-is, never derived from <paramref name="signingAlgorithm"/>.</param>
    /// <param name="payloadKind">Which shape <paramref name="payloadBytes"/> is.</param>
    /// <param name="payloadBytes">The already-encoded payload bytes.</param>
    /// <param name="signingKey">The private key that actually signs.</param>
    /// <param name="signingAlgorithm">The algorithm actually used for the cryptographic operation (must resolve to a <see cref="AttestationKeyRole.Signing"/> descriptor).</param>
    /// <exception cref="ArgumentException"><paramref name="signingAlgorithm"/> does not resolve to a signing algorithm.</exception>
    public static SignedAttestation Sign(
        IAttestationCodec codec,
        AttestationHeader header,
        AttestationPayloadKind payloadKind,
        ReadOnlyMemory<byte> payloadBytes,
        ECDsa signingKey,
        string signingAlgorithm
    ) {
        var descriptor = AttestationAlgorithms.Resolve(algorithm: signingAlgorithm);

        if (descriptor.Role != AttestationKeyRole.Signing) {
            throw new ArgumentException(
                message: $"'{signingAlgorithm}' is not a signing algorithm.",
                paramName: nameof(signingAlgorithm)
            );
        }

        var signedPortion = codec.EncodeSignedPortion(
            header: header,
            payloadKind: payloadKind,
            payloadBytes: payloadBytes.Span
        );
        var signature = signingKey.SignData(
            data: signedPortion,
            hashAlgorithm: descriptor.SignatureHash!.Value,
            signatureFormat: DSASignatureFormat.IeeeP1363FixedFieldConcatenation
        );

        // The attestation carries the bytes that were just signed rather than a promise that they could be
        // re-derived — the same bytes a verifier will check the signature against (see the remarks on
        // SignedAttestation).
        return SignedAttestation.FromSignedPortion(
            header: header,
            payloadKind: payloadKind,
            payloadBytes: payloadBytes,
            signature: signature,
            signedPortion: signedPortion
        );
    }

    /// <summary>
    /// Mints binding #1 or #2 of a chain: an attestation with purpose <see cref="AttestationPurposes.KeyBinding"/>
    /// whose payload is <paramref name="targetId"/> paired with its actual key bytes
    /// (<see cref="KeyBindingPayload"/>) — a key binding is not a separate artifact from an ordinary
    /// attestation, only this purpose value distinguishes it.
    /// </summary>
    /// <param name="codec">The serialisation to sign under.</param>
    /// <param name="domain">The chain's root fingerprint (shared by every key in the chain).</param>
    /// <param name="signerKey">The vouching key (root, for binding #1; issuing, for binding #2).</param>
    /// <param name="signerAlgorithm">The vouching key's REAL algorithm, driving the actual signature.</param>
    /// <param name="targetId">The id of the key being vouched for.</param>
    /// <param name="targetSubjectPublicKeyInfo">The vouched-for key's actual SPKI bytes.</param>
    /// <param name="notBefore">The issuer-authored window start, Unix seconds.</param>
    /// <param name="notAfter">The issuer-authored window end, Unix seconds.</param>
    /// <param name="declaredAlgorithm">
    /// What <see cref="AttestationHeader.Algorithm"/> is actually written as. Defaults to
    /// <paramref name="signerAlgorithm"/>; a test may override this to construct an algorithm-confusion
    /// attestation (a header that lies about which algorithm signed it).
    /// </param>
    public static SignedAttestation SignKeyBinding(
        IAttestationCodec codec,
        string domain,
        ECDsa signerKey,
        string signerAlgorithm,
        KeyId targetId,
        ReadOnlyMemory<byte> targetSubjectPublicKeyInfo,
        long notBefore,
        long notAfter,
        string? declaredAlgorithm = null
    ) {
        var payload = new KeyBindingPayload(
            TargetId: targetId,
            PublicKeySubjectPublicKeyInfo: targetSubjectPublicKeyInfo
        );
        var payloadBytes = codec.EncodeKeyBindingPayload(payload: payload);
        var header = new AttestationHeader(
            Domain: domain,
            Subject: null,
            Algorithm: (declaredAlgorithm ?? signerAlgorithm),
            Purpose: AttestationPurposes.KeyBinding,
            NotBefore: notBefore,
            NotAfter: notAfter,
            Audience: null,
            Sequence: null
        );

        return Sign(
            codec: codec,
            header: header,
            payloadKind: AttestationPayloadKind.KeyBinding,
            payloadBytes: payloadBytes,
            signingKey: signerKey,
            signingAlgorithm: signerAlgorithm
        );
    }

    /// <summary>Mints a claim: an attestation signed by a subject key, carrying caller-defined opaque bytes.</summary>
    /// <param name="codec">The serialisation to sign under.</param>
    /// <param name="domain">The chain's root fingerprint.</param>
    /// <param name="subject">The signing subject key's platform user id.</param>
    /// <param name="signerKey">The subject's private signing key.</param>
    /// <param name="signerAlgorithm">The subject key's REAL algorithm, driving the actual signature.</param>
    /// <param name="purpose">The claim's purpose. Must not be <see cref="AttestationPurposes.KeyBinding"/>.</param>
    /// <param name="notBefore">The issuer-authored window start, Unix seconds.</param>
    /// <param name="notAfter">The issuer-authored window end, Unix seconds.</param>
    /// <param name="audience">The one world this claim is valid at, or <see langword="null"/> for a bearer claim.</param>
    /// <param name="sequence">The optional replay-protection sequence number. It is required for a bearer claim and may also appear on a directed claim.</param>
    /// <param name="claimBytes">The opaque claim payload.</param>
    /// <param name="declaredAlgorithm">What the header actually declares; defaults to <paramref name="signerAlgorithm"/> (see <see cref="SignKeyBinding"/> for why this can be overridden).</param>
    /// <exception cref="ArgumentException"><paramref name="purpose"/> is <see cref="AttestationPurposes.KeyBinding"/>.</exception>
    public static SignedAttestation SignClaim(
        IAttestationCodec codec,
        string domain,
        string subject,
        ECDsa signerKey,
        string signerAlgorithm,
        string purpose,
        long notBefore,
        long notAfter,
        string? audience,
        ulong? sequence,
        ReadOnlyMemory<byte> claimBytes,
        string? declaredAlgorithm = null
    ) {
        if (string.Equals(
            a: purpose,
            b: AttestationPurposes.KeyBinding,
            comparisonType: StringComparison.Ordinal
        )) {
            throw new ArgumentException(
                message: $"A claim must not use the reserved purpose '{AttestationPurposes.KeyBinding}'.",
                paramName: nameof(purpose)
            );
        }

        var header = new AttestationHeader(
            Domain: domain,
            Subject: subject,
            Algorithm: (declaredAlgorithm ?? signerAlgorithm),
            Purpose: purpose,
            NotBefore: notBefore,
            NotAfter: notAfter,
            Audience: audience,
            Sequence: sequence
        );

        return Sign(
            codec: codec,
            header: header,
            payloadKind: AttestationPayloadKind.Opaque,
            payloadBytes: claimBytes,
            signingKey: signerKey,
            signingAlgorithm: signerAlgorithm
        );
    }
}
