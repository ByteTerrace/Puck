using System.Text;

namespace Puck.Carriage;

/// <summary>Optional, named capabilities layered on top of the mandatory carriage-v1 base profile.</summary>
[Flags]
public enum CarriageConformanceExtensions {
    /// <summary>Only the mandatory CBOR and ECDSA-P256-SHA256 base profile.</summary>
    None = 0,

    /// <summary>Accept and structurally validate sealed-carriage payloads.</summary>
    SealedCarriageV1 = (1 << 0),
}

/// <summary>
/// Hard resource ceilings shared by every named v1 profile. These are protocol acceptance limits, not
/// allocation hints: exceeding one is a deterministic refusal before signature work.
/// </summary>
public static class CarriageResourceLimits {
    /// <summary>Maximum encoded envelope size, including signature and framing.</summary>
    public const int EnvelopeBytes = (64 * 1024);

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
public sealed class CarriageConformanceProfile {
    private const CarriageConformanceExtensions AllKnownExtensions = CarriageConformanceExtensions.SealedCarriageV1;

    private CarriageConformanceProfile(string name, CarriageConformanceExtensions extensions) {
        Name = name;
        Extensions = extensions;
    }

    /// <summary>The mandatory interoperable profile: CBOR v1 and ECDSA-P256-SHA256 only.</summary>
    public static CarriageConformanceProfile Base { get; } = new(
        name: "carriage-v1-base",
        extensions: CarriageConformanceExtensions.None
    );

    /// <summary>The stable, verifier-side profile name. It never appears on the wire.</summary>
    public string Name { get; }

    /// <summary>The explicit extensions this verifier has enabled.</summary>
    public CarriageConformanceExtensions Extensions { get; }

    /// <summary>Creates a verifier-side profile by adding named extensions to the mandatory base.</summary>
    /// <param name="extensions">Only defined <see cref="CarriageConformanceExtensions"/> bits are accepted.</param>
    /// <returns>A new immutable profile. No parsing API constructs a profile from message data.</returns>
    public CarriageConformanceProfile WithExtensions(CarriageConformanceExtensions extensions) {
        if ((extensions & ~AllKnownExtensions) != 0) {
            throw new ArgumentOutOfRangeException(
                paramName: nameof(extensions),
                message: $"The carriage conformance extension mask contains undefined bits: 0x{(int)(extensions & ~AllKnownExtensions):X}."
            );
        }

        var combined = (Extensions | extensions);

        return new CarriageConformanceProfile(
            name: BuildName(extensions: combined),
            extensions: combined
        );
    }

    /// <summary>
    /// Decodes under this receiver-selected profile, enforcing the envelope ceiling before any parser sees
    /// attacker-controlled bytes and all projected ceilings immediately afterward.
    /// </summary>
    /// <param name="codec">The locally selected codec.</param>
    /// <param name="wire">The complete encoded envelope.</param>
    /// <exception cref="FormatException">The codec or envelope is outside this profile.</exception>
    public SignedCarriageEnvelope DecodeEnvelope(ICarriageCodec codec, ReadOnlySpan<byte> wire) {
        RequireCodec(codec: codec);

        if (wire.Length > CarriageResourceLimits.EnvelopeBytes) {
            throw new FormatException(message: $"The carriage envelope is {wire.Length} bytes; profile '{Name}' permits at most {CarriageResourceLimits.EnvelopeBytes}.");
        }

        var envelope = codec.DecodeEnvelope(wire: wire);

        if (!TryValidateEnvelope(
            codec: codec,
            envelope: envelope,
            label: "envelope",
            refusal: out var refusal
        )) {
            throw new FormatException(message: refusal);
        }

        return envelope;
    }

