namespace Puck.Carriage;

/// <summary>
/// A serialisation of the carriage envelope's one field list (README.md). The specification's §2 fixes CBOR
/// as the format; <see cref="CborCarriageCodec"/> is the sole implementation. The signing input is the exact
/// codec bytes, so the signed portion travels verbatim rather than being re-derived from a decoded model.
/// </summary>
public interface ICarriageCodec {
    /// <summary>A short label for this codec, used in report output.</summary>
    string Name { get; }

    /// <summary>
    /// Encodes exactly the bytes that are signed and verified — the header, payload kind, and payload, in
    /// this codec's canonical order, with NO signature field. This is the AEAD-associated-data half of the
    /// scheme: everything here binds the payload to its context.
    /// </summary>
    byte[] EncodeSignedPortion(CarriageEnvelopeHeader header, CarriagePayloadKind payloadKind, ReadOnlySpan<byte> payloadBytes);

    /// <summary>
    /// Encodes ONLY the context header — no payload kind, no payload, no signature. Sealed carriage uses
    /// this as the AEAD associated data (README.md, "Signed carriage": "the serialized context
    /// header as AEAD associated data"), since the header must be committed to before the ciphertext it
    /// will accompany can even be produced.
    /// </summary>
    byte[] EncodeHeader(CarriageEnvelopeHeader header);

    /// <summary>Encodes a full envelope (signed portion plus signature) for transport.</summary>
    byte[] EncodeEnvelope(SignedCarriageEnvelope envelope);

    /// <summary>Decodes a full envelope from transport bytes produced by <see cref="EncodeEnvelope"/> of THIS codec.</summary>
    /// <exception cref="FormatException">The bytes are truncated, malformed, or not this codec's shape.</exception>
    SignedCarriageEnvelope DecodeEnvelope(ReadOnlySpan<byte> wire);

    /// <summary>Encodes a key binding's payload (the nested content of a <see cref="CarriagePurposes.KeyBinding"/> envelope).</summary>
    byte[] EncodeKeyBindingPayload(KeyBindingPayload payload);

    /// <summary>Decodes a key binding's payload.</summary>
    /// <exception cref="FormatException">The bytes are truncated or malformed.</exception>
    KeyBindingPayload DecodeKeyBindingPayload(ReadOnlySpan<byte> bytes);

    /// <summary>Encodes a sealed carriage payload (the nested content of a <see cref="CarriagePayloadKind.Sealed"/> envelope).</summary>
    byte[] EncodeSealedPayload(SealedPayload payload);

    /// <summary>Decodes a sealed carriage payload.</summary>
    /// <exception cref="FormatException">The bytes are truncated or malformed.</exception>
    SealedPayload DecodeSealedPayload(ReadOnlySpan<byte> bytes);
}
