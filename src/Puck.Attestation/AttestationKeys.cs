using System.Security.Cryptography;

namespace Puck.Attestation;

/// <summary>
/// Turns private key material into a bare <see cref="ECDsa"/> the named algorithm actually describes — the one
/// import path every loader of a PKCS#8 signing key shares. A bare <c>ECDsa.Create()</c> plus import accepts a
/// key on any curve and, with <c>bytesRead</c> discarded, a key followed by arbitrary trailing bytes; a P-384
/// key would then mint an identity whose algorithm claims P-256 and fail at its first signature, far from the
/// file that was wrong. Both checks live here so that a key is refused where it is loaded, by name.
/// </summary>
public static class AttestationKeys {
    /// <summary>Imports exactly one PKCS#8 private key as a signing key on the curve <paramref name="algorithm"/> names.</summary>
    /// <param name="pkcs8">The PKCS#8 <c>PrivateKeyInfo</c> bytes. The whole span must be one encoded key; trailing bytes are refused.</param>
    /// <param name="algorithm">The signing algorithm the key must be usable under — one of <see cref="AttestationAlgorithms"/>'s name constants.</param>
    /// <returns>The imported key. The caller owns disposal.</returns>
    /// <exception cref="ArgumentException"><paramref name="algorithm"/> is not a known signing algorithm, <paramref name="pkcs8"/> carries bytes after the key, or the key is not on the curve the algorithm names.</exception>
    /// <exception cref="CryptographicException"><paramref name="pkcs8"/> does not decode as a PKCS#8 private key.</exception>
    public static ECDsa ImportPkcs8PrivateKey(ReadOnlySpan<byte> pkcs8, string algorithm) {
        ArgumentNullException.ThrowIfNull(argument: algorithm);

        var descriptor = (SigningAlgorithm(algorithm: algorithm) ?? throw new ArgumentException(
            message: $"'{algorithm}' is not an attestation SIGNING algorithm — only a signing key is imported here.",
            paramName: nameof(algorithm)
        ));
        var key = ECDsa.Create();

        try {
            key.ImportPkcs8PrivateKey(
                bytesRead: out var bytesRead,
                source: pkcs8
            );

            if (bytesRead != pkcs8.Length) {
                throw new ArgumentException(
                    message: $"The PKCS#8 private key is followed by {(pkcs8.Length - bytesRead)} trailing byte(s).",
                    paramName: nameof(pkcs8)
                );
            }

            if (!AttestationCurves.Matches(
                expected: descriptor.Curve,
                key: key.ExportParameters(includePrivateParameters: false).Curve
            )) {
                throw new ArgumentException(
                    message: $"The private key is not on the curve algorithm '{descriptor.Name}' names.",
                    paramName: nameof(pkcs8)
                );
            }

            return key;
        } catch {
            key.Dispose();

            throw;
        }
    }
    /// <summary>Signs <paramref name="data"/> under <paramref name="algorithm"/> in the fixed IEEE P1363 form every
    /// attestation signature takes, and that <see cref="VerifySignature"/> reads.</summary>
    /// <param name="key">The signing key, on the curve the algorithm names.</param>
    /// <param name="algorithm">The signing algorithm — one of <see cref="AttestationAlgorithms"/>'s name constants.</param>
    /// <param name="data">The exact bytes to sign.</param>
    /// <returns>The signature bytes.</returns>
    /// <exception cref="ArgumentException"><paramref name="algorithm"/> is not a known signing algorithm.</exception>
    public static byte[] Sign(ECDsa key, string algorithm, ReadOnlySpan<byte> data) {
        ArgumentNullException.ThrowIfNull(argument: key);

        return key.SignData(
            data: data,
            hashAlgorithm: SigningHash(algorithm: algorithm),
            signatureFormat: DSASignatureFormat.IeeeP1363FixedFieldConcatenation
        );
    }
    /// <summary>Verifies a fixed-form signature against a public key carried as SubjectPublicKeyInfo: the import must
    /// consume every SPKI byte and the key must lie on the curve <paramref name="algorithm"/> names, so trailing
    /// identity bytes or a key on another curve never verify behind a name that promises this one.</summary>
    /// <param name="publicKeySubjectPublicKeyInfo">The signer's DER SubjectPublicKeyInfo bytes.</param>
    /// <param name="algorithm">The signing algorithm the key is pinned to.</param>
    /// <param name="data">The exact bytes that were signed.</param>
    /// <param name="signature">The IEEE P1363 signature.</param>
    /// <returns><see langword="true"/> only when the key imports whole, on the algorithm's curve, and the signature verifies;
    /// an unknown or non-signing algorithm, a malformed key or a wrong signature all answer <see langword="false"/>.</returns>
    public static bool VerifySignature(ReadOnlySpan<byte> publicKeySubjectPublicKeyInfo, string algorithm, ReadOnlySpan<byte> data, ReadOnlySpan<byte> signature) {
        using var ecdsa = TryImportPublicKey(publicKeySubjectPublicKeyInfo: publicKeySubjectPublicKeyInfo, algorithm: algorithm, hash: out var hash);

        if (ecdsa is null) {
            return false;
        }

        // IEEE P1363 fixed-field r‖s only: .NET refuses any length other than twice the field width, so no
        // alternate DER encoding of the same (r, s) is even a candidate. What this does NOT buy is signature
        // uniqueness — (r, s) and (r, n-s) are both valid for the same message, so signature bytes are never
        // an identity.
        return ecdsa.VerifyData(
            data: data,
            signature: signature,
            hashAlgorithm: hash,
            signatureFormat: DSASignatureFormat.IeeeP1363FixedFieldConcatenation
        );
    }
    /// <summary>Returns whether <paramref name="publicKeySubjectPublicKeyInfo"/> is exactly one SubjectPublicKeyInfo
    /// for a key on the curve the signing algorithm names — the check <see cref="VerifySignature"/> makes before it
    /// verifies, for a caller that accepts a key before any signature arrives.</summary>
    /// <param name="publicKeySubjectPublicKeyInfo">The candidate DER SubjectPublicKeyInfo bytes.</param>
    /// <param name="algorithm">The signing algorithm the key would be pinned to.</param>
    /// <returns><see langword="true"/> when the key imports whole and on the algorithm's curve.</returns>
    public static bool IsPublicKey(ReadOnlySpan<byte> publicKeySubjectPublicKeyInfo, string algorithm) {
        using var ecdsa = TryImportPublicKey(publicKeySubjectPublicKeyInfo: publicKeySubjectPublicKeyInfo, algorithm: algorithm, hash: out _);

        return (ecdsa is not null);
    }