    /// <summary>
    /// Verifies an already-decoded claim under this receiver-selected profile. Profile and resource checks
    /// precede cryptographic work; the ordinary verifier remains the single chain-validation path.
    /// </summary>
    /// <param name="codec">The locally selected codec.</param>
    /// <param name="claim">The claim envelope.</param>
    /// <param name="chain">The claim's root-to-subject binding chain.</param>
    /// <param name="trustList">The receiver's trust policy.</param>
    /// <param name="now">The taped verification instant.</param>
    /// <param name="expectedPurpose">The receiver-authored purpose.</param>
    /// <param name="expectedAudience">The receiver-authored audience.</param>
    public CarriageVerifyResult VerifyChain(
        ICarriageCodec codec,
        SignedCarriageEnvelope claim,
        IReadOnlyList<SignedCarriageEnvelope>? chain,
        TrustList trustList,
        DateTimeOffset now,
        string expectedPurpose,
        string? expectedAudience
    ) {
        // Validate only the entry this claim can actually select. A disabled entry for some unrelated peer
        // must not poison every otherwise-valid claim in the trust list.
        var selectedEntry = (
            trustList.FindDirectSignerForVerification(domain: claim.Header.Domain, subject: claim.Header.Subject) ??
            trustList.FindVouchingRootForVerification(domain: claim.Header.Domain)
        );

        if (selectedEntry is not null) {
            if (!AllowsAlgorithm(algorithm: selectedEntry.PinnedId.Algorithm)) {
                return CarriageVerifyResult.Refuse(reason: $"selected trust entry names algorithm '{selectedEntry.PinnedId.Algorithm}', which verifier profile '{Name}' does not enable");
            }

            if (selectedEntry.PublicKeySubjectPublicKeyInfo.Length > CarriageResourceLimits.SubjectPublicKeyInfoBytes) {
                return CarriageVerifyResult.Refuse(reason: $"selected trust entry SPKI is {selectedEntry.PublicKeySubjectPublicKeyInfo.Length} bytes; profile '{Name}' permits at most {CarriageResourceLimits.SubjectPublicKeyInfoBytes}");
            }
        }

        if (!TryValidateEnvelope(
            codec: codec,
            envelope: claim,
            label: "claim",
            refusal: out var refusal
        )) {
            return CarriageVerifyResult.Refuse(reason: refusal!);
        }

        if (chain is not null) {
            foreach (var (binding, index) in chain.Select(selector: (value, index) => (value, index))) {
                if (!TryValidateEnvelope(
                    codec: codec,
                    envelope: binding,
                    label: $"binding {index + 1}",
                    refusal: out refusal
                )) {
                    return CarriageVerifyResult.Refuse(reason: refusal!);
                }
            }
        }

        // Binding targets and the terminal claim payload are profile-checked inside the verifier's
        // authenticated path, on the payloads it already decoded.
        return CarriageVerifier.VerifyChain(
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

    private bool TryValidateEnvelope(
        ICarriageCodec codec,
        SignedCarriageEnvelope envelope,
        string label,
        out string? refusal
    ) {
        if (!AllowsCodec(codec: codec)) {
            refusal = $"{label} uses codec '{codec.Name}', which verifier profile '{Name}' does not enable";

            return false;
        }

        // AllowsCodec above pinned the codec, so the complete encoded length is derived from the byte
        // lengths the envelope already carries rather than paid for with a re-encode. An envelope whose
        // parsed fields cannot encode at all is still refused, by the verifier's coherence check.
        var encodedLength = CborCarriageCodec.EncodedEnvelopeLength(envelope: envelope);

        if (encodedLength > CarriageResourceLimits.EnvelopeBytes) {
            refusal = $"{label}'s complete encoding is {encodedLength} bytes; profile '{Name}' permits at most {CarriageResourceLimits.EnvelopeBytes}";

            return false;
        }

        if (envelope.SignedPortionLength > CarriageResourceLimits.SignedPortionBytes) {
            refusal = $"{label}'s signed portion is {envelope.SignedPortionLength} bytes; profile '{Name}' permits at most {CarriageResourceLimits.SignedPortionBytes}";

            return false;
        }

        if (envelope.PayloadLength > CarriageResourceLimits.PayloadBytes) {
            refusal = $"{label}'s payload is {envelope.PayloadLength} bytes; profile '{Name}' permits at most {CarriageResourceLimits.PayloadBytes}";

            return false;
        }

        if (envelope.SignatureLength != CarriageResourceLimits.SignatureBytes) {
            refusal = $"{label}'s signature is {envelope.SignatureLength} bytes; profile '{Name}' permits exactly {CarriageResourceLimits.SignatureBytes}";

            return false;
        }

        if (!AllowsAlgorithm(algorithm: envelope.Header.Algorithm)) {
            refusal = $"{label} names algorithm '{envelope.Header.Algorithm}', which verifier profile '{Name}' does not enable";

            return false;
        }

        if (!ValidateHeaderText(header: envelope.Header, label: label, refusal: out refusal)) {
            return false;
        }

        if (
            (envelope.PayloadKind == CarriagePayloadKind.Sealed) &&
            !Includes(extension: CarriageConformanceExtensions.SealedCarriageV1)
        ) {
            refusal = $"{label} carries a sealed payload, but verifier profile '{Name}' does not enable '{ExtensionName(CarriageConformanceExtensions.SealedCarriageV1)}'";

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

        if (payload.EphemeralPublicKeySubjectPublicKeyInfo.Length > CarriageResourceLimits.SubjectPublicKeyInfoBytes) {
            refusal = $"{label}'s ephemeral SPKI is {payload.EphemeralPublicKeySubjectPublicKeyInfo.Length} bytes; profile '{Name}' permits at most {CarriageResourceLimits.SubjectPublicKeyInfoBytes}";

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

        if (payload.PublicKeySubjectPublicKeyInfo.Length > CarriageResourceLimits.SubjectPublicKeyInfoBytes) {
            refusal = $"{label}'s target SPKI is {payload.PublicKeySubjectPublicKeyInfo.Length} bytes; profile '{Name}' permits at most {CarriageResourceLimits.SubjectPublicKeyInfoBytes}";

            return false;
        }

        return ValidateKeyIdText(id: payload.TargetId, label: $"{label}'s target", refusal: out refusal);
    }

    private bool AllowsCodec(ICarriageCodec codec) => (codec is CborCarriageCodec);

    /// <summary>Whether this receiver-selected profile permits <paramref name="algorithm"/>.</summary>
    public bool AllowsAlgorithm(string algorithm) =>
        string.Equals(a: algorithm, b: CarriageAlgorithms.EcdsaP256Sha256, comparisonType: StringComparison.Ordinal) ||
        (
            Includes(extension: CarriageConformanceExtensions.SealedCarriageV1) &&
            string.Equals(a: algorithm, b: CarriageAlgorithms.EcdhP256HkdfSha256Aes256Gcm, comparisonType: StringComparison.Ordinal)
        );

    private bool Includes(CarriageConformanceExtensions extension) => ((Extensions & extension) == extension);

    private void RequireCodec(ICarriageCodec codec) {
        if (!AllowsCodec(codec: codec)) {
            throw new FormatException(message: $"Codec '{codec.Name}' is not enabled by verifier profile '{Name}'. The wire carries no profile selector and cannot enable it.");
        }
    }

    private static bool ValidateHeaderText(CarriageEnvelopeHeader header, string label, out string? refusal) =>
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
            (Encoding.UTF8.GetByteCount(s: value) > CarriageResourceLimits.TextStringUtf8Bytes)
        ) {
            refusal = $"{field} exceeds the profile limit of {CarriageResourceLimits.TextStringUtf8Bytes} UTF-8 bytes";

            return false;
        }

        refusal = null;

        return true;
    }

    private static string BuildName(CarriageConformanceExtensions extensions) {
        if (extensions == CarriageConformanceExtensions.None) {
            return Base.Name;
        }

        var names = Enum.GetValues<CarriageConformanceExtensions>()
            .Where(predicate: value => (value != CarriageConformanceExtensions.None) && ((extensions & value) == value))
            .Select(selector: ExtensionName);

        return $"carriage-v1-base+{string.Join(separator: "+", values: names)}";
    }

    private static string ExtensionName(CarriageConformanceExtensions extension) => extension switch {
        CarriageConformanceExtensions.SealedCarriageV1 => "sealed-carriage-v1",
        _ => throw new ArgumentOutOfRangeException(
            paramName: nameof(extension),
            actualValue: extension,
            message: "A conformance profile name can contain only defined, single extension values."
        ),
    };
}
