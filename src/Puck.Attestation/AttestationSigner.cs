using System.Security.Cryptography;

namespace Puck.Attestation;

/// <summary>
/// Mints attestations. Minting is randomised (ECDSA signing draws fresh randomness per signature)
/// and happens outside the tick (README.md, "Signed attestation") — nothing here is deterministic,
/// and nothing here needs to be; only verification does.
/// </summary>
public static class AttestationSigner {
    /// <summary>
    /// Signs an attestation after requiring the declared algorithm and signing key to match the selected
    /// signing algorithm. The algorithm fixes both the signature hash and the key curve.
    /// </summary>
    /// <param name="codec">The serialisation that produces the exact signed-portion bytes passed to <paramref name="signingKey"/>.</param>
    /// <param name="header">The context header to sign; its declared algorithm must equal <paramref name="signingAlgorithm"/>.</param>
    /// <param name="payloadKind">Which shape <paramref name="payloadBytes"/> is.</param>
    /// <param name="payloadBytes">The already-encoded payload bytes.</param>
    /// <param name="signingKey">The private key that actually signs.</param>
    /// <param name="signingAlgorithm">The algorithm actually used for the cryptographic operation (must resolve to a <see cref="AttestationKeyRole.Signing"/> descriptor).</param>
    /// <returns>The signed attestation carrying the exact bytes passed to the signing operation.</returns>
    /// <exception cref="ArgumentException"><paramref name="signingAlgorithm"/> does not resolve to a signing algorithm, <paramref name="header"/> declares another algorithm, or <paramref name="signingKey"/> is on another curve.</exception>
    /// <exception cref="CryptographicException">The signing operation fails or produces a signature outside the registered algorithm's fixed width.</exception>
    /// <exception cref="NotSupportedException"><paramref name="signingAlgorithm"/> is not registered.</exception>
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

        if (!string.Equals(
            a: header.Algorithm,
            b: descriptor.Name,
            comparisonType: StringComparison.Ordinal
        )) {
            throw new ArgumentException(
                message: $"The attestation header declares algorithm '{header.Algorithm}', but the signer was asked to use '{descriptor.Name}'.",
                paramName: nameof(header)
            );
        }

        if (!AttestationCurves.Matches(
            key: signingKey.ExportParameters(includePrivateParameters: false).Curve,
            expected: descriptor.Curve
        )) {
            throw new ArgumentException(
                message: $"The signing key is not on the curve algorithm '{descriptor.Name}' names.",
                paramName: nameof(signingKey)
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

        if (signature.Length != AttestationResourceLimits.SignatureBytes) {
            throw new CryptographicException(message: $"Algorithm '{descriptor.Name}' produced a {signature.Length}-byte signature; the attestation profile requires exactly {AttestationResourceLimits.SignatureBytes} bytes.");
        }

        // The attestation carries the bytes that were just signed rather than a promise that they could be
        // re-derived — the same bytes a verifier will check the signature against (see the remarks on
        // SignedAttestation).
        return SignedAttestation.FromSignedPortion(
            header: header,
            payloadBytes: payloadBytes,
            payloadKind: payloadKind,
            signature: signature,
            signedPortion: signedPortion
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
    /// <returns>The signed opaque claim.</returns>
    /// <exception cref="ArgumentException"><paramref name="purpose"/> is <see cref="AttestationPurposes.KeyBinding"/>, <paramref name="signerAlgorithm"/> does not resolve to a signing algorithm, or <paramref name="signerKey"/> is on another curve.</exception>
    /// <exception cref="CryptographicException">The signing operation fails or produces a signature outside the registered algorithm's fixed width.</exception>
    /// <exception cref="NotSupportedException"><paramref name="signerAlgorithm"/> is not registered.</exception>
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
        ReadOnlyMemory<byte> claimBytes
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
            Algorithm: signerAlgorithm,
            Audience: audience,
            Domain: domain,
            NotAfter: notAfter,
            NotBefore: notBefore,
            Purpose: purpose,
            Sequence: sequence,
            Subject: subject
        );

        return Sign(
            codec: codec,
            header: header,
            payloadBytes: claimBytes,
            payloadKind: AttestationPayloadKind.Opaque,
            signingAlgorithm: signerAlgorithm,
            signingKey: signerKey
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
    /// <returns>The signed key-binding attestation.</returns>
    /// <exception cref="ArgumentException"><paramref name="signerAlgorithm"/> does not resolve to a signing algorithm, or <paramref name="signerKey"/> is on another curve.</exception>
    /// <exception cref="CryptographicException">The signing operation fails or produces a signature outside the registered algorithm's fixed width.</exception>
    /// <exception cref="NotSupportedException"><paramref name="signerAlgorithm"/> is not registered.</exception>
    public static SignedAttestation SignKeyBinding(
        IAttestationCodec codec,
        string domain,
        ECDsa signerKey,
        string signerAlgorithm,
        KeyId targetId,
        ReadOnlyMemory<byte> targetSubjectPublicKeyInfo,
        long notBefore,
        long notAfter
    ) {
        var payload = new KeyBindingPayload(
            PublicKeySubjectPublicKeyInfo: targetSubjectPublicKeyInfo,
            TargetId: targetId
        );
        var payloadBytes = codec.EncodeKeyBindingPayload(payload: payload);
        var header = new AttestationHeader(
            Algorithm: signerAlgorithm,
            Audience: null,
            Domain: domain,
            NotAfter: notAfter,
            NotBefore: notBefore,
            Purpose: AttestationPurposes.KeyBinding,
            Sequence: null,
            Subject: null
        );

        return Sign(
            codec: codec,
            header: header,
            payloadBytes: payloadBytes,
            payloadKind: AttestationPayloadKind.KeyBinding,
            signingAlgorithm: signerAlgorithm,
            signingKey: signerKey
        );
    }
}
