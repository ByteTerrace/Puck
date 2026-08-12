namespace Puck.Carriage;

/// <summary>
/// The canonical context header that is always part of a carriage envelope's signing input
/// (README.md, "Signed carriage"): the associated-data half of AEAD applied to signatures, so a
/// signature cannot be lifted into a situation it was never minted for. Only the payload differs between a
/// key binding and a claim — see <see cref="CarriagePurposes.KeyBinding"/>.
/// </summary>
/// <param name="Domain">The root fingerprint of the chain the signer belongs to.</param>
/// <param name="Subject">The signer's platform user id, or <see langword="null"/> when the signer is a root or issuing key.</param>
/// <param name="Algorithm">The signer's claimed algorithm. Checked against the pinned key's algorithm at verify time — never used to select it (the algorithm rule).</param>
/// <param name="Purpose">What this envelope is for. <see cref="CarriagePurposes.KeyBinding"/> is reserved; every other value is game-defined and stops a binding being replayed as a claim.</param>
/// <param name="NotBefore">The issuer-authored validity window start, Unix seconds.</param>
/// <param name="NotAfter">The issuer-authored validity window end, Unix seconds. The tighter of this and the verifier's own maximum age governs.</param>
/// <param name="Audience">The one world this claim is valid at, or <see langword="null"/> for a bearer claim that travels anywhere and needs a durable sequence instead.</param>
/// <param name="Sequence">The bearer claim's per-(issuer, subject) sequence number, or <see langword="null"/> for a directed claim.</param>
public sealed record CarriageEnvelopeHeader(
    string Domain,
    string? Subject,
    string Algorithm,
    string Purpose,
    long NotBefore,
    long NotAfter,
    string? Audience,
    ulong? Sequence
);

/// <summary>Reserved envelope purposes. Every other purpose string is game-defined and opaque to the engine.</summary>
public static class CarriagePurposes {
    /// <summary>
    /// Reserved purpose for a key binding — the envelope's payload is a <see cref="KeyBindingPayload"/>
    /// naming the key being vouched for. A key binding is not a separate artifact: it is this purpose value
    /// on an ordinary envelope. No claim may use this purpose, and a binding presented where a claim's
    /// purpose is expected is refused (the purpose replay rule).
    /// </summary>
    public const string KeyBinding = "key-binding";
}

/// <summary>Which shape <see cref="SignedCarriageEnvelope.PayloadBytes"/> decodes as.</summary>
public enum CarriagePayloadKind : byte {
    /// <summary>Caller-defined claim bytes; the engine does not interpret them.</summary>
    Opaque = 1,

    /// <summary>A <see cref="KeyBindingPayload"/> — used only with <see cref="CarriagePurposes.KeyBinding"/>.</summary>
    KeyBinding = 2,

    /// <summary>A <see cref="SealedPayload"/> — the same envelope shape with the payload AEAD-encrypted.</summary>
    Sealed = 3,
}

/// <summary>
/// A key binding's payload: the id of the key being vouched for, self-certified by carrying the actual
/// public key bytes alongside it. A hash alone cannot carry the next hop's verification key, so the
/// binding must convey both — the verifier recomputes <see cref="KeyId.KeyHash"/> from
/// <see cref="PublicKeySubjectPublicKeyInfo"/> and refuses if it disagrees with <see cref="TargetId"/>
/// before trusting those bytes for the next hop.
/// </summary>
/// <param name="TargetId">The vouched-for key's id.</param>
/// <param name="PublicKeySubjectPublicKeyInfo">The vouched-for key's actual SPKI bytes.</param>
public sealed record KeyBindingPayload(KeyId TargetId, ReadOnlyMemory<byte> PublicKeySubjectPublicKeyInfo) {
    /// <summary>Whether <see cref="PublicKeySubjectPublicKeyInfo"/> actually hashes to <see cref="TargetId"/>'s <see cref="KeyId.KeyHash"/>.</summary>
    public bool IsSelfCertifying =>
        string.Equals(
            a: KeyId.ComputeKeyHash(subjectPublicKeyInfo: PublicKeySubjectPublicKeyInfo.Span),
            b: TargetId.KeyHash,
            comparisonType: StringComparison.Ordinal
        );
}

/// <summary>
/// A sealed carriage payload: an AEAD ciphertext produced by ECDH P-256 key agreement to an AES-256-GCM
/// key, with the envelope's serialized context header as associated data (README.md, "Signed
/// carriage"). Tampering any header byte changes the AAD, so decryption fails closed.
/// </summary>
/// <param name="EphemeralPublicKeySubjectPublicKeyInfo">The sender's one-time ECDH public key (SPKI bytes), carried so the recipient can redo the agreement.</param>
/// <param name="Nonce">The 12-byte AES-GCM nonce.</param>
/// <param name="Tag">The 16-byte AES-GCM authentication tag.</param>
/// <param name="Ciphertext">The encrypted payload bytes.</param>
public sealed record SealedPayload(
    ReadOnlyMemory<byte> EphemeralPublicKeySubjectPublicKeyInfo,
    ReadOnlyMemory<byte> Nonce,
    ReadOnlyMemory<byte> Tag,
    ReadOnlyMemory<byte> Ciphertext
);

