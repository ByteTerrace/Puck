using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Puck.Attestation;

/// <summary>
/// Sealed attestation: the same attestation shape with the payload encrypted (README.md, "Signed
/// attestation") — ECDH P-256 key agreement to an AES-256-GCM key, with the attestation's serialized context
/// header as AEAD associated data. Literal AEAD applied twice over: the header binds the ciphertext to its
/// context exactly as it binds a signature's payload, which is why two keypairs are provisioned (one for
/// signing, one for sealing) rather than one. This type performs the AEAD operation only; it does not sign.
/// A caller that also needs sender authentication encodes the returned payload and signs it as an ordinary
/// <see cref="AttestationPayloadKind.Sealed"/> attestation.
/// </summary>
/// <remarks>
/// <para><b>Sealed attestation is deliberately unauthenticated as to sender.</b> The agreement is
/// ephemeral-static: a fresh sender keypair per seal against the recipient's long-lived sealing key. That
/// buys confidentiality and forward secrecy on the sender's side, and buys nothing about who sealed it —
/// anyone holding the recipient's public sealing key can produce a payload that unseals cleanly. A sealed
/// payload therefore proves only "someone sealed this for you"; when the recipient needs to know who,
/// the sealed payload travels as the payload of an ordinary signed attestation, and the signature is what
/// names the sender.</para>
/// <para><b>Nonce uniqueness has two independent guarantees.</b> The nonce is 12 random bytes per seal,
/// and the AEAD key is derived from a per-seal ephemeral agreement, so a (key, nonce) pair repeats only if
/// an ephemeral keypair and a nonce both collide. No counter is kept, which is what lets sealing stay a
/// stateless operation outside the tick.</para>
/// </remarks>
public static class SealedAttestation {
    private const int DerivedKeyLength = 32;
    private const int NonceLength = 12;
    private const int TagLength = 16;
    // These v1 domain-separation labels are wire constants. They retain their original spelling across the
    // project/API rename so existing sealed payloads remain decryptable; changing either label requires a new
    // sealing protocol version and new known-answer vectors.
    private static readonly byte[] AeadContextLabel = "puck.carriage.sealed.aad.v1"u8.ToArray();

    /// <summary>
    /// Imports an agreement public key from SPKI bytes, refusing anything that is not an EC public key on
    /// the curve the sealing algorithm names. The ephemeral key travels on the wire and is therefore
    /// attacker-chosen, so this is the invalid-curve check: without it, a static recipient key can be made
    /// to agree against a key on a curve the attacker picked, which leaks the private scalar over repeated
    /// attempts.
    /// </summary>
    /// <remarks>
    /// <b>Key type first, curve second</b> (README.md §14). The import is what enforces
    /// the type: <c>ECDiffieHellman.ImportSubjectPublicKeyInfo</c> refuses an SPKI whose
    /// <c>AlgorithmIdentifier</c> is not <c>id-ecPublicKey</c> (1.2.840.10045.2.1), and it has to come first
    /// because a non-EC SPKI — an RSA key, say — has no curve to ask about at all. What there is no check
    /// for, deliberately, is signing-versus-agreement intent: an EC public key's SPKI is the same bytes
    /// either way, so an ephemeral key records no such intent and inventing one would refuse honest keys.
    /// The §4 algorithm name is what separates a signing key from a sealing key here, never the key bytes.
    /// </remarks>
    /// <param name="subjectPublicKeyInfo">The SPKI bytes to import.</param>
    /// <param name="what">What is being imported, for the refusal message.</param>
    /// <exception cref="FormatException">The bytes do not import, or import a key on some other curve.</exception>
    private static ECDiffieHellman ImportAgreementKey(ReadOnlySpan<byte> subjectPublicKeyInfo, string what) {
        var key = ECDiffieHellman.Create();

        try {
            key.ImportSubjectPublicKeyInfo(
                source: subjectPublicKeyInfo,
                bytesRead: out _
            );

            if (!AttestationCurves.IsNistP256(curve: key.ExportParameters(includePrivateParameters: false).Curve)) {
                throw new FormatException(message: $"The sealed attestation {what} is not on P-256, which is the only curve the sealing algorithm names.");
            }
        } catch (CryptographicException exception) {
            key.Dispose();

            throw new FormatException(
                message: $"The sealed attestation {what} does not import as an EC public key.",
                innerException: exception
            );
        } catch {
            key.Dispose();

            throw;
        }

        return key;
    }

