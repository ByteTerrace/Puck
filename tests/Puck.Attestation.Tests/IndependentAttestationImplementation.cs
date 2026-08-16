using System.Buffers.Binary;
using System.Formats.Cbor;
using System.Security.Cryptography;
using System.Text;

namespace Puck.Attestation.Tests;

/// <summary>
/// A test-only implementation of the v1 wire and cryptographic profile. It deliberately uses only the
/// platform CBOR and cryptography APIs: no Puck model, codec, signer, verifier, key-id, or sealing member is
/// called. Interoperability tests translate only at the byte boundary.
/// </summary>
internal static class IndependentAttestationImplementation {
    internal const ulong KeyBindingPayloadKind = 2UL;
    internal const string KeyBindingPurpose = "key-binding";
    internal const ulong OpaquePayloadKind = 1UL;
    internal const ulong SealedPayloadKind = 3UL;
    internal const string SealingAlgorithm = "ecdh-p256-hkdf-sha256-aes256gcm";
    internal const string SigningAlgorithm = "ecdsa-p256-sha256";

    private static readonly byte[] AeadLabel = "puck.attestation.sealed.aad.v1"u8.ToArray();
    private static readonly byte[] HkdfLabel = "puck.attestation.sealed.v1"u8.ToArray();

    internal static IndependentId RootId(ReadOnlySpan<byte> subjectPublicKeyInfo) {
        var fingerprint = Fingerprint(bytes: subjectPublicKeyInfo);

        return new IndependentId(
            Algorithm: SigningAlgorithm,
            Domain: fingerprint,
            KeyHash: fingerprint,
            Subject: null
        );
    }
    internal static IndependentId IssuingId(string domain, ReadOnlySpan<byte> subjectPublicKeyInfo) => new(
        Domain: domain,
        Subject: null,
        Algorithm: SigningAlgorithm,
        KeyHash: Fingerprint(bytes: subjectPublicKeyInfo)
    );
    internal static IndependentId SubjectId(string domain, string subject, ReadOnlySpan<byte> subjectPublicKeyInfo, string algorithm = SigningAlgorithm) => new(
        Domain: domain,
        Subject: subject,
        Algorithm: algorithm,
        KeyHash: Fingerprint(bytes: subjectPublicKeyInfo)
    );
    internal static byte[] SignKeyBinding(
        string domain,
        ECDsa signingKey,
        IndependentId targetId,
        ReadOnlySpan<byte> targetSubjectPublicKeyInfo,
        long notBefore,
        long notAfter
    ) {
        var payload = EncodeKeyBinding(targetId: targetId, targetSubjectPublicKeyInfo: targetSubjectPublicKeyInfo);
        var header = new IndependentHeader(
            Algorithm: SigningAlgorithm,
            Audience: null,
            Domain: domain,
            NotAfter: notAfter,
            NotBefore: notBefore,
            Purpose: KeyBindingPurpose,
            Sequence: null,
            Subject: null
        );

        return SignAttestation(header: header, payload: payload, payloadKind: KeyBindingPayloadKind, signingKey: signingKey);
    }
    internal static byte[] SignClaim(IndependentHeader header, ulong payloadKind, ReadOnlySpan<byte> payload, ECDsa signingKey) =>
        SignAttestation(header: header, payload: payload, payloadKind: payloadKind, signingKey: signingKey);
    internal static byte[] VerifyChain(
        ReadOnlySpan<byte> rootToIssuingWire,
        ReadOnlySpan<byte> issuingToSubjectWire,
        ReadOnlySpan<byte> claimWire,
        IndependentId trustedRootId,
        ReadOnlySpan<byte> trustedRootSubjectPublicKeyInfo,
        string expectedPurpose,
        string? expectedAudience,
        long now
    ) {
        var rootToIssuing = DecodeAttestation(wire: rootToIssuingWire);

        VerifyAttestation(
            attestation: rootToIssuing,
            expectedPayloadKind: KeyBindingPayloadKind,
            expectedPurpose: KeyBindingPurpose,
            now: now,
            pinnedId: trustedRootId,
            pinnedSubjectPublicKeyInfo: trustedRootSubjectPublicKeyInfo
        );

        var issuing = DecodeKeyBinding(bytes: rootToIssuing.Payload);

        RequireSelfCertifying(binding: issuing);

        if ((issuing.TargetId.Subject is not null) || !string.Equals(a: issuing.TargetId.Domain, b: trustedRootId.Domain, comparisonType: StringComparison.Ordinal)) {
            throw new CryptographicException(message: "The independently verified issuing binding has the wrong identity shape.");
        }

        var issuingToSubject = DecodeAttestation(wire: issuingToSubjectWire);

        VerifyAttestation(
            attestation: issuingToSubject,
            pinnedId: issuing.TargetId,
            pinnedSubjectPublicKeyInfo: issuing.SubjectPublicKeyInfo,
            expectedPurpose: KeyBindingPurpose,
            expectedPayloadKind: KeyBindingPayloadKind,
            now: now
        );

        var subject = DecodeKeyBinding(bytes: issuingToSubject.Payload);

        RequireSelfCertifying(binding: subject);

        if ((subject.TargetId.Subject is null) || !string.Equals(a: subject.TargetId.Domain, b: trustedRootId.Domain, comparisonType: StringComparison.Ordinal)) {
            throw new CryptographicException(message: "The independently verified subject binding has the wrong identity shape.");
        }

        var claim = DecodeAttestation(wire: claimWire);

        VerifyAttestation(
            attestation: claim,
            pinnedId: subject.TargetId,
            pinnedSubjectPublicKeyInfo: subject.SubjectPublicKeyInfo,
            expectedPurpose: expectedPurpose,
            expectedPayloadKind: ((claim.Header.Purpose == KeyBindingPurpose) ? KeyBindingPayloadKind : claim.PayloadKind),
            now: now
        );

        if ((claim.PayloadKind != OpaquePayloadKind) && (claim.PayloadKind != SealedPayloadKind)) {
            throw new CryptographicException(message: "The independently verified claim has an invalid payload kind.");
        }

        if (!string.Equals(a: claim.Header.Audience, b: expectedAudience, comparisonType: StringComparison.Ordinal)) {
            throw new CryptographicException(message: "The independently verified claim has the wrong audience.");
        }

        if (!string.Equals(a: claim.Header.Subject, b: subject.TargetId.Subject, comparisonType: StringComparison.Ordinal)) {
            throw new CryptographicException(message: "The independently verified claim has the wrong subject.");
        }

        return claim.Payload;
    }
    internal static byte[] EncodeHeader(IndependentHeader header) {
        var writer = NewWriter();

        writer.WriteStartArray(definiteLength: 9);
        WriteHeaderFields(header: header, writer: writer);
        writer.WriteEndArray();

        return writer.Encode();
    }
    internal static byte[] Seal(
        IndependentId recipientId,
        ReadOnlySpan<byte> recipientSubjectPublicKeyInfo,
        ReadOnlySpan<byte> headerBytes,
        ReadOnlySpan<byte> plaintext
    ) {
        if (!string.Equals(a: Fingerprint(bytes: recipientSubjectPublicKeyInfo), b: recipientId.KeyHash, comparisonType: StringComparison.Ordinal)) {
            throw new CryptographicException(message: "The independent sealing recipient id does not identify its public key.");
        }

        using var recipient = ECDiffieHellman.Create();
        using var ephemeral = ECDiffieHellman.Create(curve: ECCurve.NamedCurves.nistP256);

        recipient.ImportSubjectPublicKeyInfo(bytesRead: out var recipientBytesRead, source: recipientSubjectPublicKeyInfo);
        RequireEntireSubjectPublicKeyInfo(bytesRead: recipientBytesRead, encodedLength: recipientSubjectPublicKeyInfo.Length);

        var context = EncodeRecipientContext(recipientId: recipientId);
        var key = DeriveKey(privateKey: ephemeral, publicKey: recipient.PublicKey, recipientContext: context);

        try {
            var nonce = RandomNumberGenerator.GetBytes(count: 12);
            var tag = new byte[16];
            var ciphertext = new byte[plaintext.Length];

            using (var aes = new AesGcm(key: key, tagSizeInBytes: tag.Length)) {
                aes.Encrypt(
                    nonce: nonce,
                    plaintext: plaintext,
                    ciphertext: ciphertext,
                    tag: tag,
                    associatedData: BindAssociatedData(headerBytes: headerBytes, recipientContext: context)
                );
            }

            return EncodeSealedPayload(payload: new IndependentSealedPayload(
                RecipientId: recipientId,
                EphemeralSubjectPublicKeyInfo: ephemeral.ExportSubjectPublicKeyInfo(),
                Nonce: nonce,
                Tag: tag,
                Ciphertext: ciphertext
            ));
        } finally {
            CryptographicOperations.ZeroMemory(buffer: key);
        }
    }
    internal static byte[] Unseal(ReadOnlySpan<byte> sealedPayloadBytes, ECDiffieHellman recipientPrivateKey, ReadOnlySpan<byte> headerBytes) {
        var payload = DecodeSealedPayload(bytes: sealedPayloadBytes);

        if (!string.Equals(a: Fingerprint(bytes: recipientPrivateKey.ExportSubjectPublicKeyInfo()), b: payload.RecipientId.KeyHash, comparisonType: StringComparison.Ordinal)) {
            throw new CryptographicException(message: "The independent unsealer received the wrong private key.");
        }

        using var ephemeral = ECDiffieHellman.Create();

        ephemeral.ImportSubjectPublicKeyInfo(source: payload.EphemeralSubjectPublicKeyInfo, bytesRead: out var ephemeralBytesRead);
        RequireEntireSubjectPublicKeyInfo(bytesRead: ephemeralBytesRead, encodedLength: payload.EphemeralSubjectPublicKeyInfo.Length);

        var context = EncodeRecipientContext(recipientId: payload.RecipientId);
        var key = DeriveKey(privateKey: recipientPrivateKey, publicKey: ephemeral.PublicKey, recipientContext: context);

        try {
            var plaintext = new byte[payload.Ciphertext.Length];

            using var aes = new AesGcm(key: key, tagSizeInBytes: payload.Tag.Length);

            aes.Decrypt(
                nonce: payload.Nonce,
                ciphertext: payload.Ciphertext,
                tag: payload.Tag,
                plaintext: plaintext,
                associatedData: BindAssociatedData(headerBytes: headerBytes, recipientContext: context)
            );

            return plaintext;
        } finally {
            CryptographicOperations.ZeroMemory(buffer: key);
        }
    }