/// <summary>
/// One signed carriage envelope: header, payload, the signature, and — decisively —
/// <see cref="SignedPortion"/>, the exact bytes that signature covers
/// (README.md, "Signed carriage"). This is the one shape used for every purpose — a key binding
/// is an envelope with <see cref="CarriagePurposes.KeyBinding"/> as its purpose, never a separate artifact.
/// </summary>
/// <remarks>
/// <para><b>The bytes are authoritative; the parsed fields are a projection of them.</b>
/// README.md §2 requires a verifier to check the signature against the signed-portion
/// bytes <i>as they arrived</i>, never against a re-encoding of what it parsed out of them. An envelope
/// that carried only the parsed fields could not honour that: the verifier would have to re-derive the
/// signing input, and every decoder laxity anywhere in the stack would silently become an accepted
/// alternate wire form for one claim. So the arrived bytes travel with the envelope and
/// <see cref="CarriageVerifier"/> verifies against them.</para>
/// <para><b>The projection cannot drift from the bytes.</b> <see cref="Header"/>,
/// <see cref="PayloadKind"/>, and <see cref="PayloadBytes"/> are get-only rather than <c>init</c>, so
/// <c>envelope with { PayloadKind = … }</c> does not compile: there is no way to change what the envelope
/// says without going back through a codec, which recomputes the bytes. <see cref="Signature"/> stays
/// settable because it sits outside the signed portion — rewriting it is an honest attack to model, and
/// it desynchronises nothing.</para>
/// </remarks>
public sealed record SignedCarriageEnvelope {
    private SignedCarriageEnvelope(
        CarriageEnvelopeHeader header,
        CarriagePayloadKind payloadKind,
        ReadOnlyMemory<byte> payloadBytes,
        ReadOnlyMemory<byte> signature,
        ReadOnlyMemory<byte> signedPortion
    ) {
        Header = header;
        PayloadBytes = payloadBytes;
        PayloadKind = payloadKind;
        Signature = signature;
        SignedPortion = signedPortion;
    }

    /// <summary>
    /// Builds an envelope around signed-portion bytes that already exist — a decoder's arrived bytes, or
    /// the bytes a signer just put its pen to. <paramref name="signedPortion"/> must be the encoding of
    /// the other three signed arguments; nothing re-derives it, which is the whole point.
    /// </summary>
    /// <param name="header">The context header those bytes encode.</param>
    /// <param name="payloadKind">The payload kind those bytes encode.</param>
    /// <param name="payloadBytes">The payload those bytes encode.</param>
    /// <param name="signature">The signature over <paramref name="signedPortion"/>.</param>
    /// <param name="signedPortion">The exact bytes the signature covers.</param>
    public static SignedCarriageEnvelope FromSignedPortion(
        CarriageEnvelopeHeader header,
        CarriagePayloadKind payloadKind,
        ReadOnlyMemory<byte> payloadBytes,
        ReadOnlyMemory<byte> signature,
        ReadOnlyMemory<byte> signedPortion
    ) =>
        new(header: header, payloadKind: payloadKind, payloadBytes: payloadBytes, signature: signature, signedPortion: signedPortion);

    /// <summary>
    /// Builds an envelope by encoding the given fields under <paramref name="codec"/> — the wire form a
    /// party holding these values would actually transmit. This is how a modified envelope is constructed
    /// (a tampered payload kind, a rewritten header): the bytes move with the model, exactly as they would
    /// on the wire, so the signature is then checked against what an attacker really sent.
    /// </summary>
    /// <param name="codec">The serialisation to encode the signed portion under.</param>
    /// <param name="header">The context header.</param>
    /// <param name="payloadKind">Which shape <paramref name="payloadBytes"/> is.</param>
    /// <param name="payloadBytes">The already-encoded payload.</param>
    /// <param name="signature">The signature to carry. Nothing checks it here.</param>
    public static SignedCarriageEnvelope Reencode(
        ICarriageCodec codec,
        CarriageEnvelopeHeader header,
        CarriagePayloadKind payloadKind,
        ReadOnlyMemory<byte> payloadBytes,
        ReadOnlyMemory<byte> signature
    ) =>
        new(
            header: header,
            payloadKind: payloadKind,
            payloadBytes: payloadBytes,
            signature: signature,
            signedPortion: codec.EncodeSignedPortion(header: header, payloadKind: payloadKind, payloadBytes: payloadBytes.Span)
        );

    /// <summary>Gets the canonical context header, always part of the signing input.</summary>
    public CarriageEnvelopeHeader Header { get; }

    /// <summary>Gets the payload, already encoded by whichever <see cref="ICarriageCodec"/> produced this envelope.</summary>
    public ReadOnlyMemory<byte> PayloadBytes { get; }

    /// <summary>Gets the shape <see cref="PayloadBytes"/> decodes as.</summary>
    public CarriagePayloadKind PayloadKind { get; }

    /// <summary>Gets the ECDSA signature (IEEE P1363 fixed-field r‖s) over <see cref="SignedPortion"/>.</summary>
    public ReadOnlyMemory<byte> Signature { get; init; }

    /// <summary>
    /// Gets the exact bytes the signature covers — the codec's signed-portion encoding of <see cref="Header"/>,
    /// <see cref="PayloadKind"/>, and <see cref="PayloadBytes"/>, as they arrived rather than as they would
    /// re-encode. This is what <see cref="CarriageVerifier"/> verifies against.
    /// </summary>
    public ReadOnlyMemory<byte> SignedPortion { get; }
}
