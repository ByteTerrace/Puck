using System.Formats.Cbor;

namespace Puck.Carriage;

/// <summary>
/// The CBOR <see cref="ICarriageCodec"/>, built on <see cref="System.Formats.Cbor"/> (inbox in the BCL — no
/// package reference needed). A full envelope is a definite-length 2-element CBOR array
/// <c>[signedPortion: bstr, signature: bstr]</c>; wrapping the signed portion as an opaque byte string
/// means the exact bytes that were signed travel verbatim and never need re-deriving by re-encoding a
/// decoded model (definite-length CBOR arrays are canonical for a fixed field sequence, so re-encoding
/// would reproduce the same bytes regardless, but the wrapped form makes that a structural guarantee rather
/// than an encoder-implementation detail). The signed portion itself is a definite-length 11-element array,
/// same field order as <see cref="FixedLayoutCarriageCodec"/>:
/// format version, domain, subject, algorithm, purpose, not-before, not-after, audience, sequence, payload
/// kind, payload. A key binding payload is a 5-element array (target domain, target subject, target
/// algorithm, target key-hash, public key SPKI); a sealed payload is a 4-element array (ephemeral SPKI,
/// nonce, tag, ciphertext). Domain and key-hash fields are CBOR byte strings of exactly 32 raw bytes, not
/// hex text: the wire carries the fingerprint value rather than a rendering of it, and pinning the width
/// is what stops one domain having several encodings (docs/signed-carriage-wire.md, §2 and §15 row 3).
/// </summary>
public sealed class CborCarriageCodec : ICarriageCodec {
    /// <summary>The only format version this codec currently emits or accepts.</summary>
    public const ulong FormatVersion = 1;

    /// <summary>The byte width of a SHA-256 fingerprint — the exact width every domain and key-hash field must be.</summary>
    private const int FingerprintLength = 32;

    /// <inheritdoc/>
    public string Name => "cbor-v1";

    /// <inheritdoc/>
    public byte[] EncodeSignedPortion(CarriageEnvelopeHeader header, CarriagePayloadKind payloadKind, ReadOnlySpan<byte> payloadBytes) {
        var writer = new CborWriter(conformanceMode: CborConformanceMode.Strict);

        WriteSignedPortion(writer: writer, header: header, payloadKind: payloadKind, payloadBytes: payloadBytes);

        return writer.Encode();
    }

    /// <inheritdoc/>
    public byte[] EncodeHeader(CarriageEnvelopeHeader header) {
        var writer = new CborWriter(conformanceMode: CborConformanceMode.Strict);

        writer.WriteStartArray(definiteLength: 9);
        writer.WriteUInt64(value: FormatVersion);
        writer.WriteByteString(value: Convert.FromHexString(s: header.Domain));
        WriteOptionalText(writer: writer, value: header.Subject);
        writer.WriteTextString(value: header.Algorithm);
        writer.WriteTextString(value: header.Purpose);
        writer.WriteInt64(value: header.NotBefore);
        writer.WriteInt64(value: header.NotAfter);
        WriteOptionalText(writer: writer, value: header.Audience);
        WriteOptionalUInt(writer: writer, value: header.Sequence);
        writer.WriteEndArray();

        return writer.Encode();
    }

    /// <inheritdoc/>
    public byte[] EncodeEnvelope(SignedCarriageEnvelope envelope) {
        var signedPortion = EncodeSignedPortion(header: envelope.Header, payloadKind: envelope.PayloadKind, payloadBytes: envelope.PayloadBytes.Span);
        var writer = new CborWriter(conformanceMode: CborConformanceMode.Strict);

        writer.WriteStartArray(definiteLength: 2);
        writer.WriteByteString(value: signedPortion);
        writer.WriteByteString(value: envelope.Signature.Span);
        writer.WriteEndArray();

        return writer.Encode();
    }

