using System.Text;

namespace Puck.Attestation;

/// <summary>Optional, named capabilities layered on top of the mandatory attestation-v1 base profile.</summary>
[Flags]
public enum AttestationExtensions {
    /// <summary>Only the mandatory CBOR and ECDSA-P256-SHA256 base profile.</summary>
    None = 0,

    /// <summary>Accept and structurally validate sealed-attestation payloads.</summary>
    SealedAttestationV1 = (1 << 0),
}

/// <summary>
/// Hard resource ceilings shared by every named v1 profile. These are protocol acceptance limits, not
/// allocation hints: exceeding one is a deterministic refusal before signature work.
/// </summary>
public static class AttestationResourceLimits {
    /// <summary>Maximum encoded attestation size, including signature and framing.</summary>
    public const int AttestationBytes = (64 * 1024);

    /// <summary>Maximum exact signed-portion size.</summary>
    public const int SignedPortionBytes = (60 * 1024);

    /// <summary>Maximum opaque, key-binding, or sealed payload size.</summary>
    public const int PayloadBytes = (48 * 1024);

    /// <summary>Maximum UTF-8 byte count of any text field.</summary>
    public const int TextStringUtf8Bytes = 256;

    /// <summary>Maximum DER SPKI byte count.</summary>
    public const int SubjectPublicKeyInfoBytes = 512;

    /// <summary>P-256 P1363 signatures are exactly 64 bytes, so the resource ceiling is exact too.</summary>
    public const int SignatureBytes = 64;
}

/// <summary>
/// A verifier-authored conformance profile. The message carries no profile name and cannot add an
/// extension: the receiver selects one of these objects out of band and keeps using it for decode and
/// verification. The base profile is CBOR v1 plus ECDSA-P256-SHA256; every other capability is explicit.
/// </summary>
public sealed class AttestationProfile {
    private const AttestationExtensions AllKnownExtensions = AttestationExtensions.SealedAttestationV1;

    private AttestationProfile(string name, AttestationExtensions extensions) {
        Name = name;
        Extensions = extensions;
    }

    /// <summary>The mandatory interoperable profile: CBOR v1 and ECDSA-P256-SHA256 only.</summary>
    public static AttestationProfile Base { get; } = new(
        name: "attestation-v1-base",
        extensions: AttestationExtensions.None
    );

    /// <summary>The stable, verifier-side profile name. It never appears on the wire.</summary>
    public string Name { get; }

    /// <summary>The explicit extensions this verifier has enabled.</summary>
    public AttestationExtensions Extensions { get; }

    /// <summary>Creates a verifier-side profile by adding named extensions to the mandatory base.</summary>
    /// <param name="extensions">Only defined <see cref="AttestationExtensions"/> bits are accepted.</param>
    /// <returns>A new immutable profile. No parsing API constructs a profile from message data.</returns>
    public AttestationProfile WithExtensions(AttestationExtensions extensions) {
        if ((extensions & ~AllKnownExtensions) != 0) {
            throw new ArgumentOutOfRangeException(
                paramName: nameof(extensions),
                message: $"The attestation conformance extension mask contains undefined bits: 0x{(int)(extensions & ~AllKnownExtensions):X}."
            );
        }

        var combined = (Extensions | extensions);

        return new AttestationProfile(
            name: BuildName(extensions: combined),
            extensions: combined
        );
    }

    /// <summary>
    /// Decodes under this receiver-selected profile, enforcing the attestation ceiling before any parser sees
    /// attacker-controlled bytes and all projected ceilings immediately afterward.
    /// </summary>
    /// <param name="codec">The locally selected codec.</param>
    /// <param name="wire">The complete encoded attestation.</param>
    /// <exception cref="FormatException">The codec or attestation is outside this profile.</exception>
    public SignedAttestation DecodeAttestation(IAttestationCodec codec, ReadOnlySpan<byte> wire) {
        RequireCodec(codec: codec);

        if (wire.Length > AttestationResourceLimits.AttestationBytes) {
            throw new FormatException(message: $"The attestation is {wire.Length} bytes; profile '{Name}' permits at most {AttestationResourceLimits.AttestationBytes}.");
        }

        var attestation = codec.DecodeAttestation(wire: wire);

        if (!TryValidateAttestation(
            codec: codec,
            attestation: attestation,
            label: "attestation",
            refusal: out var refusal
        )) {
            throw new FormatException(message: refusal);
        }

        return attestation;
    }