    // Imports the key only when every SPKI byte is consumed and it lies on the curve the known signing algorithm
    // names; null otherwise, so no caller holds a key the name does not describe.
    private static ECDsa? TryImportPublicKey(ReadOnlySpan<byte> publicKeySubjectPublicKeyInfo, string algorithm, out HashAlgorithmName hash) {
        hash = default;
        if (SigningAlgorithm(algorithm: algorithm) is not { SignatureHash: { } signatureHash } descriptor) {
            return null;
        }

        var ecdsa = ECDsa.Create();

        try {
            ecdsa.ImportSubjectPublicKeyInfo(
                bytesRead: out var bytesRead,
                source: publicKeySubjectPublicKeyInfo
            );

            if (
                (bytesRead == publicKeySubjectPublicKeyInfo.Length) &&
                AttestationCurves.Matches(
                    key: ecdsa.ExportParameters(includePrivateParameters: false).Curve,
                    expected: descriptor.Curve
                )
            ) {
                hash = signatureHash;
                return ecdsa;
            }
        } catch (CryptographicException) {
        }
        ecdsa.Dispose();
        return null;
    }

    private static HashAlgorithmName SigningHash(string algorithm) => ((SigningAlgorithm(algorithm: algorithm) is { SignatureHash: { } hash })
        ? hash
        : throw new ArgumentException(
            message: $"'{algorithm}' is not an attestation SIGNING algorithm.",
            paramName: nameof(algorithm)
        ));
    // The one lookup every key operation here starts from: a known algorithm whose role is signing, or null.
    private static AttestationAlgorithmDescriptor? SigningAlgorithm(string? algorithm) => (
        (algorithm is not null) &&
        AttestationAlgorithms.IsKnown(algorithm: algorithm) &&
        (AttestationAlgorithms.Resolve(algorithm: algorithm) is { Role: AttestationKeyRole.Signing } descriptor)
    )
        ? descriptor
        : null;
}
