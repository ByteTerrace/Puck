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

        if (
            !AttestationAlgorithms.IsKnown(algorithm: algorithm) ||
            (AttestationAlgorithms.Resolve(algorithm: algorithm).Role != AttestationKeyRole.Signing)
        ) {
            throw new ArgumentException(
                message: $"'{algorithm}' is not an attestation SIGNING algorithm — only a signing key is imported here.",
                paramName: nameof(algorithm)
            );
        }

        var descriptor = AttestationAlgorithms.Resolve(algorithm: algorithm);
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
}