    /// <summary>
    /// Verifies an already-decoded claim under this receiver-selected profile. Profile and resource checks
    /// precede cryptographic work; the ordinary verifier remains the single chain-validation path.
    /// </summary>
    /// <param name="codec">The locally selected codec.</param>
    /// <param name="claim">The attestation carrying the claim.</param>
    /// <param name="chain">The claim's root-to-subject binding chain.</param>
    /// <param name="trustList">The receiver's trust policy.</param>
    /// <param name="now">The taped verification instant.</param>
    /// <param name="expectedPurpose">The receiver-authored purpose.</param>
    /// <param name="expectedAudience">The receiver-authored audience.</param>
    /// <returns>The verification result, including any slot-scoped replay commitment requirement.</returns>
    public AttestationVerifyResult VerifyChain(
        IAttestationCodec codec,
        SignedAttestation claim,
        IReadOnlyList<SignedAttestation>? chain,
        TrustList trustList,
        DateTimeOffset now,
        string expectedPurpose,
        string? expectedAudience
    ) {
        if (chain is { Count: > 2 }) {
            return AttestationVerifyResult.Refuse(reason: $"broken chain: expected at most two bindings, found {chain.Count}");
        }

        // Validate only the entry this claim can actually select. A disabled entry for some unrelated peer
        // must not poison every otherwise-valid claim in the trust list.
        var selectedEntry = (
            trustList.FindDirectSignerForVerification(domain: claim.Header.Domain, subject: claim.Header.Subject) ??
            trustList.FindVouchingRootForVerification(domain: claim.Header.Domain)
        );

        if (selectedEntry is not null) {
            if (!AllowsAlgorithm(algorithm: selectedEntry.PinnedId.Algorithm)) {
                return AttestationVerifyResult.Refuse(reason: $"selected trust entry names algorithm '{selectedEntry.PinnedId.Algorithm}', which verifier profile '{Name}' does not enable");
            }

            if (selectedEntry.PublicKeySubjectPublicKeyInfo.Length > AttestationResourceLimits.SubjectPublicKeyInfoBytes) {
                return AttestationVerifyResult.Refuse(reason: $"selected trust entry SPKI is {selectedEntry.PublicKeySubjectPublicKeyInfo.Length} bytes; profile '{Name}' permits at most {AttestationResourceLimits.SubjectPublicKeyInfoBytes}");
            }
        }

        if (!TryValidateAttestation(
            codec: codec,
            attestation: claim,
            label: "claim",
            refusal: out var refusal
        )) {
            return AttestationVerifyResult.Refuse(reason: refusal!);
        }

        if (chain is not null) {
            foreach (var (binding, index) in chain.Select(selector: (value, index) => (value, index))) {
                if (!TryValidateAttestation(
                    codec: codec,
                    attestation: binding,
                    label: $"binding {index + 1}",
                    refusal: out refusal
                )) {
                    return AttestationVerifyResult.Refuse(reason: refusal!);
                }
            }
        }

        // Binding targets and the terminal claim payload are profile-checked inside the verifier's
        // authenticated path, on the payloads it already decoded.
        return AttestationVerifier.VerifyChain(
            codec: codec,
            claim: claim,
            chain: chain,
            trustList: trustList,
            now: now,
            expectedPurpose: expectedPurpose,
            expectedAudience: expectedAudience,
            profile: this
        );
    }

    private bool TryValidateAttestation(
        IAttestationCodec codec,
        SignedAttestation attestation,
        string label,
        out string? refusal
    ) {
        if (!AllowsCodec(codec: codec)) {
            refusal = $"{label} uses codec '{codec.Name}', which verifier profile '{Name}' does not enable";

            return false;
        }

        // AllowsCodec above pinned the codec, so the complete encoded length is derived from the byte
        // lengths the attestation already carries rather than paid for with a re-encode. An attestation whose
        // parsed fields cannot encode at all is still refused, by the verifier's coherence check.
        var encodedLength = CborAttestationCodec.EncodedAttestationLength(attestation: attestation);

        if (encodedLength > AttestationResourceLimits.AttestationBytes) {
            refusal = $"{label}'s complete encoding is {encodedLength} bytes; profile '{Name}' permits at most {AttestationResourceLimits.AttestationBytes}";

            return false;
        }

        if (attestation.SignedPortionLength > AttestationResourceLimits.SignedPortionBytes) {
            refusal = $"{label}'s signed portion is {attestation.SignedPortionLength} bytes; profile '{Name}' permits at most {AttestationResourceLimits.SignedPortionBytes}";

            return false;
        }

        if (attestation.PayloadLength > AttestationResourceLimits.PayloadBytes) {
            refusal = $"{label}'s payload is {attestation.PayloadLength} bytes; profile '{Name}' permits at most {AttestationResourceLimits.PayloadBytes}";

            return false;
        }

        if (attestation.SignatureLength != AttestationResourceLimits.SignatureBytes) {
            refusal = $"{label}'s signature is {attestation.SignatureLength} bytes; profile '{Name}' permits exactly {AttestationResourceLimits.SignatureBytes}";

            return false;
        }

        if (!AllowsAlgorithm(algorithm: attestation.Header.Algorithm)) {
            refusal = $"{label} names algorithm '{attestation.Header.Algorithm}', which verifier profile '{Name}' does not enable";

            return false;
        }

        if (!ValidateHeaderText(header: attestation.Header, label: label, refusal: out refusal)) {
            return false;
        }

        if (
            (attestation.PayloadKind == AttestationPayloadKind.Sealed) &&
            !Includes(extension: AttestationExtensions.SealedAttestationV1)
        ) {
            refusal = $"{label} carries a sealed payload, but verifier profile '{Name}' does not enable '{ExtensionName(AttestationExtensions.SealedAttestationV1)}'";

            return false;
        }

        refusal = null;

        return true;
    }

