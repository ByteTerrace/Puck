namespace Puck.Attestation;

/// <summary>
/// A serialisation of the attestation's one field list (README.md). The specification's §2 fixes CBOR
/// as the format; <see cref="CborAttestationCodec"/> is the sole implementation. The signing input is the exact
/// codec bytes, so the signed portion travels verbatim rather than being re-derived from a decoded model.
/// </summary>
public interface IAttestationCodec {
    /// <summary>A short label for this codec, used in report output.</summary>
    string Name { get; }

    /// <summary>
    /// Encodes exactly the bytes that are signed and verified — the header, payload kind, and payload, in
    /// this codec's canonical order, with NO signature field. This is the AEAD-associated-data half of the
    /// scheme: everything here binds the payload to its context.
    /// </summary>
    byte[] EncodeSignedPortion(AttestationHeader header, AttestationPayloadKind payloadKind, ReadOnlySpan<byte> payloadBytes);
    /// <summary>
    /// Encodes ONLY the context header — no payload kind, no payload, no signature. Sealed attestation uses
    /// this as the AEAD associated data (README.md, "Signed attestation": "the serialized context
    /// header as AEAD associated data"), since the header must be committed to before the ciphertext it
    /// will accompany can even be produced.
    /// </summary>
    byte[] EncodeHeader(AttestationHeader header);
    /// <summary>Encodes a full attestation (signed portion plus signature) for transport.</summary>
    byte[] EncodeAttestation(SignedAttestation attestation);
    /// <summary>Decodes a full attestation from transport bytes produced by <see cref="EncodeAttestation"/> of THIS codec.</summary>
    /// <exception cref="FormatException">The bytes are truncated, malformed, or not this codec's shape.</exception>
    SignedAttestation DecodeAttestation(ReadOnlySpan<byte> wire);
    /// <summary>Encodes a key binding's payload (the nested content of a <see cref="AttestationPurposes.KeyBinding"/> attestation).</summary>
    byte[] EncodeKeyBindingPayload(KeyBindingPayload payload);
    /// <summary>Decodes a key binding's payload.</summary>
    /// <exception cref="FormatException">The bytes are truncated or malformed.</exception>
    KeyBindingPayload DecodeKeyBindingPayload(ReadOnlySpan<byte> bytes);
    /// <summary>Encodes a sealed attestation payload (the nested content of a <see cref="AttestationPayloadKind.Sealed"/> attestation).</summary>
    byte[] EncodeSealedPayload(SealedPayload payload);
    /// <summary>Decodes a sealed attestation payload.</summary>
    /// <exception cref="FormatException">The bytes are truncated or malformed.</exception>
    SealedPayload DecodeSealedPayload(ReadOnlySpan<byte> bytes);
}