    /// <inheritdoc/>
    public SignedCarriageEnvelope DecodeEnvelope(ReadOnlySpan<byte> wire) {
        var source = wire.ToArray();

        return Decode(
            what: "envelope",
            body: () => {
                var reader = new CborReader(data: source, conformanceMode: CborConformanceMode.Strict);

                ExpectArrayLength(reader: reader, expected: 2);

                var signedPortion = reader.ReadByteString();
                var signature = reader.ReadByteString();

                reader.ReadEndArray();
                RequireFullyConsumed(reader: reader, what: "envelope");

                var (header, payloadKind, payloadBytes) = DecodeSignedPortion(bytes: signedPortion);

                // The signed portion arrived as an opaque byte string, so the bytes that were signed are
                // carried verbatim into the envelope — never re-derived from what was parsed out of them
                // (docs/signed-carriage-wire.md §2).
                var envelope = SignedCarriageEnvelope.FromSignedPortion(
                    header: header,
                    payloadKind: payloadKind,
                    payloadBytes: payloadBytes,
                    signature: signature,
                    signedPortion: signedPortion
                );

                RequireCanonical(received: source, reencoded: EncodeEnvelope(envelope: envelope), what: "envelope");

                return envelope;
            }
        );
    }

    /// <inheritdoc/>
    public byte[] EncodeKeyBindingPayload(KeyBindingPayload payload) {
        var writer = new CborWriter(conformanceMode: CborConformanceMode.Strict);

        writer.WriteStartArray(definiteLength: 5);
        writer.WriteByteString(value: Convert.FromHexString(s: payload.TargetId.Domain));
        WriteOptionalText(writer: writer, value: payload.TargetId.Subject);
        writer.WriteTextString(value: payload.TargetId.Algorithm);
        writer.WriteByteString(value: Convert.FromHexString(s: payload.TargetId.KeyHash));
        writer.WriteByteString(value: payload.PublicKeySubjectPublicKeyInfo.Span);
        writer.WriteEndArray();

        return writer.Encode();
    }

    /// <inheritdoc/>
    public KeyBindingPayload DecodeKeyBindingPayload(ReadOnlySpan<byte> bytes) {
        var source = bytes.ToArray();

        return Decode(
            what: "key binding payload",
            body: () => {
                var reader = new CborReader(data: source, conformanceMode: CborConformanceMode.Strict);

                ExpectArrayLength(reader: reader, expected: 5);

                var domain = ReadFingerprint(reader: reader, what: "key binding payload's target domain");
                var subject = ReadOptionalText(reader: reader);
                var algorithm = reader.ReadTextString();
                var keyHash = ReadFingerprint(reader: reader, what: "key binding payload's target key hash");
                var spki = reader.ReadByteString();

                reader.ReadEndArray();
                RequireFullyConsumed(reader: reader, what: "key binding payload");

                var targetId = new KeyId {
                    Algorithm = algorithm,
                    Domain = domain,
                    KeyHash = keyHash,
                    Subject = subject,
                };

                var payload = new KeyBindingPayload(TargetId: targetId, PublicKeySubjectPublicKeyInfo: spki);

                RequireCanonical(received: source, reencoded: EncodeKeyBindingPayload(payload: payload), what: "key binding payload");

                return payload;
            }
        );
    }

    /// <inheritdoc/>
    public byte[] EncodeSealedPayload(SealedPayload payload) {
        var writer = new CborWriter(conformanceMode: CborConformanceMode.Strict);

        writer.WriteStartArray(definiteLength: 4);
        writer.WriteByteString(value: payload.EphemeralPublicKeySubjectPublicKeyInfo.Span);
        writer.WriteByteString(value: payload.Nonce.Span);
        writer.WriteByteString(value: payload.Tag.Span);
        writer.WriteByteString(value: payload.Ciphertext.Span);
        writer.WriteEndArray();

        return writer.Encode();
    }