    private static byte[] SignAttestation(IndependentHeader header, ulong payloadKind, ReadOnlySpan<byte> payload, ECDsa signingKey) {
        var signedPortion = EncodeSignedPortion(header: header, payload: payload, payloadKind: payloadKind);
        var signature = signingKey.SignData(
            data: signedPortion,
            hashAlgorithm: HashAlgorithmName.SHA256,
            signatureFormat: DSASignatureFormat.IeeeP1363FixedFieldConcatenation
        );
        var writer = NewWriter();

        writer.WriteStartArray(definiteLength: 2);
        writer.WriteByteString(value: signedPortion);
        writer.WriteByteString(value: signature);
        writer.WriteEndArray();

        return writer.Encode();
    }
    private static byte[] EncodeSignedPortion(IndependentHeader header, ulong payloadKind, ReadOnlySpan<byte> payload) {
        var writer = NewWriter();

        writer.WriteStartArray(definiteLength: 11);
        WriteHeaderFields(header: header, writer: writer);
        writer.WriteUInt64(value: payloadKind);
        writer.WriteByteString(value: payload);
        writer.WriteEndArray();

        return writer.Encode();
    }
    private static void WriteHeaderFields(CborWriter writer, IndependentHeader header) {
        writer.WriteUInt64(value: 1UL);
        writer.WriteByteString(value: Convert.FromHexString(s: header.Domain));
        WriteOptionalText(writer: writer, value: header.Subject);
        writer.WriteTextString(value: header.Algorithm);
        writer.WriteTextString(value: header.Purpose);
        writer.WriteInt64(value: header.NotBefore);
        writer.WriteInt64(value: header.NotAfter);
        WriteOptionalText(writer: writer, value: header.Audience);
        WriteOptionalUInt(writer: writer, value: header.Sequence);
    }
    private static byte[] EncodeKeyBinding(IndependentId targetId, ReadOnlySpan<byte> targetSubjectPublicKeyInfo) {
        var writer = NewWriter();

        writer.WriteStartArray(definiteLength: 5);
        writer.WriteByteString(value: Convert.FromHexString(s: targetId.Domain));
        WriteOptionalText(writer: writer, value: targetId.Subject);
        writer.WriteTextString(value: targetId.Algorithm);
        writer.WriteByteString(value: Convert.FromHexString(s: targetId.KeyHash));
        writer.WriteByteString(value: targetSubjectPublicKeyInfo);
        writer.WriteEndArray();

        return writer.Encode();
    }
    private static IndependentAttestation DecodeAttestation(ReadOnlySpan<byte> wire) {
        var outer = NewReader(bytes: wire);

        ExpectArray(length: 2, reader: outer);

        var signedPortion = outer.ReadByteString();
        var signature = outer.ReadByteString();

        outer.ReadEndArray();
        RequireConsumed(reader: outer);

        var reader = NewReader(bytes: signedPortion);

        ExpectArray(length: 11, reader: reader);

        if (reader.ReadUInt64() != 1UL) {
            throw new FormatException(message: "The independent decoder only accepts v1.");
        }

        var header = new IndependentHeader(
            Domain: ReadFingerprint(reader: reader),
            Subject: ReadOptionalText(reader: reader),
            Algorithm: reader.ReadTextString(),
            Purpose: reader.ReadTextString(),
            NotBefore: reader.ReadInt64(),
            NotAfter: reader.ReadInt64(),
            Audience: ReadOptionalText(reader: reader),
            Sequence: ReadOptionalUInt(reader: reader)
        );
        var payloadKind = reader.ReadUInt64();
        var payload = reader.ReadByteString();

        reader.ReadEndArray();
        RequireConsumed(reader: reader);

        return new IndependentAttestation(
            Header: header,
            Payload: payload,
            PayloadKind: payloadKind,
            Signature: signature,
            SignedPortion: signedPortion
        );
    }
    private static IndependentBinding DecodeKeyBinding(ReadOnlySpan<byte> bytes) {
        var reader = NewReader(bytes: bytes);

        ExpectArray(length: 5, reader: reader);

        var id = new IndependentId(
            Domain: ReadFingerprint(reader: reader),
            Subject: ReadOptionalText(reader: reader),
            Algorithm: reader.ReadTextString(),
            KeyHash: ReadFingerprint(reader: reader)
        );
        var spki = reader.ReadByteString();

        reader.ReadEndArray();
        RequireConsumed(reader: reader);

        return new IndependentBinding(SubjectPublicKeyInfo: spki, TargetId: id);
    }
    private static void VerifyAttestation(
        IndependentAttestation attestation,
        IndependentId pinnedId,
        ReadOnlySpan<byte> pinnedSubjectPublicKeyInfo,
        string expectedPurpose,
        ulong expectedPayloadKind,
        long now
    ) {
        if (
            !string.Equals(a: attestation.Header.Domain, b: pinnedId.Domain, comparisonType: StringComparison.Ordinal) ||
            !string.Equals(a: attestation.Header.Subject, b: pinnedId.Subject, comparisonType: StringComparison.Ordinal) ||
            !string.Equals(a: attestation.Header.Algorithm, b: pinnedId.Algorithm, comparisonType: StringComparison.Ordinal) ||
            !string.Equals(a: attestation.Header.Purpose, b: expectedPurpose, comparisonType: StringComparison.Ordinal) ||
            (attestation.PayloadKind != expectedPayloadKind) ||
            (now < attestation.Header.NotBefore) ||
            (now > attestation.Header.NotAfter) ||
            !string.Equals(a: Fingerprint(bytes: pinnedSubjectPublicKeyInfo), b: pinnedId.KeyHash, comparisonType: StringComparison.Ordinal)
        ) {
            throw new CryptographicException(message: "An independent attestation policy check failed.");
        }

        using var verifier = ECDsa.Create();

        verifier.ImportSubjectPublicKeyInfo(bytesRead: out var verifierBytesRead, source: pinnedSubjectPublicKeyInfo);
        RequireEntireSubjectPublicKeyInfo(bytesRead: verifierBytesRead, encodedLength: pinnedSubjectPublicKeyInfo.Length);

        if (!verifier.VerifyData(
            data: attestation.SignedPortion,
            signature: attestation.Signature,
            hashAlgorithm: HashAlgorithmName.SHA256,
            signatureFormat: DSASignatureFormat.IeeeP1363FixedFieldConcatenation
        )) {
            throw new CryptographicException(message: "An independent attestation signature check failed.");
        }
    }
    private static void RequireSelfCertifying(IndependentBinding binding) {
        if (!string.Equals(a: Fingerprint(bytes: binding.SubjectPublicKeyInfo), b: binding.TargetId.KeyHash, comparisonType: StringComparison.Ordinal)) {
            throw new CryptographicException(message: "An independent attestation binding is not self-certifying.");
        }
    }
    private static void RequireEntireSubjectPublicKeyInfo(int bytesRead, int encodedLength) {
        if (bytesRead != encodedLength) {
            throw new CryptographicException(message: $"The independent implementation found {(encodedLength - bytesRead)} trailing byte(s) after a SubjectPublicKeyInfo value.");
        }
    }
    private static byte[] EncodeSealedPayload(IndependentSealedPayload payload) {
        var writer = NewWriter();

        writer.WriteStartArray(definiteLength: 8);
        writer.WriteByteString(value: Convert.FromHexString(s: payload.RecipientId.Domain));
        WriteOptionalText(writer: writer, value: payload.RecipientId.Subject);
        writer.WriteTextString(value: payload.RecipientId.Algorithm);
        writer.WriteByteString(value: Convert.FromHexString(s: payload.RecipientId.KeyHash));
        writer.WriteByteString(value: payload.EphemeralSubjectPublicKeyInfo);
        writer.WriteByteString(value: payload.Nonce);
        writer.WriteByteString(value: payload.Tag);
        writer.WriteByteString(value: payload.Ciphertext);
        writer.WriteEndArray();

        return writer.Encode();
    }
    private static IndependentSealedPayload DecodeSealedPayload(ReadOnlySpan<byte> bytes) {
        var reader = NewReader(bytes: bytes);

        ExpectArray(length: 8, reader: reader);

        var id = new IndependentId(
            Domain: ReadFingerprint(reader: reader),
            Subject: ReadOptionalText(reader: reader),
            Algorithm: reader.ReadTextString(),
            KeyHash: ReadFingerprint(reader: reader)
        );
        var ephemeral = reader.ReadByteString();
        var nonce = reader.ReadByteString();
        var tag = reader.ReadByteString();
        var ciphertext = reader.ReadByteString();

        reader.ReadEndArray();
        RequireConsumed(reader: reader);

        if (!string.Equals(a: id.Algorithm, b: SealingAlgorithm, comparisonType: StringComparison.Ordinal) || (nonce.Length != 12) || (tag.Length != 16)) {
            throw new FormatException(message: "The independent sealed payload has the wrong algorithm, nonce, or tag shape.");
        }

        return new IndependentSealedPayload(Ciphertext: ciphertext, EphemeralSubjectPublicKeyInfo: ephemeral, Nonce: nonce, RecipientId: id, Tag: tag);
    }
    private static byte[] DeriveKey(ECDiffieHellman privateKey, ECDiffieHellmanPublicKey publicKey, ReadOnlySpan<byte> recipientContext) {
        var secret = privateKey.DeriveRawSecretAgreement(otherPartyPublicKey: publicKey);

        try {
            var info = new byte[(HkdfLabel.Length + recipientContext.Length)];

            HkdfLabel.CopyTo(array: info, index: 0);
            recipientContext.CopyTo(destination: info.AsSpan(start: HkdfLabel.Length));

            return HKDF.DeriveKey(
                hashAlgorithmName: HashAlgorithmName.SHA256,
                ikm: secret,
                outputLength: 32,
                salt: null,
                info: info
            );
        } finally {
            CryptographicOperations.ZeroMemory(buffer: secret);
        }
    }
    private static byte[] EncodeRecipientContext(IndependentId recipientId) {
        var domain = Convert.FromHexString(s: recipientId.Domain);
        var subject = ((recipientId.Subject is null) ? null : Encoding.UTF8.GetBytes(s: recipientId.Subject));
        var algorithm = Encoding.UTF8.GetBytes(s: recipientId.Algorithm);
        var keyHash = Convert.FromHexString(s: recipientId.KeyHash);
        var result = new byte[(((((domain.Length + 1) + ((subject is null) ? 0 : (sizeof(uint) + subject.Length))) + sizeof(uint)) + algorithm.Length) + keyHash.Length)];
        var offset = 0;

        domain.CopyTo(array: result, index: offset);
        offset += domain.Length;
        result[offset++] = ((subject is null) ? (byte)0 : (byte)1);

        if (subject is not null) {
            BinaryPrimitives.WriteUInt32BigEndian(destination: result.AsSpan(start: offset), value: checked((uint)subject.Length));
            offset += sizeof(uint);
            subject.CopyTo(array: result, index: offset);
            offset += subject.Length;
        }

        BinaryPrimitives.WriteUInt32BigEndian(destination: result.AsSpan(start: offset), value: checked((uint)algorithm.Length));
        offset += sizeof(uint);
        algorithm.CopyTo(array: result, index: offset);
        offset += algorithm.Length;
        keyHash.CopyTo(array: result, index: offset);

        return result;
    }
    private static byte[] BindAssociatedData(ReadOnlySpan<byte> headerBytes, ReadOnlySpan<byte> recipientContext) {
        var result = new byte[(((AeadLabel.Length + sizeof(ulong)) + headerBytes.Length) + recipientContext.Length)];
        var offset = 0;

        AeadLabel.CopyTo(array: result, index: offset);
        offset += AeadLabel.Length;
        BinaryPrimitives.WriteUInt64BigEndian(destination: result.AsSpan(start: offset), value: checked((ulong)headerBytes.Length));
        offset += sizeof(ulong);
        headerBytes.CopyTo(destination: result.AsSpan(start: offset));
        offset += headerBytes.Length;
        recipientContext.CopyTo(destination: result.AsSpan(start: offset));

        return result;
    }
    private static string Fingerprint(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(bytes: SHA256.HashData(source: bytes));
    private static CborWriter NewWriter() => new(conformanceMode: CborConformanceMode.Strict);
    private static CborReader NewReader(ReadOnlySpan<byte> bytes) => new(data: bytes.ToArray(), conformanceMode: CborConformanceMode.Strict);
    private static void ExpectArray(CborReader reader, int length) {
        if (reader.ReadStartArray() != length) {
            throw new FormatException(message: $"The independent decoder expected a {length}-element array.");
        }
    }
    private static void RequireConsumed(CborReader reader) {
        if (reader.BytesRemaining != 0) {
            throw new FormatException(message: "The independent decoder found trailing bytes.");
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
        if (reader.PeekState() != CborReaderState.Null) {
            return reader.ReadTextString();
        }

        reader.ReadNull();

        return null;
    }
    private static void WriteOptionalUInt(CborWriter writer, ulong? value) {
        if (value is null) {
            writer.WriteNull();
        } else {
            writer.WriteUInt64(value: value.Value);
        }
    }
    private static ulong? ReadOptionalUInt(CborReader reader) {
        if (reader.PeekState() != CborReaderState.Null) {
            return reader.ReadUInt64();
        }

        reader.ReadNull();

        return null;
    }
    private static string ReadFingerprint(CborReader reader) {
        var bytes = reader.ReadByteString();

        if (bytes.Length != 32) {
            throw new FormatException(message: "The independent decoder requires 32-byte fingerprints.");
        }

        return Convert.ToHexStringLower(bytes: bytes);
    }
}
internal sealed record IndependentId(string Domain, string? Subject, string Algorithm, string KeyHash);
internal sealed record IndependentHeader(
    string Domain,
    string? Subject,
    string Algorithm,
    string Purpose,
    long NotBefore,
    long NotAfter,
    string? Audience,
    ulong? Sequence
);
internal sealed record IndependentAttestation(
    IndependentHeader Header,
    ulong PayloadKind,
    byte[] Payload,
    byte[] SignedPortion,
    byte[] Signature
);
internal sealed record IndependentBinding(IndependentId TargetId, byte[] SubjectPublicKeyInfo);
internal sealed record IndependentSealedPayload(
    IndependentId RecipientId,
    byte[] EphemeralSubjectPublicKeyInfo,
    byte[] Nonce,
    byte[] Tag,
    byte[] Ciphertext
);
