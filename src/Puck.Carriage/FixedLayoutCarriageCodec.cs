namespace Puck.Carriage;

/// <summary>
/// The fixed-layout <see cref="ICarriageCodec"/> — the SHELVED alternative of docs/signed-carriage-wire.md
/// §16, kept for a context that cannot carry a CBOR implementation or wants every byte hand-specified: a
/// versioned byte stream with no unauthenticated parsing beyond bounded, length-prefixed reads (see
/// <see cref="FixedLayoutReader"/>). Field order (also the signed-portion order):
/// <list type="number">
/// <item>format version (1 byte, currently <c>1</c>)</item>
/// <item>domain (32 raw bytes — SHA-256 is fixed-width, so no length prefix is needed)</item>
/// <item>subject (optional string)</item>
/// <item>algorithm (string)</item>
/// <item>purpose (string)</item>
/// <item>not-before (8-byte big-endian signed, Unix seconds)</item>
/// <item>not-after (8-byte big-endian signed, Unix seconds)</item>
/// <item>audience (optional string)</item>
/// <item>sequence (optional 8-byte big-endian unsigned)</item>
/// <item>payload kind (1 byte)</item>
/// <item>payload (length-prefixed bytes)</item>
/// </list>
/// followed, for a full envelope only, by the signature (length-prefixed bytes). A key binding payload
/// encodes target domain (32 bytes), target subject (optional string), target algorithm (string), target
/// key-hash (32 bytes), and public key SPKI (length-prefixed bytes), in that order. A sealed payload
/// encodes ephemeral SPKI (length-prefixed), nonce (12 raw bytes), tag (16 raw bytes), and ciphertext
/// (length-prefixed), in that order.
/// </summary>
public sealed class FixedLayoutCarriageCodec : ICarriageCodec {
    /// <summary>The only format version this codec currently emits or accepts.</summary>
    public const byte FormatVersion = 1;

    /// <summary>The byte width of a SHA-256 fingerprint, used for every raw domain/key-hash field.</summary>
    private const int FingerprintLength = 32;

    /// <summary>The byte width of an AES-GCM nonce.</summary>
    private const int NonceLength = 12;

    /// <summary>The byte width of an AES-GCM authentication tag.</summary>
    private const int TagLength = 16;

    /// <inheritdoc/>
    public string Name => "fixed-layout-v1";

    /// <inheritdoc/>
    public byte[] EncodeSignedPortion(CarriageEnvelopeHeader header, CarriagePayloadKind payloadKind, ReadOnlySpan<byte> payloadBytes) {
        var writer = new FixedLayoutWriter();

        WriteHeaderAndPayload(writer: writer, header: header, payloadKind: payloadKind, payloadBytes: payloadBytes);

        return writer.ToArray();
    }

    /// <inheritdoc/>
    public byte[] EncodeHeader(CarriageEnvelopeHeader header) {
        var writer = new FixedLayoutWriter();

        WriteHeader(writer: writer, header: header);

        return writer.ToArray();
    }

    /// <inheritdoc/>
    public byte[] EncodeEnvelope(SignedCarriageEnvelope envelope) {
        var writer = new FixedLayoutWriter();

        WriteHeaderAndPayload(writer: writer, header: envelope.Header, payloadKind: envelope.PayloadKind, payloadBytes: envelope.PayloadBytes.Span);
        writer.WriteBytes(value: envelope.Signature.Span);

        return writer.ToArray();
    }

    /// <inheritdoc/>
    public SignedCarriageEnvelope DecodeEnvelope(ReadOnlySpan<byte> wire) {
        var reader = new FixedLayoutReader(buffer: wire);

        var (header, payloadKind, payloadBytes) = ReadHeaderAndPayload(reader: ref reader);

        // The signed portion is the prefix of the envelope up to the signature's length prefix, so the
        // bytes that were signed are sliced out of what ARRIVED rather than re-encoded from what was parsed
        // out of them (docs/signed-carriage-wire.md §2, and §16 which adopts it unchanged).
        var signedPortion = wire[..reader.Position].ToArray();
        var signature = reader.ReadBytes().ToArray();

        RequireFullyConsumed(reader: ref reader, what: "envelope");

        var envelope = SignedCarriageEnvelope.FromSignedPortion(
            header: header,
            payloadKind: payloadKind,
            payloadBytes: payloadBytes,
            signature: signature,
            signedPortion: signedPortion
        );

        RequireCanonical(received: wire, reencoded: EncodeEnvelope(envelope: envelope), what: "envelope");

        return envelope;
    }