    /// <summary>Checks a verified sealed claim payload's nested profile constraints — called by the verifier on the payload it already decoded, so the sealed payload is decoded once per claim.</summary>
    internal bool TryValidateSealedPayload(SealedPayload payload, string label, out string? refusal) {
        if (!AllowsAlgorithm(algorithm: payload.RecipientId.Algorithm)) {
            refusal = $"{label}'s recipient names algorithm '{payload.RecipientId.Algorithm}', which verifier profile '{Name}' does not enable";

            return false;
        }

        if (payload.EphemeralPublicKeySubjectPublicKeyInfo.Length > AttestationResourceLimits.SubjectPublicKeyInfoBytes) {
            refusal = $"{label}'s ephemeral SPKI is {payload.EphemeralPublicKeySubjectPublicKeyInfo.Length} bytes; profile '{Name}' permits at most {AttestationResourceLimits.SubjectPublicKeyInfoBytes}";

            return false;
        }

        return ValidateKeyIdText(id: payload.RecipientId, label: $"{label}'s recipient", refusal: out refusal);
    }

    /// <summary>Checks an authenticated binding's nested profile constraints before its target key is used for the next cryptographic hop.</summary>
    internal bool TryValidateKeyBindingPayload(KeyBindingPayload payload, string label, out string? refusal) {
        if (!AllowsAlgorithm(algorithm: payload.TargetId.Algorithm)) {
            refusal = $"{label}'s target names algorithm '{payload.TargetId.Algorithm}', which verifier profile '{Name}' does not enable";

            return false;
        }

        if (payload.PublicKeySubjectPublicKeyInfo.Length > AttestationResourceLimits.SubjectPublicKeyInfoBytes) {
            refusal = $"{label}'s target SPKI is {payload.PublicKeySubjectPublicKeyInfo.Length} bytes; profile '{Name}' permits at most {AttestationResourceLimits.SubjectPublicKeyInfoBytes}";

            return false;
        }

        return ValidateKeyIdText(id: payload.TargetId, label: $"{label}'s target", refusal: out refusal);
    }

    private bool AllowsCodec(IAttestationCodec codec) => (codec is CborAttestationCodec);

    /// <summary>Whether this receiver-selected profile permits <paramref name="algorithm"/>.</summary>
    public bool AllowsAlgorithm(string algorithm) =>
        string.Equals(a: algorithm, b: AttestationAlgorithms.EcdsaP256Sha256, comparisonType: StringComparison.Ordinal) ||
        (
            Includes(extension: AttestationExtensions.SealedAttestationV1) &&
            string.Equals(a: algorithm, b: AttestationAlgorithms.EcdhP256HkdfSha256Aes256Gcm, comparisonType: StringComparison.Ordinal)
        );

    private bool Includes(AttestationExtensions extension) => ((Extensions & extension) == extension);

    private void RequireCodec(IAttestationCodec codec) {
        if (!AllowsCodec(codec: codec)) {
            throw new FormatException(message: $"Codec '{codec.Name}' is not enabled by verifier profile '{Name}'. The wire carries no profile selector and cannot enable it.");
        }
    }

    private static bool ValidateHeaderText(AttestationHeader header, string label, out string? refusal) =>
        ValidateText(value: header.Subject, field: $"{label} subject", refusal: out refusal) &&
        ValidateText(value: header.Algorithm, field: $"{label} algorithm", refusal: out refusal) &&
        ValidateText(value: header.Purpose, field: $"{label} purpose", refusal: out refusal) &&
        ValidateText(value: header.Audience, field: $"{label} audience", refusal: out refusal);

    private static bool ValidateKeyIdText(KeyId id, string label, out string? refusal) =>
        ValidateText(value: id.Subject, field: $"{label} subject", refusal: out refusal) &&
        ValidateText(value: id.Algorithm, field: $"{label} algorithm", refusal: out refusal);

    private static bool ValidateText(string? value, string field, out string? refusal) {
        if (
            (value is not null) &&
            (Encoding.UTF8.GetByteCount(s: value) > AttestationResourceLimits.TextStringUtf8Bytes)
        ) {
            refusal = $"{field} exceeds the profile limit of {AttestationResourceLimits.TextStringUtf8Bytes} UTF-8 bytes";

            return false;
        }

        refusal = null;

        return true;
    }

    private static string BuildName(AttestationExtensions extensions) {
        if (extensions == AttestationExtensions.None) {
            return Base.Name;
        }

        var names = Enum.GetValues<AttestationExtensions>()
            .Where(predicate: value => (value != AttestationExtensions.None) && ((extensions & value) == value))
            .Select(selector: ExtensionName);

        return $"attestation-v1-base+{string.Join(separator: "+", values: names)}";
    }

    private static string ExtensionName(AttestationExtensions extension) => extension switch {
        AttestationExtensions.SealedAttestationV1 => "sealed-attestation-v1",
        _ => throw new ArgumentOutOfRangeException(
            paramName: nameof(extension),
            actualValue: extension,
            message: "A conformance profile name can contain only defined, single extension values."
        ),
    };
}
