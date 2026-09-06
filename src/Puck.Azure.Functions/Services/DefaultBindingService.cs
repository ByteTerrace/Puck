using Microsoft.Extensions.Options;
using Puck.Attestation;
using System.Security.Cryptography;

namespace Puck.Azure.Functions.Services;

public sealed class MintSubjectKeyBindingRequest
{
    /// <summary>The subject the binding is minted for, e.g. the user's object id.</summary>
    public required string SubjectId { get; init; }
    /// <summary>The stored key's type, e.g. "ecdsa" or "ecdh". Names the algorithm of the key being vouched FOR, which is carried in the binding's payload — the envelope's own algorithm field always names the ISSUING key that signed it.</summary>
    public required string KeyType { get; init; }
    /// <summary>SubjectPublicKeyInfo bytes of the subject's public key.</summary>
    public required byte[] PublicKey { get; init; }
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
public interface IBindingService
{
    /// <summary>Mints a NEW key binding: the issuing key vouches for a subject's public key, minting the destination-shaped id fresh.</summary>
    Task<BindingResult> MintSubjectKeyBindingAsync(
        MintSubjectKeyBindingRequest request,
        DateTimeOffset now,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// Re-attests an EXISTING binding: verifies it was signed by this service's issuing key for
    /// the configured domain and purpose, then re-signs the SAME subject and payload with a fresh validity window.
    /// </summary>
    Task<BindingResult> ReattestAsync(
        byte[] existingBinding,
        DateTimeOffset now,
        CancellationToken cancellationToken
    );
}

public sealed class DefaultBindingService(IOptionsMonitor<IssuingKeyOptions> issuingKeyOptions) : IBindingService
{
    private static readonly IAttestationCodec Codec = new CborAttestationCodec();

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

    private static DateTimeOffset ToWholeSeconds(DateTimeOffset value) =>
        DateTimeOffset.FromUnixTimeSeconds(seconds: value.ToUnixTimeSeconds());

    private static string RequireValue(string? value, string name) {
        ArgumentException.ThrowIfNullOrWhiteSpace(
            argument: value,
            paramName: name
        );

        return value;
    }

    private static string FormatKeyId(KeyId keyId) =>
        $"{keyId.Domain}/{keyId.Subject}/{keyId.Algorithm}/sha256:{keyId.KeyHash}";

    public Task<BindingResult> MintSubjectKeyBindingAsync(
        MintSubjectKeyBindingRequest request,
        DateTimeOffset now,
        CancellationToken cancellationToken
    ) {
        cancellationToken.ThrowIfCancellationRequested();

        var options = issuingKeyOptions.CurrentValue;

        using var rootPublicKey = ECDsa.Create();
        rootPublicKey.ImportFromPem(input: RequireValue(
            name: nameof(IssuingKeyOptions.RootPublicKeyPem),
            value: options.RootPublicKeyPem
        ));

        var domain = KeyId.ComputeKeyHash(subjectPublicKeyInfo: rootPublicKey.ExportSubjectPublicKeyInfo());

        using var issuingKey = ECDsa.Create();
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

        var targetId = KeyId.ForSubject(
            algorithm: RegistryAlgorithmFor(keyType: request.KeyType),
            domain: domain,
            subject: request.SubjectId,
            subjectPublicKeyInfo: request.PublicKey
        );

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
            targetSubjectPublicKeyInfo: request.PublicKey
        );

        var bindingBytes = Codec.EncodeAttestation(attestation: signedAttestation);

        return Task.FromResult(result: new BindingResult(
            Binding: bindingBytes,
            KeyId: FormatKeyId(keyId: targetId),
            NotAfter: notAfter,
            NotBefore: notBefore
        ));
    }

    public Task<BindingResult> ReattestAsync(
        byte[] existingBinding,
        DateTimeOffset now,
        CancellationToken cancellationToken
    ) {
        cancellationToken.ThrowIfCancellationRequested();

        var options = issuingKeyOptions.CurrentValue;

        using var rootPublicKey = ECDsa.Create();
        rootPublicKey.ImportFromPem(input: RequireValue(
            name: nameof(IssuingKeyOptions.RootPublicKeyPem),
            value: options.RootPublicKeyPem
        ));

        var domain = KeyId.ComputeKeyHash(subjectPublicKeyInfo: rootPublicKey.ExportSubjectPublicKeyInfo());

        using var issuingKey = ECDsa.Create();
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

        var previous = Codec.DecodeAttestation(wire: existingBinding);

        if (!string.Equals(a: previous.Header.Domain, b: domain, comparisonType: StringComparison.Ordinal) ||
            !string.Equals(a: previous.Header.Purpose, b: AttestationPurposes.KeyBinding, comparisonType: StringComparison.Ordinal)) {
            throw new InvalidOperationException(message: "Cannot re-attest an attestation with invalid domain or purpose.");
        }

        var descriptor = AttestationAlgorithms.Resolve(algorithm: previous.Header.Algorithm);
        if (descriptor.SignatureHash is null ||
            !issuingKey.VerifyData(
                data: previous.SignedPortion.Span,
                hashAlgorithm: descriptor.SignatureHash.Value,
                signature: previous.Signature.Span,
                signatureFormat: DSASignatureFormat.IeeeP1363FixedFieldConcatenation
            )) {
            throw new InvalidOperationException(message: "Cannot re-attest a binding that does not verify against the issuing key.");
        }

        var notBefore = ToWholeSeconds(value: now);
        var notAfter = ToWholeSeconds(value: now.Add(timeSpan: options.SubjectBindingValidity));

        var keyBindingPayload = Codec.DecodeKeyBindingPayload(bytes: previous.PayloadBytes.Span);

        var reattested = AttestationSigner.SignKeyBinding(
            codec: Codec,
            domain: domain,
            notAfter: notAfter.ToUnixTimeSeconds(),
            notBefore: notBefore.ToUnixTimeSeconds(),
            signerAlgorithm: AttestationAlgorithms.EcdsaP256Sha256,
            signerKey: issuingKey,
            targetId: keyBindingPayload.TargetId,
            targetSubjectPublicKeyInfo: keyBindingPayload.PublicKeySubjectPublicKeyInfo
        );

        var bindingBytes = Codec.EncodeAttestation(attestation: reattested);

        return Task.FromResult(result: new BindingResult(
            Binding: bindingBytes,
            KeyId: FormatKeyId(keyId: keyBindingPayload.TargetId),
            NotAfter: notAfter,
            NotBefore: notBefore
        ));
    }
}

