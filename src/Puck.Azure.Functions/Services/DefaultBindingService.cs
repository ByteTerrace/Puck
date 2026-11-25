using Microsoft.Extensions.Options;
using Puck.Attestation;
using System.Security.Cryptography;

namespace Puck.Azure.Functions.Services;

public sealed class MintSubjectKeyBindingRequest {
    /// <summary>The stored key's type, e.g. "ecdsa" or "ecdh". Names the algorithm of the key being vouched FOR, which is carried in the binding's payload — the envelope's own algorithm field always names the ISSUING key that signed it.</summary>
    public required string KeyType { get; init; }
    /// <summary>SubjectPublicKeyInfo bytes of the subject's public key.</summary>
    public required byte[] PublicKey { get; init; }
    /// <summary>The subject the binding is minted for, e.g. the user's object id.</summary>
    public required string SubjectId { get; init; }
}
/// <param name="KeyId">The destination-shaped id (domain/subject/algorithm/sha256:fingerprint) this binding vouches for.</param>
/// <param name="Binding">The signed attestation's wire bytes.</param>
/// <param name="NotBefore">The timestamp before which the binding is not yet valid.</param>
/// <param name="NotAfter">The timestamp after which the binding expires.</param>
public sealed record class BindingResult(
    string KeyId,
    byte[] Binding,
    DateTimeOffset NotBefore,
    DateTimeOffset NotAfter
);
/// <summary>
/// Mints and re-attests key bindings using Puck.Attestation.
/// </summary>
public interface IBindingService {
    /// <summary>Mints a NEW key binding: the issuing key vouches for a subject's public key, minting the destination-shaped id fresh.</summary>
    Task<BindingResult> MintSubjectKeyBindingAsync(
        MintSubjectKeyBindingRequest request,
        DateTimeOffset now,
        CancellationToken cancellationToken
    );
    /// <summary>
    /// Re-attests an EXISTING binding: verifies it was signed by this service's issuing key for
    /// the configured domain and purpose and matches the authenticated caller's selected key,
    /// then re-signs the SAME subject and payload with a fresh validity window.
    /// </summary>
    Task<BindingResult> ReattestAsync(
        byte[] existingBinding,
        MintSubjectKeyBindingRequest request,
        DateTimeOffset now,
        CancellationToken cancellationToken
    );
}
public sealed class DefaultBindingService(IOptionsMonitor<IssuingKeyOptions> issuingKeyOptions) : IBindingService {
    private static readonly IAttestationCodec Codec = new CborAttestationCodec();

    private static string FormatKeyId(KeyId keyId) =>
        $"{keyId.Domain}/{keyId.Subject}/{keyId.Algorithm}/sha256:{keyId.KeyHash}";
    /// <summary>
    /// Maps a stored key's type to the wire specification's registry name.
    /// </summary>
    private static string RegistryAlgorithmFor(string keyType) =>
        keyType.ToLowerInvariant() switch {
            "ecdh" => AttestationAlgorithms.EcdhP256HkdfSha256Aes256Gcm,
            "ecdsa" => AttestationAlgorithms.EcdsaP256Sha256,
            _ => throw new ArgumentException(
            message: $"'{keyType}' has no algorithm in the attestation registry, so a binding for it could never be verified.",
            paramName: nameof(keyType)
        ),
        };
    private static string RequireValue(string? value, string name) {
        ArgumentException.ThrowIfNullOrWhiteSpace(
            argument: value,
            paramName: name
        );

        return value;
    }
    private static DateTimeOffset ToWholeSeconds(DateTimeOffset value) =>
        DateTimeOffset.FromUnixTimeSeconds(seconds: value.ToUnixTimeSeconds());