    /// <summary>Encrypts <paramref name="plaintext"/> to a named recipient sealing key, binding its id and <paramref name="associatedData"/> into the AEAD operation.</summary>
    /// <param name="recipientId">The self-certifying recipient sealing-key id. Signing-algorithm ids and ids that do not hash to the supplied key are refused.</param>
    /// <param name="recipientPublicKeySubjectPublicKeyInfo">The recipient's sealing public key (SPKI bytes).</param>
    /// <param name="associatedData">The serialized context header — tampering any byte of it must fail decryption.</param>
    /// <param name="plaintext">The payload to encrypt.</param>
    /// <returns>The ephemeral sender public key, nonce, tag, and ciphertext needed to unseal.</returns>
    /// <exception cref="FormatException"><paramref name="recipientPublicKeySubjectPublicKeyInfo"/> is not an importable P-256 public key.</exception>
    public static SealedPayload Seal(KeyId recipientId, ReadOnlySpan<byte> recipientPublicKeySubjectPublicKeyInfo, ReadOnlySpan<byte> associatedData, ReadOnlySpan<byte> plaintext) {
        ValidateRecipientId(
            recipientId: recipientId,
            recipientPublicKeySubjectPublicKeyInfo: recipientPublicKeySubjectPublicKeyInfo
        );

        using var recipientPublicKey = ImportAgreementKey(
            subjectPublicKeyInfo: recipientPublicKeySubjectPublicKeyInfo,
            what: "recipient key"
        );
        using var ephemeralKey = ECDiffieHellman.Create(curve: ECCurve.NamedCurves.nistP256);

        var ephemeralSpki = ephemeralKey.ExportSubjectPublicKeyInfo();
        var recipientContext = EncodeRecipientContext(recipientId: recipientId);
        var derivedKey = DeriveAeadKey(
            privateAgreementKey: ephemeralKey,
            otherPartyPublicKey: recipientPublicKey.PublicKey,
            recipientContext: recipientContext
        );

        try {
            var boundAssociatedData = BindAssociatedData(
                associatedData: associatedData,
                recipientContext: recipientContext
            );
            var nonce = RandomNumberGenerator.GetBytes(count: NonceLength);
            var tag = new byte[TagLength];
            var ciphertext = new byte[plaintext.Length];

            using (var gcm = new AesGcm(
                key: derivedKey,
                tagSizeInBytes: TagLength
            )) {
                gcm.Encrypt(
                    nonce: nonce,
                    plaintext: plaintext,
                    ciphertext: ciphertext,
                    tag: tag,
                    associatedData: boundAssociatedData
                );
            }

            return new SealedPayload(
                Ciphertext: ciphertext,
                EphemeralPublicKeySubjectPublicKeyInfo: ephemeralSpki,
                Nonce: nonce,
                RecipientId: recipientId,
                Tag: tag
            );
        } finally {
            CryptographicOperations.ZeroMemory(buffer: derivedKey);
        }
    }

    /// <summary>
    /// Decrypts a sealed payload. Fails closed: any tamper to <paramref name="associatedData"/> (the
    /// header), the ciphertext, or the tag changes what the GCM tag check authenticates, so
    /// <see cref="AuthenticationTagMismatchException"/> is thrown rather than returning corrupted plaintext.
    /// </summary>
    /// <param name="recipientPrivateKey">The recipient's private sealing key.</param>
    /// <param name="payload">The sealed payload produced by <see cref="Seal"/>.</param>
    /// <param name="associatedData">The serialized context header the caller asserts this payload was sealed under.</param>
    /// <exception cref="AuthenticationTagMismatchException">The tag does not authenticate against the ciphertext and associated data.</exception>
    /// <exception cref="FormatException">The payload's ephemeral key, nonce, or tag is not the shape the sealing algorithm fixes.</exception>
    public static byte[] Unseal(ECDiffieHellman recipientPrivateKey, SealedPayload payload, ReadOnlySpan<byte> associatedData) {
        ValidatePayloadStructure(payload: payload);

        var recipientPublicKeySubjectPublicKeyInfo = recipientPrivateKey.ExportSubjectPublicKeyInfo();

        ValidateRecipientId(
            recipientId: payload.RecipientId,
            recipientPublicKeySubjectPublicKeyInfo: recipientPublicKeySubjectPublicKeyInfo
        );

        if (!AttestationCurves.IsNistP256(curve: recipientPrivateKey.ExportParameters(includePrivateParameters: false).Curve)) {
            throw new FormatException(message: "The sealed attestation recipient key is not on P-256, which is the only curve the sealing algorithm names.");
        }

        using var ephemeralPublicKey = ImportAgreementKey(
            subjectPublicKeyInfo: payload.EphemeralPublicKeySubjectPublicKeyInfo.Span,
            what: "ephemeral sender key"
        );

        var recipientContext = EncodeRecipientContext(recipientId: payload.RecipientId);
        var derivedKey = DeriveAeadKey(
            privateAgreementKey: recipientPrivateKey,
            otherPartyPublicKey: ephemeralPublicKey.PublicKey,
            recipientContext: recipientContext
        );

        try {
            var boundAssociatedData = BindAssociatedData(
                associatedData: associatedData,
                recipientContext: recipientContext
            );
            var plaintext = new byte[payload.Ciphertext.Length];

            using var gcm = new AesGcm(
                key: derivedKey,
                tagSizeInBytes: TagLength
            );

            gcm.Decrypt(
                nonce: payload.Nonce.Span,
                ciphertext: payload.Ciphertext.Span,
                tag: payload.Tag.Span,
                plaintext: plaintext,
                associatedData: boundAssociatedData
            );

            return plaintext;
        } finally {
            CryptographicOperations.ZeroMemory(buffer: derivedKey);
        }
    }

