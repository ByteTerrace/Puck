using System.Security.Cryptography;

namespace Puck.Carriage;

/// <summary>
/// Sealed carriage: the same envelope shape with the payload encrypted (README.md, "Signed
/// carriage") — ECDH P-256 key agreement to an AES-256-GCM key, with the envelope's serialized context
/// header as AEAD associated data. Literal AEAD applied twice over: the header binds the ciphertext to its
/// context exactly as it binds a signature's payload, which is why two keypairs are provisioned (one for
/// signing, one for sealing) rather than one. This type performs the AEAD operation only; it does not sign
/// — combining a sealed payload with the ordinary signed envelope (nesting one inside the other) is
/// straightforward if sender authentication beyond "held the matching private key" is also wanted, but is
/// out of scope for what the doc asks this prototype to prove.
/// </summary>
/// <remarks>
/// <para><b>Sealed carriage is deliberately unauthenticated as to sender.</b> The agreement is
/// ephemeral-static: a fresh sender keypair per seal against the recipient's long-lived sealing key. That
/// buys confidentiality and forward secrecy on the sender's side, and buys nothing about who sealed it —
/// anyone holding the recipient's public sealing key can produce a payload that unseals cleanly. A sealed
/// payload therefore proves only "someone sealed this for you"; when the recipient needs to know who,
/// the sealed payload travels as the payload of an ordinary signed envelope, and the signature is what
/// names the sender.</para>
/// <para><b>Nonce uniqueness has two independent guarantees.</b> The nonce is 12 random bytes per seal,
/// and the AEAD key is derived from a per-seal ephemeral agreement, so a (key, nonce) pair repeats only if
/// an ephemeral keypair and a nonce both collide. No counter is kept, which is what lets sealing stay a
/// stateless operation outside the tick.</para>
/// </remarks>
public static class SealedCarriage {
    private const int DerivedKeyLength = 32;
    private const int NonceLength = 12;
    private const int TagLength = 16;

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

            if (!CarriageCurves.IsNistP256(curve: key.ExportParameters(includePrivateParameters: false).Curve)) {
                throw new FormatException(message: $"The sealed carriage {what} is not on P-256, which is the only curve the sealing algorithm names.");
            }
        } catch (CryptographicException exception) {
            key.Dispose();

            throw new FormatException(
                message: $"The sealed carriage {what} does not import as an EC public key.",
                innerException: exception
            );
        } catch {
            key.Dispose();

            throw;
        }

        return key;
    }

    /// <summary>Encrypts <paramref name="plaintext"/> to a recipient's sealing key, binding <paramref name="associatedData"/> as AEAD associated data.</summary>
    /// <param name="recipientPublicKeySubjectPublicKeyInfo">The recipient's sealing public key (SPKI bytes).</param>
    /// <param name="associatedData">The serialized context header — tampering any byte of it must fail decryption.</param>
    /// <param name="plaintext">The payload to encrypt.</param>
    /// <returns>The ephemeral sender public key, nonce, tag, and ciphertext needed to unseal.</returns>
    /// <exception cref="FormatException"><paramref name="recipientPublicKeySubjectPublicKeyInfo"/> is not an importable P-256 public key.</exception>
    public static SealedPayload Seal(ReadOnlySpan<byte> recipientPublicKeySubjectPublicKeyInfo, ReadOnlySpan<byte> associatedData, ReadOnlySpan<byte> plaintext) {
        using var recipientPublicKey = ImportAgreementKey(
            subjectPublicKeyInfo: recipientPublicKeySubjectPublicKeyInfo,
            what: "recipient key"
        );
        using var ephemeralKey = ECDiffieHellman.Create(curve: ECCurve.NamedCurves.nistP256);

        var ephemeralSpki = ephemeralKey.ExportSubjectPublicKeyInfo();
        var derivedKey = DeriveAeadKey(
            privateAgreementKey: ephemeralKey,
            otherPartyPublicKey: recipientPublicKey.PublicKey
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
                associatedData: associatedData
            );
        }

        CryptographicOperations.ZeroMemory(buffer: derivedKey);

        return new SealedPayload(
            Ciphertext: ciphertext,
            EphemeralPublicKeySubjectPublicKeyInfo: ephemeralSpki,
            Nonce: nonce,
            Tag: tag
        );
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
        // Nonce and tag widths are fixed by the algorithm, and every one of these fields arrives from the
        // wire. Checking them here keeps a malformed payload a FormatException rather than whatever
        // ArgumentException AesGcm would raise from inside the crypto call, which a caller catching
        // CryptographicException would miss entirely.
        if (payload.Nonce.Length != NonceLength) {
            throw new FormatException(message: $"A sealed payload's nonce must be {NonceLength} bytes, but {payload.Nonce.Length} arrived.");
        }

        if (payload.Tag.Length != TagLength) {
            throw new FormatException(message: $"A sealed payload's tag must be {TagLength} bytes, but {payload.Tag.Length} arrived.");
        }

        using var ephemeralPublicKey = ImportAgreementKey(
            subjectPublicKeyInfo: payload.EphemeralPublicKeySubjectPublicKeyInfo.Span,
            what: "ephemeral sender key"
        );

        var derivedKey = DeriveAeadKey(
            privateAgreementKey: recipientPrivateKey,
            otherPartyPublicKey: ephemeralPublicKey.PublicKey
        );
        var plaintext = new byte[payload.Ciphertext.Length];

        try {
            using var gcm = new AesGcm(
                key: derivedKey,
                tagSizeInBytes: TagLength
            );

            gcm.Decrypt(
                nonce: payload.Nonce.Span,
                ciphertext: payload.Ciphertext.Span,
                tag: payload.Tag.Span,
                plaintext: plaintext,
                associatedData: associatedData
            );
        } finally {
            CryptographicOperations.ZeroMemory(buffer: derivedKey);
        }

        return plaintext;
    }

    /// <summary>The HKDF info label, ASCII, fixed by README.md §14. Two implementations that pick different labels derive different keys and fail with an AEAD tag mismatch — indistinguishable from tampering.</summary>
    private static readonly byte[] HkdfInfoLabel = "puck.carriage.sealed.v1"u8.ToArray();

    /// <summary>
    /// Derives the AES-256-GCM key from an ECDH agreement, by four of the five values
    /// README.md §14 fixes: the raw secret agreement (the shared point's X coordinate,
    /// unhashed) as HKDF input keying material, HKDF-SHA256 with an absent salt, the ASCII info label
    /// <c>puck.carriage.sealed.v1</c>, and an output length of 32 bytes. The fifth — the 16-byte AEAD tag
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
    private static byte[] DeriveAeadKey(ECDiffieHellman privateAgreementKey, ECDiffieHellmanPublicKey otherPartyPublicKey) {
        var sharedSecret = privateAgreementKey.DeriveRawSecretAgreement(otherPartyPublicKey: otherPartyPublicKey);

        try {
            return HKDF.DeriveKey(
                hashAlgorithmName: HashAlgorithmName.SHA256,
                ikm: sharedSecret,
                outputLength: DerivedKeyLength,
                salt: null,
                info: HkdfInfoLabel
            );
        } finally {
            CryptographicOperations.ZeroMemory(buffer: sharedSecret);
        }
    }
}
