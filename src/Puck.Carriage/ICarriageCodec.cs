namespace Puck.Carriage;

/// <summary>
/// A serialisation of the carriage envelope's ONE field list (README.md). Two
/// implementations exist — <see cref="CborCarriageCodec"/>, which the specification's §2 fixes as the
/// format, and <see cref="FixedLayoutCarriageCodec"/>, which its §16 keeps on the shelf for a context that
/// cannot carry a CBOR implementation at all. Both encode the SAME fields in the SAME order (see each
/// implementation's file header), but they are not interchangeable at the byte level: the signing input is
/// whichever codec's
/// bytes were actually signed, so an envelope signed under one codec never verifies re-encoded under the
/// other. That is deliberate, not a gap — see the "serialisation cross-check" scenario in Program.cs.
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