    /// <summary>The fixed ASCII prefix of HKDF info. The codec-independent recipient-id context follows it, binding the derived key to the named sealing key.</summary>
    private static readonly byte[] HkdfInfoLabel = "puck.carriage.sealed.v1"u8.ToArray();

    /// <summary>
    /// Derives the AES-256-GCM key from an ECDH agreement, by four of the five values
    /// README.md §14 fixes: the raw secret agreement (the shared point's X coordinate,
    /// unhashed) as HKDF input keying material, HKDF-SHA256 with an absent salt, the ASCII info-label prefix
    /// <c>puck.carriage.sealed.v1</c> followed by recipient-id context, and an output length of 32 bytes. The fifth — the 16-byte AEAD tag
    /// length — is a construction input to <see cref="AesGcm"/> rather than to the derivation, and lives at
    /// both call sites as <see cref="TagLength"/>.
    /// </summary>
    /// <remarks>
    /// All five have to be normative or nothing interoperates: none of them is visible in the ciphertext,
    /// and every disagreement about any one of them surfaces only as
    /// <see cref="AuthenticationTagMismatchException"/> at the far end — the same failure a tampered
    /// payload produces, with no way to tell an interoperability bug from an attack. Note that .NET's
    /// <c>DeriveKeyFromHmac</c>/<c>DeriveKeyMaterial</c> helpers hash the agreement first; this uses
    /// <see cref="ECDiffieHellman.DeriveRawSecretAgreement"/> precisely so the IKM is the raw value the
    /// specification names.
    /// </remarks>
    private static byte[] DeriveAeadKey(ECDiffieHellman privateAgreementKey, ECDiffieHellmanPublicKey otherPartyPublicKey, ReadOnlySpan<byte> recipientContext) {
        var sharedSecret = privateAgreementKey.DeriveRawSecretAgreement(otherPartyPublicKey: otherPartyPublicKey);

        try {
            var info = new byte[(HkdfInfoLabel.Length + recipientContext.Length)];

            HkdfInfoLabel.CopyTo(array: info, index: 0);
            recipientContext.CopyTo(destination: info.AsSpan(start: HkdfInfoLabel.Length));

            return HKDF.DeriveKey(
                hashAlgorithmName: HashAlgorithmName.SHA256,
                ikm: sharedSecret,
                outputLength: DerivedKeyLength,
                salt: null,
                info: info
            );
        } finally {
            CryptographicOperations.ZeroMemory(buffer: sharedSecret);
        }
    }

    /// <summary>
    /// Validates all structure a verifier can check without holding the recipient private key. A verifier
    /// calls this only after authenticating the attestation, so an attacker-controlled EC point is not imported
    /// before its signature verifies.
    /// </summary>
    internal static void ValidatePayloadStructure(SealedPayload payload) {
        ArgumentNullException.ThrowIfNull(argument: payload);

        ValidateSealingAlgorithm(recipientId: payload.RecipientId);
        var recipientContext = EncodeRecipientContext(recipientId: payload.RecipientId);

        if (payload.Nonce.Length != NonceLength) {
            throw new FormatException(message: $"A sealed payload's nonce must be {NonceLength} bytes, but {payload.Nonce.Length} arrived.");
        }

        if (payload.Tag.Length != TagLength) {
            throw new FormatException(message: $"A sealed payload's tag must be {TagLength} bytes, but {payload.Tag.Length} arrived.");
        }

        if (payload.EphemeralPublicKeySubjectPublicKeyInfo.Length > AttestationResourceLimits.SubjectPublicKeyInfoBytes) {
            throw new FormatException(message: $"A sealed payload's ephemeral SPKI is {payload.EphemeralPublicKeySubjectPublicKeyInfo.Length} bytes; the base profile permits at most {AttestationResourceLimits.SubjectPublicKeyInfoBytes}.");
        }

        using var ephemeralKey = ImportAgreementKey(
            subjectPublicKeyInfo: payload.EphemeralPublicKeySubjectPublicKeyInfo.Span,
            what: "ephemeral sender key"
        );
    }