    /// <inheritdoc/>
    public byte[] EncodeKeyBindingPayload(KeyBindingPayload payload) {
        var writer = new FixedLayoutWriter();

        writer.WriteFixedBytes(value: Convert.FromHexString(s: payload.TargetId.Domain));
        writer.WriteOptionalString(value: payload.TargetId.Subject);
        writer.WriteString(value: payload.TargetId.Algorithm);
        writer.WriteFixedBytes(value: Convert.FromHexString(s: payload.TargetId.KeyHash));
        writer.WriteBytes(value: payload.PublicKeySubjectPublicKeyInfo.Span);

        return writer.ToArray();
    }

    /// <inheritdoc/>
    public KeyBindingPayload DecodeKeyBindingPayload(ReadOnlySpan<byte> bytes) {
        var reader = new FixedLayoutReader(buffer: bytes);

        var domain = Convert.ToHexStringLower(bytes: reader.ReadFixedBytes(count: FingerprintLength));
        var subject = reader.ReadOptionalString();
        var algorithm = reader.ReadString();
        var keyHash = Convert.ToHexStringLower(bytes: reader.ReadFixedBytes(count: FingerprintLength));
        var spki = reader.ReadBytes().ToArray();

        RequireFullyConsumed(reader: ref reader, what: "key binding payload");

        var targetId = new KeyId {
            Algorithm = algorithm,
            Domain = domain,
            KeyHash = keyHash,
            Subject = subject,
        };

        var payload = new KeyBindingPayload(TargetId: targetId, PublicKeySubjectPublicKeyInfo: spki);

        RequireCanonical(received: bytes, reencoded: EncodeKeyBindingPayload(payload: payload), what: "key binding payload");

        return payload;
    }

    /// <inheritdoc/>
    public byte[] EncodeSealedPayload(SealedPayload payload) {
        if (payload.Nonce.Length != NonceLength) {
            throw new ArgumentException(message: $"A sealed payload's nonce must be {NonceLength} bytes.", paramName: nameof(payload));
        }

        if (payload.Tag.Length != TagLength) {
            throw new ArgumentException(message: $"A sealed payload's tag must be {TagLength} bytes.", paramName: nameof(payload));
        }

        var writer = new FixedLayoutWriter();

        writer.WriteBytes(value: payload.EphemeralPublicKeySubjectPublicKeyInfo.Span);
        writer.WriteFixedBytes(value: payload.Nonce.Span);
        writer.WriteFixedBytes(value: payload.Tag.Span);
        writer.WriteBytes(value: payload.Ciphertext.Span);

        return writer.ToArray();
    }

    /// <inheritdoc/>
    public SealedPayload DecodeSealedPayload(ReadOnlySpan<byte> bytes) {
        var reader = new FixedLayoutReader(buffer: bytes);

        var ephemeralSpki = reader.ReadBytes().ToArray();
        var nonce = reader.ReadFixedBytes(count: NonceLength).ToArray();
        var tag = reader.ReadFixedBytes(count: TagLength).ToArray();
        var ciphertext = reader.ReadBytes().ToArray();

        RequireFullyConsumed(reader: ref reader, what: "sealed payload");

        var payload = new SealedPayload(
            Ciphertext: ciphertext,
            EphemeralPublicKeySubjectPublicKeyInfo: ephemeralSpki,
            Nonce: nonce,
            Tag: tag
        );

        RequireCanonical(received: bytes, reencoded: EncodeSealedPayload(payload: payload), what: "sealed payload");

        return payload;
    }