    // The configured root's domain and the issuing key every binding is signed with.
    private (string Domain, ECDsa IssuingKey, IssuingKeyOptions Options) LoadIssuer() {
        var options = issuingKeyOptions.CurrentValue;

        using var rootPublicKey = ECDsa.Create();

        rootPublicKey.ImportFromPem(input: RequireValue(
            name: nameof(IssuingKeyOptions.RootPublicKeyPem),
            value: options.RootPublicKeyPem
        ));

        var issuingKey = ECDsa.Create();

        try {
            issuingKey.ImportFromEncryptedPem(
                input: RequireValue(
                    name: nameof(IssuingKeyOptions.IssuingPrivateKeyPem),
                    value: options.IssuingPrivateKeyPem
                ),
                password: RequireValue(
                    name: nameof(IssuingKeyOptions.IssuingPrivateKeyPassword),
                    value: options.IssuingPrivateKeyPassword
                )
            );
        } catch {
            issuingKey.Dispose();
            throw;
        }

        return (KeyId.ComputeKeyHash(subjectPublicKeyInfo: rootPublicKey.ExportSubjectPublicKeyInfo()), issuingKey, options);
    }
    // Signs a binding of the target key for a fresh validity window from now.
    private static Task<BindingResult> Bind(string domain, ECDsa issuingKey, IssuingKeyOptions options, KeyId targetId, ReadOnlyMemory<byte> targetSubjectPublicKeyInfo, DateTimeOffset now) {
        var notBefore = ToWholeSeconds(value: now);
        var notAfter = ToWholeSeconds(value: now.Add(timeSpan: options.SubjectBindingValidity));
        var signedAttestation = AttestationSigner.SignKeyBinding(
            codec: Codec,
            domain: domain,
            notAfter: notAfter.ToUnixTimeSeconds(),
            notBefore: notBefore.ToUnixTimeSeconds(),
            signerAlgorithm: AttestationAlgorithms.EcdsaP256Sha256,
            signerKey: issuingKey,
            targetId: targetId,
            targetSubjectPublicKeyInfo: targetSubjectPublicKeyInfo
        );

        return Task.FromResult(result: new BindingResult(
            Binding: Codec.EncodeAttestation(attestation: signedAttestation),
            KeyId: FormatKeyId(keyId: targetId),
            NotAfter: notAfter,
            NotBefore: notBefore
        ));
    }

    public Task<BindingResult> MintSubjectKeyBindingAsync(
        MintSubjectKeyBindingRequest request,
        DateTimeOffset now,
        CancellationToken cancellationToken
    ) {
        cancellationToken.ThrowIfCancellationRequested();

        var (domain, issuingKey, options) = LoadIssuer();

        using (issuingKey) {
            var targetId = KeyId.ForSubject(
                algorithm: RegistryAlgorithmFor(keyType: request.KeyType),
                domain: domain,
                subject: request.SubjectId,
                subjectPublicKeyInfo: request.PublicKey
            );

            return Bind(domain: domain, issuingKey: issuingKey, now: now, options: options, targetId: targetId, targetSubjectPublicKeyInfo: request.PublicKey);
        }
    }
    public Task<BindingResult> ReattestAsync(
        byte[] existingBinding,
        MintSubjectKeyBindingRequest request,
        DateTimeOffset now,
        CancellationToken cancellationToken
    ) {
        cancellationToken.ThrowIfCancellationRequested();

        var (domain, issuingKey, options) = LoadIssuer();

        using (issuingKey) {
            var previous = Codec.DecodeAttestation(wire: existingBinding);

            if (
                !string.Equals(a: previous.Header.Domain, b: domain, comparisonType: StringComparison.Ordinal) ||
                !string.Equals(a: previous.Header.Purpose, b: AttestationPurposes.KeyBinding, comparisonType: StringComparison.Ordinal)
            ) {
                throw new InvalidOperationException(message: "Cannot re-attest an attestation with invalid domain or purpose.");
            }
            if (!AttestationKeys.VerifySignature(
                algorithm: previous.Header.Algorithm,
                data: previous.SignedPortion.Span,
                publicKeySubjectPublicKeyInfo: issuingKey.ExportSubjectPublicKeyInfo(),
                signature: previous.Signature.Span
            )) {
                throw new InvalidOperationException(message: "Cannot re-attest a binding that does not verify against the issuing key.");
            }

            var keyBindingPayload = Codec.DecodeKeyBindingPayload(bytes: previous.PayloadBytes.Span);
            var expectedId = KeyId.ForSubject(
                algorithm: RegistryAlgorithmFor(keyType: request.KeyType),
                domain: domain,
                subject: request.SubjectId,
                subjectPublicKeyInfo: request.PublicKey
            );

            // The caller owns the storage containing this public binding. A valid issuer signature alone
            // cannot authorize renewal: another account's binding can be copied into that storage.
            if (
                (keyBindingPayload.TargetId != expectedId) ||
                !keyBindingPayload.PublicKeySubjectPublicKeyInfo.Span.SequenceEqual(other: request.PublicKey)
            ) {
                throw new InvalidOperationException(message: "Cannot re-attest a binding for another subject or key.");
            }

            return Bind(domain: domain, issuingKey: issuingKey, now: now, options: options, targetId: keyBindingPayload.TargetId, targetSubjectPublicKeyInfo: keyBindingPayload.PublicKeySubjectPublicKeyInfo);
        }
    }
}