    private static void ValidateRecipientId(KeyId recipientId, ReadOnlySpan<byte> recipientPublicKeySubjectPublicKeyInfo) {
        ValidateSealingAlgorithm(recipientId: recipientId);

        if (!string.Equals(
            a: recipientId.KeyHash,
            b: KeyId.ComputeKeyHash(subjectPublicKeyInfo: recipientPublicKeySubjectPublicKeyInfo),
            comparisonType: StringComparison.Ordinal
        )) {
            throw new FormatException(message: "The sealed attestation recipient id does not identify the supplied recipient key.");
        }
    }

    private static void ValidateSealingAlgorithm(KeyId recipientId) {
        ArgumentNullException.ThrowIfNull(argument: recipientId);

        AttestationAlgorithmDescriptor descriptor;

        try {
            descriptor = AttestationAlgorithms.Resolve(algorithm: recipientId.Algorithm);
        } catch (NotSupportedException exception) {
            throw new FormatException(
                message: $"The sealed attestation recipient id names unsupported algorithm '{recipientId.Algorithm}'.",
                innerException: exception
            );
        }

        if (descriptor.Role != AttestationKeyRole.Sealing) {
            throw new FormatException(message: $"The sealed attestation recipient id names '{recipientId.Algorithm}', which is a {descriptor.Role.ToString().ToLowerInvariant()} algorithm rather than a sealing algorithm.");
        }

        if (!string.Equals(
            a: descriptor.Name,
            b: AttestationAlgorithms.EcdhP256HkdfSha256Aes256Gcm,
            comparisonType: StringComparison.Ordinal
        )) {
            throw new FormatException(message: $"The sealed attestation implementation does not support recipient sealing algorithm '{descriptor.Name}'.");
        }
    }

    private static byte[] BindAssociatedData(ReadOnlySpan<byte> associatedData, ReadOnlySpan<byte> recipientContext) {
        var bound = new byte[(AeadContextLabel.Length + sizeof(ulong) + associatedData.Length + recipientContext.Length)];
        var offset = 0;

        AeadContextLabel.CopyTo(
            array: bound,
            index: offset
        );
        offset += AeadContextLabel.Length;
        BinaryPrimitives.WriteUInt64BigEndian(
            destination: bound.AsSpan(start: offset),
            value: (ulong)associatedData.Length
        );
        offset += sizeof(ulong);
        associatedData.CopyTo(destination: bound.AsSpan(start: offset));
        offset += associatedData.Length;
        recipientContext.CopyTo(destination: bound.AsSpan(start: offset));

        return bound;
    }

    /// <summary>A codec-independent, length-delimited encoding used in both HKDF info and AEAD associated data.</summary>
    private static byte[] EncodeRecipientContext(KeyId recipientId) {
        var domain = DecodeFingerprint(value: recipientId.Domain, what: "domain");
        var keyHash = DecodeFingerprint(value: recipientId.KeyHash, what: "key hash");
        var subject = ((recipientId.Subject is null)
            ? null
            : Encoding.UTF8.GetBytes(s: recipientId.Subject));
        var algorithm = Encoding.UTF8.GetBytes(s: recipientId.Algorithm);
        var result = new byte[(domain.Length + 1 + ((subject is null) ? 0 : (sizeof(uint) + subject.Length)) + sizeof(uint) + algorithm.Length + keyHash.Length)];
        var offset = 0;

        domain.CopyTo(array: result, index: offset);
        offset += domain.Length;
        result[offset++] = ((subject is null) ? (byte)0 : (byte)1);

        if (subject is not null) {
            BinaryPrimitives.WriteUInt32BigEndian(
                destination: result.AsSpan(start: offset),
                value: checked((uint)subject.Length)
            );
            offset += sizeof(uint);
            subject.CopyTo(array: result, index: offset);
            offset += subject.Length;
        }

        BinaryPrimitives.WriteUInt32BigEndian(
            destination: result.AsSpan(start: offset),
            value: checked((uint)algorithm.Length)
        );
        offset += sizeof(uint);
        algorithm.CopyTo(array: result, index: offset);
        offset += algorithm.Length;
        keyHash.CopyTo(array: result, index: offset);

        return result;
    }

    private static byte[] DecodeFingerprint(string value, string what) {
        byte[] bytes;

        try {
            bytes = Convert.FromHexString(s: value);
        } catch (FormatException exception) {
            throw new FormatException(
                message: $"The sealed attestation recipient {what} is not a SHA-256 fingerprint.",
                innerException: exception
            );
        }

        if (
            (bytes.Length != 32) ||
            !string.Equals(
                a: value,
                b: Convert.ToHexStringLower(bytes: bytes),
                comparisonType: StringComparison.Ordinal
            )
        ) {
            throw new FormatException(message: $"The sealed attestation recipient {what} must be a 32-byte lowercase hexadecimal SHA-256 fingerprint.");
        }

        return bytes;
    }
}