    /// <summary>
    /// Refuses trailing bytes beyond the last field. A decoder that ignores them hands an attacker a family
    /// of distinct wire forms that all decode to one accepted claim.
    /// </summary>
    private static void RequireFullyConsumed(ref FixedLayoutReader reader, string what) {
        if (reader.Remaining != 0) {
            throw new FormatException(message: $"The carriage {what} carries {reader.Remaining} trailing byte(s) beyond its last field — a decoded envelope must account for every byte that arrived.");
        }
    }

    /// <summary>
    /// Enforces this codec's canonicality rule the way <see cref="CborCarriageCodec"/> does: what decoded
    /// must re-encode to EXACTLY what arrived. Every field here is fixed-width or minimally
    /// length-prefixed, so one model HAS exactly one encoding — but "by construction" is a property of the
    /// reader, and a reader that quietly widened what it accepted (a presence flag read as "non-zero",
    /// say) would reintroduce many wire forms per model with nothing to catch it. Checking the identity
    /// makes the guarantee structural rather than a claim about the code beside it, and it cannot drift
    /// from the encoder because it IS the encoder.
    /// </summary>
    private static void RequireCanonical(ReadOnlySpan<byte> received, byte[] reencoded, string what) {
        if (!received.SequenceEqual(other: reencoded)) {
            throw new FormatException(message: $"The carriage {what} is not canonically encoded: it decodes, but re-encoding what it decoded to produces different bytes ({reencoded.Length} vs the {received.Length} that arrived).");
        }
    }
    private static void WriteHeader(FixedLayoutWriter writer, CarriageEnvelopeHeader header) {
        writer.WriteByte(value: FormatVersion);
        writer.WriteFixedBytes(value: Convert.FromHexString(s: header.Domain));
        writer.WriteOptionalString(value: header.Subject);
        writer.WriteString(value: header.Algorithm);
        writer.WriteString(value: header.Purpose);
        writer.WriteInt64(value: header.NotBefore);
        writer.WriteInt64(value: header.NotAfter);
        writer.WriteOptionalString(value: header.Audience);
        writer.WriteOptionalUInt64(value: header.Sequence);
    }
    private static void WriteHeaderAndPayload(FixedLayoutWriter writer, CarriageEnvelopeHeader header, CarriagePayloadKind payloadKind, ReadOnlySpan<byte> payloadBytes) {
        WriteHeader(writer: writer, header: header);
        writer.WriteByte(value: (byte)payloadKind);
        writer.WriteBytes(value: payloadBytes);
    }
    private static (CarriageEnvelopeHeader Header, CarriagePayloadKind PayloadKind, byte[] PayloadBytes) ReadHeaderAndPayload(ref FixedLayoutReader reader) {
        var version = reader.ReadByte();

        if (version != FormatVersion) {
            throw new FormatException(message: $"The carriage envelope declares format version {version}, but this codec only understands version {FormatVersion}.");
        }

        var domain = Convert.ToHexStringLower(bytes: reader.ReadFixedBytes(count: FingerprintLength));
        var subject = reader.ReadOptionalString();
        var algorithm = reader.ReadString();
        var purpose = reader.ReadString();
        var notBefore = reader.ReadInt64();
        var notAfter = reader.ReadInt64();
        var audience = reader.ReadOptionalString();
        var sequence = reader.ReadOptionalUInt64();
        var payloadKindValue = reader.ReadByte();

        // Refused at the DECODER, not left to the verifier (docs/signed-carriage-wire.md §2: "a value
        // outside it MUST be refused by the decoder"). The verifier re-refuses an out-of-set kind on a
        // claim, so this is verdict-neutral today — but only today: a fourth kind, or any codec consumer
        // that does not run the verifier, turns a cast with no range check into an accepted non-kind.
        if ((payloadKindValue < (byte)CarriagePayloadKind.Opaque) || (payloadKindValue > (byte)CarriagePayloadKind.Sealed)) {
            throw new FormatException(message: $"The carriage envelope declares payload kind {payloadKindValue}, which is outside the closed set {{1, 2, 3}}.");
        }

        var payloadKind = (CarriagePayloadKind)payloadKindValue;
        var payloadBytes = reader.ReadBytes().ToArray();

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
}