    /// <inheritdoc/>
    public SealedPayload DecodeSealedPayload(ReadOnlySpan<byte> bytes) {
        var source = bytes.ToArray();

        return Decode(
            what: "sealed payload",
            body: () => {
                var reader = new CborReader(data: source, conformanceMode: CborConformanceMode.Strict);

                ExpectArrayLength(reader: reader, expected: 4);

                var ephemeralSpki = reader.ReadByteString();
                var nonce = reader.ReadByteString();
                var tag = reader.ReadByteString();
                var ciphertext = reader.ReadByteString();

                reader.ReadEndArray();
                RequireFullyConsumed(reader: reader, what: "sealed payload");

                var payload = new SealedPayload(
                    Ciphertext: ciphertext,
                    EphemeralPublicKeySubjectPublicKeyInfo: ephemeralSpki,
                    Nonce: nonce,
                    Tag: tag
                );

                RequireCanonical(received: source, reencoded: EncodeSealedPayload(payload: payload), what: "sealed payload");

                return payload;
            }
        );
    }

    private static void WriteSignedPortion(CborWriter writer, CarriageEnvelopeHeader header, CarriagePayloadKind payloadKind, ReadOnlySpan<byte> payloadBytes) {
        writer.WriteStartArray(definiteLength: 11);
        writer.WriteUInt64(value: FormatVersion);
        writer.WriteByteString(value: Convert.FromHexString(s: header.Domain));
        WriteOptionalText(writer: writer, value: header.Subject);
        writer.WriteTextString(value: header.Algorithm);
        writer.WriteTextString(value: header.Purpose);
        writer.WriteInt64(value: header.NotBefore);
        writer.WriteInt64(value: header.NotAfter);
        WriteOptionalText(writer: writer, value: header.Audience);
        WriteOptionalUInt(writer: writer, value: header.Sequence);
        writer.WriteUInt64(value: (ulong)payloadKind);
        writer.WriteByteString(value: payloadBytes);
        writer.WriteEndArray();
    }
    private static (CarriageEnvelopeHeader Header, CarriagePayloadKind PayloadKind, byte[] PayloadBytes) DecodeSignedPortion(byte[] bytes) {
        var reader = new CborReader(data: bytes, conformanceMode: CborConformanceMode.Strict);

        ExpectArrayLength(reader: reader, expected: 11);

        var version = reader.ReadUInt64();

        if (version != FormatVersion) {
            throw new FormatException(message: $"The carriage envelope declares format version {version}, but this codec only understands version {FormatVersion}.");
        }

        var domain = ReadFingerprint(reader: reader, what: "envelope's domain");
        var subject = ReadOptionalText(reader: reader);
        var algorithm = reader.ReadTextString();
        var purpose = reader.ReadTextString();
        var notBefore = reader.ReadInt64();
        var notAfter = reader.ReadInt64();
        var audience = ReadOptionalText(reader: reader);
        var sequence = ReadOptionalUInt(reader: reader);
        var payloadKindValue = reader.ReadUInt64();

        // Refused at the DECODER, not left to the verifier: the model's underlying type is a byte, so an
        // out-of-range wire value would silently truncate into a legitimate kind (258 becomes 2). The
        // canonicality rule would still catch that, but only as a second line — a kind outside the closed
        // set is not a canonicality problem, it is not a kind.
        if ((payloadKindValue < (ulong)CarriagePayloadKind.Opaque) || (payloadKindValue > (ulong)CarriagePayloadKind.Sealed)) {
            throw new FormatException(message: $"The carriage envelope declares payload kind {payloadKindValue}, which is outside the closed set {{1, 2, 3}}.");
        }

        var payloadKind = (CarriagePayloadKind)payloadKindValue;
        var payloadBytes = reader.ReadByteString();

        reader.ReadEndArray();

        var header = new CarriageEnvelopeHeader(
            Domain: domain,
            Subject: subject,
            Algorithm: algorithm,
            Purpose: purpose,
            NotBefore: notBefore,
            NotAfter: notAfter,
            Audience: audience,
            Sequence: sequence
        );

        return (header, payloadKind, payloadBytes);
    }

    /// <summary>
    /// Runs a decode and normalises every way malformed input can surface into <see cref="FormatException"/>.
    /// <see cref="CborReader"/> raises <see cref="CborContentException"/> for ill-formed data,
    /// <see cref="InvalidOperationException"/> for a data item of the wrong major type, and
    /// <see cref="OverflowException"/> for an integer too wide for the requested width — three unrelated
    /// types for one situation, and a caller that catches only one of them treats the other two as a crash.
    /// The <see cref="ICarriageCodec"/> contract says malformed bytes are a <see cref="FormatException"/>,
    /// so this is where that becomes true.
    /// </summary>
    private static T Decode<T>(Func<T> body, string what) {
        try {
            return body();
        } catch (FormatException) {
            throw;
        } catch (Exception exception) when (((exception is CborContentException) || (exception is InvalidOperationException) || (exception is OverflowException) || (exception is ArgumentException))) {
            throw new FormatException(message: $"The carriage {what} is not well-formed CBOR of the expected shape: {exception.Message}", innerException: exception);
        }
    }

    /// <summary>
    /// Refuses trailing bytes after the outer data item. <see cref="CborConformanceMode.Strict"/> checks
    /// well-formedness of what it reads and says nothing about what follows, so without this a valid
    /// envelope with arbitrary bytes appended decodes exactly as the original.
    /// </summary>
    private static void RequireFullyConsumed(CborReader reader, string what) {
        if (reader.BytesRemaining != 0) {
            throw new FormatException(message: $"The carriage {what} carries {reader.BytesRemaining} trailing byte(s) beyond its outer CBOR item — a decoded envelope must account for every byte that arrived.");
        }
    }

    /// <summary>
    /// Enforces this codec's canonicality rule: what decoded must re-encode to EXACTLY what arrived.
    /// <see cref="CborConformanceMode.Strict"/> deliberately tolerates indefinite-length strings and
    /// non-minimal integer encodings, so without this rule one model has many valid wire forms — and a
    /// verifier that re-derives the signing input from a decoded model would then refuse honest bytes for
    /// the wrong reason while a receiver deduplicating on wire bytes would see one claim as many. One
    /// encoding per model, or refused.
    /// </summary>
    private static void RequireCanonical(ReadOnlySpan<byte> received, byte[] reencoded, string what) {
        if (!received.SequenceEqual(other: reencoded)) {
            throw new FormatException(message: $"The carriage {what} is not canonically encoded: it decodes, but re-encoding what it decoded to produces different bytes ({reencoded.Length} vs the {received.Length} that arrived).");
        }
    }

    /// <summary>
    /// Reads a fingerprint field and refuses any width but 32 bytes. Without this a domain is whatever
    /// length arrived, two implementations disagree about what a domain even is, and the hex rendering the
    /// model carries stops round-tripping to one wire form.
    /// </summary>
    private static string ReadFingerprint(CborReader reader, string what) {
        var bytes = reader.ReadByteString();

        if (bytes.Length != FingerprintLength) {
            throw new FormatException(message: $"The carriage {what} is {bytes.Length} byte(s); a fingerprint field is exactly {FingerprintLength}.");
        }

        return Convert.ToHexStringLower(bytes: bytes);
    }
    private static void ExpectArrayLength(CborReader reader, int expected) {
        var length = reader.ReadStartArray();

        if (length != expected) {
            throw new FormatException(message: $"Expected a {expected}-element carriage array, but found {(length?.ToString() ?? "an indefinite-length")} element(s).");
        }
    }
    private static void WriteOptionalText(CborWriter writer, string? value) {
        if (value is null) {
            writer.WriteNull();
        } else {
            writer.WriteTextString(value: value);
        }
    }
    private static string? ReadOptionalText(CborReader reader) {
        if (reader.PeekState() == CborReaderState.Null) {
            reader.ReadNull();

            return null;
        }

        return reader.ReadTextString();
    }
    private static void WriteOptionalUInt(CborWriter writer, ulong? value) {
        if (value is null) {
            writer.WriteNull();
        } else {
            writer.WriteUInt64(value: value.Value);
        }
    }
    private static ulong? ReadOptionalUInt(CborReader reader) {
        if (reader.PeekState() == CborReaderState.Null) {
            reader.ReadNull();

            return null;
        }

        return reader.ReadUInt64();
    }
}
